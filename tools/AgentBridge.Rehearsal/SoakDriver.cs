using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;

namespace AgentBridge.Rehearsal;

// What the driver needs of Home Assistant: a state, a service call, a publish. A disposable
// instance only (HaCheck.Refusal), and every call through the connector's own client, so the
// one-entity guard (ServiceTarget) applies to every press.
internal interface ISoakHa
{
    Task<HaState?> StateAsync(string entityId, CancellationToken ct);
    Task CallAsync(string domain, string service, JsonObject data, CancellationToken ct);
    Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct);
}

internal sealed class SoakHa(HomeAssistantClient client) : ISoakHa
{
    public Task<HaState?> StateAsync(string entityId, CancellationToken ct) => client.GetStateAsync(entityId, ct);
    public Task CallAsync(string domain, string service, JsonObject data, CancellationToken ct) => client.CallServiceAsync(domain, service, data, ct);
    public Task PublishAsync(string topic, string payload, bool retain, CancellationToken ct) => client.PublishAsync(topic, payload, retain, ct);
}

internal sealed record SoakOptions(
    string Slug,
    string Workspace,
    IReadOnlyDictionary<string, string> Models,
    IReadOnlyDictionary<string, string> Efforts,
    TimeSpan Every,
    TimeSpan Hold,
    TimeSpan TurnTimeout,
    int Count,
    int? RestartDaemonAt,
    int? RestartShadowAt,
    string? Only);

// One episode's record in soak.json, so a driver restarted carries on where it stopped and the
// report can say what was done.
internal sealed class SoakRecord
{
    public int Index { get; set; }
    public string Scenario { get; set; } = "";
    public string Agent { get; set; } = "";
    public string? Session { get; set; }
    public string? Node { get; set; }
    public DateTimeOffset Started { get; set; }
    public DateTimeOffset? Ended { get; set; }
    public int Turns { get; set; }
    public int Questions { get; set; }
    public int Answered { get; set; }
    public int Abandoned { get; set; }
    public int Compactions { get; set; }
    public string? ResumedFrom { get; set; }
    public List<string> Problems { get; set; } = [];
}

internal sealed class SoakState
{
    public DateTimeOffset Began { get; set; }
    public List<SoakRecord> Episodes { get; set; } = [];
    public bool DaemonRestarted { get; set; }
    public bool ShadowRestarted { get; set; }
    public string? Stopped { get; set; }
}

internal sealed class SoakDriver(ISoakHa ha, SoakOptions options, string stateDir, Homes homes, Func<DateTimeOffset>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    readonly Func<DateTimeOffset> now = clock ?? (() => DateTimeOffset.UtcNow);
    readonly Func<TimeSpan, CancellationToken, Task> wait = delay ?? Task.Delay;
    string StatePath => Path.Combine(stateDir, "soak.json");
    string LogPath => Path.Combine(stateDir, "soak.log");
    string M(string domain, string key) => $"{domain}.agent_bridge_{options.Slug}_{key}";

    public static SoakState Load(string dir)
    {
        var path = Path.Combine(dir, "soak.json");
        return File.Exists(path) ? JsonSerializer.Deserialize<SoakState>(File.ReadAllText(path), Json) ?? new() : new();
    }

    void Save(SoakState state)
    {
        Directory.CreateDirectory(stateDir);
        var tmp = StatePath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, Json));
        File.Move(tmp, StatePath, overwrite: true);
    }

    void Log(string line)
    {
        Directory.CreateDirectory(stateDir);
        var text = $"{now():yyyy-MM-ddTHH:mm:ssZ} {line}";
        Console.WriteLine(text);
        File.AppendAllText(LogPath, text + Environment.NewLine);
    }

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var state = Load(stateDir);
        if (state.Began == default) { state.Began = now(); }
        var plan = SoakScript.Plan(options.Count);
        // After the last episode recorded, not after as many as were recorded: --only skips some.
        var next = state.Episodes.Count == 0 ? 0 : state.Episodes.Max(e => e.Index) + 1;
        Log($"soak: {plan.Count} episode(s), one every {options.Every.TotalMinutes:0} min, from episode {next}");
        // An episode a stopped driver left under way: its session is ended, not left running.
        if (state.Episodes.LastOrDefault() is { Ended: null } interrupted)
        {
            interrupted.Problems.Add("the driver stopped during it");
            if (interrupted.Node is { } left) { await EndAsync(left, interrupted, ct); }
            interrupted.Ended = now();
            Save(state);
        }
        for (var i = next; i < plan.Count && !ct.IsCancellationRequested; i++)
        {
            var episode = plan[i];
            if (options.Only is { } only && episode.Scenario.Name != only) { continue; }
            var due = state.Began + options.Every * i;
            // After a stop, the overdue ones are spread out again rather than run back to back.
            if (state.Episodes.LastOrDefault()?.Ended is { } last && last + options.Every / 4 > due) { due = last + options.Every / 4; }
            if (due > now()) { Log($"waiting until {due:HH:mm:ss}Z for episode {i}"); await wait(due - now(), ct); }
            if (i >= options.RestartDaemonAt && !state.DaemonRestarted)
            {
                state.DaemonRestarted = await RestartAsync("agent-ha-bridge's daemon (agent-ha-bridge restart)", $"& \"{Path.Combine(homes.BridgeHome, "bin", "agent-ha-bridge.ps1")}\" restart", ct);
                Save(state);
                await wait(TimeSpan.FromSeconds(90), ct);
            }
            if (i >= options.RestartShadowAt && !state.ShadowRestarted)
            {
                state.ShadowRestarted = await RestartAsync("the shadow host (its logon task)", ShadowRestartScript, ct);
                Save(state);
                await wait(TimeSpan.FromSeconds(60), ct);
            }
            var record = new SoakRecord { Index = i, Scenario = episode.Scenario.Name, Agent = episode.Scenario.Agent, Started = now() };
            state.Episodes.Add(record);
            Save(state);
            try { await EpisodeAsync(episode, record, state, ct); }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                record.Problems.Add($"episode failed: {e.GetType().Name}: {e.Message}");
                Log($"episode {i} failed: {e.Message}");
                if (record.Node is { } node) { await EndAsync(node, record, ct); }
            }
            record.Ended = now();
            Save(state);
            Log($"episode {i} {episode.Scenario.Name}: {record.Turns} turn(s), {record.Questions} question(s), {record.Answered} answered, {record.Abandoned} abandoned{(record.Problems.Count > 0 ? "; " + string.Join("; ", record.Problems) : "")}");
        }
        return 0;
    }

    // The serve task of profile shadow, and only the host it started: the comparison (the run
    // task, from the same folder) keeps running, so it sees the host go and come back.
    const string ShadowRestartScript =
        "$t = Get-ScheduledTask | Where-Object TaskName -like 'AgentBridgeShadowHost_shadow_*'; if (-not $t) { throw 'no shadow host task' }; " +
        "Stop-ScheduledTask -TaskName $t.TaskName; " +
        "Get-CimInstance Win32_Process -Filter \"Name='agent-bridge-host.exe'\" | Where-Object { $_.ExecutablePath -like \"$env:LOCALAPPDATA\\AgentBridgeShadow\\bin\\*\" -and $_.CommandLine -match ' serve ' } | ForEach-Object { Stop-Process -Id $_.ProcessId -Force }; " +
        "Start-Sleep 5; Start-ScheduledTask -TaskName $t.TaskName; 'restarted ' + $t.TaskName";

    // True once done. A refused or failed restart is logged and not counted, so the next
    // episode tries it again rather than the soak going without it.
    async Task<bool> RestartAsync(string what, string command, CancellationToken ct)
    {
        Log($"restarting {what}");
        try
        {
            Log(await RunPwshAsync(command, ct));
            return true;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            Log($"restarting {what} failed: {e.Message}");
            return false;
        }
    }

    static async Task<string> RunPwshAsync(string command, CancellationToken ct)
    {
        var psi = new ProcessStartInfo("pwsh", ["-NoProfile", "-NonInteractive", "-Command", command]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("pwsh did not start");
        var output = await p.StandardOutput.ReadToEndAsync(ct) + await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        // A refused or failed restart is not counted as done (RestartAsync).
        if (p.ExitCode != 0) { throw new InvalidOperationException($"exit {p.ExitCode}: {output.Trim()}"); }
        return output.Trim();
    }

    async Task EpisodeAsync(SoakEpisode episode, SoakRecord record, SoakState state, CancellationToken ct)
    {
        var s = episode.Scenario;
        string? resume = null;
        if (s.Resume)
        {
            var from = state.Episodes.LastOrDefault(e => e.Agent == s.Agent && e.Index < record.Index && e.Session is not null && e.Ended is not null && e.Problems.Count == 0);
            if (from is null) { record.Problems.Add("nothing of this agent to resume yet"); return; }
            resume = from.Session;
            record.ResumedFrom = from.Session;
        }
        var turns = s.Turns;
        var first = SoakScript.Prompt(turns[0], episode);
        var started = await LaunchAsync(s, first, resume, ct);
        if (started is null) { record.Problems.Add("the launch did not start"); return; }
        record.Node = started;
        record.Session = FullId(started) ?? resume;
        Log($"episode {record.Index} {s.Name}: started {s.Agent} {started}{(resume is null ? "" : " resuming " + resume)}");
        Save(state);
        // The launch's prompt is the first turn: its turn is already under way.
        var handled = new HashSet<string>(StringComparer.Ordinal);
        var ended = false;
        for (var t = 0; t < turns.Count && !ended; t++)
        {
            var turn = turns[t];
            string? marker = null;
            if (t > 0)
            {
                marker = await MarkerAsync(started, ct);
                await SendAsync(started, SoakScript.Prompt(turn, episode), ct);
            }
            record.Turns++;
            if (turn.Compact) { record.Compactions++; }
            ended = await TurnAsync(started, turn, marker, record, handled, ct, freshLaunch: resume is null);
            record.Session ??= FullId(started);
            Save(state);
        }
        if (!ended) { await EndAsync(started, record, ct); }
    }

    async Task<string?> LaunchAsync(SoakScenario s, string prompt, string? resume, CancellationToken ct)
    {
        var settings = new JsonObject
        {
            ["agent"] = s.Agent,
            ["permissions"] = s.AskPermission ? "Ask permission" : "Allow all",
        };
        if (options.Models.TryGetValue(s.Agent, out var model)) { settings["model"] = model; }
        if (options.Efforts.TryGetValue(s.Agent, out var effort)) { settings["effort"] = effort; }
        // A resumed session reopens in its own folder: agent-ha-bridge refuses a workspace beside it.
        if (resume is not null) { settings["resume"] = resume; } else { settings["workspace"] = options.Workspace; }
        for (var attempt = 0; attempt < 6; attempt++)
        {
            var before = await SessionNodesAsync(ct);
            var request = Guid.NewGuid().ToString();
            var payload = new JsonObject { ["at"] = now().ToString("o"), ["text"] = prompt, ["request"] = request, ["settings"] = settings.DeepClone() };
            // Retained, as the launch card and agent-ha-bridge's MCP tool publish it; the press
            // waits until the slot shows this request, so it never launches another.
            await ha.PublishAsync($"copilot/cli/machine/{options.Slug}/newsession/promptpayload", payload.ToJsonString(), true, ct);
            var landed = false;
            for (var i = 0; i < 50 && !landed; i++)
            {
                landed = Attr(await ha.StateAsync(M("sensor", "new_prompt_payload"), ct), "request") == request;
                if (!landed) { await wait(TimeSpan.FromMilliseconds(300), ct); }
            }
            if (!landed) { Log("the launch request did not show in the slot; trying again"); continue; }
            await ha.CallAsync("button", "press", new() { ["entity_id"] = M("button", "new_session") }, ct);
            var until = now() + TimeSpan.FromMinutes(3);
            var busy = false;
            while (now() < until && !busy)
            {
                var outcome = Outcome(await ha.StateAsync(M("sensor", "new_session_result"), ct), request);
                if (outcome is { } o && o.Outcome is "started" or "refused" or "failed" or "busy" or "unconfirmed")
                {
                    if (o.Outcome == "started" && o.Session is { Length: > 0 } sid) { return NodeOf(sid); }
                    Log($"launch {o.Outcome}: {o.Note}");
                    if (o.Outcome is "refused" or "failed") { return null; }
                    busy = o.Outcome == "busy";
                    if (!busy) { break; }
                }
                else { await wait(TimeSpan.FromSeconds(2), ct); }
            }
            if (!busy)
            {
                // Unconfirmed, or no answer: it may have started all the same, and a second
                // request would leave that one running untracked. A new session on the machine's
                // list is taken as this one; otherwise nothing more is launched for it.
                await wait(TimeSpan.FromSeconds(30), ct);
                var added = (await SessionNodesAsync(ct)).Except(before).ToList();
                if (added.Count == 1) { Log($"taking {added[0]} as the launch's session"); return added[0]; }
                Log($"the launch's outcome is unknown and {added.Count} new session(s) appeared; not launching again");
                return null;
            }
            await wait(TimeSpan.FromSeconds(30), ct);
        }
        return null;
    }

    // The nodes of the sessions agent-ha-bridge lists for this machine.
    async Task<HashSet<string>> SessionNodesAsync(CancellationToken ct)
    {
        var nodes = new HashSet<string>(StringComparer.Ordinal);
        var s = await ha.StateAsync(M("sensor", "sessions"), ct);
        if (s is not null && s.Attributes.ValueKind == JsonValueKind.Object && s.Attributes.TryGetProperty("sessions", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in list.EnumerateArray())
            {
                if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty("node", out var n) && n.GetString() is { Length: > 0 } node) { nodes.Add(node); }
            }
        }
        return nodes;
    }

    static (string Outcome, string? Session, string? Note)? Outcome(HaState? result, string request)
    {
        if (result is null || !result.Attributes.TryGetProperty("recent", out var recent) || recent.ValueKind != JsonValueKind.Array) { return null; }
        foreach (var r in recent.EnumerateArray())
        {
            if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("request", out var q) && q.GetString() == request)
            {
                return (Get(r, "outcome") ?? "", Get(r, "session"), Get(r, "note"));
            }
        }
        return null;
        static string? Get(JsonElement e, string n) => e.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    // agent-ha-bridge's session node: its id's letters and digits, the first 16, lowercased
    // (Get-CopilotMqttNodeId), which is also what its launch outcome names.
    public static string NodeOf(string sessionId)
    {
        var alnum = new string([.. sessionId.Where(char.IsAsciiLetterOrDigit)]).ToLowerInvariant();
        return "agent_bridge_" + alnum[..Math.Min(16, alnum.Length)];
    }

    // The session's full id, from its transcript: resuming and collecting need it, and the
    // node keeps only its first 16 letters and digits.
    string? FullId(string node)
    {
        var want = node["agent_bridge_".Length..];
        foreach (var id in CandidateIds())
        {
            if (NodeOf(id) == "agent_bridge_" + want) { return id; }
        }
        return null;
    }

    IEnumerable<string> CandidateIds()
    {
        var copilot = Path.Combine(homes.CopilotHome, "session-state");
        if (Directory.Exists(copilot)) { foreach (var d in Directory.EnumerateDirectories(copilot)) { yield return Path.GetFileName(d); } }
        var projects = Path.Combine(homes.ClaudeHome, "projects");
        if (Directory.Exists(projects))
        {
            foreach (var f in Directory.EnumerateFiles(projects, "*.jsonl", SearchOption.AllDirectories)) { yield return Path.GetFileNameWithoutExtension(f); }
        }
        var codex = Path.Combine(homes.CodexHome, "sessions");
        if (Directory.Exists(codex))
        {
            foreach (var f in Directory.EnumerateFiles(codex, "rollout-*.jsonl", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (name.Length >= 36 && Guid.TryParse(name[^36..], out _)) { yield return name[^36..]; }
            }
        }
    }

    async Task<string> MarkerAsync(string node, CancellationToken ct)
    {
        var a = await ha.StateAsync($"sensor.{node}_activity", ct);
        return Attr(a, "updated") ?? (Attr(a, "response") is { } r ? "r:" + r.GetHashCode(StringComparison.Ordinal) : "");
    }

    // A prompt as the reply box sends it: words, then Send (agent-ha-bridge's replyToSession).
    async Task SendAsync(string node, string text, CancellationToken ct)
    {
        await ha.CallAsync("text", "set_value", new() { ["entity_id"] = $"text.{node}_reply", ["value"] = text }, ct);
        await wait(TimeSpan.FromSeconds(1), ct);
        await ha.CallAsync("button", "press", new() { ["entity_id"] = $"button.{node}_submit" }, ct);
    }

    // Runs one turn to its end: answering what it asks, and waiting out background work it
    // started. True when the session was ended (a question abandoned).
    async Task<bool> TurnAsync(string node, SoakTurn turn, string? marker, SoakRecord record, HashSet<string> handled, CancellationToken ct, bool freshLaunch = false)
    {
        var until = now() + options.TurnTimeout + (turn.Background ? TimeSpan.FromMinutes(6) : TimeSpan.Zero);
        var launchTurn = marker is null;
        var moved = false;
        DateTimeOffset? idleSince = null;
        var seen = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
        var asked = false;
        var sawWork = false;
        var sawCard = false;
        var begun = now();
        while (now() < until)
        {
            await wait(TimeSpan.FromSeconds(3), ct);
            var status = (await ha.StateAsync($"sensor.{node}_status", ct))?.State ?? "gone";
            // A launched session's card appears a few seconds after the launch says it started.
            if (status is "gone" && !sawCard && now() < begun + TimeSpan.FromMinutes(2)) { continue; }
            sawCard = true;
            if (status is "gone" or "unavailable" or "ended")
            {
                record.Problems.Add($"the session went ({status}) during turn {record.Turns}");
                return true;
            }
            if (status is "working" or "waiting" or "agents" or "shell") { sawWork = true; }
            var current = await MarkerAsync(node, ct);
            // A launch's turn is measured from the card as first seen (a resumed session's shows
            // what it said last time); a later turn's from just before its prompt was sent.
            marker ??= current;
            if (current != marker) { moved = true; }
            // A new session's first reply, if the turn was over before the first look.
            var replied = freshLaunch && launchTurn && Attr(await ha.StateAsync($"sensor.{node}_activity", ct), "response") is { Length: > 0 };
            var q = CardQuestion.Read(await ha.StateAsync($"select.{node}_decision", ct));
            if (q is not null && !handled.Contains(q.DecisionId))
            {
                idleSince = null;
                if (!seen.TryGetValue(q.DecisionId, out var first))
                {
                    seen[q.DecisionId] = first = now();
                    record.Questions++;
                    Log($"  question {q.DecisionId} ({(q.FieldCount > 0 ? q.FieldCount + " field(s)" : q.Choices.Count > 0 ? "choice" : "text")}{(q.AnswerOnTap ? ", tap" : "")})");
                }
                var style = turn.Question ?? AnswerStyle.Card;
                // Held long enough for both sides to sample it (every 15 s) several times.
                var hold = style == AnswerStyle.Late ? TimeSpan.FromMinutes(3) : style == AnswerStyle.Abandon ? TimeSpan.FromMinutes(2) : options.Hold;
                if (now() - first < hold) { continue; }
                if (style == AnswerStyle.Abandon)
                {
                    Log($"  leaving {q.DecisionId} unanswered and ending the session");
                    record.Abandoned++;
                    await EndAsync(node, record, ct);
                    return true;
                }
                await AnswerAsync(node, q, style, record.Questions, ct);
                handled.Add(q.DecisionId);
                record.Answered++;
                asked = true;
                continue;
            }
            // A launched session reads idle for a moment before it starts, so a turn is over once
            // it has been seen working, its response has moved on, or a new session has replied.
            // Not while the card still shows the question just answered: agent-ha-bridge resets
            // the reply box when it clears the card, and a prompt typed before that is wiped,
            // leaving its Send armed to take the prompt after it (seen on VM-M, 2026-11-02).
            if (status == "idle" && q is null && (asked || sawWork || moved || replied))
            {
                idleSince ??= now();
                // Idle for a while: a background shell's end, a woken agent or a question can
                // start the session working again moments after.
                if (now() - idleSince >= TimeSpan.FromSeconds(turn.Background ? 45 : 15)) { return false; }
            }
            else { idleSince = null; }
        }
        record.Problems.Add($"turn {record.Turns} did not end in time");
        Log($"  turn {record.Turns} timed out");
        return false;
    }

    async Task AnswerAsync(string node, CardQuestion q, AnswerStyle style, int pick, CancellationToken ct)
    {
        var slots = new Dictionary<int, IReadOnlyList<string>>();
        for (var i = 1; i <= q.FieldCount; i++)
        {
            var slot = await ha.StateAsync($"select.{node}_f{i}", ct);
            slots[i] = slot is not null && slot.Attributes.TryGetProperty("options", out var o) && o.ValueKind == JsonValueKind.Array
                ? [.. o.EnumerateArray().Select(x => x.GetString() ?? "")] : [];
        }
        var actions = CardAnswer.Plan(node, q, style == AnswerStyle.Late ? AnswerStyle.Card : style, i => slots.GetValueOrDefault(i) ?? [], pick, now());
        Log($"  answering {q.DecisionId} by {style}: {string.Join(", ", actions.Select(a => a.Topic is null ? $"{a.Domain}.{a.Service} {a.Data?["entity_id"]}" : "reply card"))}");
        foreach (var a in actions)
        {
            if (a.Topic is not null) { await ha.PublishAsync(a.Topic, a.Payload!, false, ct); }
            else { await ha.CallAsync(a.Domain, a.Service, a.Data!, ct); }
            // The card commits each control before Send reads them.
            await wait(TimeSpan.FromSeconds(1.5), ct);
        }
    }

    // End session: one press for an idle session, a second within its 10 s window otherwise.
    async Task EndAsync(string node, SoakRecord record, CancellationToken ct)
    {
        var stop = $"button.{node}_stop";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var status = (await ha.StateAsync($"sensor.{node}_status", ct))?.State;
            if (status is null or "unavailable") { return; }
            await ha.CallAsync("button", "press", new() { ["entity_id"] = stop }, ct);
            if (status != "idle")
            {
                await wait(TimeSpan.FromSeconds(3), ct);
                await ha.CallAsync("button", "press", new() { ["entity_id"] = stop }, ct);
            }
            for (var i = 0; i < 30; i++)
            {
                await wait(TimeSpan.FromSeconds(3), ct);
                var s = (await ha.StateAsync($"sensor.{node}_status", ct))?.State;
                if (s is null or "unavailable") { return; }
            }
        }
        record.Problems.Add("the session did not end");
    }

    static string? Attr(HaState? s, string name) =>
        s is not null && s.Attributes.ValueKind == JsonValueKind.Object && s.Attributes.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // The status line for the wrapper and the coordinator: what was done, by agent.
    public static string Summary(SoakState state)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"began {state.Began:o}, {state.Episodes.Count} episode(s), daemon restarted: {state.DaemonRestarted}, shadow host restarted: {state.ShadowRestarted}");
        foreach (var g in state.Episodes.GroupBy(e => e.Agent))
        {
            sb.AppendLine($"  {g.Key}: {g.Count(e => e.ResumedFrom is null)} launched, {g.Count(e => e.ResumedFrom is not null)} resumed, {g.Sum(e => e.Questions)} questions ({g.Sum(e => e.Answered)} answered, {g.Sum(e => e.Abandoned)} abandoned), {g.Sum(e => e.Compactions)} compactions, {g.Sum(e => e.Turns)} turns");
        }
        foreach (var e in state.Episodes.Where(e => e.Problems.Count > 0).TakeLast(8))
        {
            sb.AppendLine($"  episode {e.Index} {e.Scenario}: {string.Join("; ", e.Problems)}");
        }
        return sb.ToString().TrimEnd();
    }

    // A session's raw transcripts, zipped: its own and its sub-agents' (Claude keeps those in a
    // folder beside it, Codex in rollouts of their own that name the parent).
    public static int Collect(Homes homes, string sessionId, string zipPath)
    {
        var files = new List<(string Path, string Name)>();
        var copilot = Path.Combine(homes.CopilotHome, "session-state", sessionId);
        if (Directory.Exists(copilot))
        {
            foreach (var f in Directory.EnumerateFiles(copilot, "*", SearchOption.AllDirectories)) { files.Add((f, "copilot/" + Path.GetRelativePath(copilot, f).Replace('\\', '/'))); }
        }
        var projects = Path.Combine(homes.ClaudeHome, "projects");
        if (Directory.Exists(projects))
        {
            foreach (var dir in Directory.EnumerateDirectories(projects))
            {
                var main = Path.Combine(dir, sessionId + ".jsonl");
                if (!File.Exists(main)) { continue; }
                files.Add((main, "claude/" + sessionId + ".jsonl"));
                var side = Path.Combine(dir, sessionId);
                if (Directory.Exists(side))
                {
                    foreach (var f in Directory.EnumerateFiles(side, "*", SearchOption.AllDirectories)) { files.Add((f, "claude/" + sessionId + "/" + Path.GetRelativePath(side, f).Replace('\\', '/'))); }
                }
            }
        }
        var codex = Path.Combine(homes.CodexHome, "sessions");
        if (Directory.Exists(codex))
        {
            foreach (var f in Directory.EnumerateFiles(codex, "rollout-*.jsonl", SearchOption.AllDirectories))
            {
                if (f.EndsWith(sessionId + ".jsonl", StringComparison.OrdinalIgnoreCase) || Head(f).Contains(sessionId, StringComparison.OrdinalIgnoreCase))
                {
                    files.Add((f, "codex/" + Path.GetFileName(f)));
                }
            }
        }
        if (files.Count == 0) { return 0; }
        if (File.Exists(zipPath)) { File.Delete(zipPath); }
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var (path, name) in files)
        {
            // Copilot's in-use markers are held locked by the running CLI and carry nothing.
            var leaf = Path.GetFileName(path);
            if (leaf.StartsWith("inuse.", StringComparison.Ordinal) || leaf.EndsWith(".lock", StringComparison.Ordinal)) { continue; }
            using var src = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = zip.CreateEntry(name, CompressionLevel.SmallestSize).Open();
            src.CopyTo(dst);
        }
        return files.Count;

        static string Head(string path)
        {
            using var s = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[8192];
            var n = s.Read(buffer, 0, buffer.Length);
            return Encoding.UTF8.GetString(buffer, 0, n);
        }
    }
}
