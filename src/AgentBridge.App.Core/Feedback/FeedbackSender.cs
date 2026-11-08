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

        if (LooksSensitive(bundle.CanonicalBytes.Span, bundle.BundleId)) { return new FeedbackSendResult.Failed(FeedbackFailureReason.BadRequest, FeedbackRetry.Never); }

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
    bool LooksSensitive(ReadOnlySpan<byte> bytes, byte[] bundleId)
    {
        string text;
        try { text = StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException) { return true; }
        // Every scan or parse error (malformed JSON included) refuses; a text fallback could miss escaped names.
        try
        {
            using var doc = JsonDocument.Parse(text);
            // An S1 bundle is always an object; anything else could hide a screenshotPng the redactor skips.
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { return true; }
            var scanned = WithoutStructuralIdAndRealScreenshot(doc.RootElement, text, bundleId);
            using var scannedDoc = JsonDocument.Parse(scanned);
            if (HasSensitiveJoinedClasses(scannedDoc.RootElement)) { return true; }
            if (HasSensitiveText(scannedDoc.RootElement)) { return true; }
            if ((bundleScan ?? FeedbackRedactor.BundleHasSecret)(scanned)) { return true; }
            return HasUnsanitizedString(scannedDoc.RootElement);
        }
        catch (Exception) { return true; }
    }

    static bool HasSensitiveJoinedClasses(JsonElement root)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (property.Name is "journal" or "crashes" && property.Value.ValueKind == JsonValueKind.Array)
            {
                var joined = new StringBuilder();
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String) { return true; }
                    joined.Append(item.GetString());
                }
                if (FeedbackRedactor.MentionsSensitiveField(joined.ToString().Replace(FeedbackRedactor.EscapedMark, "", StringComparison.Ordinal))) { return true; }
            }
            else if (property.NameEquals("sessionTurns") && property.Value.ValueKind == JsonValueKind.Object
                && property.Value.TryGetProperty("turns", out var turns) && turns.ValueKind == JsonValueKind.Array)
            {
                var joined = new StringBuilder();
                foreach (var turn in turns.EnumerateArray())
                {
                    if (turn.ValueKind != JsonValueKind.Object || !turn.TryGetProperty("text", out var text)
                        || text.ValueKind != JsonValueKind.String) { return true; }
                    joined.Append(text.GetString());
                }
                if (FeedbackRedactor.MentionsSensitiveField(joined.ToString().Replace(FeedbackRedactor.EscapedMark, "", StringComparison.Ordinal))) { return true; }
            }
        }
        return false;
    }

    static bool HasSensitiveText(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString()!;
            if (text is FeedbackBundleSource.JournalOmitted or FeedbackBundleSource.CrashOmitted
                or FeedbackBundleSource.TextOmitted or FeedbackBundleSource.NoteOmitted or FeedbackBundleSource.TurnsOmitted) { return false; }
            return FeedbackRedactor.MentionsSensitiveField(text.Replace(FeedbackRedactor.EscapedMark, "", StringComparison.Ordinal));
        }
        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) { if (HasSensitiveText(item)) { return true; } }
        }
        else if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (FeedbackRedactor.MentionsSensitiveField(property.Name) || HasSensitiveText(property.Value)) { return true; }
            }
        }
        return false;
    }

    // The redactor skips any property named screenshotPng, at any depth. Only the top-level one holding valid base64 PNG data may
    // be skipped: it is dropped here, and every other property with that name is renamed so it is scanned like any other value.
    static string WithoutStructuralIdAndRealScreenshot(JsonElement root, string original, byte[] bundleId)
    {
        if (!root.TryGetProperty("bundleId", out _) && !ContainsName(root)) { return original; }
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms))
        {
            w.WriteStartObject();
            foreach (var p in root.EnumerateObject())
            {
                if (p.NameEquals(ScreenshotName) && IsPng(p.Value)) { continue; }
                if (p.NameEquals("bundleId"))
                {
                    if (p.Value.ValueKind != JsonValueKind.String) { throw new JsonException("Invalid bundle id."); }
                    var value = p.Value.GetString();
                    var decoded = BlobFormat.FromBase64Url(value);
                    if (decoded is not { Length: 16 } || BlobFormat.Base64Url(decoded) != value || !decoded.AsSpan().SequenceEqual(bundleId))
                    {
                        throw new JsonException("Invalid bundle id.");
                    }
                    continue;
                }
                w.WritePropertyName(p.Name == ScreenshotName ? "screenshotPng_" : p.Name);
                Write(p.Value, w);
            }
            w.WriteEndObject();
        }
        return StrictUtf8.GetString(ms.ToArray());

        static void Write(JsonElement e, Utf8JsonWriter w)
        {
            switch (e.ValueKind)
            {
                case JsonValueKind.Object:
                    w.WriteStartObject();
                    foreach (var p in e.EnumerateObject()) { w.WritePropertyName(p.Name == ScreenshotName ? "screenshotPng_" : p.Name); Write(p.Value, w); }
                    w.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    w.WriteStartArray();
                    foreach (var item in e.EnumerateArray()) { Write(item, w); }
                    w.WriteEndArray();
                    break;
                default: e.WriteTo(w); break;
            }
        }
    }

    const string ScreenshotName = "screenshotPng";

    static bool ContainsName(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Array) { foreach (var i in e.EnumerateArray()) { if (ContainsName(i)) { return true; } } return false; }
        if (e.ValueKind != JsonValueKind.Object) { return false; }
        foreach (var p in e.EnumerateObject()) { if (p.NameEquals(ScreenshotName) || ContainsName(p.Value)) { return true; } }
        return false;
    }

    static bool IsPng(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.String) { return false; }
        var b64 = e.GetString()!;
        if (b64.Length == 0 || b64.Length > 12 * 1024 * 1024) { return false; }
        var buffer = new byte[b64.Length / 4 * 3 + 3];
        return Convert.TryFromBase64String(b64, buffer, out var n) && n >= PngSignature.Length && buffer.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature);
    }

    static readonly byte[] PngSignature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    static readonly string[] Placeholders = [FeedbackRedactor.Mark, FeedbackRedactor.EscapedMark, "[redacted: truncated]", "<user>", "<host>"];

    // S1 redacts and then truncates with an ellipsis, so a line can end in a cut placeholder such as "<us…". Drop that tail,
    // and only a real prefix of a placeholder S1 emits, before checking; any other '<' or '[' fragment stays in the scan.
    static string WithoutTruncationTail(string s)
    {
        if (!s.EndsWith('…')) { return s; }
        s = s[..^1];
        var open = s.LastIndexOfAny(['<', '[']);
        if (open < 0) { return s; }
        var fragment = s.AsSpan(open);
        foreach (var p in Placeholders)
        {
            if (fragment.Length < p.Length && p.AsSpan().StartsWith(fragment, StringComparison.Ordinal)) { return s[..open]; }
        }
        return s;
    }

    // The bundle constructor is public, so an email or profile path S1 would have redacted also refuses.
    static bool HasUnsanitizedString(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.String: return FeedbackRedactor.WouldRedact(WithoutTruncationTail(e.GetString()!));
            case JsonValueKind.Array:
                foreach (var item in e.EnumerateArray()) { if (HasUnsanitizedString(item)) { return true; } }
                return false;
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject())
                {
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

