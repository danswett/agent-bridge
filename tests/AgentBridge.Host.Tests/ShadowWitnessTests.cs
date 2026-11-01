using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Shadow;
using Xunit;

// The evidence for agent-ha-bridge's known daemon bugs (agent-ha-bridge#258 to #262), read from a
// Copilot transcript written here to Copilot's shapes. Every line is invented; none is copied from
// a real session.
public sealed class ShadowWitnessTests : IDisposable
{
    const string Id = "88888888-8888-4888-8888-888888888888";
    readonly Fixture f = new();

    AgentHaBridgePaths Paths => new(Path.Combine(f.Root, "home", ".agent-ha-bridge"), f.CopilotHome);

    public void Dispose() => f.Dispose();

    // Appends events and returns the transcript's length: where a daemon that has read it all is.
    long Write(params string[] lines)
    {
        var path = Paths.CopilotTranscript(Id)!;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, string.Concat(lines.Select(l => l + "\n")));
        return new FileInfo(path).Length;
    }

    static string Event(string type, object data, string? agentId = null) =>
        agentId is null ? JsonSerializer.Serialize(new { type, data }) : JsonSerializer.Serialize(new { type, data, agentId });

    static string Result(string content, string? agentId = null) =>
        Event("tool.execution_complete", new { toolCallId = "call-" + Guid.NewGuid().ToString("N")[..6], success = true, result = new { content } }, agentId);

    static ShadowSession Daemon(long offset, string[]? agents = null, string[]? shells = null, string? model = "own-model") => new()
    {
        Id = Id,
        Agent = "copilot",
        Status = "idle",
        Agents = agents?.Length ?? 0,
        Shells = shells?.Length ?? 0,
        AgentIds = agents ?? [],
        ShellIds = shells ?? [],
        Offset = offset,
        Model = model,
    };

    static ShadowSession Host(int agents = 0, int shells = 0, string? model = "own-model") => new()
    {
        Id = Id,
        Agent = "copilot",
        Status = "idle",
        Agents = agents,
        Shells = shells,
        Model = model,
    };

    // What the witness makes of the one difference of a kind between these two.
    string? Known(ShadowSession daemon, ShadowSession host, string kind)
    {
        var (d, h) = (new ShadowView { Sessions = new Dictionary<string, ShadowSession> { [Id] = daemon } }, new ShadowView { Sessions = new Dictionary<string, ShadowSession> { [Id] = host }, SeesAgentHaBridge = true });
        var differences = new ShadowWitness(Paths).Explain(d, h, ShadowCompare.Compare(d, h).Differences);
        return Assert.Single(differences, x => x.Kind == kind).Known;
    }

    [Fact]
    public void Extra_shells_are_put_down_to_the_daemons_bugs_only_when_each_shows_its_signature_and_the_rest_add_up()
    {
        var beforeEnd = Write(
            // The session's own, which both count.
            Result("<command started in background with shellId: own1>"),
            // A subagent's, which the daemon counts as the session's (#261).
            Result("<command with shellId: sub1 moved to background by the user>", agentId: "agent-a"),
            // A "still running" line quoted in the middle of a file the session viewed (#262).
            Result("1. a line of the file\n<command with shellId: quoted1 is still running after 30 seconds>\n3. and more of it"),
            Result("<command started in background with shellId: ended1>"));
        // It ended, and the daemon kept it (#259).
        var through = Write(Result("output\n<shellId: ended1 completed with exit code 0>"));

        Assert.Equal("agent-ha-bridge#259, agent-ha-bridge#261, agent-ha-bridge#262",
            Known(Daemon(through, shells: ["own1", "sub1", "quoted1", "ended1"]), Host(shells: 1), ShadowKinds.BackgroundShells));
        // The session's own shell missing on the host is no daemon bug.
        Assert.Null(Known(Daemon(through, shells: ["own1", "sub1", "quoted1", "ended1"]), Host(shells: 0), ShadowKinds.BackgroundShells));
        // Nor a count past the ids the daemon's list carries.
        Assert.Null(Known(Daemon(through, shells: ["own1", "sub1", "quoted1", "ended1"]) with { Shells = 5 }, Host(shells: 1), ShadowKinds.BackgroundShells));
        // Nor is one the transcript never started.
        Assert.Null(Known(Daemon(through, shells: ["own1", "sub1", "mystery"]), Host(shells: 1), ShadowKinds.BackgroundShells));
        // An end the daemon has not read yet is the daemon being behind, not #259.
        Assert.Null(Known(Daemon(beforeEnd, shells: ["own1", "ended1"]), Host(shells: 1), ShadowKinds.BackgroundShells));
    }

    // Seen on DEV-VM1 (#40): a watcher completed, was started again under the same id 40 s later,
    // and the daemon, folding both into one batch, listed nothing while it ran (agent-ha-bridge#276).
    [Fact]
    public void A_shell_the_daemon_does_not_list_is_put_down_to_a_restart_under_the_same_id_only_when_it_read_both()
    {
        Write(Result("<command with shellId: watch is still running after 3 seconds. The command is still running.>"));
        Write(
            Event("system.notification", new { kind = new { type = "shell_completed", shellId = "watch", exitCode = 0 } }),
            Result("<shellId: watch completed with exit code 0>"));
        var beforeRestart = Write(Result("<shellId: other completed with exit code 0>"));
        var through = Write(Result("<command started in background with shellId: watch>"));

        Assert.Equal("agent-ha-bridge#276", Known(Daemon(through), Host(shells: 1), ShadowKinds.BackgroundShells));
        // Not read the restart yet: the daemon is behind.
        Assert.Null(Known(Daemon(beforeRestart), Host(shells: 1), ShadowKinds.BackgroundShells));
        // One more on the host than the restart accounts for.
        Assert.Null(Known(Daemon(through), Host(shells: 2), ShadowKinds.BackgroundShells));

        // A shell started once and never ended, missing on the daemon, is no restart.
        var fresh = Write(Result("<command started in background with shellId: fresh>"));
        Assert.Null(Known(Daemon(fresh, shells: ["watch"]), Host(shells: 2), ShadowKinds.BackgroundShells));
    }

    [Fact]
    public void A_restart_by_a_subagent_one_ended_again_or_one_before_a_shutdown_is_not_put_down_to_the_daemon()
    {
        Write(Result("<command started in background with shellId: watch>"));
        Write(Result("<shellId: watch completed with exit code 0>"));
        var bySubagent = Write(Result("<command started in background with shellId: watch>", agentId: "agent-a"));
        Assert.Null(Known(Daemon(bySubagent), Host(shells: 1), ShadowKinds.BackgroundShells));
        Write(Result("<shellId: watch completed with exit code 0>", agentId: "agent-a"));

        // Found in review (#162): the session's own restart counts whatever is quoted about the id
        // after it, and stops counting at the end that follows it, not at an earlier run's.
        Write(Result("<command started in background with shellId: watch>"));
        var quoted = Write(Result("1. a line of the file\n<command with shellId: watch is still running after 30 seconds>\n3. and more of it"));
        Assert.Equal("agent-ha-bridge#276", Known(Daemon(quoted), Host(shells: 1), ShadowKinds.BackgroundShells));
        var endedAgain = Write(Result("<shellId: watch completed with exit code 0>"));
        Assert.Null(Known(Daemon(endedAgain), Host(shells: 1), ShadowKinds.BackgroundShells));

        // Found in review (#162): a restart before the session shut down is over; a fresh shell
        // after it that the daemon does not list is a mismatch, not this restart.
        Write(Result("<command started in background with shellId: watch>"));
        Write(Event("session.shutdown", new { }));
        var fresh = Write(Event("session.resume", new { }), Result("<command started in background with shellId: fresh>"));
        Assert.Null(Known(Daemon(fresh), Host(shells: 1), ShadowKinds.BackgroundShells));
    }

    // Found in review (#162): only the ends the host takes make a restart, and a quoted start line
    // before a genuine last-line poll of the same id is a start the host takes.
    [Fact]
    public void A_restart_is_judged_by_the_ends_and_starts_the_host_takes()
    {
        Write(Result("<command started in background with shellId: watch>"));
        Write(Result("<shellId: watch completed with exit code 0>"));
        var mixed = Write(Result("a quoted line:\n<command started in background with shellId: watch>\noutput\n<command with shellId: watch is still running after 30 seconds>"));
        Assert.Equal("agent-ha-bridge#276", Known(Daemon(mixed), Host(shells: 1), ShadowKinds.BackgroundShells));
        // A completion quoted after the daemon's offset is not one it has read.
        Write(Result("1. a line\n<shellId: watch completed with exit code 0>\n3. another"));
        Assert.Equal("agent-ha-bridge#276", Known(Daemon(mixed), Host(shells: 1), ShadowKinds.BackgroundShells));
    }

    [Fact]
    public void A_quoted_completion_between_two_polls_is_no_restart()
    {
        Write(Result("<command started in background with shellId: watch>"));
        Write(Result("1. a line\n<shellId: watch completed with exit code 0>\n3. another"));
        var polled = Write(Result("<command with shellId: watch is still running after 30 seconds>"));
        Assert.Null(Known(Daemon(polled), Host(shells: 1), ShadowKinds.BackgroundShells));
    }

    // Found in review (#162): the host takes no marker longer than it reads one, and an end the
    // witness dropped for room may be one that rules a restart out.
    [Fact]
    public void An_over_long_completion_line_is_no_host_end_and_a_dropped_end_leaves_a_restart_unknown()
    {
        Write(Result("<command started in background with shellId: long>"));
        Write(Result("output\n<shellId: long completed with exit code 0> " + new string('x', 1100)));
        var polled = Write(Result("<command with shellId: long is still running after 30 seconds>"));
        Assert.Null(Known(Daemon(polled), Host(shells: 1), ShadowKinds.BackgroundShells));

        Write(Result("<command started in background with shellId: watch>"));
        Write(Result("<shellId: watch completed with exit code 0>"));
        Write(Result("<command started in background with shellId: watch>"));
        var blocked = Write(Result("1. a line\n<shellId: watch completed with exit code 0>\n3. another"));
        Assert.Null(Known(Daemon(blocked, shells: ["long"]), Host(shells: 2), ShadowKinds.BackgroundShells));
        Write([.. Enumerable.Range(0, 40).Select(_ => Result("1. a line\n<shellId: watch completed with exit code 0>\n3. another"))]);
        Assert.Null(Known(Daemon(blocked, shells: ["long"]), Host(shells: 2), ShadowKinds.BackgroundShells));
    }

    // Found in review (#162): with only the host's count to go on, a daemon extra and a host extra of
    // other causes must not cancel out into a restart.
    [Fact]
    public void A_restart_is_not_put_down_to_the_daemon_when_it_lists_a_shell_the_host_does_not_run()
    {
        Write(Result("<command started in background with shellId: watch>"));
        Write(Result("<shellId: watch completed with exit code 0>"));
        var through = Write(Result("<command started in background with shellId: watch>"), Result("<command started in background with shellId: other>"));
        Assert.Equal("agent-ha-bridge#276", Known(Daemon(through, shells: ["other"]), Host(shells: 2), ShadowKinds.BackgroundShells));
        Assert.Null(Known(Daemon(through, shells: ["mystery"]), Host(shells: 2), ShadowKinds.BackgroundShells));
    }

    // Found in review (#162): the same id started again after a shutdown is a run of its own, not
    // the end of the one before it.
    [Fact]
    public void A_restart_under_the_same_id_after_a_shutdown_and_resume_is_put_down_to_the_daemon()
    {
        Write(Result("<command started in background with shellId: watch>"));
        Write(Result("<shellId: watch completed with exit code 0>"));
        Write(Result("<command started in background with shellId: watch>"));
        Write(Event("session.shutdown", new { }), Event("session.resume", new { }));
        var again = Write(Result("<command started in background with shellId: watch>"));
        Assert.Equal("agent-ha-bridge#276", Known(Daemon(again), Host(shells: 1), ShadowKinds.BackgroundShells));
    }

    [Fact]
    public void Missing_agents_are_put_down_to_the_daemon_only_when_they_are_exactly_those_woken_again_after_completing()
    {
        var completedOnly = Write(
            Event("subagent.started", new { toolCallId = "c1", executionMode = "background" }, agentId: "a1"),
            Event("assistant.turn_end", new { }, agentId: "a1"),
            Event("subagent.completed", new { toolCallId = "c1" }));
        Write(
            // Woken again with a message: its own turn, and no second subagent.started.
            Event("user.message", new { content = "carry on" }, agentId: "a1"),
            Event("assistant.turn_start", new { }, agentId: "a1"),
            Event("subagent.started", new { toolCallId = "c2", executionMode = "background" }, agentId: "a2"));
        var through = Write(
            Event("subagent.started", new { toolCallId = "c3", executionMode = "background" }, agentId: "a3"),
            Event("assistant.turn_end", new { }, agentId: "a3"),
            Event("subagent.completed", new { toolCallId = "c3" }),
            Event("subagent.started", new { toolCallId = "c4", executionMode = "background" }, agentId: "a4"),
            Event("assistant.turn_end", new { }, agentId: "a4"),
            Event("subagent.completed", new { toolCallId = "c4" }));

        Assert.Equal("agent-ha-bridge#258", Known(Daemon(through, agents: ["c2"]), Host(agents: 2), ShadowKinds.BackgroundAgents));
        // The daemon still listing two whose completions it read past as well (#259).
        Assert.Equal("agent-ha-bridge#258, agent-ha-bridge#259", Known(Daemon(through, agents: ["c2", "c3", "c4"]), Host(agents: 2), ShadowKinds.BackgroundAgents));
        // One more on the host than the woken agent accounts for.
        Assert.Null(Known(Daemon(through, agents: ["c2"]), Host(agents: 3), ShadowKinds.BackgroundAgents));
        // A daemon that has not read the wake yet is behind, whatever it will make of it.
        Assert.Null(Known(Daemon(completedOnly), Host(agents: 1), ShadowKinds.BackgroundAgents));
        // Nor is a count whose ids the daemon's list does not all carry.
        Assert.Null(Known(Daemon(through, agents: ["c2"]) with { Agents = 2 }, Host(agents: 3), ShadowKinds.BackgroundAgents));
    }

    // Found in review (#77): a shell polled more often than the witness kept starts lost the start
    // that ruled the signature out.
    [Fact]
    public void A_shell_polled_for_hours_keeps_the_start_that_decides_its_signature()
    {
        Write(Result("<command started in background with shellId: own1>"));
        Write([.. Enumerable.Range(0, 40).Select(_ => Result("viewing a file\n<command with shellId: own1 is still running after 30 seconds>\nmore of it"))]);
        Write(Result("<command with shellId: sub1 moved to background by the user>", agentId: "agent-a"));
        var through = Write([.. Enumerable.Range(0, 40).Select(_ => Result("output\n<command with shellId: sub1 is still running after 30 seconds>", agentId: "agent-a"))]);

        // The session's own start, read long before, still rules #262 out for own1.
        Assert.Null(Known(Daemon(through, shells: ["own1"]), Host(), ShadowKinds.BackgroundShells));
        // And forty polls of a subagent's shell are still only a subagent's.
        Assert.Equal("agent-ha-bridge#261", Known(Daemon(through, shells: ["sub1"]), Host(), ShadowKinds.BackgroundShells));
    }

    // Found in review (#77): every message of a woken agent moved where it was woken, so a daemon
    // a message behind lost the #258 signature.
    [Fact]
    public void A_woken_agent_is_woken_where_it_started_working_again_not_at_its_latest_message()
    {
        var woken = Write(
            Event("subagent.started", new { toolCallId = "c1", executionMode = "background" }, agentId: "a1"),
            Event("assistant.turn_end", new { }, agentId: "a1"),
            Event("subagent.completed", new { toolCallId = "c1" }),
            Event("user.message", new { content = "carry on" }, agentId: "a1"));
        Write(Event("assistant.message", new { content = "working on it", toolRequests = new[] { new { name = "view" } } }, agentId: "a1"));

        Assert.Equal("agent-ha-bridge#258", Known(Daemon(woken), Host(agents: 1), ShadowKinds.BackgroundAgents));
    }
    [Fact]
    public void A_daemon_model_is_put_down_to_a_subagent_only_when_a_subagent_named_it_last()
    {
        var through = Write(
            Event("assistant.message", new { content = "mine", model = "own-model" }),
            Event("assistant.message", new { content = "theirs", model = "agent-model" }, agentId: "a1"));

        Assert.Equal("agent-ha-bridge#260", Known(Daemon(through, model: "agent-model"), Host(), ShadowKinds.Model));
        Assert.Null(Known(Daemon(through, model: "a-third-model"), Host(), ShadowKinds.Model));
        Write(Event("assistant.message", new { content = "mine again", model = "own-model" }));
        // The session spoke last: the daemon then has no excuse.
        Assert.Null(Known(Daemon(new FileInfo(Paths.CopilotTranscript(Id)!).Length, model: "agent-model"), Host(), ShadowKinds.Model));
    }

    static readonly DateTimeOffset Asked = new(2026, 1, 2, 8, 0, 0, TimeSpan.Zero);

    static string AskUser(string call) => JsonSerializer.Serialize(new { type = "tool.execution_start", timestamp = Asked.ToString("o"), data = new { toolCallId = call, toolName = "ask_user", arguments = new { question = "Which one?" } } });

    static string Answered(string call) => JsonSerializer.Serialize(new { type = "tool.execution_complete", data = new { toolCallId = call, success = true, result = new { content = "User selected: the first" } } });

    // What a busy session writes while its question waits, none of which the witness parses.
    static string Filler(int bytes) => Event("assistant.reasoning", new { content = new string('x', bytes) });

    // The daemon's view of a session whose hook armed a question as the ask_user started.
    static ShadowSession Asking(string status = "working", TimeSpan? armedAfter = null) => Daemon(0) with
    {
        Status = status,
        Question = new ShadowQuestion { AskedAt = Asked + (armedAfter ?? TimeSpan.FromMilliseconds(50)), Fields = [new ShadowField("choice", 2, null)] },
    };

    ShadowDifference Difference(ShadowSession daemon, ShadowSession host, string kind)
    {
        var (d, h) = (new ShadowView { Sessions = new Dictionary<string, ShadowSession> { [Id] = daemon } }, new ShadowView { Sessions = new Dictionary<string, ShadowSession> { [Id] = host }, SeesAgentHaBridge = true });
        return Assert.Single(new ShadowWitness(Paths).Explain(d, h, ShadowCompare.Compare(d, h).Differences), x => x.Kind == kind);
    }

    // Seen on DEV-VM1 (#40): a question asked at 00:58 and answered at 09:32 stayed on the daemon's
    // card, and so did its waiting: the ask_user had started 7 MiB before its answer, beyond the
    // 4 MiB the daemon reads to see it answered (agent-ha-bridge#259, filed as #263).
    [Fact]
    public void A_question_the_daemon_keeps_after_its_answer_is_put_down_to_its_tail_only_when_the_start_was_beyond_it()
    {
        Write(AskUser("ask-1"), Filler((int)TranscriptEvidence.DaemonQuestionTail), Answered("ask-1"));
        var host = Host() with { Status = "working" };

        Assert.Equal("agent-ha-bridge#259", Difference(Asking(), host, ShadowKinds.QuestionMissingOnHost).Known);
        Assert.Equal("agent-ha-bridge#259", Difference(Asking(), host, ShadowKinds.Status).Known);
        // The daemon's own status is not the host's either: that is the mismatch, and it says so.
        var status = Difference(Asking(status: "idle"), host, ShadowKinds.Status);
        Assert.Null(status.Known);
        Assert.Equal("daemon=idle host=working", status.Detail);
        // A marker armed well away from that ask_user is some other question.
        Assert.Null(Difference(Asking(armedAfter: TimeSpan.FromMinutes(5)), host, ShadowKinds.QuestionMissingOnHost).Known);
    }

    [Fact]
    public void A_question_answered_within_the_daemons_tail_or_not_answered_at_all_stays_a_mismatch()
    {
        var host = Host() with { Status = "working" };
        // Still waiting in the transcript: the host missing it is the host's problem. Another
        // call's completion that happens to quote its id is not its answer.
        Write(AskUser("ask-1"), Filler((int)TranscriptEvidence.DaemonQuestionTail),
            JsonSerializer.Serialize(new { type = "tool.execution_complete", data = new { toolCallId = "other-call", success = true, result = new { content = "done", about = "ask-1" } } }));
        Assert.Null(Difference(Asking(), host, ShadowKinds.QuestionMissingOnHost).Known);

        // Answered with its start still within the tail: the daemon can see it, and is only behind.
        File.Delete(Paths.CopilotTranscript(Id)!);
        Write(AskUser("ask-1"), Filler(1024), Answered("ask-1"), Filler((int)TranscriptEvidence.DaemonQuestionTail));
        Assert.Null(Difference(Asking(), host, ShadowKinds.QuestionMissingOnHost).Known);
        Assert.Null(Difference(Asking(), host, ShadowKinds.Status).Known);
    }

    [Fact]
    public void Only_a_copilot_session_s_transcript_is_read_and_a_session_with_no_readable_one_keeps_its_difference()
    {
        var through = Write(Result("<command with shellId: sub1 moved to background by the user>", agentId: "agent-a"));

        Assert.Equal("agent-ha-bridge#261", Known(Daemon(through, shells: ["sub1"]), Host(), ShadowKinds.BackgroundShells));
        Assert.Null(Known(Daemon(through, shells: ["sub1"]) with { Agent = "claude" }, Host() with { Agent = "claude" }, ShadowKinds.BackgroundShells));
        File.Delete(Paths.CopilotTranscript(Id)!);
        Assert.Null(Known(Daemon(through, shells: ["sub1"]), Host(), ShadowKinds.BackgroundShells));
    }
}
