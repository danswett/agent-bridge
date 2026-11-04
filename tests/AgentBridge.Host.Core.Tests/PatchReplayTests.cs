using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;
using Xunit;
using Xunit.Abstractions;

// session.patch over the recorded sessions (tests/fixtures): each stepped through its recording
// every 250 ms, the host's coalescing interval, as a subscriber would be sent it. A patch applied to
// what the client holds must give exactly the session an upsert of that moment would. The same
// sequences are the shared vectors' session-patches.json, which the PWA replays.
public class PatchReplayTests(ITestOutputHelper output)
{
    const double Step = 0.25;
    const string File = "session-patches.json";
    const string Generator = "tests/AgentBridge.Host.Core.Tests/PatchReplayTests.cs";

    // The list form a subscriber is sent at each step where it changed.
    static async Task<List<Session>> Steps(Func<double, Task> through, Func<Session> listForm, double from, double to)
    {
        var steps = new List<Session>();
        string? last = null;
        for (var at = from; at <= to; at += Step)
        {
            await through(at);
            var s = listForm();
            var json = JsonSerializer.Serialize(s, ProtocolJson.Options);
            if (json == last) { continue; }
            steps.Add(s);
            last = json;
        }
        return steps;
    }

    static async Task<IReadOnlyList<(string Name, string Recording, List<Session> Steps)>> ReplayAsync()
    {
        var all = new List<(string, string, List<Session>)>();
        await using (var copilot = new SubagentTests.Recorded())
        {
            all.Add(("copilot-delegates", "tests/fixtures/copilot/subagents.jsonl",
                await Steps(s => copilot.Through(s), () => Assert.Single(copilot.Source.Snapshot()), 0, 102)));
        }
        await using (var claude = new ClaudeSubagentTests.Replay())
        {
            all.Add(("claude-delegates", "tests/fixtures/claude/0e0fb277-3adb-4aaa-9031-9ef818be1a11.jsonl",
                await Steps(s => claude.Through(s), () => Assert.Single(claude.Source.Snapshot()), 0, 124)));
        }
        await using (var codex = new CodexSubagentTests.Replay())
        {
            all.Add(("codex-sub-agent", "tests/fixtures/codex/rollout-2026-10-28T03-59-56-019a2b3c-f474-71f7-85bc-1e9f57b11ff5.jsonl",
                await Steps(s => codex.Through(s), () => Assert.Single(codex.Source.Snapshot()), -4, 77)));
        }
        return all;
    }

    // Replayed once for the class: each replay reads its recording a quarter-second at a time.
    static readonly Lazy<Task<IReadOnlyList<(string Name, string Recording, List<Session> Steps)>>> Recorded = new(ReplayAsync);

    // What an event costs on the channel before encryption: its envelope and its data, as sent.
    static int EventBytes(string name, object data) => ProtocolJson.ByteCount(new { ev = name, seq = 1000, data });

    // Each recording as a subscriber that asked for patches is sent it: its first state as the
    // snapshot's upsert at seq 1, then each change as the host's feed sends it, with the session the
    // client must then hold.
    static async Task<JsonObject> Generate()
    {
        var sequences = new JsonArray();
        foreach (var (name, recording, steps) in await Recorded.Value)
        {
            var events = new JsonArray();
            JsonObject? held = null;
            long seq = 0;
            foreach (var session in steps)
            {
                var json = SessionPatches.Json(session);
                seq++;
                if (held is null) { events.Add(new JsonObject { ["seq"] = seq, ["upsert"] = json.DeepClone() }); }
                else
                {
                    var patch = SessionPatches.Next(held, seq - 1, json, revision: seq)!;
                    events.Add(new JsonObject { ["seq"] = seq, ["patch"] = JsonSerializer.SerializeToNode(patch, ProtocolJson.Options), ["session"] = json.DeepClone() });
                }
                held = json;
            }
            sequences.Add(new JsonObject { ["name"] = name, ["recording"] = recording, ["events"] = events });
        }
        return new JsonObject
        {
            ["about"] = "Recorded sessions as a subscriber that asked for patches is sent them, a change every 250 ms at most: the snapshot's upsert, then session.patch events, each with the session the client then holds.",
            ["spec"] = "v1-sessions.md#patches-sessionpatch",
            ["generatedBy"] = Generator,
            ["sequences"] = sequences,
        };
    }

    [Fact]
    public async Task The_committed_patch_sequences_are_the_ones_the_recordings_give()
    {
        var stale = VectorFiles.Stale(new Dictionary<string, JsonObject> { [File] = await Generate() });
        Assert.True(stale.Count == 0, $"docs/protocol/vectors/{File} differs from what {Generator} generates. If the change is meant, regenerate it ({VectorFiles.UpdateVariable}=1, docs/protocol/vectors/README.md) and commit it with it.");
    }

    [Fact]
    public async Task Patches_over_each_recorded_session_give_exactly_the_session_each_upsert_would_for_a_fraction_of_the_bytes()
    {
        foreach (var (name, _, steps) in await Recorded.Value)
        {
            Assert.True(steps.Count > 10, $"{name} changed only {steps.Count} times");
            JsonObject? held = null;
            long seq = 0;
            long upserts = 0, patches = 0;
            var based = 0;
            foreach (var session in steps)
            {
                var json = SessionPatches.Json(session);
                var patch = SessionPatches.Next(held, seq, json, revision: seq + 1)!;
                // On the wire, read as a client reads a host's record.
                var read = Received.Read<SessionPatch>(JsonSerializer.SerializeToElement(patch, ProtocolJson.Options));
                var applied = SessionPatches.Apply(held, held is null ? null : seq, read);
                Assert.True(JsonNode.DeepEquals(json, applied), $"{name}: {applied.ToJsonString()}");
                Assert.Equal(JsonSerializer.Serialize(session, ProtocolJson.Options),
                    JsonSerializer.Serialize(Received.Read<Session>(JsonSerializer.SerializeToElement(applied)), ProtocolJson.Options));
                if (patch.Base is not null) { based++; }
                upserts += EventBytes(Events.SessionUpsert, session);
                patches += EventBytes(Events.SessionPatch, patch);
                (held, seq) = (applied, seq + 1);
            }
            output.WriteLine($"{name}: {steps.Count} upserts, {upserts:N0} bytes whole, {patches:N0} bytes as patches ({100.0 * patches / upserts:F1}%), {based} on the last");
            Assert.True(based >= steps.Count / 2, $"{name}: only {based} of {steps.Count} were patches");
            Assert.True(patches * 2 < upserts, $"{name}: {patches} bytes of patches for {upserts} of upserts");
        }
    }

    [Fact]
    public async Task Patches_between_states_further_apart_than_one_step_apply_as_well()
    {
        // A client sent fewer changes than these steps (a busier host coalesces more), so a patch
        // spans several of them.
        foreach (var (name, _, steps) in await Recorded.Value)
        {
            foreach (var stride in new[] { 2, 7 })
            {
                for (var i = 0; i + stride < steps.Count; i += stride)
                {
                    var start = SessionPatches.Json(steps[i]);
                    var end = SessionPatches.Json(steps[i + stride]);
                    var patch = SessionPatches.Diff(start, 9, end);
                    var applied = patch is null ? start : SessionPatches.Apply(start, 9, patch);
                    Assert.True(JsonNode.DeepEquals(end, applied), $"{name} {i}+{stride}: {applied.ToJsonString()}");
                }
            }
        }
    }

    // The audit's idle session (#597): 21.7 KB as an upsert, 16.6 KB of it twenty sub-agents and
    // 4 KB of reasoning, sent again whole when one sub-agent moved to its next tool.
    static Session Idle(int moved, int times)
    {
        var at = Fixture.T0.AddSeconds(10 * times);
        var subagents = Enumerable.Range(0, 20).Select(i => new Subagent
        {
            Id = $"{i:x8}-1111-4111-8111-111111111111", Kind = SubagentKind.Background, Name = $"Check part {i} of the suite", Model = "claude-haiku-5.5",
            Task = string.Concat(Enumerable.Repeat($"Run part {i} of the suite and report each failure with its log. ", 5)),
            Status = SubagentStatus.Working, StartedAt = Fixture.T0, Shells = i % 2,
            Line = string.Concat(Enumerable.Repeat("Reading the failures before retrying them. ", 3)),
            CurrentTool = new ToolCall
            {
                Id = $"toolu_{i:D2}{(i == moved ? times : 0):D4}", Name = "powershell", Status = ToolStatus.Running,
                Summary = $"dotnet test --filter Part{i} (attempt {(i == moved ? times : 0)})", StartedAt = i == moved ? at : Fixture.T0,
            },
        });
        return new Session
        {
            Id = "02d9e5d5-6b35-49bd-b083-39572fbff648", Agent = AgentKind.Copilot, Title = "Run the whole suite in parts", Cwd = "/work/repo",
            Repository = "octo/repo", Branch = "main", Model = "claude-opus-5.5", Status = SessionStatus.Idle, StatusSince = Fixture.T0,
            StartedAt = Fixture.T0, UpdatedAt = at, ProcessId = 4120, Subagents = [.. subagents],
            Activity = new Activity
            {
                Response = string.Concat(Enumerable.Repeat("Started twenty delegates, one for each part of the suite. ", 12)),
                Reasoning = string.Concat(Enumerable.Repeat("Split the suite so each part finishes in a few minutes. ", 72)),
                UserMessage = "Run the whole suite in parts and tell me what fails.", TurnCount = 1, Background = Background.Of(20, 0),
            },
        };
    }

    // What one subscriber is sent for a list of each session's changes, whole and as patches.
    static (long Whole, long Patched) Cost(IEnumerable<IReadOnlyList<Session>> sessions)
    {
        long whole = 0, patched = 0;
        foreach (var changes in sessions)
        {
            JsonObject? held = null;
            foreach (var s in changes)
            {
                var json = SessionPatches.Json(s);
                whole += EventBytes(Events.SessionUpsert, s);
                // The first is the snapshot's upsert, whole either way.
                patched += held is null ? EventBytes(Events.SessionUpsert, s) : EventBytes(Events.SessionPatch, SessionPatches.Next(held, 1, json, 1)!);
                held = json;
            }
        }
        return (whole, patched);
    }

    [Fact]
    public async Task A_window_like_the_audits_forty_seconds_costs_a_subscriber_a_fraction_of_its_bytes()
    {
        // Four sessions as on 2026-11-03: the idle one upserted three times, and the first 40 s of
        // each recording, at work. Each session's snapshot upsert counts too, as it did then.
        var recorded = (await Recorded.Value).Select(r => (IReadOnlyList<Session>)[.. r.Steps.Where(s => s.UpdatedAt < r.Steps[0].UpdatedAt.AddSeconds(40))]).ToList();
        IReadOnlyList<Session> idle = [Idle(-1, 0), Idle(3, 1), Idle(11, 2), Idle(7, 3)];
        output.WriteLine($"the idle session: {ProtocolJson.ByteCount(idle[0]):N0} bytes, {ProtocolJson.ByteCount(idle[0].Subagents):N0} of them sub-agents");

        var (liveWhole, livePatched) = Cost([.. recorded.Select(r => r.Skip(1).Prepend(r[0]).ToList()), idle]);
        var snapshot = recorded.Sum(r => EventBytes(Events.SessionUpsert, r[0])) + EventBytes(Events.SessionUpsert, idle[0]);
        output.WriteLine($"40 s, 4 sessions, a {snapshot:N0}-byte snapshot and {recorded.Sum(r => r.Count) + idle.Count - 4} changes: {liveWhole:N0} bytes with whole upserts, {livePatched:N0} with patches; the changes alone {liveWhole - snapshot:N0} and {livePatched - snapshot:N0} ({100.0 * (livePatched - snapshot) / (liveWhole - snapshot):F1}%)");
        Assert.True((livePatched - snapshot) * 5 < liveWhole - snapshot);

        // Back after a drop in which one session changed: the whole snapshot again, or that one
        // session and the ids of the four.
        var latest = recorded.Select(r => r[^1]).Append(idle[^1]).ToList();
        var whole = latest.Sum(s => EventBytes(Events.SessionUpsert, s)) + EventBytes(Events.SessionsSynced, new SessionsSynced(131));
        var resumed = EventBytes(Events.SessionUpsert, latest[0]) + EventBytes(Events.SessionsSynced, new SessionsSynced(131, Live: [.. latest.Select(s => s.Id)]));
        output.WriteLine($"a reconnect: {whole:N0} bytes of snapshot, {resumed:N0} resumed ({100.0 * resumed / whole:F1}%)");
        Assert.True(resumed * 2 < whole);
    }}
