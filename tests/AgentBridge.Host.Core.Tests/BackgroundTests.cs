using AgentBridge.Protocol;
using Xunit;

// Work a Copilot session started that outlives its own turn: background agents and shells.
// Event shapes follow real transcripts; every line here is invented.
public class BackgroundTests
{
    const string Id = "55555555-5555-4555-8555-555555555555";

    sealed class Session_ : IAsyncDisposable
    {
        public readonly Fixture F = new();
        public readonly string Events;
        public readonly AgentBridge.Host.Core.SessionSource Source;

        public Session_(long tailBytes = 4 * 1024 * 1024)
        {
            Events = F.CopilotSession(Id, 100);
            F.Processes.Run(AgentKind.Copilot, 100);
            Source = F.Source(tailBytes);
        }

        public async Task<Session> After(params string[] lines)
        {
            Fixture.Append(Events, lines);
            await Source.RefreshAsync();
            return Source.Get(Id)!;
        }

        public async ValueTask DisposeAsync()
        {
            await Source.DisposeAsync();
            F.Dispose();
        }
    }

    // The main agent starts a background task agent with the task tool and ends its turn.
    static string[] Delegate(string callId, string agentId, int s, string mode = "background") =>
    [
        Copilot.User("delegate it", s), Copilot.TurnStart(s + 1), Copilot.Assistant("", s + 2, tools: [callId]),
        Copilot.ToolStart(callId, "task", new { name = "helper", description = "Help out", mode }, s + 2),
        Copilot.SubagentStarted(callId, agentId, s + 2, mode), Copilot.ToolDone(callId, s + 3), Copilot.TurnEnd(s + 3), Copilot.TurnStart(s + 4),
        Copilot.User("the task prompt", s + 4, agentId), Copilot.TurnStart(s + 4, agentId),
        Copilot.Assistant("Started it in the background.", s + 5), Copilot.TurnEnd(s + 6),
    ];

    [Fact]
    public async Task A_background_agent_counts_from_its_start_until_its_own_turn_ends_without_asking_for_tools()
    {
        await using var s = new Session_();

        var started = await s.After(Delegate("t1", "a1", 1));
        Assert.Equal(SessionStatus.Idle, started.Status);
        Assert.Equal(Fixture.T0.AddSeconds(7), started.StatusSince);
        Assert.Equal(new Background { Agents = 1 }, started.Activity.Background);

        // A round of tools ends a turn of the agent's but not its work.
        var between = await s.After(Copilot.Assistant("", 10, tools: ["s1"], agentId: "a1"),
            Copilot.ToolStart("s1", "view", new { path = "/work" }, 10, "a1"), Copilot.ToolDone("s1", 11, agentId: "a1"), Copilot.TurnEnd(12, "a1"));
        Assert.Equal(new Background { Agents = 1 }, between.Activity.Background);

        var finished = await s.After(Copilot.TurnStart(13, "a1"), Copilot.Assistant("Here is what I found.", 14, agentId: "a1"), Copilot.TurnEnd(15, "a1"));
        Assert.Null(finished.Activity.Background);
        Assert.Equal(SessionStatus.Idle, finished.Status);
        Assert.Equal(Fixture.T0.AddSeconds(7), finished.StatusSince);
        // Visible, so it moves updatedAt, to when the agent stopped.
        Assert.Equal(Fixture.T0.AddSeconds(15), finished.UpdatedAt);

        var completed = await s.After(Copilot.SubagentCompleted("t1", "a1", 15), Copilot.AgentIdle("a1", 75));
        Assert.Null(completed.Activity.Background);
        Assert.Equal(Fixture.T0.AddSeconds(15), completed.UpdatedAt);
    }

    [Fact]
    public async Task A_completion_written_again_at_shutdown_does_not_count_down_an_agent_still_working()
    {
        await using var s = new Session_();

        await s.After([.. Delegate("t1", "a1", 1), .. Delegate("t2", "a2", 20)]);
        var one = await s.After(Copilot.Assistant("Done.", 40, agentId: "a1"), Copilot.TurnEnd(41, "a1"), Copilot.SubagentCompleted("t1", "a1", 41));
        Assert.Equal(new Background { Agents = 1 }, one.Activity.Background);

        // Copilot writes subagent.completed again for every agent it still tracks as the
        // session shuts down; a count would have gone to zero here with a2 still running.
        var duplicate = await s.After(Copilot.SubagentCompleted("t1", "a1", 50));
        Assert.Equal(new Background { Agents = 1 }, duplicate.Activity.Background);

        var shutdown = await s.After(Copilot.SubagentCompleted("t2", "a2", 50), Copilot.Event("session.shutdown", new { shutdownType = "routine" }, 50));
        Assert.Null(shutdown.Activity.Background);
    }

    [Fact]
    public async Task A_background_agent_woken_by_a_message_counts_again_until_its_own_turn_ends_though_nothing_else_says_so()
    {
        await using var s = new Session_();

        await s.After(Delegate("t1", "a1", 1));
        var idle = await s.After(Copilot.Assistant("Report.", 10, agentId: "a1"), Copilot.TurnEnd(11, "a1"),
            Copilot.SubagentCompleted("t1", "a1", 11), Copilot.AgentIdle("a1", 12));
        Assert.Null(idle.Activity.Background);

        // write_agent: the message arrives stamped with the agent's id. Copilot writes no
        // second subagent.started or subagent.completed for this run, and its agent_idle
        // notification can come an hour and a half late or not at all.
        var woken = await s.After(Copilot.User("one more thing", 30, "a1"), Copilot.TurnStart(30, "a1"));
        Assert.Equal(new Background { Agents = 1 }, woken.Activity.Background);
        Assert.Equal(SessionStatus.Idle, woken.Status);

        var working = await s.After(Copilot.Assistant("", 31, tools: ["s2"], agentId: "a1"), Copilot.ToolStart("s2", "edit", new { path = "/x" }, 31, "a1"),
            Copilot.ToolDone("s2", 32, agentId: "a1"), Copilot.TurnEnd(33, "a1"));
        Assert.Equal(new Background { Agents = 1 }, working.Activity.Background);

        var done = await s.After(Copilot.TurnStart(34, "a1"), Copilot.Assistant("Did the one more thing.", 35, agentId: "a1"), Copilot.TurnEnd(36, "a1"));
        Assert.Null(done.Activity.Background);
    }

    [Fact]
    public async Task A_late_idle_notification_for_an_earlier_run_does_not_end_the_run_after_it()
    {
        await using var s = new Session_();

        await s.After([.. Delegate("t1", "a1", 1), Copilot.Assistant("Report.", 10, agentId: "a1"), Copilot.TurnEnd(11, "a1"), Copilot.SubagentCompleted("t1", "a1", 11)]);
        // Woken before the parent took the first run's notification: it then arrives in
        // the middle of the second run, which is waiting on a long tool.
        var woken = await s.After(Copilot.User("one more thing", 20, "a1"), Copilot.TurnStart(20, "a1"), Copilot.Assistant("", 21, tools: ["s1"], agentId: "a1"),
            Copilot.ToolStart("s1", "powershell", new { command = "dotnet test" }, 21, "a1"), Copilot.AgentIdle("a1", 25));
        Assert.Equal(new Background { Agents = 1 }, woken.Activity.Background);

        var done = await s.After(Copilot.ToolDone("s1", 90, agentId: "a1"), Copilot.TurnEnd(90, "a1"), Copilot.TurnStart(91, "a1"),
            Copilot.Assistant("Tests pass.", 92, agentId: "a1"), Copilot.TurnEnd(93, "a1"), Copilot.AgentIdle("a1", 95));
        Assert.Null(done.Activity.Background);

        // Each run's notification is spent once: a third run is not ended by the second's.
        var third = await s.After(Copilot.User("and another", 100, "a1"), Copilot.TurnStart(100, "a1"), Copilot.Assistant("", 101, tools: ["s2"], agentId: "a1"));
        Assert.Equal(new Background { Agents = 1 }, third.Activity.Background);
    }

    [Fact]
    public async Task An_agent_that_aborts_or_shuts_down_on_its_own_stops_without_ending_the_sessions_other_work()
    {
        await using var s = new Session_();

        var before = await s.After([.. Delegate("t1", "a1", 1), .. Delegate("t2", "a2", 20), Copilot.ToolResult("c1", "<command started in background with shellId: ci>", 30)]);
        Assert.Equal(new Background { Agents = 2, Shells = 1 }, before.Activity.Background);

        var aborted = await s.After(Copilot.Event("abort", new { reason = "user initiated" }, 40, agentId: "a1"));
        Assert.Equal(new Background { Agents = 1, Shells = 1 }, aborted.Activity.Background);

        var shut = await s.After(Copilot.Event("session.shutdown", new { shutdownType = "routine" }, 41, agentId: "a2"));
        Assert.Equal(new Background { Shells = 1 }, shut.Activity.Background);
        Assert.Equal(SessionStatus.Idle, shut.Status);
    }

    [Fact]
    public async Task An_agent_idle_notification_alone_also_ends_a_run()
    {
        await using var s = new Session_();

        var started = await s.After(Delegate("t1", "a1", 1));
        Assert.Equal(new Background { Agents = 1 }, started.Activity.Background);
        var idle = await s.After(Copilot.AgentIdle("a1", 20));

        Assert.Null(idle.Activity.Background);
    }

    [Fact]
    public async Task A_sync_agent_is_the_sessions_own_work_and_never_background()
    {
        await using var s = new Session_();

        var s1 = await s.After([.. Delegate("t0", "a0", 1), Copilot.User("go", 11), Copilot.TurnStart(12), Copilot.ToolStart("t1", "task", new { mode = "sync" }, 13),
            Copilot.SubagentStarted("t1", "a1", 13, mode: "sync"), Copilot.User("prompt", 14, "a1"), Copilot.TurnStart(14, "a1")]);

        Assert.Equal(SessionStatus.Working, s1.Status);
        Assert.Equal(new Background { Agents = 1 }, s1.Activity.Background);
    }

    [Fact]
    public async Task An_agent_whose_start_is_before_the_parsed_window_is_still_known_by_its_mode()
    {
        await using var s = new Session_(tailBytes: 4096);
        var filler = Enumerable.Range(0, 8).Select(i => Copilot.Assistant(new string('x', 1000), 10 + i)).ToArray();

        var s1 = await s.After([
            .. Delegate("t1", "a1", 1), Copilot.ToolStart("t2", "task", new { mode = "sync" }, 8), Copilot.SubagentStarted("t2", "a2", 8, mode: "sync"),
            .. filler,
            Copilot.TurnStart(30, "a1"), Copilot.Assistant("", 30, tools: ["s1"], agentId: "a1"),
            Copilot.TurnStart(31, "a2"), Copilot.Assistant("", 31, tools: ["s2"], agentId: "a2"),
        ]);

        Assert.Equal(new Background { Agents = 1 }, s1.Activity.Background);
    }

    [Fact]
    public async Task Background_shells_count_from_their_start_until_they_complete_are_stopped_or_are_notified()
    {
        await using var s = new Session_();

        var s1 = await s.After(Copilot.User("run things", 1), Copilot.TurnStart(2),
            Copilot.ToolResult("c1", "<command started in background with shellId: build>", 3),
            Copilot.ToolResult("c2", "Building...\n<command with shellId: 7 is still running after 30 seconds. The command is still running. You will be automatically notified when it completes.>", 4),
            Copilot.ToolResult("c3", "<command with shellId: watch moved to background by the user. You will be automatically notified when it completes.>", 5),
            Copilot.Assistant("All three are running.", 6), Copilot.TurnEnd(7));
        Assert.Equal(SessionStatus.Idle, s1.Status);
        Assert.Equal(new Background { Shells = 3 }, s1.Activity.Background);

        var s2 = await s.After(Copilot.ShellCompleted("build", 20));
        Assert.Equal(new Background { Shells = 2 }, s2.Activity.Background);
        Assert.Equal(Fixture.T0.AddSeconds(20), s2.UpdatedAt);

        var s3 = await s.After(Copilot.ToolResult("c4", "<command with id: watch stopped>", 21));
        Assert.Equal(new Background { Shells = 1 }, s3.Activity.Background);

        var s4 = await s.After(Copilot.ToolResult("c5", "Built.\n<shellId: 7 completed with exit code 0>", 22));
        Assert.Null(s4.Activity.Background);
    }

    [Fact]
    public async Task Markers_quoted_in_output_a_subagents_shells_and_detached_services_are_not_counted()
    {
        await using var s = new Session_();
        await s.After(Copilot.ToolResult("c0", "<command started in background with shellId: real>", 1));

        var s1 = await s.After(
            // A command printing a transcript back: markers inside its output are text.
            Copilot.ToolResult("c1", "line one\n<command started in background with shellId: quoted>\n<shellId: real completed with exit code 0>\nlast line", 2),
            Copilot.ToolResult("c2", "<command started in background with shellId: sub>", 3, agentId: "a9"),
            Copilot.ToolResult("c3", "<command started in detached background with shellId: server>", 4));

        Assert.Equal(new Background { Shells = 1 }, s1.Activity.Background);
    }

    [Fact]
    public async Task Work_started_by_a_CLI_process_that_has_since_been_replaced_no_longer_counts()
    {
        await using var s = new Session_();

        var before = await s.After([.. Delegate("t1", "a1", 1), Copilot.ToolResult("c1", "<command started in background with shellId: ci>", 8)]);
        Assert.Equal(new Background { Agents = 1, Shells = 1 }, before.Activity.Background);

        // The CLI exits without writing anything and a new one resumes the session. Nothing
        // the old one started can ever report back.
        s.F.Processes.Exit(AgentKind.Copilot, 100);
        s.F.Processes.Run(AgentKind.Copilot, 200, startedAt: Fixture.T0.AddSeconds(60));
        s.F.CopilotSession(Id, 200, lockWrite: Fixture.T0.AddSeconds(61));
        var after = await s.After(Copilot.Event("session.resume", new { resumeTime = Copilot.At(61) }, 61));
        Assert.Equal(200, after.ProcessId);
        Assert.Null(after.Activity.Background);

        var mine = await s.After(Copilot.ToolResult("c2", "<command started in background with shellId: ci>", 70));
        Assert.Equal(new Background { Shells = 1 }, mine.Activity.Background);
    }

    [Fact]
    public async Task A_host_that_starts_after_the_CLI_was_replaced_does_not_count_the_old_CLIs_work()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(Id, 200, lockWrite: Fixture.T0.AddSeconds(61));
        Fixture.Append(events, [.. Delegate("t1", "a1", 1), Copilot.ToolResult("c1", "<command started in background with shellId: ci>", 8),
            Copilot.Event("session.resume", new { resumeTime = Copilot.At(61) }, 61), Copilot.ToolResult("c2", "<command started in background with shellId: mine>", 70)]);
        f.Processes.Run(AgentKind.Copilot, 200, startedAt: Fixture.T0.AddSeconds(60));
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Equal(new Background { Shells = 1 }, source.Get(Id)!.Activity.Background);
    }
}
