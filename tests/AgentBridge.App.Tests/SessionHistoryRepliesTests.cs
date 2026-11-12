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

    static Session Snapshot(string turn, string prompt, string? reply = null, string? answered = null, int seconds = 0, UserMessageFrom? from = null) =>
        Make.Session(updated: T0.AddSeconds(seconds)) with
        {
            Activity = new Activity { Turn = turn, UserMessage = prompt, UserMessageFrom = from, Response = reply, ResponseTurn = answered },
        };

    static SessionCardViewModel Card(HostConnection connection, Session session) =>
        new(session, new CardContext
        {
            Host = connection, Deliveries = new Deliveries(connection, new LockedDispatcher()),
            Folds = new FoldStore(null), Capabilities = () => connection.Capabilities,
        });

    static HistoryTurn Notice(string id, string text, int seconds) => new()
    {
        Id = id, At = T0.AddSeconds(seconds), Kind = TurnKind.Notification,
        Prompt = new HistoryPrompt { Text = "Command finished", From = UserMessageFrom.Notification },
        Parts = [new HistoryPart { Id = id + ":reply", At = T0.AddSeconds(seconds + 1), Kind = HistoryPartKind.Response, Text = text }],
    };

    [Fact]
    public async Task A_notification_turn_that_ran_before_the_owners_queued_answer_leaves_each_reply_under_its_own_prompt()
    {
        await using var connection = HostConnection.ForProfile("not-connected", new LockedDispatcher());
        var answer = new string('a', 800);
        var card = Card(connection, Snapshot("owner", "Owner question"));
        try
        {
            card.Update(Snapshot("owner", "Owner question", answer, "owner"));
            card.Update(Snapshot("schedule", "Scheduled check", answer, "owner", 3, UserMessageFrom.Schedule));
            card.Update(Snapshot("schedule", "Scheduled check", "Scheduled reply", "schedule", 4, UserMessageFrom.Schedule));
            var scheduled = Snapshot("schedule", "Scheduled check", "Scheduled reply", "schedule", 8, UserMessageFrom.Schedule);
            card.Update(scheduled with { Activity = scheduled.Activity with { Notice = Notice("notice", "Notification reply", 8) } });
            card.Update(scheduled with { Activity = scheduled.Activity with { Notice = Notice("notice", "Notification reply", 8) with { Prompt = null, Parts = [], Truncated = true } } });
            Assert.Equal("Notification reply", Assert.Single(card.Stored!.Turns.Single(t => t.Id == "notice").Parts).Text);
            var followup = Snapshot("followup", "Next question", "Fourth reply", "followup", 10);
            card.Update(followup with { Activity = followup.Activity with { Notice = Notice("notice", "Notification reply", 8) } });

            var rows = HistoryDraw.Rows(card.Stored!.Turns, null);
            Assert.Equal(["owner", "schedule", "notice", "followup"], rows.Select(r => r.Id));
            Assert.Equal([answer, "Scheduled reply", "Notification reply", "Fourth reply"], rows.Select(r => Assert.Single(r.Blocks).Text));
            Assert.Equal("Owner question", rows[0].Prompt);
            Assert.False(rows[1].Disclosure!.Open);
            Assert.False(rows[2].Disclosure!.Open);
        }
        finally { card.Detach(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_notification_reply_stays_with_its_turn_while_the_owner_is_queued_and_after_the_owner_is_answered(bool opening)
    {
        await using var connection = HostConnection.ForProfile("not-connected", new LockedDispatcher());
        var earlier = Snapshot("earlier", "Earlier question", "Earlier answer", "earlier");
        var notification = Notice("notice", "Notification reply", 1);
        var queued = Snapshot("owner", "Owner question", "Earlier answer", "earlier", 3);
        queued = queued with { Activity = queued.Activity with { Notice = notification } };
        var card = Card(connection, opening ? queued : earlier);
        try
        {
            if (!opening)
            {
                card.Update(earlier with { Activity = earlier.Activity with { Notice = notification } });
                Assert.False(card.HasVoice);
                Assert.False(card.HasUserMessage);
                card.Update(queued);
            }
            var notice = HistoryDraw.Rows(card.Stored!.Turns, card.UserMessage)[1];
            Assert.Equal("notice", notice.Id);
            Assert.Equal("Notification reply", Assert.Single(notice.Blocks).Text);
            Assert.False(card.HasVoice);
            Assert.Empty(card.Stored.Turns.Single(t => t.Id == "owner").Parts);

            card.Update(Snapshot("owner", "Owner question", "Owner answer", "owner", 2));
            Assert.True(card.HasVoice);
            Assert.Equal("Owner answer", card.Response);
            Assert.Equal("Notification reply", HistoryDraw.Rows(card.Stored.Turns, card.UserMessage)[1].Blocks[0].Text);
        }
        finally { card.Detach(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [Trait("Category", "Network")]
    public async Task A_delayed_page_preserves_newer_live_words_but_not_budget_only_metadata(bool empty, bool budget)
    {
        await using var host = Serving(() => 0);
        host.Start();
        await using var connection = host.Connect();
        await connection.WhenConnectedAsync().WaitAsync(FakeHost.Patience);
        var original = Snapshot("owner", "Owner question", "Old", "owner");
        if (budget) { original = original with { Activity = original.Activity with { Notice = Notice("notice", "Old", 0) } }; }
        var card = Card(connection, original);
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Notice(budget ? "notice" : "owner", budget ? "New from history" : "Old", 0) with { Kind = budget ? TurnKind.Notification : TurnKind.User, Prompt = null };
        var page = new HistoryResult { Turns = [empty ? old with { Parts = [] } : old] };
        host.OnAsync(HistoryProtocol.Op, async _ => { captured.SetResult(); await release.Task; return page; });
        try
        {
            var loading = card.Stored!.LoadNewestAsync();
            await captured.Task.WaitAsync(FakeHost.Patience);
            if (budget) { card.Update(original with { Activity = original.Activity with { Notice = original.Activity.Notice! with { Prompt = null, Parts = [], Truncated = true } } }); }
            else
            {
                card.Update(Snapshot("owner", "Owner question", "Complete newer answer", "owner", 2));
                card.Update(Snapshot("queued", "Next question", "Complete newer answer", "owner", 3));
            }
            release.SetResult();
            await loading;
            var row = HistoryDraw.Rows(card.Stored.Turns, card.UserMessage).Single(r => r.Id == old.Id);
            var expected = budget ? "New from history" : "Complete newer answer";
            Assert.Equal(expected, Assert.Single(row.Blocks).Text);
            if (!budget) { Assert.Equal("Owner question", row.Prompt); Assert.Null(row.Reply); }
            host.On(HistoryProtocol.Op, _ => new HistoryResult { Turns = [Notice(old.Id, expected, 0) with { Kind = old.Kind, Prompt = new HistoryPrompt { Text = budget ? "Command finished" : "Owner question", From = budget ? UserMessageFrom.Notification : null } }] });
            await card.Stored.LoadNewestAsync();
            Assert.Equal(T0.AddSeconds(1), HistoryDraw.Rows(card.Stored.Turns, card.UserMessage).Single(r => r.Id == old.Id).Reply!.At);
        }
        finally { release.TrySetResult(); card.Detach(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_empty_or_delegate_only_snapshot_does_not_clear_a_held_main_agent_reply(bool delegated)
    {
        await using var connection = HostConnection.ForProfile("not-connected", new LockedDispatcher());
        var card = Card(connection, Snapshot("owner", "Owner question", "Owner answer", "owner"));
        try
        {
            var next = Snapshot("owner", "Owner question", delegated ? "Delegate text" : null, "owner");
            card.Update(next with { Activity = next.Activity with { Source = delegated ? new ActivitySource { Response = "worker" } : null } });
            Assert.Equal("Owner answer", Assert.Single(HistoryDraw.Rows(card.Stored!.Turns, null)).Blocks[0].Text);
            Assert.Equal("Owner answer", card.Response);
            Assert.True(card.HasVoice);
            var row = Assert.Single(HistoryDraw.Rows(card.Stored.Turns, null));
            Assert.Null(row.PromptUnder);
            Assert.Null(row.Reply);
        }
        finally { card.Detach(); }
    }

    [Theory]
    [InlineData(UserMessageFrom.Person)]
    [InlineData(UserMessageFrom.Schedule)]
    public async Task A_budget_trimmed_live_prompt_stays_visible_with_its_original_origin(UserMessageFrom from)
    {
        await using var connection = HostConnection.ForProfile("not-connected", new LockedDispatcher());
        var original = Snapshot("owner", "Original prompt", "Owner answer", "owner", from: from);
        var card = Card(connection, original);
        try
        {
            card.Update(original with { Activity = original.Activity with { UserMessage = null } });
            Assert.Equal("Original prompt", card.UserMessage);
            Assert.Equal(from == UserMessageFrom.Person, card.HasUserMessage);
            Assert.Equal(from == UserMessageFrom.Schedule, card.HasPrompt);
        }
        finally { card.Detach(); }
    }

    [Fact]
    [Trait("Category", "Network")]
    public async Task A_reply_loaded_from_history_refreshes_the_live_response_without_another_session_update()
    {
        await using var host = Serving(() => 0);
        host.Start();
        await using var connection = host.Connect();
        await connection.WhenConnectedAsync().WaitAsync(FakeHost.Patience);
        var card = Card(connection, Snapshot("owner", "Owner question", answered: "owner"));
        try
        {
            card.Update(card.Session with { Activity = card.Session.Activity with { Notice = Notice("notice", "Notice reply", 0) } });
            var rendered = "";
            card.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(card.Response)) { rendered = card.Response; }
            };
            host.On(HistoryProtocol.Op, _ => new HistoryResult
            {
                Turns = [Notice("notice", "Notice reply", 0), new HistoryTurn
                {
                    Id = "owner", At = T0, Prompt = new HistoryPrompt { Text = "Owner question" },
                    Parts = [new HistoryPart { Id = "answer", At = T0, Kind = HistoryPartKind.Response, Text = "Stored answer" }],
                }],
            });
            await card.Stored!.LoadNewestAsync();
            Assert.Equal("Stored answer", rendered);
            Assert.True(card.HasVoice);
        }
        finally { card.Detach(); }
    }

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
