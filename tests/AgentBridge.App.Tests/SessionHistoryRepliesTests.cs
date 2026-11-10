using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Xunit;

// #1628: every turn the person starts stays its own entry, in order, whether the live session said it
// first or a page did.
public sealed class SessionHistoryRepliesTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 9, 20, 0, 0, TimeSpan.Zero);

    static HistoryTurn Turn(int n) => new() { Id = $"t{n}", At = T0.AddSeconds(n * 2), Prompt = new HistoryPrompt { Text = $"ok {n}" } };

    static FakeHost Serving(Func<int> count)
    {
        var host = new FakeHost();
        host.Capabilities.Add(HistoryProtocol.Capability);
        host.On(HistoryProtocol.Op, (JsonElement d) =>
        {
            var end = count();
            var from = Math.Max(0, end - HistoryLimits.DefaultTurns);
            return new HistoryResult { Turns = [.. Enumerable.Range(from + 1, end - from).Select(Turn)], Start = new HistoryStart { At = T0 } };
        });
        return host;
    }

    static async Task<SessionHistory> OpenAsync(FakeHost host)
    {
        host.Start();
        var connection = host.Connect();
        await connection.WhenConnectedAsync().WaitAsync(FakeHost.Patience);
        return new SessionHistory(connection, "s1");
    }

    static int[] Numbers(SessionHistory h) => [.. h.Turns.Select(t => int.Parse(t.Id[1..]))];

    [Fact]
    [Trait("Category", "Network")]
    public async Task Five_turns_the_live_session_said_in_ten_seconds_stay_five_entries_after_the_page_that_has_them()
    {
        var count = 25;
        await using var host = Serving(() => count);
        var history = await OpenAsync(host);
        await history.LoadNewestAsync();

        for (var n = 26; n <= 30; n++) { Assert.True(history.AddLive(Turn(n))); }
        Assert.Equal(Enumerable.Range(6, 25), Numbers(history));
        count = 30;
        await history.LoadNewestAsync();

        Assert.Equal(Enumerable.Range(6, 25), Numbers(history));
        Assert.All(history.Turns, t => Assert.Equal($"ok {t.Id[1..]}", t.Prompt!.Text));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Live_turns_the_host_page_has_not_reached_stay_after_it_and_in_order()
    {
        var count = 25;
        await using var host = Serving(() => count);
        var history = await OpenAsync(host);
        for (var n = 28; n <= 30; n++) { history.AddLive(Turn(n)); }

        await history.LoadNewestAsync();

        Assert.Equal([.. Enumerable.Range(6, 20), 28, 29, 30], Numbers(history));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_turn_the_live_session_skipped_is_put_in_its_place_by_the_next_page()
    {
        var count = 25;
        await using var host = Serving(() => count);
        var history = await OpenAsync(host);
        await history.LoadNewestAsync();
        history.AddLive(Turn(28));

        count = 28;
        await history.LoadNewestAsync();

        Assert.Equal(Enumerable.Range(6, 23), Numbers(history));
    }
}
