using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Xunit;

// The history client (#292 W1) against a host that serves a transcript of numbered turns "t1".."tN"
// in pages, as session.history says (docs/protocol/v1-history.md).
public sealed class SessionHistoryTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 28, 4, 0, 0, TimeSpan.Zero);

    static HistoryTurn Turn(int n, bool truncated = false, bool unreadable = false) => new()
    {
        Id = $"t{n}",
        At = T0.AddMinutes(n),
        Prompt = unreadable ? null : new HistoryPrompt { Text = $"prompt {n}" },
        Truncated = truncated ? true : null,
        Unreadable = unreadable ? true : null,
    };

    // A host with `count` turns: cursor "b<k>" means the turns before the k-th (0-based).
    static HistoryResult Page(JsonElement d, int count, string cursor = "b", Func<int, HistoryTurn>? make = null)
    {
        var end = d.TryGetProperty("before", out var b) && b.ValueKind == JsonValueKind.String ? int.Parse(b.GetString()![1..]) : count;
        var from = Math.Max(0, end - HistoryLimits.DefaultTurns);
        return new HistoryResult
        {
            Turns = [.. Enumerable.Range(from + 1, end - from).Select(n => (make ?? (i => Turn(i)))(n))],
            Before = from > 0 ? $"{cursor}{from}" : null,
            Start = from > 0 ? null : new HistoryStart { At = T0 },
        };
    }

    static FakeHost Serving(int count, Func<int, HistoryTurn>? make = null)
    {
        var host = new FakeHost();
        host.Capabilities.Add(HistoryProtocol.Capability);
        host.On(HistoryProtocol.Op, d => Page(d, count, make: make));
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
    public async Task Opening_a_session_loads_its_newest_twenty_turns_oldest_first()
    {
        await using var host = Serving(45);
        var history = await OpenAsync(host);

        await history.LoadNewestAsync();

        Assert.Equal(Enumerable.Range(26, 20), Numbers(history));
        Assert.True(history.HasOlder);
        Assert.Equal(HistoryLoad.Idle, history.State);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Loading_older_pages_puts_each_above_the_oldest_turn_until_the_start_is_reached()
    {
        await using var host = Serving(45);
        var history = await OpenAsync(host);

        await history.LoadNewestAsync();
        await history.LoadOlderAsync();
        Assert.Equal(Enumerable.Range(6, 40), Numbers(history));
        await history.LoadOlderAsync();

        Assert.Equal(Enumerable.Range(1, 45), Numbers(history));
        Assert.Equal(HistoryLoad.StartReached, history.State);
        Assert.False(history.HasOlder);
        Assert.Equal(T0, history.Start?.At);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_turn_the_live_session_already_drew_is_held_once_and_a_newer_one_goes_last()
    {
        await using var host = Serving(25);
        var history = await OpenAsync(host);
        await history.LoadNewestAsync();

        var again = history.AddLive(Turn(25));
        var next = history.AddLive(Turn(26));

        Assert.False(again);
        Assert.True(next);
        Assert.Equal(Enumerable.Range(6, 21), Numbers(history));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_page_that_holds_a_live_turn_does_not_draw_it_twice()
    {
        await using var host = Serving(25);
        var history = await OpenAsync(host);
        history.AddLive(Turn(25));

        await history.LoadNewestAsync();

        Assert.Equal(Enumerable.Range(6, 20), Numbers(history));
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_failed_older_page_keeps_what_is_shown_and_trying_again_loads_it()
    {
        await using var host = Serving(45);
        var history = await OpenAsync(host);
        await history.LoadNewestAsync();
        var failing = true;
        host.On(HistoryProtocol.Op, d =>
        {
            if (failing) { throw new InvalidOperationException("the host fails it"); }
            return Page(d, 45);
        });

        await history.LoadOlderAsync();
        Assert.Equal(HistoryLoad.Failed, history.State);
        Assert.Equal(Enumerable.Range(26, 20), Numbers(history));

        failing = false;
        await history.LoadOlderAsync();

        Assert.Equal(Enumerable.Range(6, 40), Numbers(history));
        Assert.Equal(HistoryLoad.Idle, history.State);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_machine_without_the_history_capability_is_asked_nothing_and_has_none()
    {
        await using var host = Serving(5);
        host.Capabilities.Remove(HistoryProtocol.Capability);
        var history = await OpenAsync(host);

        await history.LoadNewestAsync();

        Assert.Equal(HistoryLoad.NoHistory, history.State);
        Assert.Empty(history.Turns);
        Assert.DoesNotContain(host.Received, r => r.Op == HistoryProtocol.Op);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_host_that_says_it_has_no_history_for_the_session_gives_the_reason_to_show()
    {
        await using var host = Serving(5);
        host.On(HistoryProtocol.Op, _ => HistoryResult.NotAvailable("this session's transcript can't be found"));
        var history = await OpenAsync(host);

        await history.LoadNewestAsync();

        Assert.Equal(HistoryLoad.NoHistory, history.State);
        Assert.Equal("this session's transcript can't be found", history.Reason);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_stale_cursor_keeps_the_turns_held_and_reads_the_newest_page_again_without_drawing_one_twice()
    {
        await using var host = Serving(45);
        var history = await OpenAsync(host);
        await history.LoadNewestAsync();
        host.On(HistoryProtocol.Op, d =>
        {
            if (d.TryGetProperty("before", out var b) && b.GetString()!.StartsWith('b')) { return HistoryResult.StaleCursor; }
            return Page(d, 45, "c");
        });

        await history.LoadOlderAsync();

        Assert.Equal(Enumerable.Range(6, 40), Numbers(history));
        Assert.Equal(HistoryLoad.Idle, history.State);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_session_with_no_prompt_yet_has_no_turns_and_its_start_reached()
    {
        await using var host = Serving(0);
        var history = await OpenAsync(host);

        await history.LoadNewestAsync();

        Assert.Empty(history.Turns);
        Assert.Equal(HistoryLoad.StartReached, history.State);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Turns_cut_short_or_unreadable_are_kept_and_flagged()
    {
        await using var host = Serving(3, n => n == 1 ? Turn(n, unreadable: true) : n == 2 ? Turn(n, truncated: true) : Turn(n));
        var history = await OpenAsync(host);

        await history.LoadNewestAsync();

        Assert.True(history.Turns[0].Unreadable);
        Assert.True(history.Turns[1].Truncated);
        Assert.True(history.AnyPartial);
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task Loading_older_pages_past_five_hundred_turns_lets_the_newest_go_and_keeps_five_hundred()
    {
        await using var host = Serving(600);
        var history = await OpenAsync(host);
        await history.LoadNewestAsync();
        for (var i = 0; i < 25; i++) { await history.LoadOlderAsync(); }

        // 26 pages of 20 is 520: the newest 20 went, the oldest page stayed.
        Assert.Equal(Enumerable.Range(81, HistoryLimits.Kept), Numbers(history));
        Assert.True(history.NewestDropped);
    }
}
