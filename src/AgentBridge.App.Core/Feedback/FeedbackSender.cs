using System.Net;
using System.Text;
using System.Text.Json;
using AgentBridge.FeedbackEnvelope;
using AgentBridge.Transport;

namespace AgentBridge.App.Core.Feedback;

// The feedback recipient: the public key the bundle is sealed to, and its id.
public sealed record FeedbackRecipient(string KeyId, byte[] PublicKey)
{
    public static FeedbackRecipient? TryParse(string? keyId, string? publicKeyBase64Url)
    {
        var key = BlobFormat.FromBase64Url(publicKeyBase64Url);
        if (key is not { Length: 32 } || BlobFormat.Base64Url(key) != publicKeyBase64Url) { return null; }
        if (keyId is not { Length: >= 1 and <= 64 } || keyId.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))) { return null; }
        return new FeedbackRecipient(keyId, key);
    }
}

// The relay as the sender needs it, so tests run without a connection.
public interface IFeedbackUplink
{
    // The largest stored bundle the relay takes; 0 when it takes none.
    long MaxBytes { get; }
    // Throws BlobRefusedException with the relay's code (rate-limited, quota, ...).
    Task<string> RequestSlotAsync(long storedSize, CancellationToken ct);
    // Throws HttpRequestException (with StatusCode when storage answered) on failure.
    Task UploadAsync(string slotUrl, byte[] stored, CancellationToken ct);
}

public sealed class RelayFeedbackUplink(RelayClient relay, HttpClient http) : IFeedbackUplink
{
    public long MaxBytes => relay.FeedbackMaxBytes;

    public async Task<string> RequestSlotAsync(long storedSize, CancellationToken ct) =>
        (await relay.FeedbackPutAsync(storedSize, ct)).Url;

    public async Task UploadAsync(string slotUrl, byte[] stored, CancellationToken ct)
    {
        // The relay is not trusted: its URL is only followed to itself or to Azure Storage.
        var url = BlobHttp.Resolve(relay.Relay, slotUrl) ?? throw new BlobRefusedException("bad-url");
        await BlobHttp.UploadAsync(http, url, new MemoryStream(stored), stored.Length, ct);
    }
}

// Seals the exact canonical bytes the person reviewed and uploads them write-only (S2 of #738,
// docs/protocol/feedback.md). Each attempt seals afresh and takes a new single-use URL, so Retry is
// always a call to SendAsync again with the same bundle. Nothing is written to disk, queued or
// kept between calls, and no result, exception or log carries report content.
public sealed class FeedbackSender(IFeedbackUplink uplink, FeedbackRecipient? recipient, TimeProvider? clock = null, TimeSpan? timeout = null, Func<string, bool>? bundleScan = null) : IFeedbackSender
{
    // What the window shows for every failed send; the reason code only picks the Retry button.
    public const string UserMessage = "Couldn't send. Try again when you're online.";

    // The relay does not say when to come back; these are its window sizes (3 a minute, 10 a day).
    public static readonly TimeSpan RateLimitDelay = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan QuotaDelay = TimeSpan.FromHours(1);
    // The whole send (slot request and upload); the shared blob client has no timeout of its own.
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(60);

    const int HardCap = 8 * 1024 * 1024;
    // The person-attached image is base64 by design, so it is not scanned as a secret.

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public async Task<FeedbackSendResult> SendAsync(FeedbackBundle bundle, CancellationToken cancellationToken)
    {
        if (bundle.Length == 0) { return new FeedbackSendResult.Refused(FeedbackSendRefusal.Empty); }
        if (recipient is null) { return new FeedbackSendResult.Failed(FeedbackFailureReason.KeyUnavailable, FeedbackRetry.Never); }
        if (uplink.MaxBytes <= 0) { return new FeedbackSendResult.Failed(FeedbackFailureReason.Disabled, FeedbackRetry.Never); }
        // The envelope's hard cap; checked before the scan so an oversized input is never read through.
        if (bundle.Length >= HardCap) { return new FeedbackSendResult.TooLarge(bundle.Length, uplink.MaxBytes); }
        if (cancellationToken.IsCancellationRequested) { return Cancelled(); }

        if (LooksSensitive(bundle.CanonicalBytes.Span)) { return new FeedbackSendResult.Failed(FeedbackFailureReason.BadRequest, FeedbackRetry.Never); }

        byte[] stored;
        try { stored = FeedbackEnvelopeV1.Seal(bundle.CanonicalBytes.Span, recipient.KeyId, recipient.PublicKey, bundle.BundleId); }
        catch (ArgumentException) { return new FeedbackSendResult.TooLarge(bundle.Length, uplink.MaxBytes); }
        catch (FeedbackEnvelopeException) { return new FeedbackSendResult.Failed(FeedbackFailureReason.KeyUnavailable, FeedbackRetry.Never); }
        if (stored.Length > uplink.MaxBytes) { return new FeedbackSendResult.TooLarge(stored.Length, uplink.MaxBytes); }

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout ?? SendTimeout);

        string url;
        try { url = await uplink.RequestSlotAsync(stored.Length, limit.Token); }
        catch (OperationCanceledException) { return Interrupted(cancellationToken); }
        catch (BlobRefusedException e) { return FromRelay(e.Reason, stored.Length, uplink.MaxBytes); }
        catch (Exception) { return Offline(); }

        try { await uplink.UploadAsync(url, stored, limit.Token); }
        catch (OperationCanceledException) { return Interrupted(cancellationToken); }
        catch (BlobRefusedException) { return new FeedbackSendResult.Failed(FeedbackFailureReason.UploadRejected, FeedbackRetry.Never); }
        catch (HttpRequestException e) { return FromStorage(e.StatusCode); }
        catch (Exception) { return Offline(); }
        return new FeedbackSendResult.Sent((clock ?? TimeProvider.System).GetUtcNow());
    }

    // Defence in depth behind the gatherer: the redactor's own bundle scan (one source of truth) on what is about to be sealed; non-JSON is scanned as raw text.
    // Fails closed: any redactor error or timeout counts as a hit, and nothing about the content is kept.
    bool LooksSensitive(ReadOnlySpan<byte> bytes)
    {
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return true; }
        // Every scan or parse error (malformed JSON included) refuses; a text fallback could miss escaped names.
        try
        {
            if ((bundleScan ?? FeedbackRedactor.BundleHasSecret)(text)) { return true; }
            using var doc = JsonDocument.Parse(text);
            return HasUnsanitizedString(doc.RootElement);
        }
        catch (Exception) { return true; }
    }

    // The bundle constructor is public, so an email or profile path S1 would have redacted also refuses. The screenshot is skipped.
    static bool HasUnsanitizedString(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: return FeedbackRedactor.WouldRedact(e.GetString()!);
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) { if (HasUnsanitizedString(item)) { return true; } }
                return false;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
                    if (p.NameEquals("screenshotPng")) { continue; }
                    if (FeedbackRedactor.WouldRedact(p.Name) || HasUnsanitizedString(p.Value)) { return true; }
                }
                return false;
            default: return false;
        }
    }

    static FeedbackSendResult Cancelled() => new FeedbackSendResult.Failed(FeedbackFailureReason.Cancelled, FeedbackRetry.Now);

    static FeedbackSendResult Offline() => new FeedbackSendResult.Failed(FeedbackFailureReason.Offline, FeedbackRetry.WhenOnline);

    // The caller cancelling is Cancelled; the send timing out on its own is an unreachable relay.
    static FeedbackSendResult Interrupted(CancellationToken caller) =>
        caller.IsCancellationRequested ? Cancelled() : new FeedbackSendResult.Failed(FeedbackFailureReason.Unreachable, FeedbackRetry.Now);

    static FeedbackSendResult Unreachable() => new FeedbackSendResult.Failed(FeedbackFailureReason.Unreachable, FeedbackRetry.Now);

    static FeedbackSendResult RateLimited() => new FeedbackSendResult.Failed(FeedbackFailureReason.RateLimited, FeedbackRetry.AfterDelay, RateLimitDelay);

    static FeedbackSendResult FromRelay(string reason, long size, long max) => reason switch
    {
        "too-large" => new FeedbackSendResult.TooLarge(size, max),
        "rate-limited" => RateLimited(),
        "quota" => new FeedbackSendResult.Failed(FeedbackFailureReason.Quota, FeedbackRetry.AfterDelay, QuotaDelay),
        "disabled" => new FeedbackSendResult.Failed(FeedbackFailureReason.Disabled, FeedbackRetry.Never),
        "unavailable" => new FeedbackSendResult.Failed(FeedbackFailureReason.Unavailable, FeedbackRetry.Now),
        _ => new FeedbackSendResult.Failed(FeedbackFailureReason.UploadRejected, FeedbackRetry.Never),
    };

    // No status means no answer at all (offline); 5xx and timeouts are the relay's store failing;
    // 403 is an expired ticket, fixed by asking again; the rest (400, 409, 413) mean the request
    // itself was wrong.
    static FeedbackSendResult FromStorage(HttpStatusCode? status) => status switch
    {
        null => Offline(),
        HttpStatusCode.TooManyRequests => RateLimited(),
        HttpStatusCode.InternalServerError or HttpStatusCode.ServiceUnavailable or HttpStatusCode.RequestTimeout
            or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout => Unreachable(),
        HttpStatusCode.Forbidden => new FeedbackSendResult.Failed(FeedbackFailureReason.Unavailable, FeedbackRetry.Now),
        _ => new FeedbackSendResult.Failed(FeedbackFailureReason.UploadRejected, FeedbackRetry.Never),
    };
}

