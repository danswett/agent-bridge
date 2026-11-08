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
        public IReadOnlyList<string>? Journal;
        public IReadOnlyList<string>? Crashes;
        public IReadOnlyList<FeedbackSessionTurn>? Turns;
        public FeedbackEnvironment? Env;
        public IReadOnlyList<FeedbackAgent>? Agents;
        public IReadOnlyDictionary<string, string>? Config;
        public FeedbackEnvironment Environment() => Env ?? new("0.1.42", "beta", "Windows 11", "x64", 1, ["0.1.42", "0.1.41"]);
        public Task<IReadOnlyList<FeedbackAgent>> DetectAgentsAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<FeedbackAgent>>(Agents ?? [new("Copilot", "1.0.107"), new("Claude", null)]);
        public Task<IReadOnlyDictionary<string, string>> ConfigAsync(CancellationToken ct) => Task.FromResult<IReadOnlyDictionary<string, string>>(Config ?? new Dictionary<string, string>
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
            if (Journal is not null) { return Journal; }
            return ["connected", $"Authorization: Bearer {Secret}", @"opened C:\Users\alice\repos\x", "mail bob@example.com"];
        }
        public Task<IReadOnlyList<string>> CrashRecordsAsync(int max, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>(Crashes ?? (Junk ? Enumerable.Repeat(new string((char)1, 8000), 5).ToList() : [$"System.Exception: boom token={Secret} at C:\\Users\\alice\\a.cs"]));
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
    public async Task Sensitive_journal_and_crash_classes_are_omitted_whole()
    {
        var source = new Fake
        {
            Journal = ["connected", "token expired\nstill connected"],
            Crashes = ["failure\npassword=TAIL\nstack frame", "before\n-----BEGIN PRIVATE KEY-----\nq7R2\n-----END PRIVATE KEY-----\nafter", "failure\nsafe stack frame"],
        };
        var bundle = Ready(await Gather(source));
        using var doc = JsonDocument.Parse(bundle.Text);
        Assert.Equal(FeedbackBundleSource.JournalOmitted, doc.RootElement.GetProperty("journal").GetString());
        Assert.Equal(FeedbackBundleSource.CrashOmitted, doc.RootElement.GetProperty("crashes").GetString());
        Assert.DoesNotContain("safe stack frame", bundle.Text);
        Assert.Contains("left out", bundle.Items.Single(i => i.Id == "journal").Summary);
        Assert.Contains("left out", bundle.Items.Single(i => i.Id == "crashes").Summary);
        Assert.DoesNotContain("TAIL", bundle.Text);
        Assert.DoesNotContain("q7R2", bundle.Text);
        Assert.False(FeedbackRedactor.BundleHasSecret(bundle.Text));
    }

    [Fact]
    public async Task A_sensitive_name_before_the_journal_tail_omits_the_whole_journal()
    {
        var source = new Fake { Journal = ["password=", "MapleBoat7!", .. Enumerable.Repeat("connected", 199)] };
        var bundle = Ready(await Gather(source));
        using var doc = JsonDocument.Parse(bundle.Text);
        Assert.Equal(FeedbackBundleSource.JournalOmitted, doc.RootElement.GetProperty("journal").GetString());
        Assert.DoesNotContain("MapleBoat7!", bundle.Text);
    }

    [Fact]
    public async Task A_sensitive_name_in_dropped_crash_context_omits_the_retained_records()
    {
        var source = new Fake { Journal = [], Crashes = ["MapleBoat7!", "safe", "safe", "safe", "safe", "password="] };
        var bundle = Ready(await Gather(source));
        using var doc = JsonDocument.Parse(bundle.Text);
        Assert.Equal(FeedbackBundleSource.CrashOmitted, doc.RootElement.GetProperty("crashes").GetString());
        Assert.DoesNotContain("MapleBoat7!", bundle.Text);
    }

    [Fact]
    public async Task A_credential_continuation_in_a_separate_crash_record_is_not_sent()
    {
        var source = new Fake { Journal = [], Crashes = ["failure\npassword=", "MapleBoat7!", "safe"] };
        var bundle = Ready(await Gather(source));
        using var doc = JsonDocument.Parse(bundle.Text);
        Assert.Equal(FeedbackBundleSource.CrashOmitted, doc.RootElement.GetProperty("crashes").GetString());
        Assert.Equal(FeedbackBundleSource.CrashOmitted, bundle.Items.Single(i => i.Id == "crashes").Summary);
        Assert.DoesNotContain("MapleBoat7!", bundle.Text);
    }

    [Fact]
    public async Task Sensitive_names_split_across_adjacent_items_omit_each_whole_class()
    {
        var source = new Fake
        {
            Journal = ["connected", "pass", "word=journal-value"],
            Crashes = ["failure\npass", "word=crash-value"],
            Turns = [new("user", "pass", DateTimeOffset.UnixEpoch),
                new("assistant", "word=turn-value", DateTimeOffset.UnixEpoch)],
        };
        var bundle = Ready(await Gather(source, new FeedbackRequest { SessionTurnsFor = "s" }));
        using var doc = JsonDocument.Parse(bundle.Text);
        Assert.Equal(FeedbackBundleSource.JournalOmitted, doc.RootElement.GetProperty("journal").GetString());
        Assert.Equal(FeedbackBundleSource.CrashOmitted, doc.RootElement.GetProperty("crashes").GetString());
        Assert.Equal(FeedbackBundleSource.TurnsOmitted, doc.RootElement.GetProperty("sessionTurns").GetString());
        Assert.DoesNotContain("journal-value", bundle.Text);
        Assert.DoesNotContain("crash-value", bundle.Text);
        Assert.DoesNotContain("turn-value", bundle.Text);
    }

    [Fact]
    public async Task Each_class_is_omitted_if_any_item_exceeds_its_bound()
    {
        var source = new Fake
        {
            Journal = ["connected", new string('a', 401)],
            Crashes = ["safe", new string('b', 8001)],
            Turns = [new("user", new string('c', FeedbackLimits.MaxTurnChars + 1), DateTimeOffset.UnixEpoch)],
        };
        var bundle = Ready(await Gather(source, new FeedbackRequest { SessionTurnsFor = "s" }));
        using var doc = JsonDocument.Parse(bundle.Text);
        Assert.Equal(FeedbackBundleSource.JournalOmitted, doc.RootElement.GetProperty("journal").GetString());
        Assert.Equal(FeedbackBundleSource.CrashOmitted, doc.RootElement.GetProperty("crashes").GetString());
        Assert.Equal(FeedbackBundleSource.TurnsOmitted, doc.RootElement.GetProperty("sessionTurns").GetString());
        Assert.All(bundle.Items.Where(i => i.Id is "journal" or "crashes" or "sessionTurns"), i => Assert.Contains("left out", i.Summary));
    }

    [Fact]
    public async Task Sensitive_notes_and_turns_are_left_out_with_plain_rewording_guidance()
    {
        var turns = new[] { new FeedbackSessionTurn("user", "password=", DateTimeOffset.UnixEpoch),
            new FeedbackSessionTurn("assistant", "MapleBoat7!", DateTimeOffset.UnixEpoch) }
            .Concat(Enumerable.Repeat(new FeedbackSessionTurn("user", "connected", DateTimeOffset.UnixEpoch), 19)).ToArray();
        var bundle = Ready(await Gather(new Fake { Turns = turns }, new FeedbackRequest { Note = "passwordValue -> MapleBoat7!", SessionTurnsFor = "s" }));
        using var doc = JsonDocument.Parse(bundle.Text);
        Assert.Equal(FeedbackBundleSource.NoteOmitted, doc.RootElement.GetProperty("note").GetString());
        Assert.Equal(FeedbackBundleSource.TurnsOmitted, doc.RootElement.GetProperty("sessionTurns").GetString());
        Assert.Equal(FeedbackBundleSource.NoteOmitted, bundle.Items.Single(i => i.Id == "note").Summary);
        Assert.Equal(FeedbackBundleSource.TurnsOmitted, bundle.Items.Single(i => i.Id == "sessionTurns").Summary);
        Assert.DoesNotContain("MapleBoat7!", bundle.Text);
    }

    [Fact]
    public async Task Every_metadata_value_uses_the_same_suffix_aware_detector_before_truncation()
    {
        var unsafeText = "passwordValue -> MapleBoat7!";
        var source = new Fake
        {
            Env = new(unsafeText, "beta", unsafeText, "x64", 1, [unsafeText]),
            Agents = [new(unsafeText, unsafeText)],
            Config = new Dictionary<string, string> { ["theme"] = unsafeText },
            Journal = ["connected"],
            Crashes = [],
        };
        var bundle = Ready(await Gather(source));
        using var doc = JsonDocument.Parse(bundle.Text);
        Assert.Equal(FeedbackBundleSource.TextOmitted, doc.RootElement.GetProperty("versions").GetProperty("app").GetString());
        Assert.Equal(FeedbackBundleSource.TextOmitted, doc.RootElement.GetProperty("agents")[0].GetProperty("version").GetString());
        Assert.Equal(FeedbackBundleSource.TextOmitted, doc.RootElement.GetProperty("config").GetProperty("theme").GetString());
        Assert.All(bundle.Items.Where(i => i.Id is "versions" or "agents" or "config"), item => Assert.Contains("left out", item.Summary));
        Assert.DoesNotContain("MapleBoat7!", bundle.Text);
    }

    [Theory]
    [InlineData("password=")]
    [InlineData("password:   ")]
    [InlineData("password: |")]
    [InlineData("\"password\":")]
    [InlineData("passwordValue ->")]
    [InlineData("passwordValue=")]
    [InlineData("Authorization:")]
    [InlineData("connection string:")]
    [InlineData("ｐａｓｓｗｏｒｄ：")]
    [InlineData("pass\u200bword=")]
    [InlineData("\\u0070assword\\u003d")]
    public async Task Sensitive_names_omit_the_entire_journal_and_crash_class(string key)
    {
        var source = new Fake
        {
            Journal = ["connected", key, "", "  ", "journal-value", "still connected"],
            Crashes = ["failure\r\n" + key + "\r\n\r\n  crash-value\r\nstack frame"],
        };
        var bundle = Ready(await Gather(source));
        using var doc = JsonDocument.Parse(bundle.Text);
        Assert.Equal(FeedbackBundleSource.JournalOmitted, doc.RootElement.GetProperty("journal").GetString());
        Assert.Equal(FeedbackBundleSource.CrashOmitted, doc.RootElement.GetProperty("crashes").GetString());
        Assert.DoesNotContain("journal-value", bundle.Text);
        Assert.DoesNotContain("crash-value", bundle.Text);
        Assert.False(FeedbackRedactor.BundleHasSecret(bundle.Text));
    }

    [Fact]
    public async Task Items_without_sensitive_names_keep_line_redaction()
    {
        var source = new Fake { Journal = ["connected", "mail person@example.com"], Crashes = ["failure\nmail person@example.com\nstack frame"] };
        var bundle = Ready(await Gather(source));
        using var doc = JsonDocument.Parse(bundle.Text);
        var journal = doc.RootElement.GetProperty("journal");
        Assert.Equal("connected", journal[0].GetString());
        Assert.Equal("mail [redacted]", journal[1].GetString());
        Assert.Equal("failure\nmail [redacted]\nstack frame", doc.RootElement.GetProperty("crashes")[0].GetString());
    }

    [Fact]
    public async Task Refusals_cover_a_long_note_a_bad_or_huge_screenshot_an_unknown_session_and_an_oversize_bundle()
    {
        Assert.Equal(FeedbackRefusalReason.NoteTooLong, Assert.IsType<FeedbackGatherResult.Refused>(await Gather(new Fake(), new FeedbackRequest { Note = new string('x', 1001) })).Reason);
        Assert.Equal(FeedbackRefusalReason.ScreenshotNotPng, Assert.IsType<FeedbackGatherResult.Refused>(await Gather(new Fake(), new FeedbackRequest { ScreenshotPng = [1, 2, 3, 4, 5, 6, 7, 8, 9] })).Reason);
        Assert.Equal(FeedbackRefusalReason.ScreenshotTooLarge, Assert.IsType<FeedbackGatherResult.Refused>(await Gather(new Fake(), new FeedbackRequest { ScreenshotPng = new byte[FeedbackLimits.MaxScreenshotBytes + 1] })).Reason);
        Assert.Equal(FeedbackRefusalReason.SessionNotFound, Assert.IsType<FeedbackGatherResult.Refused>(await Gather(new Fake { Turns = null }, new FeedbackRequest { SessionTurnsFor = "nope" })).Reason);
        var unavailable = Ready(await Gather(new Fake { FailTurns = true }, new FeedbackRequest { SessionTurnsFor = "s" }));
        using var doc = JsonDocument.Parse(unavailable.Text);
        Assert.Equal(FeedbackBundleSource.TurnsOmitted, doc.RootElement.GetProperty("sessionTurns").GetString());
        Assert.Equal(FeedbackBundleSource.TurnsOmitted, unavailable.Items.Single(i => i.Id == "sessionTurns").Summary);
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
    public async Task Oversize_turn_is_omitted_and_a_lone_surrogate_in_the_note_is_sanitized()
    {
        // "ab " filler matches no redaction rule, so the 2000-char cut really lands inside the emoji.
        var filler = string.Concat(Enumerable.Repeat("ab ", 666)) + "a";
        Assert.Equal(1999, filler.Length);
        var f = new Fake { Turns = [new("user", filler + "😀 tail", DateTimeOffset.UnixEpoch)] };
        var bundle = Ready(await Gather(f, new FeedbackRequest { Note = "bad \ud83d half", SessionTurnsFor = "s" }));
        using var doc = JsonDocument.Parse(bundle.Text);
        Assert.Contains("\uFFFD", doc.RootElement.GetProperty("note").GetString());
        Assert.Equal(FeedbackBundleSource.TurnsOmitted, doc.RootElement.GetProperty("sessionTurns").GetString());
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
    [InlineData("{\"password\":\"[redacted]\"}", true)]
    [InlineData("{\"password\":\"\"}", true)]
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
    public void A_secret_in_a_query_string_is_redacted_fail_closed()
    {
        var r = FeedbackRedactor.Redact("https://h/x?token=abc123def&page=2&password=zzz999");
        Assert.DoesNotContain("abc123def", r);
        Assert.DoesNotContain("zzz999", r);
        Assert.Equal(FeedbackRedactor.EscapedMark, r);
    }

    [Fact]
    public void Redaction_is_case_insensitive_under_the_turkish_culture()
    {
        var before = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("tr-TR");
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
        // Generous on purpose: CI coverage is slow, and a quadratic redactor on this input would take far longer.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), sw.Elapsed.ToString());
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
        "key AAAAB3NzaC1yc2EAAAADAQABAAABAQC7vbqajDhA9bP8f0dQ1c2e3f4g5h6i7j8k9l0m",
        "{\"msg\":\"{\\u0022password\\u0022:\\u0022hunter2\\u0022}\"}",
        "{\"msg\":\"--password\\u0020hunter2\"}",
        "{\"msg\":\"password\\u003Dhunter2\"}",
        "{\"msg\":\"--token\\u0020hunter2zz\"}",
        "\\u005cu0070assword=hunter2",
        "\\u005cu005cu0070assword=hunter2",
        "\\u005c\\u0075\\u0030\\u0030\\u0037\\u0030assword=hunter2",
        "password=\\u0022hun\\u0022ter2",
        "caf\\u00e9 and plain text",
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

    [Theory]
    [InlineData("pass\u200Bword=TAIL")]
    [InlineData("p\u0301assword=TAIL")]
    [InlineData("ｐａｓｓｗｏｒｄ=TAIL")]
    [InlineData("pass\\u200Bword=TAIL")]
    [InlineData("ｐａｓｓｗｏｒｄ reset failed")]
    public void Normalized_sensitive_key_mentions_fail_closed_in_every_view(string input)
    {
        var redacted = FeedbackRedactor.Redact(input);
        Assert.Equal(FeedbackRedactor.EscapedMark, redacted);
        Assert.DoesNotContain("TAIL", redacted);
        Assert.True(FeedbackRedactor.ContainsSecret(input));
        Assert.True(FeedbackRedactor.BundleHasSecret(JsonSerializer.Serialize(new { note = input })));
        Assert.False(FeedbackRedactor.ContainsSecret(redacted));
        Assert.Equal(redacted, FeedbackRedactor.Redact(redacted));
    }

    [Fact]
    public void BundleHasSecret_inspects_credential_names_and_values()
    {
        Assert.False(FeedbackRedactor.BundleHasSecret("{\"tokenCount\":5,\"max_tokens\":100,\"secretsEnabled\":\"yes\"}"));
        Assert.True(FeedbackRedactor.BundleHasSecret("{\"password\":\"[redacted]\",\"apiKey\":\"[redacted]\"}"));
        Assert.True(FeedbackRedactor.BundleHasSecret("{\"password\":\"see [redacted]\"}"));
        Assert.True(FeedbackRedactor.BundleHasSecret("{\"apiKey\":\"abc\"}"));
        Assert.True(FeedbackRedactor.BundleHasSecret("{\"password\":\"'hunter2\"}"));
        Assert.True(FeedbackRedactor.BundleHasSecret("{\"password\":\",x\"}"));
        Assert.True(FeedbackRedactor.BundleHasSecret("{\"token\":[\"abc\"]}"));
        Assert.True(FeedbackRedactor.BundleHasSecret("{\"token\":[\"" + FeedbackRedactor.EscapedMark + "\"]}"));
        Assert.True(FeedbackRedactor.ContainsSecret("token hunter2"));
        Assert.True(FeedbackRedactor.ContainsSecret("token\\u0020hunter2"));
    }

    [Fact]
    public void A_clean_bundle_with_redacted_values_round_trips()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["detail"] = FeedbackRedactor.Mark,
            ["tokenCount"] = 3,
            ["note"] = FeedbackRedactor.Redact("password=hunter2 and tokenCount=3 see Authorization: Basic dXNlcjpwYXNz"),
        });
        Assert.False(FeedbackRedactor.BundleHasSecret(json), json);
    }

    [Theory]
    [InlineData("{\"token\":true}")]
    [InlineData("{\"token\":false}")]
    [InlineData("{\"token\":{\"v\":\"abc\"}}")]
    [InlineData("{\"token\":[{\"a\":\"abc\"}]}")]
    [InlineData("{\"a\":{\"password\":{\"b\":{\"c\":1}}}}")]
    public void BundleHasSecret_refuses_any_value_nested_under_a_credential_key(string json)
        => Assert.True(FeedbackRedactor.BundleHasSecret(json));

    [Fact]
    public void A_unicode_escaped_key_inside_a_string_is_still_a_credential()
    {
        const string line = "event {\"pass\\u0077ord\":\"hunter2\"}";
        Assert.DoesNotContain("hunter2", FeedbackRedactor.Redact(line));
        Assert.True(FeedbackRedactor.ContainsSecret(line));
        Assert.True(FeedbackRedactor.BundleHasSecret(System.Text.Json.JsonSerializer.Serialize(new { text = line })));
        Assert.False(FeedbackRedactor.ContainsSecret("caf\\u00e9 is fine"));
    }

    [Theory]
    [InlineData("{\"password\":\"first\\\"second\"}", "second")]
    [InlineData("{\"password\":\"hun\\u0022ter2\"}", "ter2")]
    [InlineData("{\"password\":\"abc\\u0022tail-secret\"}", "tail-secret")]
    [InlineData("'password':'x\\u0027y-secret'", "y-secret")]
    [InlineData("{\"password\":\"hun\\u000ater2 more\"}", "more")]
    [InlineData("{\"msg\":\"{\\u0022password\\u0022:\\u0022hunter2\\u0022}\"}", "hunter2")]
    [InlineData("{\"msg\":\"password\\u003Dhunter2\"}", "hunter2")]
    [InlineData("{\"msg\":\"password\\u003Ahunter2\"}", "hunter2")]
    [InlineData("{\"msg\":\"--password\\u0020hunter2\"}", "hunter2")]
    [InlineData("{\"msg\":\"--token\\u0020hunter2zz\"}", "hunter2zz")]
    [InlineData("\\u005cu0070assword=hunter2", "hunter2")]
    [InlineData("\\u005cu005cu0070assword=hunter2", "hunter2")]
    [InlineData("\\u005c\\u0075\\u0030\\u0030\\u0037\\u0030assword=hunter2", "hunter2")]
    [InlineData("{\"msg\":\"password\\u003D\\u0022hunter2 more\\u0022\"}", "hunter2")]
    public void An_escaped_quote_or_control_char_inside_a_value_does_not_end_it(string input, string gone)
    {
        var r = FeedbackRedactor.Redact(input);
        Assert.DoesNotContain(gone, r);
        Assert.DoesNotContain('\n', r);
        Assert.False(FeedbackRedactor.ContainsSecret(r));
        Assert.Equal(r, FeedbackRedactor.Redact(r));
        Assert.True(FeedbackRedactor.ContainsSecret(input));
    }

    public static IEnumerable<object[]> AmbiguousQuotedValues()
    {
        string[] keys = ["password", "secret", "token", "api_key"];
        string[] values =
        [
            "'first\\'TAIL'",
            "'first\\x22TAIL'",
            "'first\\u0027TAIL'",
            "'first'TAIL'",
            "'first' TAIL'",
            "\"first'TAIL",
            "'first\\qTAIL'",
            "'first TAIL",
        ];
        foreach (var key in keys)
        foreach (var value in values)
        foreach (var (before, after) in new[] { ("", " suffix"), ("prefix ", " suffix"), ("prefix ", "") })
        {
            yield return [before + key + "=" + value + after];
        }
    }

    [Theory]
    [MemberData(nameof(AmbiguousQuotedValues))]
    public void Ambiguous_quoted_credentials_never_leave_the_tail(string input)
    {
        var result = FeedbackRedactor.Redact(input);
        Assert.DoesNotContain("TAIL", result);
        Assert.True(FeedbackRedactor.ContainsSecret(input));
        Assert.True(FeedbackRedactor.BundleHasSecret(System.Text.Json.JsonSerializer.Serialize(new { text = input })));
        Assert.False(FeedbackRedactor.ContainsSecret(result));
        Assert.Equal(result, FeedbackRedactor.Redact(result));
    }

    [Theory]
    [InlineData("prefix password='ordinary' suffix")]
    [InlineData("prefix token=\"ordinary\" suffix")]
    public void Quoted_credentials_fail_closed_for_the_whole_text(string input)
        => Assert.Equal(FeedbackRedactor.EscapedMark, FeedbackRedactor.Redact(input));

    [Theory]
    [InlineData("pass\\x77ord=hunter2")]
    [InlineData("secret=\\x27hunter2\\x27")]
    [InlineData("secret=\\'hunter2\\'")]
    public void Hex_and_single_quote_escapes_are_checked_in_the_decoded_view(string input)
    {
        Assert.Equal(FeedbackRedactor.EscapedMark, FeedbackRedactor.Redact(input));
        Assert.True(FeedbackRedactor.ContainsSecret(input));
    }

    [Theory]
    [InlineData("{\"pass\\u0077ord\":\"\\nTAIL\"}")]
    [InlineData("password=first'TAIL")]
    public void Sensitive_key_variants_never_leave_the_credential_tail(string input)
    {
        var result = FeedbackRedactor.Redact(input);
        Assert.DoesNotContain("TAIL", result);
        Assert.True(FeedbackRedactor.ContainsSecret(input));
        Assert.True(FeedbackRedactor.WouldRedact(input));
        Assert.True(FeedbackRedactor.BundleHasSecret(JsonSerializer.Serialize(new { text = input })));
        Assert.False(FeedbackRedactor.ContainsSecret(result));
        Assert.Equal(result, FeedbackRedactor.Redact(result));
    }

    public static IEnumerable<object[]> CredentialTailMatrix()
    {
        string[] keys = ["password", "secret", "token", "authorization", "api_key", "apiKey", "pwd", "connectionString"];
        string[] values =
        [
            "TAIL", "first'TAIL", "first\"TAIL", "'TAIL'", "\"TAIL\"",
            "'first\\'TAIL'", "\"first\\\"TAIL\"", "'first\\x22TAIL'", "'first\\x27TAIL'",
            "'first\\u0027TAIL'", "\"first\\u0022TAIL\"", "'first'TAIL'", "'first' TAIL'",
            "\"first'TAIL", "'first\"TAIL", "'first\\qTAIL'", "\"first\\qTAIL\"",
            "'first TAIL", "\"first TAIL", "\\nTAIL", "\"\\nTAIL\"", "'\\rTAIL'",
            "'\\tTAIL'", "\\u0022TAIL", "\\x27TAIL", "[redacted]TAIL",
        ];
        (string Before, string After)[] positions =
        [
            ("", ""), ("", " suffix"), ("prefix ", ""), ("prefix ", " suffix"),
            ("prefix\n", "\nsuffix"), ("{", "}"),
        ];
        foreach (var key in keys)
        {
            string[] forms =
            [
                key, key.ToUpperInvariant(), "'" + key + "'", "\"" + key + "\"",
                $"\\u{(int)key[0]:x4}" + key[1..], $"\\x{(int)key[0]:x2}" + key[1..],
            ];
            foreach (var form in forms)
            foreach (var value in values)
            foreach (var (before, after) in positions)
            {
                yield return [before + form + "=" + value + after];
            }
        }
    }

    [Theory]
    [MemberData(nameof(CredentialTailMatrix))]
    public void Credential_key_matrix_redacts_and_refuses_every_tail(string input)
    {
        var result = FeedbackRedactor.Redact(input);
        Assert.DoesNotContain("TAIL", result);
        Assert.True(FeedbackRedactor.ContainsSecret(input));
        Assert.True(FeedbackRedactor.WouldRedact(input));
        Assert.True(FeedbackRedactor.BundleHasSecret(JsonSerializer.Serialize(new { text = input })));
        Assert.False(FeedbackRedactor.ContainsSecret(result));
        Assert.Equal(result, FeedbackRedactor.Redact(result));
    }

    [Fact]
    public void Escaped_input_stays_fast_in_both_views()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var big = string.Concat(Enumerable.Repeat("\\u0061-", 350_000));
        FeedbackRedactor.Redact(big);
        FeedbackRedactor.ContainsSecret(big);
        // Generous on purpose: CI coverage is slow, and a quadratic redactor on this input would take far longer.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(60), sw.Elapsed.ToString());
    }

    [Fact]
    public void Ordinary_escapes_are_not_rewritten()
        => Assert.Equal("caf\\u00e9 \\u0041", FeedbackRedactor.Redact("caf\\u00e9 \\u0041"));

    [Theory]
    [InlineData("password='hunter2", "hunter2")]
    [InlineData("password=\"hunter2", "hunter2")]
    public void An_unterminated_quote_still_redacts_the_value(string input, string gone)
        => Assert.DoesNotContain(gone, FeedbackRedactor.Redact(input));

    [Fact]
    public void A_regex_timeout_fails_closed()
    {
        FeedbackRedactor.SecretPassOverride = _ => throw new System.Text.RegularExpressions.RegexMatchTimeoutException();
        try
        {
            Assert.Equal(FeedbackRedactor.Mark, FeedbackRedactor.Redact("anything"));
            Assert.True(FeedbackRedactor.ContainsSecret("anything"));
            Assert.True(FeedbackRedactor.BundleHasSecret("{\"a\":\"anything\"}"));
        }
        finally { FeedbackRedactor.SecretPassOverride = null; }
    }

    public static TheoryData<string, string> SecretKindsToCut => new()
    {
        { "password=hunter2", "hunter2" },
        { "Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNz" },
        { "tok ghp_" + "abcdefghijklmnopqrstuvwxyz0123456789", "abcdefghij" },
        { "jwt eyJhbGciOiJIUzI1" + "NiJ9.eyJzdWIiOiIxMjM0NTY3ODkw.SflKxwRJSMeKKF2QT4fwpM", "SflKxwRJ" },
        { "mail bob@example.com", "bob@" },
        { @"at C:\Users\alice\x", "alice" },
        { "connection_string: Server=db;Pwd=hunter2;AccountKey=acckey9z", "acckey9z" },
        { "run --token abcd1234efgh now", "abcd1234efgh" },
        { "{\"msg\":\"{\\u0022password\\u0022:\\u0022hunter2\\u0022}\"}", "hunter2" },
        { "{\"msg\":\"password\\u003Dhunter2\"}", "hunter2" },
        { "{\"msg\":\"--token\\u0020hunter2zz\"}", "hunter2zz" },
        { "\\u005cu0070assword=hunter2", "hunter2" },
    };

    [Theory]
    [MemberData(nameof(SecretKindsToCut))]
    public void Truncation_never_leaves_text_the_sender_would_refuse(string secret, string leak)
    {
        var line = "start " + new string('x', 40) + " " + secret + " " + new string('y', 150) + " token=zzz999 end";
        var source = new FeedbackBundleSource(new Fake(), null, null, ["zzzzzz"]);
        for (var max = 1; max < line.Length; max++)
        {
            var cleaned = source.Clean(line, max);
            Assert.False(FeedbackRedactor.ContainsSecret(cleaned), $"{max}: {cleaned}");
            Assert.False(FeedbackRedactor.BundleHasSecret(System.Text.Json.JsonSerializer.Serialize(new { journal = new[] { cleaned } })), $"{max}: {cleaned}");
            Assert.DoesNotContain(leak, cleaned);
            Assert.DoesNotContain("zzz999", cleaned);
        }
    }

    [Fact]
    public void Two_MiB_runs_of_a_backslash_escape_and_a_letter_escape_stay_fast()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        foreach (var unit in new[] { "a", "\\u005c", "\\u0061" })
        {
            var big = string.Concat(Enumerable.Repeat(unit, 2 * 1024 * 1024 / unit.Length));
            FeedbackRedactor.Redact(big);
            FeedbackRedactor.ContainsSecret(big);
        }
        // Generous on purpose: CI coverage is slow, and a quadratic redactor on 2 MiB would take far longer than this.
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(90), sw.Elapsed.ToString());
    }

    [Fact]
    public void An_ordinary_line_is_truncated_as_before()
        => Assert.Equal("hello worl…", new FeedbackBundleSource(new Fake(), null, null, ["zzzzzz"]).Clean("hello world, plain text", 10));

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
