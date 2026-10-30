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
        // Nor is one the transcript never started.
        Assert.Null(Known(Daemon(through, shells: ["own1", "sub1", "mystery"]), Host(shells: 1), ShadowKinds.BackgroundShells));
        // An end the daemon has not read yet is the daemon being behind, not #259.
        Assert.Null(Known(Daemon(beforeEnd, shells: ["own1", "ended1"]), Host(shells: 1), ShadowKinds.BackgroundShells));
    }

    [Fact]
    public void Missing_agents_are_put_down_to_the_daemon_only_when_they_are_exactly_those_woken_again_after_completing()
    {
        Write(
            Event("subagent.started", new { toolCallId = "c1", executionMode = "background" }, agentId: "a1"),
            Event("assistant.turn_end", new { }, agentId: "a1"),
            Event("subagent.completed", new { toolCallId = "c1" }),
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
