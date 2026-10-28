using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using Xunit;

public class SourceTests
{
    const string Id = "88888888-8888-4888-8888-888888888888";

    static List<SessionEvent> Drain(SessionSubscription s) => DiscoveryTests.Drain(s);

    [Fact]
    public async Task Upserts_for_one_session_are_coalesced_to_one_per_interval_and_the_latest_state_arrives()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source();
        using var subscription = source.Subscribe();
        Assert.Equal(Events.SessionsSynced, Assert.Single(Drain(subscription)).Name);

        Fixture.Append(events, Copilot.User("go", 1));
        await source.RefreshAsync();
        Assert.Single(Drain(subscription));

        f.Time.Advance(TimeSpan.FromMilliseconds(100));
        Fixture.Append(events, Copilot.TurnStart(2), Copilot.Assistant("half", 3));
        await source.RefreshAsync();
        Fixture.Append(events, Copilot.Assistant("whole", 4), Copilot.TurnEnd(5));
        await source.RefreshAsync();
        Assert.Empty(Drain(subscription));

        f.Time.Advance(TimeSpan.FromMilliseconds(149));
        Assert.Empty(Drain(subscription));
        f.Time.Advance(TimeSpan.FromMilliseconds(1));
        var latest = (Session)Assert.Single(Drain(subscription)).Data;
        Assert.Equal(("whole", SessionStatus.Idle), (latest.Activity.Response, latest.Status));
        Assert.Null(latest.Activity.History);
    }

    [Fact]
    public async Task Hook_usage_and_model_events_change_nothing_and_send_no_upsert()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source();
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2), Copilot.Assistant("answer", 3), Copilot.TurnEnd(4));
        await source.RefreshAsync();
        var before = source.Get(Id)!;
        var revision = source.Revision;
        using var subscription = source.Subscribe();
        Drain(subscription);
        f.Time.Advance(TimeSpan.FromSeconds(1));

        Fixture.Append(events,
            Copilot.Event("hook.start", new { hookInvocationId = "h", hookType = "agentStop", input = new { } }, 10),
            Copilot.Event("hook.end", new { hookInvocationId = "h", hookType = "agentStop", success = true }, 11),
            Copilot.Event("session.usage_record", new { usage = new { tokens = 5 } }, 12),
            Copilot.Event("model.turn_ended", new { kind = "x", turn = 1 }, 13),
            Copilot.Event("system.notification", new { kind = new { type = "shell_completed", shellId = "1" } }, 14),
            Copilot.TurnStart(15, agentId: "sub"), Copilot.TurnEnd(16, agentId: "sub"));
        await source.RefreshAsync();
        f.Time.Advance(TimeSpan.FromSeconds(1));

        Assert.Empty(Drain(subscription));
        Assert.Equal(revision, source.Revision);
        Assert.Equal(before.UpdatedAt, source.Get(Id)!.UpdatedAt);
        Assert.Equal(Fixture.T0.AddSeconds(4), before.UpdatedAt);
    }

    [Fact]
    public async Task An_event_of_a_visible_kind_that_changes_nothing_does_not_move_updatedAt()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source();
        Fixture.Append(events, Copilot.User("go", 1), Copilot.TurnStart(2));
        await source.RefreshAsync();
        var revision = source.Revision;

        Fixture.Append(events, Copilot.TurnStart(9));
        await source.RefreshAsync();

        Assert.Equal(revision, source.Revision);
        Assert.Equal(Fixture.T0.AddSeconds(2), source.Get(Id)!.UpdatedAt);
    }

    [Fact]
    public async Task Subscribing_sends_every_live_session_then_synced_and_then_live_changes()
    {
        using var f = new Fixture();
        const string other = "99999999-9999-4999-8999-999999999999";
        Fixture.Append(f.CopilotSession(Id, 100), Copilot.User("first", 1));
        var second = f.CopilotSession(other, 101);
        Fixture.Append(second, Copilot.User("second", 2));
        f.Processes.Run(AgentKind.Copilot, 100);
        f.Processes.Run(AgentKind.Copilot, 101);
        await using var source = f.Source();
        await source.RefreshAsync();

        using var subscription = source.Subscribe();
        Assert.Equal(new SubscribeResult(source.Revision, 2), subscription.Result);
        var snapshot = Drain(subscription);
        Assert.Equal([Events.SessionUpsert, Events.SessionUpsert, Events.SessionsSynced], snapshot.Select(e => e.Name));
        Assert.Equal([other, Id], snapshot.Take(2).Select(e => ((Session)e.Data).Id));
        Assert.Equal(source.Revision, ((SessionsSynced)snapshot[2].Data).Revision);

        f.Time.Advance(TimeSpan.FromSeconds(1));
        Fixture.Append(second, Copilot.Assistant("reply", 3));
        f.Processes.Exit(AgentKind.Copilot, 100);
        await source.RefreshAsync();
        var live = Drain(subscription);
        Assert.Equal([Events.SessionRemove, Events.SessionUpsert], live.Select(e => e.Name));
        Assert.Equal(Id, ((SessionRemoved)live[0].Data).Id);
    }

    [Fact]
    public async Task Subscribing_again_resends_the_whole_snapshot_and_the_replaced_subscription_hears_nothing_more()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(Id, 100);
        Fixture.Append(events, Copilot.User("first", 1));
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source();
        await source.RefreshAsync();
        var first = source.Subscribe();
        Drain(first);

        // What a channel does on a second sessions.subscribe: drop the first, start again.
        first.Dispose();
        using var second = source.Subscribe();
        f.Time.Advance(TimeSpan.FromSeconds(1));
        Fixture.Append(events, Copilot.Assistant("later", 2));
        await source.RefreshAsync();

        Assert.Equal(new SubscribeResult(source.Revision - 1, 1), second.Result);
        Assert.Equal([Events.SessionUpsert, Events.SessionsSynced, Events.SessionUpsert], Drain(second).Select(e => e.Name));
        Assert.Empty(Drain(first));
        Assert.True(first.Events.Completion.IsCompleted);
    }

    [Fact]
    public async Task The_list_is_paged_so_every_page_fits_a_message_and_the_cursor_reaches_every_session()
    {
        using var f = new Fixture();
        var ids = Enumerable.Range(0, 7).Select(i => $"{i:D8}-0000-4000-8000-000000000000").ToList();
        for (var i = 0; i < ids.Count; i++)
        {
            Fixture.Append(f.CopilotSession(ids[i], 100 + i), Copilot.User("go", 1), Copilot.Assistant(new string((char)('a' + i), 15_000), 2 + i));
            f.Processes.Run(AgentKind.Copilot, 100 + i);
        }
        await using var source = f.Source();
        await source.RefreshAsync();

        var seen = new List<string>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = source.List(cursor);
            Assert.InRange(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(page, ProtocolJson.Options).Length, 1, ProtocolJson.MaxMessageBytes - 128);
            Assert.NotEmpty(page.Sessions);
            seen.AddRange(page.Sessions.Select(s => s.Id));
            cursor = page.Next;
            pages++;
        }
        while (cursor is not null);

        Assert.True(pages > 1);
        Assert.Equal(Enumerable.Reverse(ids), seen);
        Assert.Throws<ArgumentException>(() => source.List("not-a-cursor"));
    }

    [Fact]
    public async Task The_revision_goes_up_once_per_change_and_not_on_a_pass_that_changes_nothing()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(Id, 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source();

        await source.RefreshAsync();
        var first = source.Revision;
        await source.RefreshAsync();
        Assert.Equal(first, source.Revision);

        Fixture.Append(events, Copilot.User("go", 1));
        await source.RefreshAsync();
        Assert.Equal(first + 1, source.Revision);
    }

    [Fact]
    public async Task Watching_picks_up_appended_lines_without_an_explicit_refresh()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(Id, 100);
        Fixture.Append(events, Copilot.User("go", 1));
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = new SessionSource(f.Options(watch: true), f.Processes);
        await source.StartAsync();
        using var subscription = source.Subscribe();

        Fixture.Append(events, Copilot.Assistant("seen by the watcher or the poll", 2));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await foreach (var e in subscription.Events.ReadAllAsync(timeout.Token))
        {
            if (e.Data is Session { Activity.Response: "seen by the watcher or the poll" }) { return; }
        }
    }

    [Fact]
    public async Task A_Changed_handler_that_throws_does_not_stop_the_handlers_after_it()
    {
        using var f = new Fixture();
        Fixture.Append(f.CopilotSession(Id, 100), Copilot.User("go", 1));
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source();
        var heard = new TaskCompletionSource<SessionChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        source.Changed += (_, _) => throw new InvalidOperationException("first handler fails");
        source.Changed += (_, change) => heard.TrySetResult(change);

        await source.RefreshAsync();

        var change = await heard.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal((SessionChangeKind.Upserted, Id), (change.Kind, change.Id));
    }

    [Fact]
    public async Task Discovery_still_runs_every_poll_interval_while_transcript_writes_keep_waking_the_loop()
    {
        using var f = new Fixture();
        await using var source = f.Source();
        var woken = 0;
        var discovered = 0;

        // Transcript appends wake the loop every 50 ms, more often than the 200 ms poll.
        for (var i = 0; i < 20; i++)
        {
            f.Time.Advance(TimeSpan.FromMilliseconds(50));
            woken++;
            if (source.DiscoveryDue(woke: true)) { discovered++; }
        }

        Assert.Equal(20, woken);
        Assert.InRange(discovered, 4, 5);
    }

    [Fact]
    public async Task A_full_scan_and_watch_cycle_never_writes_under_the_agent_and_bridge_roots()
    {
        using var f = new Fixture();
        Fixture.Append(f.CopilotSession(Id, 100), Copilot.User("go", 1), Copilot.ToolStart("q1", "ask_user", new { message = "?" }, 2));
        const string claudeId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa", codexId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";
        var claude = f.ClaudeTranscript(claudeId);
        Fixture.Append(claude, Claude.User("hi", 1), Claude.Assistant(2, "end_turn", Claude.Text("hello")));
        f.ClaudeRegistration(claudeId, 200, claude, "idle", Fixture.T0.AddSeconds(5));
        var codex = f.CodexTranscript(codexId);
        Fixture.Append(codex, Codex.Event(new { type = "task_started" }, 1));
        f.CodexRegistration(codexId, 300, codex);
        File.WriteAllText(Path.Combine(f.CodexRegistrations, codexId + ".approval.json"), "{\"SessionId\":\"x\",\"DecisionId\":\"d\",\"Question\":\"q\"}");
        // agent-ha-bridge's spool sits beside the registration folders.
        var spool = Path.Combine(f.Runtime, "agent-bridge-spool");
        Directory.CreateDirectory(spool);
        File.WriteAllText(Path.Combine(spool, "event-1.json"), "{}");
        f.Processes.Run(AgentKind.Copilot, 100);
        f.Processes.Run(AgentKind.Claude, 200);
        f.Processes.Run(AgentKind.Codex, 300);
        // File times have coarse resolution on some file systems: let any write the setup
        // made settle before the listing that is compared.
        await Task.Delay(50);
        var before = f.Listing();

        await using (var source = new SessionSource(f.Options(watch: true), f.Processes))
        {
            await source.StartAsync();
            using var subscription = source.Subscribe();
            await Task.Delay(700);
            await source.RefreshAsync();
            Assert.Equal(3, source.Snapshot().Count);
            source.List();
            source.Get(Id);
        }

        Assert.Equal(before, f.Listing());
    }
}
