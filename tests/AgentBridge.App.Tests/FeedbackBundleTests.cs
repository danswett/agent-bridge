using System.Text;
using System.Text.Json;
using AgentBridge.App.Core.Feedback;
using Xunit;

namespace AgentBridge.App.Tests;

public class FeedbackBundleTests
{
    static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    // Built from pieces so the repository's secret scanner never sees a whole token.
    static readonly string Pat = "gh" + "p_" + new string('A', 36);
    static readonly string Aws = "AK" + "IA" + "ABCDEFGHIJKLMNOP";
    static readonly string Jwt = "ey" + "JhbGciOiJIUzI1NiJ9." + "eyJzdWIiOiIxMjM0NTY3ODkwIn0." + "abcdefghij";

    sealed class Fake : IFeedbackSources
    {
        public string Secret = "";
        public bool FailJournal;
        public bool Hang;
        public bool Junk;
        public bool IgnoreCancel;
        public bool FailTurns;
        public IReadOnlyList<FeedbackSessionTurn>? Turns;
        public FeedbackEnvironment Environment() => new("0.1.42", "beta", "Windows 11", "x64", 1, ["0.1.42", "0.1.41"]);
        public Task<IReadOnlyList<FeedbackAgent>> DetectAgentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<FeedbackAgent>>([new("Copilot", "1.0.107"), new("Claude", null)]);
        public Task<IReadOnlyDictionary<string, string>> ConfigAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>
        {
            ["theme"] = "dark",
            ["relayToken"] = Secret,
            ["homeAssistant.url"] = "http://192.168.1.5:8123",
            ["notifications"] = "on " + Secret,
        });
        public async Task<IReadOnlyList<string>> JournalTailAsync(int max, CancellationToken ct)
        {
            if (Hang) { await Task.Delay(Timeout.Infinite, ct); }
            if (IgnoreCancel) { await Task.Delay(Timeout.Infinite); }
            if (Junk) { return Enumerable.Repeat(new string((char)1, 400), 200).ToList(); }
            if (FailJournal) { throw new IOException(@"C:\Users\alice\secret.log"); }
            return ["connected", $"Authorization: Bearer {Secret}", @"opened C:\Users\alice\repos\x", "mail bob@example.com"];
        }
        public Task<IReadOnlyList<string>> CrashRecordsAsync(int max, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Junk ? Enumerable.Repeat(new string((char)1, 8000), 5).ToList() : [$"System.Exception: boom token={Secret} at C:\\Users\\alice\\a.cs"]);
        public Task<IReadOnlyList<FeedbackSessionTurn>?> SessionTurnsAsync(string id, int max, CancellationToken ct) => FailTurns ? throw new IOException("x") : Task.FromResult(Turns);
    }

    static Task<FeedbackGatherResult> Gather(Fake f, FeedbackRequest? r = null, TimeSpan? timeout = null, CancellationToken ct = default)
        => new FeedbackBundleSource(f, () => new DateTimeOffset(2026, 11, 8, 10, 0, 0, TimeSpan.Zero), timeout).GatherAsync(r ?? new FeedbackRequest(), null, ct);

    static FeedbackBundle Ready(FeedbackGatherResult r) => Assert.IsType<FeedbackGatherResult.Ready>(r).Bundle;

    [Fact]
    public async Task A_secret_shaped_string_in_any_source_or_in_the_note_never_reaches_the_bytes()
    {
        foreach (var secret in new[] { Pat, Aws, Jwt })
        {
            var f = new Fake { Secret = secret, Turns = [new("user", $"my key is {secret}", DateTimeOffset.UnixEpoch)] };
            var bundle = Ready(await Gather(f, new FeedbackRequest { Note = $"it broke, token {secret} sorry bob@example.com", SessionTurnsFor = "s1" }));
            var text = Encoding.UTF8.GetString(bundle.CanonicalBytes.Span);
            Assert.DoesNotContain(secret, text);
            Assert.DoesNotContain("alice", text);
            Assert.DoesNotContain("bob@example.com", text);
            Assert.DoesNotContain("192.168.1.5", text);
        }
    }

    [Fact]
    public async Task Only_allow_listed_settings_are_shown_and_the_rest_are_counted()
    {
        using var doc = JsonDocument.Parse((await Gather(new Fake { Secret = Pat })).Pipe(Ready).Text);
        var config = doc.RootElement.GetProperty("config");
        Assert.Equal("dark", config.GetProperty("theme").GetString());
        Assert.False(config.TryGetProperty("relayToken", out _));
        Assert.Equal(2, config.GetProperty("otherSettingsNotIncluded").GetInt32());
        Assert.Contains("[redacted]", config.GetProperty("notifications").GetString());
    }

    [Fact]
    public async Task The_preview_text_is_the_exact_bytes_that_are_sent()
    {
        var bundle = Ready(await Gather(new Fake { Secret = Pat }, new FeedbackRequest { Note = "hello" }));
        Assert.Equal(bundle.CanonicalBytes.ToArray(), Encoding.UTF8.GetBytes(bundle.Text));
        Assert.NotEqual(0xEF, bundle.CanonicalBytes.Span[0]);
        Assert.DoesNotContain('\r', bundle.Text);
        Assert.Equal(16, bundle.BundleId.Length);
        using var doc = JsonDocument.Parse(bundle.Text);
        Assert.Equal(Convert.ToBase64String(bundle.BundleId).TrimEnd('=').Replace('+', '-').Replace('/', '_'), doc.RootElement.GetProperty("bundleId").GetString());
        Assert.Equal(bundle.Length, bundle.CanonicalBytes.Length);
    }

    [Fact]
    public async Task Session_turns_and_screenshot_are_off_unless_asked_for_and_listed_as_optional_when_on()
    {
        var off = Ready(await Gather(new Fake()));
        Assert.DoesNotContain("sessionTurns", off.Text);
        Assert.DoesNotContain("screenshotPng", off.Text);
        Assert.DoesNotContain(off.Items, i => i.Optional);

        var on = Ready(await Gather(new Fake { Turns = [new("user", "hi", DateTimeOffset.UnixEpoch)] }, new FeedbackRequest { SessionTurnsFor = "s1", ScreenshotPng = Png }));
        Assert.Equal(["screenshot", "sessionTurns"], on.Items.Where(i => i.Optional).Select(i => i.Id));
    }

    [Fact]
    public async Task One_source_failing_leaves_that_item_unavailable_and_leaks_nothing_of_the_error()
    {
        var bundle = Ready(await Gather(new Fake { FailJournal = true }));
        using var doc = JsonDocument.Parse(bundle.Text);
        Assert.Equal("unavailable", doc.RootElement.GetProperty("journal").GetString());
        Assert.DoesNotContain("alice", bundle.Text);
    }

    [Fact]
    public async Task Refusals_cover_a_long_note_a_bad_or_huge_screenshot_an_unknown_session_and_an_oversize_bundle()
    {
        Assert.Equal(FeedbackRefusalReason.NoteTooLong, Assert.IsType<FeedbackGatherResult.Refused>(await Gather(new Fake(), new FeedbackRequest { Note = new string('x', 1001) })).Reason);
        Assert.Equal(FeedbackRefusalReason.ScreenshotNotPng, Assert.IsType<FeedbackGatherResult.Refused>(await Gather(new Fake(), new FeedbackRequest { ScreenshotPng = [1, 2, 3, 4, 5, 6, 7, 8, 9] })).Reason);
        Assert.Equal(FeedbackRefusalReason.ScreenshotTooLarge, Assert.IsType<FeedbackGatherResult.Refused>(await Gather(new Fake(), new FeedbackRequest { ScreenshotPng = new byte[FeedbackLimits.MaxScreenshotBytes + 1] })).Reason);
        Assert.Equal(FeedbackRefusalReason.SessionNotFound, Assert.IsType<FeedbackGatherResult.Refused>(await Gather(new Fake { Turns = null }, new FeedbackRequest { SessionTurnsFor = "nope" })).Reason);
        Assert.Equal(FeedbackRefusalReason.SessionUnavailable, Assert.IsType<FeedbackGatherResult.Refused>(await Gather(new Fake { FailTurns = true }, new FeedbackRequest { SessionTurnsFor = "s" })).Reason);
    }

    [Fact]
    public async Task A_bundle_over_the_cap_is_refused_as_too_large()
    {
        // Control characters stay as they are when redacted but take six bytes each as JSON escapes.
        var big = new byte[FeedbackLimits.MaxScreenshotBytes];
        Array.Copy(Png, big, Png.Length);
        var turns = Enumerable.Range(0, 20).Select(i => new FeedbackSessionTurn("user", new string((char)1, 2000), DateTimeOffset.UnixEpoch)).ToList();
        var result = await Gather(new Fake { Junk = true, Turns = turns }, new FeedbackRequest { ScreenshotPng = big, SessionTurnsFor = "s" });
        Assert.Equal(FeedbackRefusalReason.BundleTooLarge, Assert.IsType<FeedbackGatherResult.Refused>(result).Reason);
    }
    [Fact]
    public async Task Text_cut_mid_emoji_or_holding_a_lone_surrogate_still_gathers()
    {
        // "ab " filler matches no redaction rule, so the 2000-char cut really lands inside the emoji.
        var filler = string.Concat(Enumerable.Repeat("ab ", 666)) + "a";
        Assert.Equal(1999, filler.Length);
        var f = new Fake { Turns = [new("user", filler + "😀 tail", DateTimeOffset.UnixEpoch)] };
        var bundle = Ready(await Gather(f, new FeedbackRequest { Note = "bad \ud83d half", SessionTurnsFor = "s" }));
        using var doc = JsonDocument.Parse(bundle.Text);
        Assert.Contains("\uFFFD", doc.RootElement.GetProperty("note").GetString());
        var text = doc.RootElement.GetProperty("sessionTurns").GetProperty("turns")[0].GetProperty("text").GetString()!;
        Assert.Equal(filler + "…", text);
    }

    [Fact]
    public async Task A_source_that_ignores_cancellation_cannot_hang_gathering()
    {
        var gather = Gather(new Fake { IgnoreCancel = true }, timeout: TimeSpan.FromMilliseconds(100));
        Assert.Same(gather, await Task.WhenAny(gather, Task.Delay(5000)));
        await Assert.ThrowsAsync<TimeoutException>(() => gather);
    }

    [Fact]
    public async Task The_machine_and_user_names_are_cut_from_free_text()
    {
        var bundle = Ready(await new FeedbackBundleSource(new Fake(), null, null, ["Swett", "DAN-PC"]).GatherAsync(new FeedbackRequest { Note = "swett on dan-pc broke" }, null, default));
        Assert.DoesNotContain("wett", bundle.Text);
        Assert.DoesNotContain("DAN-PC", bundle.Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_finished_bundle_passes_the_same_secret_check_the_sender_uses()
    {
        var f = new Fake { Secret = Pat, Turns = [new("user", Pat, DateTimeOffset.UnixEpoch)] };
        var big = new byte[5000];
        Array.Copy(Png, big, Png.Length);
        var bundle = Ready(await Gather(f, new FeedbackRequest { Note = "key " + Pat, ScreenshotPng = big, SessionTurnsFor = "s" }));
        Assert.False(FeedbackRedactor.BundleHasSecret(bundle.Text));
        Assert.True(FeedbackRedactor.BundleHasSecret(bundle.Text.Replace("\"note\": \"", "\"note\": \"" + Pat + " ")));
    }
    [Theory]
    [InlineData("{\"password\":\"hunter2\"}", true)]
    [InlineData("{\"x\":{\"api_key\":\"abc def ghi\"}}", true)]
    [InlineData("{\"token\":12345678}", true)]
    [InlineData("{\"password\":\"[redacted]\"}", false)]
    [InlineData("{\"password\":\"\"}", false)]
    [InlineData("{\"name\":\"hunter2\"}", false)]
    public void BundleHasSecret_sees_name_value_pairs_split_across_json_tokens(string json, bool expected)
        => Assert.Equal(expected, FeedbackRedactor.BundleHasSecret(json));

    [Fact]
    public async Task A_failing_environment_source_becomes_unavailable_instead_of_throwing()
    {
        var bundle = Ready(await new FeedbackBundleSource(new ThrowingEnv()).GatherAsync(new FeedbackRequest(), null, default));
        Assert.Contains("unavailable", bundle.Text);
    }

    sealed class ThrowingEnv : IFeedbackSources
    {
        public FeedbackEnvironment Environment() => throw new InvalidOperationException(@"C:\Users\alice");
        public Task<IReadOnlyList<FeedbackAgent>> DetectAgentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<FeedbackAgent>>([]);
        public Task<IReadOnlyDictionary<string, string>> ConfigAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string>());
        public Task<IReadOnlyList<string>> JournalTailAsync(int max, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<string>> CrashRecordsAsync(int max, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public Task<IReadOnlyList<FeedbackSessionTurn>?> SessionTurnsAsync(string id, int max, CancellationToken ct) => Task.FromResult<IReadOnlyList<FeedbackSessionTurn>?>(null);
    }

    [Fact]
    public async Task A_hung_source_times_out_and_cancellation_is_passed_on()
    {
        await Assert.ThrowsAsync<TimeoutException>(() => Gather(new Fake { Hang = true }, timeout: TimeSpan.FromMilliseconds(50)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Gather(new Fake { Hang = true }, ct: cts.Token));
    }

    [Theory]
    [InlineData(@"opened C:\Users\Dan Swett\x", "Swett")]
    [InlineData("file:///home/dan/x", "dan")]
    [InlineData("sent Bearer opaque.session-value1 to server", "opaque.session")]
    [InlineData("connection_string: Server=db;Pwd=hunter2;AccountKey=acckey9z", "acckey9z")]
    [InlineData("--access-token tok99abc and --client-secret sec88xyz", "tok99abc")]
    [InlineData("--client-secret sec88xyz", "sec88xyz")]
    [InlineData("Pwd=hunter2", "hunter2")]
    [InlineData("{\"p\":\"C:\\\\Users\\\\Dan Swett\\\\x\"}", "Swett")]
    [InlineData("{\"p\":\"\\\\\\\\FILESRV01\\\\share\"}", "FILESRV01")]
    [InlineData("-----BEGIN PGP PRIVATE KEY BLOCK-----\nlQdGBF\n-----END PGP PRIVATE KEY BLOCK-----", "lQdGBF")]
    [InlineData(@"open \\FILESRV01\share\x", "FILESRV01")]
    [InlineData("/mnt/c/Users/alice/y", "alice")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNz")]
    [InlineData("password: \"horse battery staple\"", "battery")]
    [InlineData("run --token abcd1234efgh now", "abcd1234efgh")]
    [InlineData("run --password x now", " x ")]
    [InlineData("API_KEY=zzz111 AUTHORIZATION=qqq222", "zzz111")]
    [InlineData("see /home/dan/.ssh", "dan")]
    [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----", "abc")]
    [InlineData("password=hunter2hunter2", "hunter2")]
    [InlineData("{\\\"password\\\":\\\"hunter2\\\"}", "hunter2")]
    [InlineData("https://user:pw1234@example.com/x", "pw1234")]
    [InlineData("key AAAAB3NzaC1yc2EAAAADAQABAAABAQC7vbqajDhA9bP8f0dQ1c2e3f4g5h6i7j8k9l0m", "AAAAB3NzaC1")]
    public void Redaction_removes_these_shapes(string input, string gone)
    {
        Assert.DoesNotContain(gone, FeedbackRedactor.Redact(input));
    }

    [Fact]
    public void A_secret_in_a_query_string_does_not_swallow_the_next_parameter()
    {
        var r = FeedbackRedactor.Redact("https://h/x?token=abc123def&page=2&password=zzz999");
        Assert.DoesNotContain("abc123def", r);
        Assert.DoesNotContain("zzz999", r);
        Assert.Contains("page=2", r);
    }

    [Fact]
    public void Redaction_is_case_insensitive_under_the_turkish_culture()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
            var r = FeedbackRedactor.Redact("API_KEY=zzz111 AUTHORIZATION: qqq222");
            Assert.DoesNotContain("zzz111", r);
            Assert.DoesNotContain("qqq222", r);
            Assert.DoesNotContain("SILVIA", FeedbackRedactor.Redact("SILVIA broke it", ["silvia"]));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void Hostile_long_input_finishes_quickly()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var s in new[] { string.Concat(Enumerable.Repeat("a-", 40_000)), string.Concat(Enumerable.Repeat("connection_string", 5000)), new string('a', 80_000) + "@", string.Concat(Enumerable.Repeat("a://", 20_000)) })
        {
            FeedbackRedactor.Redact(s);
            FeedbackRedactor.ContainsSecret(s);
        }
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(8), sw.Elapsed.ToString());
    }

    public static TheoryData<string> OneSamplePerRule => new()
    {
        "ghp_" + "abcdefghijklmnopqrstuvwxyz0123456789",
        "eyJhbGciOiJIUzI1" + "NiJ9.eyJzdWIiOiIxMjM0NTY3ODkw.SflKxwRJSMeKKF2QT4fwpM",
        "sent Bearer opaque.session-value1 to server",
        "Authorization: Basic dXNlcjpwYXNzd29yZA==",
        "password: \"horse battery staple\"",
        "run --token abcd1234efgh now",
        "connection_string: Server=db;Password=secret",
        "connection_string: Server=db;Pwd=hunter2;AccountKey=acckey9z",
        "https://h/x?token=abc123def&page=2&password=zzz999",
        "https://user:pw1234@example.com/x",
        "-----BEGIN PGP PRIVATE KEY BLOCK-----\nlQdGBF\n-----END PGP PRIVATE KEY BLOCK-----",
        @"open \\FILESRV01\share\x",
        @"opened C:\Users\Dan Swett\x",
        @"{""p"":""C:\\Users\\Dan Swett\\x""}",
        "see /home/dan/.ssh and file:///home/dan/x",
        "/mnt/c/Users/alice/y",
        "mail bob@example.com",
        "API_KEY=zzz111 AUTHORIZATION=qqq222",
        "{\\\"password\\\":\\\"hunter2\\\"}",
    };

    [Theory]
    [MemberData(nameof(OneSamplePerRule))]
    public void Redaction_is_idempotent_and_leaves_nothing_for_the_sender_to_refuse(string sample)
    {
        var once = FeedbackRedactor.Redact(sample);
        Assert.Equal(once, FeedbackRedactor.Redact(once));
        Assert.False(FeedbackRedactor.ContainsSecret(once), once);
        Assert.False(FeedbackRedactor.BundleHasSecret(System.Text.Json.JsonSerializer.Serialize(new { text = once })), once);
    }

    [Fact]
    public void BundleHasSecret_only_flags_exact_credential_names_and_unredacted_values()
    {
        Assert.False(FeedbackRedactor.BundleHasSecret("{\"tokenCount\":5,\"max_tokens\":100,\"secretsEnabled\":\"yes\",\"password\":\"see [redacted]\",\"apiKey\":\"[redacted]\"}"));
        Assert.True(FeedbackRedactor.BundleHasSecret("{\"apiKey\":\"abc\"}"));
        Assert.True(FeedbackRedactor.BundleHasSecret("{\"password\":\"'hunter2\"}"));
        Assert.True(FeedbackRedactor.BundleHasSecret("{\"password\":\",x\"}"));
        Assert.True(FeedbackRedactor.BundleHasSecret("{\"token\":[\"abc\"]}"));
    }

    [Fact]
    public void A_clean_bundle_with_credential_named_keys_and_partly_redacted_values_round_trips()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["password"] = FeedbackRedactor.Mark,
            ["tokenCount"] = 3,
            ["note"] = FeedbackRedactor.Redact("password=hunter2 and tokenCount=3 see Authorization: Basic dXNlcjpwYXNz"),
        });
        Assert.False(FeedbackRedactor.BundleHasSecret(json), json);
    }

    [Fact]
    public void Redaction_leaves_ordinary_text_alone()
    {
        const string text = "connection.reconnecting after 3 attempts; host 0.1.42";
        Assert.Equal(text, FeedbackRedactor.Redact(text));
    }
}

static class PipeExt
{
    public static TOut Pipe<TIn, TOut>(this TIn x, Func<TIn, TOut> f) => f(x);
}
