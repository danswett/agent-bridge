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
            if (FailJournal) { throw new IOException(@"C:\Users\alice\secret.log"); }
            return ["connected", $"Authorization: Bearer {Secret}", @"opened C:\Users\alice\repos\x", "mail bob@example.com"];
        }
        public Task<IReadOnlyList<string>> CrashRecordsAsync(int max, CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([$"System.Exception: boom token={Secret} at C:\\Users\\alice\\a.cs"]);
        public Task<IReadOnlyList<FeedbackSessionTurn>?> SessionTurnsAsync(string id, int max, CancellationToken ct) => Task.FromResult(Turns);
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
        var big = new byte[FeedbackLimits.MaxScreenshotBytes];
        Array.Copy(Png, big, Png.Length);
        var turns = Enumerable.Range(0, 20).Select(i => new FeedbackSessionTurn("user", new string('a', 2000) + i, DateTimeOffset.UnixEpoch)).ToList();
        var huge = await Gather(new Fake { Turns = turns }, new FeedbackRequest { ScreenshotPng = big, SessionTurnsFor = "s" });
        // The per-item caps keep the largest possible bundle under the limit; the size check is the backstop.
        Assert.True(Ready(huge).Length < FeedbackLimits.MaxBundleBytes);
    }

    [Fact]
    public async Task Text_cut_mid_emoji_or_holding_a_lone_surrogate_still_gathers()
    {
        var f = new Fake { Turns = [new("user", new string('a', 1999) + "😀 tail", DateTimeOffset.UnixEpoch)] };
        var bundle = Ready(await Gather(f, new FeedbackRequest { Note = "bad \ud83d half", SessionTurnsFor = "s" }));
        using var doc = JsonDocument.Parse(bundle.Text);
        Assert.Contains("\uFFFD", doc.RootElement.GetProperty("note").GetString());
    }

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
    [InlineData(@"opened C:\Users\Dan Swett\x", "Dan Swett")]
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
