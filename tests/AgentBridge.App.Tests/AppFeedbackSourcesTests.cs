using System.Security.Cryptography;
using AgentBridge.App.Core;
using AgentBridge.App.Core.Feedback;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Xunit;

namespace AgentBridge.App.Tests;

public sealed class AppFeedbackSourcesTests : IDisposable
{
    readonly string dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "s3c-" + Guid.NewGuid().ToString("N"))).FullName;

    public void Dispose() => Directory.Delete(dir, true);

    string Settings => Path.Combine(dir, "settings.json");

    AppFeedbackSources Sources(Func<string, int, CancellationToken, Task<HistoryResult?>>? history = null) => new(
        () => AppFeedbackSources.Describe("1.2.3", "prerelease", []),
        dir, Settings, _ => Task.FromResult<AgentBridge.Local.AgentReadinessReport?>(null),
        history ?? ((_, _, _) => Task.FromResult<HistoryResult?>(null)));

    [Fact]
    public async Task The_journal_tail_is_the_newest_lines_with_the_rotated_file_first()
    {
        File.WriteAllText(Path.Combine(dir, "app.log.1"), "a\nb\n");
        File.WriteAllText(Path.Combine(dir, "app.log"), "c\nd\n");

        Assert.Equal(["b", "c", "d"], await Sources().JournalTailAsync(3, default));
    }

    [Fact]
    public async Task A_missing_journal_is_empty()
    {
        Assert.Empty(await Sources().JournalTailAsync(200, default));
        Assert.Empty(await Sources().CrashRecordsAsync(5, default));
    }

    [Fact]
    public async Task A_journal_the_app_still_has_open_can_be_read()
    {
        using var writer = new FileStream(Path.Combine(dir, "app.log"), FileMode.Create, FileAccess.Write, FileShare.Read);
        writer.Write("one\ntwo\n"u8);
        writer.Flush();

        Assert.Equal(["one", "two"], await Sources().JournalTailAsync(10, default));
    }

    [Fact]
    public async Task A_huge_journal_gives_only_whole_lines_from_its_end()
    {
        File.WriteAllText(Path.Combine(dir, "app.log"), string.Concat(Enumerable.Range(0, 100_000).Select(i => $"line {i}\n")));

        var tail = await Sources().JournalTailAsync(3, default);

        Assert.Equal(["line 99997", "line 99998", "line 99999"], tail);
    }

    [Fact]
    public async Task Crash_records_keep_their_stack_lines_and_come_newest_first()
    {
        File.WriteAllText(Path.Combine(dir, "crash.log"),
            "2026-11-01T10:00:00.0000000-07:00 System.Exception: first\n   at A()\n" +
            "2026-11-02T10:00:00.0000000-07:00 System.Exception: second\n   at B()\n   at C()\n" +
            "2026-11-03T10:00:00.0000000-07:00 System.Exception: third\n");

        var records = await Sources().CrashRecordsAsync(2, default);

        Assert.Equal(2, records.Count);
        Assert.Contains("third", records[0]);
        Assert.Contains("second", records[1]);
        Assert.Contains("at C()", records[1]);
    }

    [Fact]
    public async Task A_crash_log_cut_mid_record_drops_the_partial_head_and_keeps_whole_records()
    {
        var old = "2026-10-01T10:00:00.0000000-07:00 System.Exception: old\n" + string.Concat(Enumerable.Repeat("   at Old.Frame()\n", 20_000));
        File.WriteAllText(Path.Combine(dir, "crash.log"), old + "2026-11-03T10:00:00.0000000-07:00 System.Exception: new\n   at New()\n");

        var records = await Sources().CrashRecordsAsync(5, default);

        var only = Assert.Single(records);
        Assert.Contains("new", only);
    }

    [Fact]
    public async Task Config_has_the_settings_scalars_and_the_update_channel()
    {
        File.WriteAllText(Settings, "{\"toasts\":true,\"relay\":\"https://r\",\"nested\":{\"x\":1},\"prereleases\":false}");

        var config = await Sources().ConfigAsync(default);

        Assert.Equal("True", config["toasts"]);
        Assert.Equal("stable", config["updateChannel"]);
        Assert.False(config.ContainsKey("nested"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("{not json")]
    [InlineData("[1,2]")]
    public async Task Config_from_a_missing_or_malformed_file_is_empty(string? content)
    {
        if (content is not null) { File.WriteAllText(Settings, content); }

        Assert.Empty(await Sources().ConfigAsync(default));
    }

    [Fact]
    public async Task An_unknown_session_gives_null_and_an_unreadable_one_throws()
    {
        Assert.Null(await Sources(History(HistoryResult.Gone)).SessionTurnsAsync("s", 20, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sources(History(HistoryResult.NotAvailable("no"))).SessionTurnsAsync("s", 20, default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Sources().SessionTurnsAsync("s", 20, default));
    }

    [Fact]
    public async Task Session_turns_are_prompts_and_responses_and_only_the_last_ones_are_kept()
    {
        var at = DateTimeOffset.UnixEpoch;
        var turns = Enumerable.Range(0, 3).Select(i => new HistoryTurn
        {
            Id = $"t{i}", At = at,
            Prompt = new HistoryPrompt { Text = $"q{i}", From = UserMessageFrom.Person },
            Parts = [
                new HistoryPart { Id = $"r{i}", Kind = HistoryPartKind.Response, At = at, Text = $"a{i}" },
                new HistoryPart { Id = $"x{i}", Kind = HistoryPartKind.Reasoning, At = at, Text = "thinking" }],
        }).ToList();

        var result = await Sources(History(new HistoryResult { Turns = turns })).SessionTurnsAsync("s", 3, default);

        Assert.Equal([("assistant", "a1"), ("user", "q2"), ("assistant", "a2")], result!.Select(t => (t.Role, t.Text)));
    }

    static Func<string, int, CancellationToken, Task<HistoryResult?>> History(HistoryResult r) => (_, _, _) => Task.FromResult<HistoryResult?>(r);
}

public sealed class FeedbackSenderFactoryTests
{
    static AppPreferences Ready() => new()
    {
        FeedbackKeyId = "beta-v1",
        FeedbackPublicKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
    };

    static FeedbackSenderFactory Factory(AppPreferences prefs, RelayClient? relay) => new(() => prefs, () => relay, new HttpClient());

    [Fact]
    public void Without_a_key_feedback_is_unavailable_and_nothing_is_built()
    {
        var f = Factory(new AppPreferences(), null);

        Assert.Equal(FeedbackAvailability.NoRecipientKey, f.Availability);
        Assert.Null(f.TryCreate());
    }

    [Fact]
    public void Turning_upload_off_wins_over_everything_else()
    {
        Assert.Equal(FeedbackAvailability.UploadOff, Factory(Ready() with { FeedbackUpload = false }, null).Availability);
    }

    [Fact]
    public void A_bad_key_is_the_same_as_no_key()
    {
        Assert.Equal(FeedbackAvailability.NoRecipientKey, Factory(Ready() with { FeedbackPublicKey = "short" }, null).Availability);
    }

    [Fact]
    public void The_staging_override_needs_both_the_id_and_the_key()
    {
        Assert.Equal(FeedbackAvailability.NoRecipientKey, Factory(Ready() with { FeedbackKeyId = null }, null).Availability);
    }

    [Fact]
    public void A_key_without_a_relay_connection_is_unavailable()
    {
        Assert.Equal(FeedbackAvailability.NoRelay, Factory(Ready(), null).Availability);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_relay_that_takes_no_feedback_is_unavailable_and_one_that_does_gives_a_sender()
    {
        await using var off = await TestRelay.StartAsync();
        var offClient = await off.ConnectAsync(off.TokenFor(1, "octo"), "phone", "pwa");
        await using var on = await TestRelay.StartAsync(o => o.Feedback.Backend = "local");
        var onClient = await on.ConnectAsync(on.TokenFor(1, "octo"), "phone", "pwa");

        Assert.Equal(FeedbackAvailability.RelayNotAccepting, Factory(Ready(), offClient.Relay).Availability);
        var f = Factory(Ready(), onClient.Relay);
        Assert.Equal(FeedbackAvailability.Available, f.Availability);
        Assert.NotNull(f.TryCreate());
    }
}
