using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgentBridge.App.Core.Feedback;
using AgentBridge.FeedbackEnvelope;
using AgentBridge.FeedbackInbox;
using AgentBridge.Relay;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using NSec.Cryptography;
using Xunit;

namespace AgentBridge.App.Tests;

public class FeedbackSenderTests
{
    const string KeyId = "beta-v1";
    const string Json = "{\"schema\":\"slipgates-feedback.v1\",\"app\":\"0.1.100\",\"os\":\"Windows 11\",\"note\":\"The toast did not appear.\"}";

    // Built from pieces so the repository's secret scanner never sees a whole token.
    static readonly string Pat = "gh" + "p_" + new string('A', 36);

    sealed class Fake(long max = 8 * 1024 * 1024) : IFeedbackUplink
    {
        public long MaxBytes => max;
        public Exception? SlotError, UploadError;
        public List<byte[]> Uploaded = [];
        public int Slots;
        public Task<string> RequestSlotAsync(long storedSize, CancellationToken ct)
        {
            Slots++;
            return SlotError is null ? Task.FromResult("/v1/feedback/x") : Task.FromException<string>(SlotError);
        }
        public Task UploadAsync(string slotUrl, byte[] stored, CancellationToken ct)
        {
            if (UploadError is not null) { return Task.FromException(UploadError); }
            Uploaded.Add(stored);
            return Task.CompletedTask;
        }
    }

    sealed class Hung : IFeedbackUplink
    {
        public long MaxBytes => 8 * 1024 * 1024;
        public Task<string> RequestSlotAsync(long storedSize, CancellationToken ct) => Task.FromResult("/v1/feedback/x");
        public Task UploadAsync(string slotUrl, byte[] stored, CancellationToken ct) => Task.Delay(Timeout.Infinite, ct);
    }

    sealed class Keys(byte[] key) : IFeedbackPrivateKeyProvider
    {
        public Task<FeedbackPrivateKey> GetAsync(string keyId, CancellationToken ct) => Task.FromResult(new FeedbackPrivateKey((byte[])key.Clone()));
    }

    static (FeedbackRecipient Recipient, byte[] Private) NewRecipient()
    {
        using var key = Key.Create(KeyAgreementAlgorithm.X25519, new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });
        return (new FeedbackRecipient(KeyId, key.PublicKey.Export(KeyBlobFormat.RawPublicKey)), key.Export(KeyBlobFormat.RawPrivateKey));
    }

    static FeedbackBundle Bundle(string text = Json, byte[]? id = null) =>
        new(Encoding.UTF8.GetBytes(text), id ?? RandomNumberGenerator.GetBytes(16), DateTimeOffset.UnixEpoch, []);

    static Task<FeedbackSendResult> Send(IFeedbackUplink uplink, FeedbackRecipient? recipient, FeedbackBundle? bundle = null, CancellationToken ct = default) =>
        new FeedbackSender(uplink, recipient).SendAsync(bundle ?? Bundle(), ct);

    [Fact]
    public async Task The_exact_bundle_bytes_are_sealed_padded_and_open_with_the_private_key()
    {
        var (recipient, priv) = NewRecipient();
        var fake = new Fake();
        var bundle = Bundle();
        var now = new DateTimeOffset(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);

        var result = await new FeedbackSender(fake, recipient, new FakeTimeProvider(now)).SendAsync(bundle, default);

        var stored = Assert.Single(fake.Uploaded);
        Assert.Equal(new FeedbackSendResult.Sent(now), result);
        Assert.Equal(4096, stored.Length);
        Assert.Equal(bundle.CanonicalBytes.ToArray(), FeedbackEnvelopeV1.Open(stored, priv));
        var header = FeedbackEnvelopeV1.Parse(stored);
        Assert.Equal(KeyId, header.KeyId);
        Assert.Equal(bundle.BundleId, header.BundleId.ToArray());
    }

    sealed class Sources : IFeedbackSources
    {
        public FeedbackEnvironment Environment() => new("1.2.3", "beta", "Windows 11", "x64", 1, ["host 1.0.0"]);
        public Task<IReadOnlyList<FeedbackAgent>> DetectAgentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<FeedbackAgent>>([new("copilot", "1.0")]);
        public Task<IReadOnlyDictionary<string, string>> ConfigAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["channel"] = "beta", ["theme"] = "dark", ["autoUpdate"] = "true" });
        public Task<IReadOnlyList<string>> JournalTailAsync(int n, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(["relay connected", "token refreshed ok"]);
        public Task<IReadOnlyList<string>> CrashRecordsAsync(int n, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<FeedbackSessionTurn>?> SessionTurnsAsync(string id, int n, CancellationToken ct) => Task.FromResult<IReadOnlyList<FeedbackSessionTurn>?>(null);
    }

    [Fact]
    public async Task A_real_gathered_bundle_with_a_screenshot_is_not_refused_by_the_scan()
    {
        var (recipient, priv) = NewRecipient();
        var fake = new Fake();
        byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. RandomNumberGenerator.GetBytes(3000)];
        var gathered = await new FeedbackBundleSource(new Sources()).GatherAsync(new FeedbackRequest { Note = "The window froze when I clicked Send.", ScreenshotPng = png }, null, default);
        var bundle = Assert.IsType<FeedbackGatherResult.Ready>(gathered).Bundle;

        var result = await new FeedbackSender(fake, recipient).SendAsync(bundle, default);

        Assert.IsType<FeedbackSendResult.Sent>(result);
        Assert.Equal(bundle.CanonicalBytes.ToArray(), FeedbackEnvelopeV1.Open(Assert.Single(fake.Uploaded), priv));
    }

    [Fact]
    public async Task A_retry_seals_again_with_a_fresh_key_and_a_new_slot()
    {
        var (recipient, _) = NewRecipient();
        var fake = new Fake();
        var sender = new FeedbackSender(fake, recipient);
        var bundle = Bundle();

        await sender.SendAsync(bundle, default);
        await sender.SendAsync(bundle, default);

        Assert.Equal(2, fake.Slots);
        Assert.NotEqual(FeedbackEnvelopeV1.Parse(fake.Uploaded[0]).EphemeralPublicKey.ToArray(), FeedbackEnvelopeV1.Parse(fake.Uploaded[1]).EphemeralPublicKey.ToArray());
    }

    public static TheoryData<string> Leaks() => new()
    {
        "{\"note\":\"token " + Pat + "\"}",
        "{\"note\":\"header Bearer abcdef0123456789\"}",
        "{\"note\":\"-----BEGIN OPENSSH PRIVATE KEY-----\"}",
        // Quoted keys: the closing quote sits between the name and the colon.
        "{\"password\":\"hunter2\"}",
        "{\"token\": \"abc123\"}",
        "{\"config\":{\"api_key\":\"abc123\"}}",
        "{\"list\":[{\"secret\":\"abc123\"}]}",
        // Escaped inside a string value.
        "{\"note\":\"he said \\\"password\\\": \\\"hunter2\\\" ok\"}",
        "{\"note\":\"password=hunter2\"}",
        "{\"note\":\"" + "ey" + "JhbGciOiJIUzI1NiJ9." + "eyJzdWIiOiIxMjM0NTY3ODkwIn0." + "abcdefghij\"}",
        // Not JSON at all: scanned as raw text.
        "password: hunter2",
        // Non-string scalars under a credential name, an escaped JSON snippet inside a string, and a key spelled with a \u escape.
        "{\"password\":123456}",
        "{\"token\":true}",
        "{\"journal\":\"{\\\"password\\\":\\\"hunter2\\\"}\"}",
        "{\"journal\":\"{\\\"pass\\\\u0077ord\\\":\\\"hunter2\\\"}\"}",
        "{\"\\u0070assword\":\"hunter2\"}",
        // Malformed JSON (missing outer brace) is refused, never text-scanned.
        "{\"journal\":\"{\\\"pass\\\\u0077ord\\\":\\\"hunter2\\\"}\"",
        // The public bundle constructor takes any bytes, so what S1 would have redacted as PII also refuses.
        "{\"note\":\"write me at someone@example.com\"}",
        "{\"note\":\"saved in C:\\\\Users\\\\dswett\\\\Documents\\\\a.txt\"}",
    };

    [Fact]
    public async Task The_pre_send_gate_is_what_refuses_and_a_hit_stops_everything_before_sealing()
    {
        var (recipient, _) = NewRecipient();
        var fake = new Fake();
        var seen = new List<string>();

        var refused = await new FeedbackSender(fake, recipient, bundleScan: t => { seen.Add(t); return true; }).SendAsync(Bundle(), default);
        var allowed = await new FeedbackSender(fake, recipient, bundleScan: _ => false).SendAsync(Bundle(), default);

        Assert.Equal(new FeedbackSendResult.Failed(FeedbackFailureReason.BadRequest, FeedbackRetry.Never), refused);
        Assert.Equal(Json, Assert.Single(seen));
        Assert.IsType<FeedbackSendResult.Sent>(allowed);
        Assert.Single(fake.Uploaded);
    }

    [Theory]
    [InlineData(typeof(System.Text.RegularExpressions.RegexMatchTimeoutException))]
    [InlineData(typeof(InvalidOperationException))]
    [InlineData(typeof(StackOverflowGuardException))]
    public async Task A_scan_that_throws_or_times_out_refuses_and_carries_no_content(Type failure)
    {
        var (recipient, _) = NewRecipient();
        var fake = new Fake();
        var sender = new FeedbackSender(fake, recipient, bundleScan: t => throw (Exception)Activator.CreateInstance(failure)!);

        var result = await sender.SendAsync(Bundle(), default);

        Assert.Equal(new FeedbackSendResult.Failed(FeedbackFailureReason.BadRequest, FeedbackRetry.Never), result);
        Assert.DoesNotContain("hunter", result.ToString());
        Assert.Empty(fake.Uploaded);
        Assert.Equal(0, fake.Slots);
    }

    sealed class StackOverflowGuardException : Exception;

    [Theory]
    [MemberData(nameof(Leaks))]
    public async Task A_bundle_that_still_looks_like_a_secret_is_not_sent_and_nothing_leaves_the_device(string leak)
    {
        var (recipient, _) = NewRecipient();
        var fake = new Fake();

        var result = await Send(fake, recipient, Bundle(leak));

        Assert.Equal(new FeedbackSendResult.Failed(FeedbackFailureReason.BadRequest, FeedbackRetry.Never), result);
        Assert.Empty(fake.Uploaded);
        Assert.Equal(0, fake.Slots);
    }

    static string FakePng() => Convert.ToBase64String([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, .. new byte[300]]);

    [Theory]
    [InlineData("{\"note\":{\"screenshotPng\":{\"password\":\"hunter2\",\"email\":\"someone@example.com\"}}}")]
    [InlineData("{\"screenshotPng\":{\"password\":\"hunter2\"}}")]
    [InlineData("{\"screenshotPng\":\"not a png, mail someone@example.com\"}")]
    [InlineData("{\"screenshotPng\":\"" + "QUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFB" + "\"}")]
    public async Task A_screenshotPng_that_is_not_the_real_top_level_png_is_scanned_and_refused(string bundle)
    {
        var (recipient, _) = NewRecipient();
        var fake = new Fake();

        var result = await Send(fake, recipient, Bundle(bundle));

        Assert.Equal(new FeedbackSendResult.Failed(FeedbackFailureReason.BadRequest, FeedbackRetry.Never), result);
        Assert.Equal(0, fake.Slots);
    }

    [Fact]
    public async Task A_screenshotPng_at_the_wrong_path_is_scanned_and_a_real_one_at_the_top_is_not()
    {
        var (recipient, _) = NewRecipient();
        var fake = new Fake();

        var wrong = await Send(fake, recipient, Bundle("{\"note\":{\"screenshotPng\":\"" + FakePng() + "\"}}"));
        var right = await Send(fake, recipient, Bundle("{\"note\":\"hi\",\"screenshotPng\":\"" + FakePng() + "\"}"));

        Assert.IsType<FeedbackSendResult.Failed>(wrong);
        Assert.IsType<FeedbackSendResult.Sent>(right);
    }

    [Fact]
    public async Task A_line_cut_inside_a_placeholder_is_sent_but_a_real_email_at_the_end_still_refuses()
    {
        var (recipient, _) = NewRecipient();
        var fake = new Fake();

        var cut = await Send(fake, recipient, Bundle("{\"journal\":[\"opened C:\\\\Users\\\\<us…\",\"mail [redac…\"]}"));
        var email = await Send(fake, recipient, Bundle("{\"journal\":[\"write someone@example.com…\"]}"));
        var emailTail = await Send(fake, recipient, Bundle("{\"journal\":[\"write someone@example.com\"]}"));

        Assert.IsType<FeedbackSendResult.Sent>(cut);
        Assert.IsType<FeedbackSendResult.Failed>(email);
        Assert.IsType<FeedbackSendResult.Failed>(emailTail);
    }

    [Fact]
    public async Task Redaction_markers_and_the_attached_screenshot_are_not_mistaken_for_secrets()
    {
        var (recipient, _) = NewRecipient();
        var text = "{\"password\":\"[redacted]\",\"token\":\"[redacted]\",\"path\":\"C:\\\\Users\\\\<user>\\\\x\",\"note\":\"mail [redacted] Bearer [redacted]\",\"screenshotPng\":\"" + FakePng() + "\"}";

        var result = await Send(new Fake(), recipient, Bundle(text));

        Assert.IsType<FeedbackSendResult.Sent>(result);
    }

    [Fact]
    public async Task Empty_and_unconfigured_sends_do_not_seal()
    {
        var (recipient, _) = NewRecipient();
        var fake = new Fake();

        Assert.Equal(new FeedbackSendResult.Refused(FeedbackSendRefusal.Empty), await Send(fake, recipient, Bundle("")));
        Assert.Equal(new FeedbackSendResult.Failed(FeedbackFailureReason.KeyUnavailable, FeedbackRetry.Never), await Send(fake, null));
        Assert.Equal(new FeedbackSendResult.Failed(FeedbackFailureReason.Disabled, FeedbackRetry.Never), await Send(new Fake(0), recipient));
        Assert.Empty(fake.Uploaded);
    }

    [Fact]
    public async Task A_bundle_whose_sealed_size_is_over_the_relays_limit_is_too_large()
    {
        var (recipient, _) = NewRecipient();
        var fake = new Fake(max: 4096);

        var result = await Send(fake, recipient, Bundle("{\"note\":\"" + string.Concat(Enumerable.Repeat("word ", 1000)) + "\"}"));

        Assert.Equal(new FeedbackSendResult.TooLarge(8192, 4096), result);
        Assert.Equal(0, fake.Slots);
    }

    [Fact]
    public async Task A_bundle_over_the_envelopes_hard_cap_is_too_large_without_sealing()
    {
        var (recipient, _) = NewRecipient();
        var big = new byte[8 * 1024 * 1024];
        Array.Fill(big, (byte)'a');

        var result = await Send(new Fake(), recipient, new FeedbackBundle(big, new byte[16], DateTimeOffset.UnixEpoch, []));

        Assert.IsType<FeedbackSendResult.TooLarge>(result);
    }

    [Theory]
    [InlineData("rate-limited", FeedbackFailureReason.RateLimited, FeedbackRetry.AfterDelay, 60)]
    [InlineData("quota", FeedbackFailureReason.Quota, FeedbackRetry.AfterDelay, 3600)]
    [InlineData("disabled", FeedbackFailureReason.Disabled, FeedbackRetry.Never, null)]
    [InlineData("unavailable", FeedbackFailureReason.Unavailable, FeedbackRetry.Now, null)]
    [InlineData("bad-size", FeedbackFailureReason.UploadRejected, FeedbackRetry.Never, null)]
    public async Task The_relays_refusal_codes_become_typed_failures_with_a_retry_hint(string code, FeedbackFailureReason reason, FeedbackRetry retry, int? afterSeconds)
    {
        var (recipient, _) = NewRecipient();
        var fake = new Fake { SlotError = new BlobRefusedException(code) };

        var result = await Send(fake, recipient);

        Assert.Equal(new FeedbackSendResult.Failed(reason, retry, afterSeconds is { } s ? TimeSpan.FromSeconds(s) : null), result);
    }

    [Theory]
    [InlineData(null, FeedbackFailureReason.Offline, FeedbackRetry.WhenOnline, null)]
    [InlineData(HttpStatusCode.ServiceUnavailable, FeedbackFailureReason.Unreachable, FeedbackRetry.Now, null)]
    [InlineData(HttpStatusCode.InternalServerError, FeedbackFailureReason.Unreachable, FeedbackRetry.Now, null)]
    [InlineData(HttpStatusCode.TooManyRequests, FeedbackFailureReason.RateLimited, FeedbackRetry.AfterDelay, 60)]
    [InlineData(HttpStatusCode.Forbidden, FeedbackFailureReason.Unavailable, FeedbackRetry.Now, null)]
    [InlineData(HttpStatusCode.Conflict, FeedbackFailureReason.UploadRejected, FeedbackRetry.Never, null)]
    [InlineData(HttpStatusCode.RequestEntityTooLarge, FeedbackFailureReason.UploadRejected, FeedbackRetry.Never, null)]
    public async Task A_failed_upload_is_typed_by_what_storage_said(HttpStatusCode? status, FeedbackFailureReason reason, FeedbackRetry retry, int? afterSeconds)
    {
        var (recipient, _) = NewRecipient();
        var fake = new Fake { UploadError = new HttpRequestException("x", null, status) };

        var result = await Send(fake, recipient);

        Assert.Equal(new FeedbackSendResult.Failed(reason, retry, afterSeconds is { } s ? TimeSpan.FromSeconds(s) : null), result);
    }

    [Fact]
    public async Task A_dropped_connection_is_offline_and_retries_when_online()
    {
        var (recipient, _) = NewRecipient();

        var result = await Send(new Fake { SlotError = new IOException("gone") }, recipient);

        Assert.Equal(new FeedbackSendResult.Failed(FeedbackFailureReason.Offline, FeedbackRetry.WhenOnline), result);
    }

    [Fact]
    public async Task The_caller_cancelling_is_cancelled_not_an_exception()
    {
        var (recipient, _) = NewRecipient();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Equal(new FeedbackSendResult.Failed(FeedbackFailureReason.Cancelled, FeedbackRetry.Now), await Send(new Fake(), recipient, ct: cts.Token));
        Assert.Equal(new FeedbackSendResult.Failed(FeedbackFailureReason.Cancelled, FeedbackRetry.Now),
            await Send(new Fake { SlotError = new OperationCanceledException(cts.Token) }, recipient, ct: cts.Token));
    }

    [Fact]
    public async Task An_upload_that_never_finishes_times_out_as_unreachable()
    {
        var (recipient, _) = NewRecipient();

        var result = await new FeedbackSender(new Hung(), recipient, timeout: TimeSpan.FromMilliseconds(100)).SendAsync(Bundle(), default);

        Assert.Equal(new FeedbackSendResult.Failed(FeedbackFailureReason.Unreachable, FeedbackRetry.Now), result);
    }

    [Fact]
    public async Task The_failure_result_carries_no_report_content()
    {
        var (recipient, _) = NewRecipient();
        var fake = new Fake { UploadError = new HttpRequestException(Json) };

        var result = await Send(fake, recipient);

        Assert.DoesNotContain("toast", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_recipient_needs_a_canonical_32_byte_key_and_a_valid_id()
    {
        var key = BlobFormat.Base64Url(RandomNumberGenerator.GetBytes(32));

        Assert.NotNull(FeedbackRecipient.TryParse(KeyId, key));
        Assert.Null(FeedbackRecipient.TryParse(KeyId, BlobFormat.Base64Url(new byte[31])));
        Assert.Null(FeedbackRecipient.TryParse("bad id", key));
        Assert.Null(FeedbackRecipient.TryParse(KeyId, null));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_bundle_is_stored_by_a_real_relay_and_opens_with_the_inbox_decryptor()
    {
        await using var relay = await TestRelay.StartAsync(o => o.Feedback.Backend = "local");
        var phone = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "phone", "pwa");
        var (recipient, priv) = NewRecipient();
        using var http = BlobHttp.CreateClient();
        var sender = new FeedbackSender(new RelayFeedbackUplink(phone.Relay, http), recipient);
        var bundle = Bundle();

        Assert.IsType<FeedbackSendResult.Sent>(await sender.SendAsync(bundle, default));

        var stored = Assert.Single(((LocalFeedbackSink)relay.App.Services.GetRequiredService<FeedbackService>().Sink!).Stored);
        Assert.Equal(bundle.BundleId, FeedbackEnvelopeV1.Parse(stored.Value).BundleId.ToArray());
        var opened = await new FeedbackEnvelopeV1Decryptor().DecryptAsync(stored.Value, new Keys(priv), default);
        Assert.Equal(bundle.Text, opened.PrivateContent);
    }
}
