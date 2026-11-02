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

    // What the witness makes of the one difference of a kind between these two, on the last of
    // as many samples in a row.
    string? Known(ShadowSession daemon, ShadowSession host, string kind, int samples = 1)
    {
        var (d, h) = (new ShadowView { Sessions = new Dictionary<string, ShadowSession> { [Id] = daemon } }, new ShadowView { Sessions = new Dictionary<string, ShadowSession> { [Id] = host }, SeesAgentHaBridge = true });
        var witness = new ShadowWitness(Paths);
        IReadOnlyList<ShadowDifference> differences = [];
        for (var i = 0; i < samples; i++) { differences = witness.Explain(d, h, ShadowCompare.Compare(d, h).Differences); }
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

    // #175: with the host's subagents list, the agents behind its count are checked one by one, so a
    // count that adds up from the wrong agents is no longer put down to the daemon.
    [Fact]
    public void The_hosts_listed_agents_are_matched_to_the_daemons_one_by_one_before_a_difference_is_put_down_to_it()
    {
        Write(
            Event("subagent.started", new { toolCallId = "c1", executionMode = "background" }, agentId: "a1"),
            Event("assistant.turn_end", new { }, agentId: "a1"),
            Event("subagent.completed", new { toolCallId = "c1" }),
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

        // a1 woken again (#258) and a2 running: the daemon counts a2 only.
        Assert.Equal("agent-ha-bridge#258", Known(Daemon(through, agents: ["c2"]), Host(agents: 2) with { RunningSubagents = ["a1", "a2"] }, ShadowKinds.BackgroundAgents));
        // The same counts, but the host's second agent is a3, which ended, and a2 is not counted:
        // the arithmetic alone would put that down to #258.
        Assert.Equal("agent-ha-bridge#258", Known(Daemon(through, agents: ["c2"]), Host(agents: 2), ShadowKinds.BackgroundAgents));
        Assert.Null(Known(Daemon(through, agents: ["c2"]), Host(agents: 2) with { RunningSubagents = ["a1", "a3"] }, ShadowKinds.BackgroundAgents));
        // The daemon still listing a3 and a4, whose completions it read past (#259), beside the woken a1.
        Assert.Equal("agent-ha-bridge#258, agent-ha-bridge#259", Known(Daemon(through, agents: ["c2", "c3", "c4"]), Host(agents: 2) with { RunningSubagents = ["a1", "a2"] }, ShadowKinds.BackgroundAgents));
        // A list that disagrees with the host's own count explains nothing.
        Assert.Null(Known(Daemon(through, agents: ["c2"]), Host(agents: 2) with { RunningSubagents = ["a1"] }, ShadowKinds.BackgroundAgents));

        // The same number on both sides is still compared agent by agent (on two samples in a row,
        // so a handoff between the reads is not): a woken a1 (#258) where the daemon keeps a3,
        // whose end it read past (#259), is two known bugs cancelling out...
        Assert.Equal("agent-ha-bridge#258, agent-ha-bridge#259", Known(Daemon(through, agents: ["c2", "c3"]), Host(agents: 2) with { RunningSubagents = ["a1", "a2"] }, ShadowKinds.BackgroundAgents, samples: 2));
        // ...and the host counting a3, which ended, in place of a2 is a mismatch.
        var other = Known(Daemon(through, agents: ["c2"]), Host(agents: 1) with { RunningSubagents = ["a3"] }, ShadowKinds.BackgroundAgents, samples: 2);
        Assert.Null(other);
        // The same agents on both sides are no difference at all.
        var (d, h) = (new ShadowView { Sessions = new Dictionary<string, ShadowSession> { [Id] = Daemon(through, agents: ["c2"]) } },
            new ShadowView { Sessions = new Dictionary<string, ShadowSession> { [Id] = Host(agents: 1) with { RunningSubagents = ["a2"] } }, SeesAgentHaBridge = true });
        Assert.Empty(new ShadowWitness(Paths).Explain(d, h, ShadowCompare.Compare(d, h).Differences));
    }

    // Found in review: a daemon a poll behind a handoff (one agent done, another started) lists the
    // old agent and the host the new one, the same count. That is no difference, or successive
    // handoffs, each caught for a sample, would join into one episode longer than the grace.
    [Fact]
    public void A_handoff_the_daemon_has_not_read_yet_is_no_difference_however_many_follow()
    {
        var witness = new ShadowWitness(Paths);
        IReadOnlyList<ShadowDifference> Explain(long offset, string[] daemonCalls, string[] hostAgents)
        {
            var (d, h) = (new ShadowView { Sessions = new Dictionary<string, ShadowSession> { [Id] = Daemon(offset, agents: daemonCalls) } },
                new ShadowView { Sessions = new Dictionary<string, ShadowSession> { [Id] = Host(agents: hostAgents.Length) with { RunningSubagents = hostAgents } }, SeesAgentHaBridge = true });
            return witness.Explain(d, h, ShadowCompare.Compare(d, h).Differences);
        }
        string[] Handoff(string from, string to) =>
        [
            Event("assistant.turn_end", new { }, agentId: "a" + from),
            Event("subagent.completed", new { toolCallId = "c" + from }),
            Event("subagent.started", new { toolCallId = "c" + to, executionMode = "background" }, agentId: "a" + to),
        ];

        var read = Write(Event("subagent.started", new { toolCallId = "c1", executionMode = "background" }, agentId: "a1"));
        Assert.Empty(Explain(read, ["c1"], ["a1"]));
        for (var n = 1; n < 4; n++)
        {
            Write(Handoff($"{n}", $"{n + 1}"));
            Assert.Empty(Explain(read, [$"c{n}"], [$"a{n + 1}"]));
            read = new FileInfo(Paths.CopilotTranscript(Id)!).Length;
        }
        // A failure is a handoff too: the host ends a failed agent's run there (found in review).
        Write(Event("subagent.failed", new { toolCallId = "c4" }, agentId: "a4"),
            Event("subagent.started", new { toolCallId = "c5", executionMode = "background" }, agentId: "a5"));
        Assert.Empty(Explain(read, ["c4"], ["a5"]));
        read = new FileInfo(Paths.CopilotTranscript(Id)!).Length;
        // Once the daemon has read the handoff, the same count of different agents is a mismatch,
        // reported once the same agents are apart on a second sample (a host behind for one is
        // the next test's).
        Assert.Empty(Explain(read, ["c4"], ["a5"]));
        Assert.Null(Assert.Single(Explain(read, ["c4"], ["a5"])).Known);
    }

    // One sample of a session's background agents, the host listing these agents and the daemon
    // these calls, read through offset.
    IReadOnlyList<ShadowDifference> Sample(ShadowWitness witness, long offset, string[] daemonCalls, string[] hostAgents)
    {
        var (d, h) = (new ShadowView { Sessions = new Dictionary<string, ShadowSession> { [Id] = Daemon(offset, agents: daemonCalls) } },
            new ShadowView { Sessions = new Dictionary<string, ShadowSession> { [Id] = Host(agents: hostAgents.Length) with { RunningSubagents = hostAgents } }, SeesAgentHaBridge = true });
        return witness.Explain(d, h, ShadowCompare.Compare(d, h).Differences);
    }

    long Handoff(int from) => Write(
        Event("assistant.turn_end", new { }, agentId: $"a{from}"),
        Event("subagent.completed", new { toolCallId = $"c{from}" }),
        Event("subagent.started", new { toolCallId = $"c{from + 1}", executionMode = "background" }, agentId: $"a{from + 1}"));

    // Found in review: the host is read before the daemon, so a handoff between the two reads leaves
    // the host listing the agent the daemon has seen end, for a sample. But a host always a handoff
    // behind (one applying an event a pass late) lists other agents every sample, and is a bug.
    [Fact]
    public void A_handoff_the_host_has_not_read_yet_is_no_difference_but_a_host_always_one_behind_is()
    {
        var witness = new ShadowWitness(Paths);
        var read = Write(Event("subagent.started", new { toolCallId = "c1", executionMode = "background" }, agentId: "a1"));
        Assert.Empty(Sample(witness, read, ["c1"], ["a1"]));
        // One read-order lag, then caught up: nothing.
        read = Handoff(1);
        Assert.Empty(Sample(witness, read, ["c2"], ["a1"]));
        Assert.Empty(Sample(witness, read, ["c2"], ["a2"]));

        // A handoff behind on every sample: reported from the third in a row, and on each after.
        for (var n = 2; n < 7; n++)
        {
            read = Handoff(n);
            var found = Sample(witness, read, [$"c{n + 1}"], [$"a{n}"]);
            if (n - 1 < ShadowWitness.ApartSamples) { Assert.Empty(found); }
            else { Assert.Null(Assert.Single(found).Known); }
        }
        Assert.Empty(Sample(witness, read, ["c7"], ["a7"]));
    }

    // Found in review: an agent stuck on one side is reported a sample later however the other side's
    // agents change meanwhile. The same two agents trading sides are a handoff each time, until they
    // have been apart as many samples in a row as any agents may be.
    [Fact]
    public void A_side_stuck_on_a_wrong_agent_is_reported_while_the_other_hands_off_and_agents_trading_sides_only_once_they_keep_on()
    {
        var stuck = new ShadowWitness(Paths);
        Write(Event("subagent.started", new { toolCallId = "c1", executionMode = "background" }, agentId: "a1"),
            Event("assistant.turn_end", new { }, agentId: "a1"),
            Event("subagent.completed", new { toolCallId = "c1" }));
        var read = Write(Event("subagent.started", new { toolCallId = "c2", executionMode = "background" }, agentId: "a2"));
        Assert.Empty(Sample(stuck, read, ["c2"], ["a1"]));
        read = Handoff(2);
        // The host still on a1, the daemon handed on to a3.
        Assert.Null(Assert.Single(Sample(stuck, read, ["c3"], ["a1"])).Known);

        var trading = new ShadowWitness(Paths);
        Assert.Empty(Sample(trading, read, ["c2"], ["a1"]));
        Assert.Empty(Sample(trading, read, ["c1"], ["a2"]));
        Assert.Null(Assert.Single(Sample(trading, read, ["c2"], ["a1"])).Known);
    }

    // Found in review: a sample whose counts differ is ShadowCompare's difference, and used to wipe
    // what was apart before it. A host stuck on a wrong agent, with other agents' starts a sample
    // late on one side or the other in between, then never had two samples in a row to show it.
    [Fact]
    public void A_stuck_agent_between_count_lags_of_other_agents_is_still_reported()
    {
        var witness = new ShadowWitness(Paths);
        Write(Event("subagent.started", new { toolCallId = "c1", executionMode = "background" }, agentId: "a1"),
            Event("assistant.turn_end", new { }, agentId: "a1"),
            Event("subagent.completed", new { toolCallId = "c1" }),
            Event("subagent.started", new { toolCallId = "c2", executionMode = "background" }, agentId: "a2"));
        var read = Write(Event("subagent.started", new { toolCallId = "c3", executionMode = "background" }, agentId: "a3"));
        // The host has not shown a3 yet: the counts differ.
        Assert.Contains(Sample(witness, read, ["c2", "c3"], ["a1"]), d => d.Kind == ShadowKinds.BackgroundAgents);
        // The same count, a1 in place of a2: one sample apart.
        Assert.Empty(Sample(witness, read, ["c2", "c3"], ["a1", "a3"]));
        // The daemon has not read a4 yet: the counts differ the other way.
        var before = read;
        read = Write(Event("subagent.started", new { toolCallId = "c4", executionMode = "background" }, agentId: "a4"));
        Assert.Contains(Sample(witness, before, ["c2", "c3"], ["a1", "a3", "a4"]), d => d.Kind == ShadowKinds.BackgroundAgents);
        // a1 is still where a2 should be: every sample now has a background.agents difference.
        var found = Assert.Single(Sample(witness, read, ["c2", "c3", "c4"], ["a1", "a3", "a4"]));
        Assert.Equal((ShadowKinds.BackgroundAgents, (string?)null), (found.Kind, found.Known));
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

    // #84: as the host now reads it, a woken agent's turn whose latest reply asks for nothing
    // more ends its run, even after a reply in the same turn asked for tools. The witness must
    // agree, or it would put an agent the host no longer counts down to the daemon.
    [Fact]
    public void A_woken_agents_turn_ends_its_run_when_its_latest_reply_asks_for_nothing_after_one_that_asked_for_tools()
    {
        Write(
            Event("subagent.started", new { toolCallId = "c1", executionMode = "background" }, agentId: "a1"),
            Event("assistant.turn_end", new { }, agentId: "a1"),
            Event("subagent.completed", new { toolCallId = "c1" }),
            Event("user.message", new { content = "carry on" }, agentId: "a1"),
            Event("assistant.turn_start", new { }, agentId: "a1"));
        var asked = Write(Event("assistant.message", new { content = "", toolRequests = new[] { new { name = "view" } } }, agentId: "a1"),
            Event("assistant.turn_end", new { }, agentId: "a1"));
        Assert.Equal("agent-ha-bridge#258", Known(Daemon(asked), Host(agents: 1), ShadowKinds.BackgroundAgents));

        var replied = Write(Event("assistant.turn_start", new { }, agentId: "a1"),
            Event("assistant.message", new { content = "", toolRequests = new[] { new { name = "view" } } }, agentId: "a1"),
            Event("assistant.message", new { content = "Done." }, agentId: "a1"),
            Event("assistant.turn_end", new { }, agentId: "a1"));
        Assert.Null(Known(Daemon(replied), Host(agents: 1), ShadowKinds.BackgroundAgents));
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

    // A Codex session's rollout, registered as agent-ha-bridge's Codex hook registers it.
    const string CodexId = "01a19aa3-e821-7952-bd5d-d080ec282632";

    string CodexRollout(params string[] lines)
    {
        var rollout = Path.Combine(f.CodexHome, "sessions", "2026", "11", "02", $"rollout-2026-11-02T05-03-26-{CodexId}.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(rollout)!);
        File.AppendAllText(rollout, string.Concat(lines.Select(l => l + "\n")));
        Directory.CreateDirectory(f.CodexRegistrations);
        File.WriteAllText(Path.Combine(f.CodexRegistrations, CodexId + ".json"), JsonSerializer.Serialize(new { SessionId = CodexId, TranscriptPath = rollout }));
        return rollout;
    }

    static string Compacted() => JsonSerializer.Serialize(new { timestamp = "2026-11-02T05:04:44Z", type = "compacted", payload = new { message = "summary" } });

    string? CompactionsKnown(int daemon, int host, string agent = "codex")
    {
        ShadowSession Side(int n) => new() { Id = CodexId, Agent = agent, Status = "idle", Compactions = n };
        var (d, h) = (new ShadowView { Sessions = new Dictionary<string, ShadowSession> { [CodexId] = Side(daemon) } },
            new ShadowView { Sessions = new Dictionary<string, ShadowSession> { [CodexId] = Side(host) }, SeesAgentHaBridge = true });
        var differences = new ShadowWitness(Paths).Explain(d, h, ShadowCompare.Compare(d, h).Differences);
        return Assert.Single(differences, x => x.Kind == ShadowKinds.Compactions).Known;
    }

    [Fact]
    public void A_codex_compaction_the_daemon_does_not_count_is_put_down_to_it_only_when_the_rollout_holds_exactly_the_hosts_count()
    {
        CodexRollout(
            JsonSerializer.Serialize(new { type = "session_meta", payload = new { id = CodexId } }),
            Compacted(),
            // The word in a message is no compaction.
            JsonSerializer.Serialize(new { type = "event_msg", payload = new { type = "agent_message", message = "\"compacted\"" } }),
            Compacted());

        Assert.Equal("agent-ha-bridge#279", CompactionsKnown(daemon: 0, host: 2));
        // More than the rollout records, a daemon that counts some, and another agent all stay mismatches.
        Assert.Null(CompactionsKnown(daemon: 0, host: 3));
        Assert.Null(CompactionsKnown(daemon: 1, host: 2));
        Assert.Null(CompactionsKnown(daemon: 0, host: 2, agent: "claude"));
        Assert.Null(CompactionsKnown(daemon: 0, host: 2, agent: "copilot"));
        // As does one whose rollout the daemon has no registration for.
        File.Delete(Path.Combine(f.CodexRegistrations, CodexId + ".json"));
        Assert.Null(CompactionsKnown(daemon: 0, host: 2));
    }

    [Fact]
    public void A_registration_naming_another_sessions_file_is_not_read()
    {
        var rollout = CodexRollout(Compacted());
        var other = Path.Combine(Path.GetDirectoryName(rollout)!, "rollout-2026-11-02T05-03-26-01a19aa3-0000-7952-bd5d-d080ec282632.jsonl");
        File.Copy(rollout, other);
        File.WriteAllText(Path.Combine(f.CodexRegistrations, CodexId + ".json"), JsonSerializer.Serialize(new { SessionId = CodexId, TranscriptPath = other }));
        Assert.Null(CompactionsKnown(daemon: 0, host: 1));
    }

    // Copilot sessions in the shape Copilot CLI 1.0.105 writes: workspace.yaml's title as `name:`,
    // with no `summary:` (agent-ha-bridge#278).
    void CopilotSession(string id, string yaml, string? prompt)
    {
        var dir = Path.Combine(f.CopilotHome, "session-state", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), yaml);
        File.WriteAllText(Path.Combine(dir, "events.jsonl"), prompt is null ? Event("session.start", new { }) + "\n" : Event("user.message", new { content = prompt }) + "\n");
    }

    const string Named = "11111111-1111-4111-8111-111111111111", Summarised = "22222222-2222-4222-8222-222222222222", Claude = "33333333-3333-4333-8333-333333333333";

    static ShadowResumeEntry Entry(string label, string id, string agent) => new(label, id, agent, agent, @"C:\r\repo", null);

    IReadOnlyList<ShadowDifference> Resume(IReadOnlyList<string> daemon, params ShadowResumeEntry[] host) => Resume(daemon, null, host);

    IReadOnlyList<ShadowDifference> Resume(IReadOnlyList<string> daemon, string? liveAgent, params ShadowResumeEntry[] host) => Resume(daemon, liveAgent, "live", host);

    IReadOnlyList<ShadowDifference> Resume(IReadOnlyList<string> daemon, string? liveAgent, string liveId, params ShadowResumeEntry[] host)
    {
        var d = new ShadowView { Resumable = [.. daemon.Select(ShadowHash.Normalize)] };
        var live = liveAgent is null ? new Dictionary<string, ShadowSession>() : new Dictionary<string, ShadowSession> { [liveId] = new() { Id = liveId, Agent = liveAgent, Status = "working" } };
        var h = new ShadowView { Sessions = live, Resumable = [.. host.Select(e => ShadowHash.Normalize(e.Label))], ResumableEntries = host, SeesAgentHaBridge = true };
        var differences = new ShadowWitness(Paths).Explain(d, h, ShadowCompare.Compare(d, h).Differences);
        return [.. differences.Where(x => x.Kind.StartsWith("resumable.", StringComparison.Ordinal))];
    }

    [Fact]
    public void A_resume_list_differing_only_by_the_name_the_daemon_does_not_read_is_put_down_to_it()
    {
        CopilotSession(Named, "cwd: C:\\r\\repo\nname: Fix The Widget\n", "fix the widget please");
        CopilotSession(Summarised, "cwd: C:\\r\\repo\nsummary: Tidy up\nname: Tidy Up Things\n", "tidy");
        ShadowResumeEntry[] host = [Entry("Copilot: Fix The Widget - repo", Named, "copilot"), Entry("Copilot: Tidy up - repo", Summarised, "copilot"), Entry("Claude: Hello - repo", Claude, "claude")];

        var differences = Resume(["Copilot: fix the widget please - repo", "Copilot: Tidy up - repo", "Claude: Hello - repo"], host);
        Assert.NotEmpty(differences);
        Assert.All(differences, x => Assert.Equal("agent-ha-bridge#278", x.Known));

        // Any other difference beside it keeps the whole list a mismatch: a session with a summary
        // labelled otherwise, one the daemon has and the host does not, or a prompt that is not the first.
        Assert.All(Resume(["Copilot: fix the widget please - repo", "Copilot: Tidy Up Things - repo", "Claude: Hello - repo"], host), x => Assert.Null(x.Known));
        Assert.All(Resume(["Copilot: fix the widget please - repo", "Copilot: Tidy up - repo", "Claude: Hello - repo", "Codex: More - repo"], host), x => Assert.Null(x.Known));
        Assert.All(Resume(["Copilot: something else - repo", "Copilot: Tidy up - repo", "Claude: Hello - repo"], host), x => Assert.Null(x.Known));
    }

    [Fact]
    public void A_session_the_daemon_leaves_out_for_want_of_a_summary_is_put_down_to_it_only_when_its_transcript_has_no_prompt()
    {
        CopilotSession(Named, "cwd: C:\\r\\repo\nname: Fix The Widget\n", prompt: null);
        ShadowResumeEntry[] host = [Entry("Copilot: Fix The Widget - repo", Named, "copilot"), Entry("Claude: Hello - repo", Claude, "claude")];

        // Its leaving makes the daemon's list one kind of session, which it does not name.
        Assert.All(Resume(["Hello - repo"], host), x => Assert.Equal("agent-ha-bridge#278", x.Known));
        Assert.All(Resume(["Claude: Hello - repo"], host), x => Assert.Null(x.Known));
        // Unless a live session of another kind could still have it named: that cannot be told here.
        Assert.All(Resume(["Hello - repo"], "copilot", host), x => Assert.Null(x.Known));
        Assert.All(Resume(["Claude: Hello - repo"], "copilot", host), x => Assert.Null(x.Known));

        // With a prompt to label it by, the daemon would have offered it: leaving it out is something else.
        CopilotSession(Named, "cwd: C:\\r\\repo\nname: Fix The Widget\n", "fix the widget please");
        Assert.All(Resume(["Claude: Hello - repo"], host), x => Assert.Null(x.Known));
        // And a session with no name is no case of it at all.
        CopilotSession(Named, "cwd: C:\\r\\repo\n", prompt: null);
        Assert.All(Resume(["Claude: Hello - repo"], host), x => Assert.Null(x.Known));
    }

    [Fact]
    public void A_label_told_apart_from_a_namesake_is_freed_only_when_the_namesake_is_in_the_list()
    {
        CopilotSession(Named, "cwd: C:\\r\\repo\nname: Fix The Widget\n", "fix the widget please");
        CopilotSession(Summarised, "cwd: C:\\r\\repo\nsummary: Fix The Widget\n", "widget");

        // Told apart from the named session, which the daemon labels by its prompt instead.
        Assert.All(Resume(["Copilot: fix the widget please - repo", "Copilot: Fix The Widget - repo", "Claude: Hello - repo"],
            Entry("Copilot: Fix The Widget - repo", Named, "copilot"), Entry("Copilot: Fix The Widget - repo (22222222)", Summarised, "copilot"), Entry("Claude: Hello - repo", Claude, "claude")),
            x => Assert.Equal("agent-ha-bridge#278", x.Known));
        // Told apart from a namesake that is not offered (a live session holds it): the daemon's
        // plain label is not shown to be right, so it stays a mismatch.
        Assert.All(Resume(["Copilot: fix the widget please - repo", "Copilot: Tidy up - repo", "Claude: Hello - repo"],
            Entry("Copilot: Fix The Widget - repo", Named, "copilot"), Entry("Copilot: Tidy up - repo (22222222)", Summarised, "copilot"), Entry("Claude: Hello - repo", Claude, "claude")),
            x => Assert.Null(x.Known));
    }

    [Fact]
    public void A_relabelled_title_a_live_session_could_hold_or_an_agency_label_is_not_put_down_to_it()
    {
        const string Live = "55555555-5555-4555-8555-555555555555";
        CopilotSession(Named, "cwd: C:\\r\\repo\nname: Fix The Widget\n", "fix the widget please");
        ShadowResumeEntry[] host = [Entry("Copilot: Fix The Widget - repo", Named, "copilot"), Entry("Claude: Hello - repo", Claude, "claude")];
        string[] daemon = ["Copilot: fix the widget please - repo", "Claude: Hello - repo"];

        // A live session elsewhere, or of another agent in a list that names agents, holds no such title.
        CopilotSession(Live, "cwd: C:\\r\\repo\nname: Other\n", "something else");
        Assert.All(Resume(daemon, "copilot", Live, host), x => Assert.Equal("agent-ha-bridge#278", x.Known));
        Assert.All(Resume(daemon, "claude", host), x => Assert.Equal("agent-ha-bridge#278", x.Known));
        // One begun with the same prompt in the same folder would have the daemon tell the two apart.
        CopilotSession(Live, "cwd: C:\\r\\repo\nname: Other\n", "fix the widget please");
        Assert.All(Resume(daemon, "copilot", Live, host), x => Assert.Null(x.Known));

        // Agency's entries are Copilot sessions too, but their labels come from Agency, not workspace.yaml.
        Assert.All(Resume(daemon, new ShadowResumeEntry("Copilot: Fix The Widget - repo", Named, "copilot", "agency", @"C:\r\repo", null), Entry("Claude: Hello - repo", Claude, "claude")),
            x => Assert.Null(x.Known));

        // A title freed of its suffix is held by a live session just the same.
        CopilotSession(Summarised, "cwd: C:\\r\\repo\nsummary: Fix The Widget\n", "widget");
        CopilotSession(Live, "cwd: C:\\r\\repo\nname: Other\n", "Fix The Widget");
        Assert.All(Resume(["Copilot: fix the widget please - repo", "Copilot: Fix The Widget - repo", "Claude: Hello - repo"], "copilot", Live,
            Entry("Copilot: Fix The Widget - repo", Named, "copilot"), Entry("Copilot: Fix The Widget - repo (22222222)", Summarised, "copilot"), Entry("Claude: Hello - repo", Claude, "claude")),
            x => Assert.Null(x.Known));
    }

    [Fact]
    public void A_transcript_that_cannot_be_read_is_no_evidence_the_daemon_found_no_prompt()
    {
        CopilotSession(Named, "cwd: C:\\r\\repo\nname: Fix The Widget\n", "fix the widget please");
        ShadowResumeEntry[] host = [Entry("Copilot: Fix The Widget - repo", Named, "copilot"), Entry("Claude: Hello - repo", Claude, "claude")];
        using (new FileStream(Path.Combine(f.CopilotHome, "session-state", Named, "events.jsonl"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.All(Resume(["Hello - repo"], host), x => Assert.Null(x.Known));
        }
        Assert.All(Resume(["Copilot: fix the widget please - repo", "Claude: Hello - repo"], host), x => Assert.Equal("agent-ha-bridge#278", x.Known));
    }

    [Fact]
    public void A_rollout_replaced_at_the_same_length_is_counted_again()
    {
        var rollout = CodexRollout(Compacted(), JsonSerializer.Serialize(new { type = "event_msg", payload = new { type = "x" } }));
        var count = new RolloutCompactions(rollout);
        Assert.Equal(1, count.Count());
        var length = new FileInfo(rollout).Length;
        var filler = JsonSerializer.Serialize(new { type = "event_msg" });
        File.WriteAllText(rollout, filler + new string(' ', (int)length - filler.Length - 1) + "\n");
        Assert.Equal(length, new FileInfo(rollout).Length);
        Assert.Equal(0, count.Count());
    }

    // Claude sessions as agent-ha-bridge's Claude hook registers them (agent-ha-bridge#289, #290).
    const string Ended = "ccf05ae6-ac74-4e3c-adff-acac38854bbd", Running = "f06fdc33-5899-4111-b9f9-8f5d5540959b";

    void ClaudeRegistration(string id, int pid, string updated, string? transcript = null)
    {
        var dir = Path.Combine(Paths.Runtime, "agent-bridge-claude");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"), JsonSerializer.Serialize(new { SessionId = id, ProcessId = pid, TranscriptPath = transcript ?? "", WorkingDirectory = @"C:\r", Updated = updated, HookStatus = "idle" }));
    }

    string? GhostKnown(bool hostHasRunning = true, string agent = "claude", string runningOnDaemon = "claude")
    {
        ShadowSession S(string id, string status) => new() { Id = id, Agent = agent == "claude" || id == Running ? "claude" : agent, Status = status };
        var d = new ShadowView { Sessions = new Dictionary<string, ShadowSession> { [Ended] = S(Ended, "idle") with { Agent = agent }, [Running] = S(Running, "working") with { Agent = runningOnDaemon } } };
        var h = new ShadowView { Sessions = hostHasRunning ? new Dictionary<string, ShadowSession> { [Running] = S(Running, "working") } : [], SeesAgentHaBridge = true };
        var differences = new ShadowWitness(Paths).Explain(d, h, ShadowCompare.Compare(d, h).Differences);
        return Assert.Single(differences, x => x.Kind == ShadowKinds.MissingOnHost && x.Session == ShadowHash.Session(Ended)).Known;
    }

    [Fact]
    public void An_ended_Claude_session_back_on_the_daemon_is_put_down_to_a_reused_pid_only_when_a_newer_live_registration_has_its_pid()
    {
        ClaudeRegistration(Ended, 2988, "2026-11-02T10:43:30Z");
        ClaudeRegistration(Running, 2988, "2026-11-02T14:49:50Z");
        Assert.Equal("agent-ha-bridge#289", GhostKnown());

        // Not when the host does not have the newer session either, nor for another agent.
        Assert.Null(GhostKnown(hostHasRunning: false));
        Assert.Null(GhostKnown(agent: "codex"));
        // Nor when the daemon does not have the newer session as Claude.
        Assert.Null(GhostKnown(runningOnDaemon: "copilot"));
        // Not when the pids differ, or the one with that pid is the older registration.
        ClaudeRegistration(Running, 2989, "2026-11-02T14:49:50Z");
        Assert.Null(GhostKnown());
        ClaudeRegistration(Running, 2988, "2026-11-02T09:00:00Z");
        Assert.Null(GhostKnown());
        // Nor when the missing session has no registration of its own.
        ClaudeRegistration(Running, 2988, "2026-11-02T14:49:50Z");
        File.Delete(Path.Combine(Paths.Runtime, "agent-bridge-claude", Ended + ".json"));
        Assert.Null(GhostKnown());
    }

    const string Call = "toolu_01U43bXYZ";

    string ClaudeTranscript(params object[] lines)
    {
        var path = Path.Combine(f.Root, "claude-projects", Running + ".jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Concat(lines.Select(l => JsonSerializer.Serialize(l) + "\n")));
        ClaudeRegistration(Running, 11388, "2026-11-02T16:16:15Z", path);
        return path;
    }

    static object Assistant(object[] content, string stop, string model = "claude-haiku-5-5") => new { type = "assistant", message = new { model, content, stop_reason = stop } };
    static object AskCall => Assistant([new { type = "tool_use", id = Call, name = "AskUserQuestion" }], "tool_use");
    static object Answer => new { type = "user", message = new { content = new object[] { new { type = "tool_result", tool_use_id = Call, content = "gamma" } } } };
    static object Sidechain(object entry)
    {
        var o = JsonSerializer.SerializeToNode(entry)!.AsObject();
        o["isSidechain"] = true;
        return o;
    }
    static object EndTurn => Assistant([new { type = "text", text = "ok" }], "end_turn");

    IReadOnlyList<ShadowDifference> HeldAnswer(string daemonModel = "haiku", string agent = "claude", string? call = Call)
    {
        var d = new ShadowView { Sessions = new Dictionary<string, ShadowSession> { [Running] = new() { Id = Running, Agent = agent, Status = "working", Model = daemonModel, Question = new ShadowQuestion { CallId = call } } } };
        var h = new ShadowView { Sessions = new Dictionary<string, ShadowSession> { [Running] = new() { Id = Running, Agent = agent, Status = "idle", Model = "claude-haiku-5-5" } }, SeesAgentHaBridge = true };
        return new ShadowWitness(Paths).Explain(d, h, ShadowCompare.Compare(d, h).Differences);
    }

    static string? KnownOf(IReadOnlyList<ShadowDifference> differences, string kind) => Assert.Single(differences, x => x.Kind == kind).Known;

    [Fact]
    public void A_Claude_answer_the_daemon_still_holds_is_put_down_to_it_only_once_the_transcript_has_the_answer_and_a_finished_turn()
    {
        ClaudeTranscript(AskCall, Answer, EndTurn);
        var held = HeldAnswer();
        Assert.Equal("agent-ha-bridge#290", KnownOf(held, ShadowKinds.Status));
        Assert.Equal("agent-ha-bridge#290", KnownOf(held, ShadowKinds.Model));

        // Still asking: the waiting is right, so the status stays a mismatch; the alias is the same either way.
        ClaudeTranscript(AskCall);
        Assert.Null(KnownOf(HeldAnswer(), ShadowKinds.Status));
        Assert.Equal("agent-ha-bridge#290", KnownOf(HeldAnswer(), ShadowKinds.Model));
        // Answered, but the turn is still going.
        ClaudeTranscript(AskCall, Answer);
        Assert.Null(KnownOf(HeldAnswer(), ShadowKinds.Status));

        ClaudeTranscript(AskCall, Answer, EndTurn);
        // Not a model the alias names, another agent, or a question the transcript never asked.
        Assert.Null(KnownOf(HeldAnswer(daemonModel: "claude-haiku-4-5"), ShadowKinds.Model));
        Assert.Null(KnownOf(HeldAnswer(daemonModel: "sonnet"), ShadowKinds.Model));
        Assert.All(HeldAnswer(agent: "codex").Where(x => x.Kind is ShadowKinds.Status or ShadowKinds.Model), x => Assert.Null(x.Known));
        Assert.All(HeldAnswer(call: "toolu_other").Where(x => x.Kind is ShadowKinds.Status or ShadowKinds.Model), x => Assert.Null(x.Known));
        // A sub-agent's (sidechain) finished turn is not the session's, nor is its model.
        ClaudeTranscript(AskCall, Answer, Sidechain(Assistant([new { type = "text", text = "sub" }], "end_turn", "claude-sonnet-5")));
        Assert.Null(KnownOf(HeldAnswer(), ShadowKinds.Status));
        Assert.Equal("agent-ha-bridge#290", KnownOf(HeldAnswer(), ShadowKinds.Model));
        // Nor when the transcript's latest model is not the host's.
        ClaudeTranscript(AskCall, Answer, Assistant([new { type = "text", text = "ok" }], "end_turn", "claude-sonnet-5"));
        Assert.Null(KnownOf(HeldAnswer(), ShadowKinds.Model));
    }
}