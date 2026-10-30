using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AgentBridge.Protocol;

namespace AgentBridge.HomeAssistant;

// The host's launcher, as the connector drives it: whether launching is offered here now, what
// a launch can name, and one launch (launch.options and session.launch, docs/host-launch.md).
public sealed record HaLauncher(
    Func<bool> Available,
    Func<CancellationToken, Task<LaunchOptions>> Options,
    Func<LaunchRequest, CancellationToken, Task<LaunchResult>> Launch);

// Stage 5: the launch card (docs/ha-connector.md, "Stage 5"). agent-ha-bridge's
// Sync-DaemonNewSession: the card's controls from what the host's launcher offers, its selectors
// kept on a usable default, and a press of Launch taken as a person's (from the card) or an
// agent's (its request and settings in the prompt payload, #227), handed to the host's launcher,
// and what came of it said on the card's note and in the outcomes an agent reads
// (Fixtures/ha-launch.json).
public sealed partial class HaConnector
{
    readonly HaLauncher? launcher;
    // Decided at start: whether this machine's launch card is this connector's to serve.
    bool launchServing;
    LaunchOptions? launchOptions;
    DateTimeOffset launchOptionsAt = DateTimeOffset.MinValue;
    string? launchSignature;
    // The card last published, while a selector it was to set to its default did not exist yet:
    // Home Assistant names a discovered entity after it, and the pin onto the dashboard's id
    // follows a pass later.
    (LaunchOptions Offered, HaContract.LaunchCard Card, LaunchAgent? Selected)? launchDefaultsPending;
    DateTimeOffset launchDefaultsRetry;
    bool launchPressBaselined;
    string? launchPressHandled;
    bool launchBusy;
    int launchTouched;
    int launchAgentTouched;
    DateTimeOffset nextLaunchSweep;
    DateTimeOffset nextNoteCheck;
    readonly ConcurrentQueue<LaunchDone> launchesDone = new();
    // Requests served, by id, so one whose payload could not be cleared is never served twice.
    readonly List<string> launchRequestsHandled = [];
    DateTimeOffset launchConsumedPress = DateTimeOffset.MinValue;
    // The outcomes an agent reads, newest first (Set-DaemonLaunchOutcome).
    readonly List<JsonObject> launchOutcomes = [];
    Dictionary<string, (string Id, string Launcher)> resumeByLabel = new(StringComparer.Ordinal);
    // Sessions an agent's press launched, to show as the agent's from the moment they appear.
    readonly Dictionary<string, DateTimeOffset> launchedByAgent = new(StringComparer.Ordinal);

    sealed record LaunchDone(LaunchRequest Request, string? RequestId, string Pressed, bool FromCard, bool HadPrompt, string Driver, JsonObject? Launched, LaunchResult Result);

    // A launch request read from the prompt payload (Get-DaemonLaunchRequest).
    sealed record PayloadRequest(string Kind, string? Id = null, string Text = "", IReadOnlyDictionary<string, string>? Settings = null, string Problem = "");

    static readonly string[] LaunchSettings = ["agent", "workspace", "profile", "resume", "model", "effort", "context", "permissions"];

    string LaunchEntity(string domain, string key) => HaContract.MachineEntityId(domain, key, Slug);

    // A change the watch reported on the card's Launch button or agent selector.
    bool LaunchHit(HaTrigger hit)
    {
        if (!launchServing) { return false; }
        if (hit.EntityId == LaunchEntity("button", "new_session")) { Interlocked.Exchange(ref launchTouched, 1); return true; }
        if (hit.EntityId == LaunchEntity("select", "new_agent")) { Interlocked.Exchange(ref launchAgentTouched, 1); return true; }
        return false;
    }

    IEnumerable<string> LaunchWatched() =>
        launchServing ? [LaunchEntity("button", "new_session"), LaunchEntity("select", "new_agent")] : [];

    async Task LaunchAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!launchServing) { return; }
        await LaunchesDoneAsync(now, ct);
        var agentMoved = Interlocked.Exchange(ref launchAgentTouched, 0) == 1;
        if (launchOptions is null || agentMoved || now - launchOptionsAt >= options.LaunchRefresh)
        {
            await LaunchControlsAsync(now, ct);
        }
        else if (launchDefaultsPending is { } pending && now >= launchDefaultsRetry)
        {
            launchDefaultsRetry = now + TimeSpan.FromSeconds(5);
            launchDefaultsPending = null;
            await LaunchDefaultsAsync(pending.Offered, pending.Card, pending.Selected, ct);
        }
        if (!options.ConsumeAnswers) { Interlocked.Exchange(ref launchTouched, 0); return; }
        var pressed = Interlocked.Exchange(ref launchTouched, 0) == 1;
        if (pressed || now >= nextLaunchSweep || !launchPressBaselined)
        {
            nextLaunchSweep = now + options.AnswerSweep;
            try { await LaunchPressAsync(now, ct); }
            catch { if (pressed) { Interlocked.Exchange(ref launchTouched, 1); } throw; }
        }
        if (now >= nextNoteCheck && !launchBusy)
        {
            nextNoteCheck = now + TimeSpan.FromSeconds(60);
            await ExpireLaunchNoteAsync(ct);
        }
    }

    // Publish-DaemonNewSessionControls and Set-DaemonNewSessionDefaults.
    async Task LaunchControlsAsync(DateTimeOffset now, CancellationToken ct)
    {
        var offered = await launcher!.Options(ct);
        launchOptions = offered;
        launchOptionsAt = now;
        var agentState = await StateOrNullAsync(LaunchEntity("select", "new_agent"), ct);
        var selected = AgentNamed(offered, agentState?.State) ?? DefaultAgent(offered);
        var card = Card(offered, selected);
        var signature = HaContract.Serialize(new JsonObject
        {
            ["w"] = new JsonArray([.. card.Workspaces.Select(x => (JsonNode)x)]),
            ["p"] = new JsonArray([.. card.Profiles.Select(x => (JsonNode)x)]),
            ["r"] = new JsonArray([.. card.Resumable.Select(x => (JsonNode)x)]),
            ["a"] = new JsonArray([.. card.Agents.Select(x => (JsonNode)x)]),
            ["t"] = new JsonArray([.. HaContract.TuningAxes.Select(t => (JsonNode)string.Join("|", card.Tuning[t.Axis]))]),
        });
        if (signature != launchSignature)
        {
            foreach (var (topic, config) in HaContract.LaunchConfigs(Slug, options.MachineName, card)) { await PublishAsync(topic, config, true, ct); }
            foreach (var (domain, key) in HaContract.LaunchEntities) { AddPin($"agent_bridge_{Slug}_{key}", LaunchEntity(domain, key), now); }
            launchSignature = signature;
            log($"Home Assistant connector published the launch card ({card.Workspaces.Count} workspace(s), agents: {(card.Agents.Count > 0 ? string.Join(", ", card.Agents) : "none")}, {card.Resumable.Count} resumable)");
        }
        await LaunchDefaultsAsync(offered, card, selected, ct);
    }

    HaContract.LaunchCard Card(LaunchOptions offered, LaunchAgent? selected)
    {
        var sessions = offered.Resumable.OrderByDescending(r => r.UpdatedAt ?? DateTimeOffset.MinValue).Take(options.ResumeCount)
            .Select(r => (r.Id, r.Launcher, AgentLabel(offered, r.Launcher), (string?)r.Title, r.Cwd)).ToList();
        var labels = HaContract.ResumeLabels(sessions);
        resumeByLabel = labels.ToDictionary(l => l.Label, l => (sessions[l.Index].Id, sessions[l.Index].Launcher), StringComparer.Ordinal);
        return new HaContract.LaunchCard
        {
            Workspaces = [.. offered.Workspaces.Select(w => w.Label)],
            Profiles = Agency(offered)?.Profiles ?? [],
            Resumable = [.. labels.Select(l => l.Label)],
            Agents = [.. offered.Agents.Select(a => a.Label)],
            Tuning = new Dictionary<string, IReadOnlyList<string>>
            {
                ["model"] = selected is null ? [] : [HaContract.TuningDefault, .. selected.Models],
                ["effort"] = selected is null ? [] : [HaContract.TuningDefault, .. selected.Efforts],
                ["context"] = selected is null ? [] : [HaContract.TuningDefault, .. selected.Contexts],
            },
        };
    }

    static LaunchAgent? Agency(LaunchOptions offered) => offered.Agents.FirstOrDefault(a => a.Id == "agency");

    // Resolve-BridgeLauncher: an agent by its label or its id, as PowerShell's -eq compares them.
    static LaunchAgent? AgentNamed(LaunchOptions offered, string? name) =>
        string.IsNullOrWhiteSpace(name) ? null
            : offered.Agents.FirstOrDefault(a => string.Equals(a.Label, name, StringComparison.OrdinalIgnoreCase) || string.Equals(a.Id, name, StringComparison.OrdinalIgnoreCase));

    static LaunchAgent? DefaultAgent(LaunchOptions offered) =>
        offered.Agents.FirstOrDefault(a => a.Id == offered.Defaults.Agent) ?? offered.Agents.FirstOrDefault();

    static string AgentLabel(LaunchOptions offered, string id) => offered.Agents.FirstOrDefault(a => a.Id == id)?.Label ?? id;

    static bool Stale(string? state) => state is null or "" or "unknown" or "unavailable";

    async Task<HaState?> StateOrNullAsync(string entityId, CancellationToken ct)
    {
        try { return await ha.GetStateAsync(entityId, ct); }
        catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { return null; }
    }

    // Set-DaemonNewSessionDefaults: optimistic selectors read unknown when created and after every
    // Home Assistant restart, so each is driven to the machine's default whenever it holds nothing
    // usable; a value someone chose is never overwritten.
    async Task LaunchDefaultsAsync(LaunchOptions offered, HaContract.LaunchCard card, LaunchAgent? selected, CancellationToken ct)
    {
        var missing = false;
        async Task Keep(string key, IReadOnlyList<string> valid, string? wanted)
        {
            if (valid.Count == 0 || string.IsNullOrEmpty(wanted)) { return; }
            var entity = LaunchEntity("select", key);
            var state = await StateOrNullAsync(entity, ct);
            if (state is null) { missing = true; return; }
            if (Stale(state.State) || !valid.Contains(state.State, StringComparer.Ordinal)) { await SelectAsync(entity, wanted, ct); }
        }

        await Keep("new_agent", card.Agents, DefaultAgent(offered)?.Label);
        await Keep("new_workspace", card.Workspaces, offered.Defaults.Workspace is { Length: > 0 } w && card.Workspaces.Contains(w) ? w : card.Workspaces.FirstOrDefault());
        var profile = Agency(offered)?.Defaults?.Profile;
        await Keep("new_profile", card.Profiles, profile is not null && card.Profiles.Contains(profile) ? profile : card.Profiles.FirstOrDefault());
        foreach (var (axis, _, _) in HaContract.TuningAxes)
        {
            var axisOptions = card.Tuning[axis];
            var wanted = axis switch { "model" => selected?.Defaults?.Model, "effort" => selected?.Defaults?.Effort, _ => selected?.Defaults?.Context };
            await Keep($"new_{axis}", axisOptions, wanted is not null && axisOptions.Contains(wanted) ? wanted : axisOptions.FirstOrDefault());
        }
        await Keep("new_resume", [HaContract.NewSessionOption, .. card.Resumable], HaContract.NewSessionOption);
        await Keep("new_permissions", HaContract.PermissionOptions, offered.Defaults.Permissions == LaunchProtocol.AllowAll ? HaContract.PermissionAllowAll : HaContract.PermissionAsk);
        // The prompt is optional: blank and inviting rather than "unknown".
        var prompt = LaunchEntity("text", "new_prompt");
        var promptState = await StateOrNullAsync(prompt, ct);
        if (promptState is null) { missing = true; }
        else if (promptState.State is "unknown" or "unavailable")
        {
            try { await ha.CallServiceAsync("text", "set_value", new JsonObject { ["entity_id"] = prompt, ["value"] = HaDecision.ReplyBlank }, ct); }
            catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { }
        }
        launchDefaultsPending = missing ? (offered, card, selected) : null;
    }

    // Test-DaemonNewSessionPressed, Use-DaemonLaunchRequest and Resolve-DaemonLaunchRequest.
    async Task LaunchPressAsync(DateTimeOffset now, CancellationToken ct)
    {
        var press = await ObserveAsync(LaunchEntity("button", "new_session"), ct);
        if (!launchPressBaselined)
        {
            if (press.Kind == "unknown") { return; }
            launchPressHandled = press.Present ? press.Value : null;
            launchPressBaselined = true;
            return;
        }
        if (!press.Present || press.Value == launchPressHandled) { return; }
        launchPressHandled = press.Value;
        if (!DateTimeOffset.TryParse(press.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var pressedAt)) { return; }
        var driver = DriverOf(press.Snapshot?.UserId);
        var pressed = Instant(pressedAt);

        var read = await ReadLaunchRequestAsync(pressedAt, ct);
        var payloadSkip = false;
        switch (read.Kind)
        {
            case "newer":
                log("Home Assistant connector left a Launch press alone: a launch request published after it is waiting for its own press");
                return;
            case "malformed":
                await ClearPromptPayloadAsync(ct);
                await LaunchNoteAsync("Launch refused: the request carried an id that is not valid, so nothing was started.", ct);
                return;
            case "stale" or "handled":
                log($"Home Assistant connector: a Launch press ignored a {read.Kind} launch request left in the prompt payload");
                await ClearPromptPayloadAsync(ct);
                payloadSkip = true;
                break;
        }

        if (read.Kind != "fresh")
        {
            if (driver == "agent")
            {
                var since = (pressedAt - launchConsumedPress).TotalSeconds;
                if (launchConsumedPress != DateTimeOffset.MinValue && since is >= 0 and <= 10)
                {
                    log($"Home Assistant connector: Launch pressed by an agent {since:F1} s after another press used the launch request waiting; nothing launched");
                    return;
                }
            }
            if (launchBusy)
            {
                log("Home Assistant connector: Launch pressed while a launch is still starting; nothing more launched");
                return;
            }
            await LaunchFromCardAsync(pressed, payloadSkip, driver, now, ct);
            return;
        }

        // An agent's request: answered before the slot is cleared, so it is always in one place or
        // the other, then taken (Use-DaemonLaunchRequest).
        var id = read.Id!;
        await OutcomeAsync(id, "starting", "Received; checking its settings before launching.", pressed: pressed, ct: ct);
        await ClearPromptPayloadAsync(ct);
        launchRequestsHandled.Insert(0, id);
        if (launchRequestsHandled.Count > 50) { launchRequestsHandled.RemoveAt(50); }
        launchConsumedPress = pressedAt;
        log($"Home Assistant connector: launch request {id} received ({(read.Settings!.Count > 0 ? string.Join(", ", read.Settings.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => $"{k.Key}={k.Value}")) : "machine defaults")}; prompt of {read.Text.Length} chars)");
        var refusal = read.Problem;
        if (refusal.Length == 0 && time.GetUtcNow() - pressedAt > TimeSpan.FromMinutes(5))
        {
            refusal = "Launch refused: the request waited more than 5 minutes for this machine, so it was not started late.";
        }
        if (refusal.Length == 0 && launchBusy)
        {
            refusal = "Busy: another launch on this machine is still starting, so nothing was launched. Try again in a moment.";
            await OutcomeAsync(id, "busy", refusal, ct: ct);
            return;
        }
        if (refusal.Length > 0)
        {
            await LaunchNoteAsync(refusal, ct);
            await OutcomeAsync(id, "refused", refusal, ct: ct);
            return;
        }
        var (request, launched, why) = RequestedLaunch(read, pressed);
        if (request is null)
        {
            await LaunchNoteAsync(why!, ct);
            await OutcomeAsync(id, "refused", why!, ct: ct);
            return;
        }
        await LaunchNoteAsync(StartingNote(launched!), ct);
        Dispatch(request, id, pressed, fromCard: false, hadPrompt: false, driver, launched);
    }

    // A person's press, or an agent's without a request: everything from the card.
    async Task LaunchFromCardAsync(string pressed, bool payloadSkip, string driver, DateTimeOffset now, CancellationToken ct)
    {
        var offered = launchOptions!;
        var agentName = (await StateOrNullAsync(LaunchEntity("select", "new_agent"), ct))?.State;
        if (Stale(agentName)) { agentName = DefaultAgent(offered)?.Label; }
        var agent = AgentNamed(offered, agentName);
        if (agent is null && offered.Agents.Count > 0)
        {
            await LaunchNoteAsync($"Unknown agent '{agentName}'", ct);
            return;
        }

        string? resume = null;
        string? resumeLabel = null;
        var resumeState = (await StateOrNullAsync(LaunchEntity("select", "new_resume"), ct))?.State;
        if (!Stale(resumeState) && resumeState != HaContract.NewSessionOption)
        {
            if (!resumeByLabel.TryGetValue(resumeState!, out var chosen))
            {
                await LaunchNoteAsync("That session is no longer resumable", ct);
                return;
            }
            resume = chosen.Id;
            resumeLabel = resumeState;
            agent = offered.Agents.FirstOrDefault(a => a.Id == chosen.Launcher) ?? agent;
        }

        string? workspace = null;
        if (resume is null)
        {
            workspace = (await StateOrNullAsync(LaunchEntity("select", "new_workspace"), ct))?.State;
            if (Stale(workspace)) { workspace = offered.Defaults.Workspace; }
            if (offered.Workspaces.Count > 0 && !offered.Workspaces.Any(w => w.Label == workspace))
            {
                await LaunchNoteAsync($"Unknown workspace '{workspace}'", ct);
                return;
            }
        }

        var prompt = await CardPromptAsync(payloadSkip, ct);

        string? profile = null;
        var profiles = Agency(offered)?.Profiles ?? [];
        if (agent?.Id == "agency" && profiles.Count > 0)
        {
            profile = (await StateOrNullAsync(LaunchEntity("select", "new_profile"), ct))?.State;
            if (Stale(profile)) { profile = Agency(offered)?.Defaults?.Profile ?? profiles[0]; }
            if (!profiles.Contains(profile!, StringComparer.Ordinal))
            {
                await LaunchNoteAsync($"Unknown profile '{profile}' - this machine has {string.Join(", ", profiles)}", ct);
                return;
            }
        }

        // A value the selected agent does not offer is one left from another agent: the machine's
        // default then, as an untouched card would be. Agent default is the agent's own.
        var tuning = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (axis, _, _) in HaContract.TuningAxes)
        {
            var value = (await StateOrNullAsync(LaunchEntity("select", $"new_{axis}"), ct))?.State;
            var offeredValues = agent is null ? [] : axis switch { "model" => agent.Models, "effort" => agent.Efforts, _ => agent.Contexts };
            tuning[axis] = value == HaContract.TuningDefault ? "" : value is not null && offeredValues.Contains(value, StringComparer.Ordinal) ? value : null;
        }

        // Only the exact Allow all allows all (Test-BridgePermissionAllowsAll); unreadable is the
        // machine's own setting.
        var permissionState = (await StateOrNullAsync(LaunchEntity("select", "new_permissions"), ct))?.State;
        string? permissions = Stale(permissionState) ? null : permissionState!.Trim() == HaContract.PermissionAllowAll ? LaunchProtocol.AllowAll : LaunchProtocol.Ask;

        var request = new LaunchRequest
        {
            Request = RequestId(Slug, "launch", pressed),
            Agent = agent?.Id,
            Workspace = workspace,
            Resume = resume,
            Profile = profile,
            Model = tuning["model"],
            Effort = tuning["effort"],
            Context = tuning["context"],
            Permissions = permissions,
            Prompt = prompt.Length > 0 ? prompt : null,
        };
        var launched = Launched(offered, request, resumeLabel);
        await LaunchNoteAsync(StartingNote(launched), ct);
        Dispatch(request, null, pressed, fromCard: true, hadPrompt: prompt.Length > 0, driver, launched);
    }

    // Get-DaemonLaunchPrompt: the payload's whole prompt when it has one, else the box's.
    async Task<string> CardPromptAsync(bool payloadSkip, CancellationToken ct)
    {
        if (!payloadSkip && await StateOrNullAsync(LaunchEntity("sensor", "new_prompt_payload"), ct) is { } payload
            && !Stale(payload.State) && payload.Attributes is { ValueKind: JsonValueKind.Object } a
            && a.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(t.GetString()))
        {
            return t.GetString()!.Trim();
        }
        var typed = (await StateOrNullAsync(LaunchEntity("text", "new_prompt"), ct))?.State;
        return Stale(typed) ? "" : typed!.Trim();
    }

    // Get-DaemonLaunchRequest: the request and settings riding on the prompt payload, and whether
    // they belong to the press at pressedAt. Both times are Home Assistant's.
    async Task<PayloadRequest> ReadLaunchRequestAsync(DateTimeOffset pressedAt, CancellationToken ct)
    {
        var state = await StateOrNullAsync(LaunchEntity("sensor", "new_prompt_payload"), ct);
        if (state is null || Stale(state.State) || state.Attributes.ValueKind != JsonValueKind.Object) { return new("none"); }
        var a = state.Attributes;
        var hasId = a.TryGetProperty("request", out var idElement) && idElement.ValueKind != JsonValueKind.Null;
        var hasSettings = a.TryGetProperty("settings", out var settings) && settings.ValueKind != JsonValueKind.Null;
        if (!hasId && !hasSettings) { return new("none"); }
        var age = state.LastUpdated is { } published ? (pressedAt - published).TotalSeconds : double.PositiveInfinity;
        if (age < 0) { return new("newer"); }
        if (age > 60) { return new("stale"); }
        var id = idElement.ValueKind == JsonValueKind.String ? idElement.GetString() : null;
        if (id is null || !RequestIdPattern().IsMatch(id)) { return new("malformed"); }
        if (launchRequestsHandled.Contains(id, StringComparer.Ordinal)) { return new("handled"); }

        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        var problem = "";
        if (hasSettings)
        {
            if (settings.ValueKind != JsonValueKind.Object) { problem = "Launch refused: its settings were not a set of named values."; }
            else
            {
                foreach (var pair in settings.EnumerateObject())
                {
                    if (!LaunchSettings.Contains(pair.Name, StringComparer.Ordinal))
                    {
                        problem = $"Launch refused: unknown setting '{pair.Name}' - a launch takes {string.Join(", ", LaunchSettings)}.";
                        break;
                    }
                    if (pair.Value.ValueKind == JsonValueKind.Null) { continue; }
                    if (pair.Value.ValueKind != JsonValueKind.String)
                    {
                        problem = $"Launch refused: the {pair.Name} setting has to be text.";
                        break;
                    }
                    var value = pair.Value.GetString()!.Trim();
                    if (value.Length > 0) { parsed[pair.Name] = value; }
                }
            }
        }
        if (problem.Length > 0) { parsed.Clear(); }
        var text = problem.Length == 0 && a.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()!.Trim() : "";
        return new("fresh", id, text, parsed, problem);
    }

    [GeneratedRegex("^[A-Za-z0-9-]{8,64}$")]
    private static partial Regex RequestIdPattern();

    // Resolve-DaemonRequestedLaunch: every setting from the request, none from the card; one left
    // out takes the machine's default, and one this machine does not offer is refused, naming
    // what it does offer.
    (LaunchRequest? Request, JsonObject? Launched, string? Refusal) RequestedLaunch(PayloadRequest read, string pressed)
    {
        var offered = launchOptions!;
        var s = read.Settings!;
        string Get(string key) => s.TryGetValue(key, out var v) ? v : "";
        var agentName = Get("agent");
        var agent = AgentNamed(offered, agentName.Length > 0 ? agentName : DefaultAgent(offered)?.Label);
        if (agent is null)
        {
            if (offered.Agents.Count > 0) { return (null, null, $"Unknown agent '{(agentName.Length > 0 ? agentName : DefaultAgent(offered)?.Label)}' - this machine has {string.Join(", ", offered.Agents.Select(x => x.Label))}"); }
            if (agentName.Length > 0) { return (null, null, $"Unknown agent '{agentName}' - no agent is installed on this machine"); }
        }

        string? resume = null;
        string? resumeLabel = null;
        var wantedResume = Get("resume");
        if (wantedResume.Length > 0)
        {
            if (Get("workspace").Length > 0) { return (null, null, "A resumed session reopens in its own folder - name a workspace or a session to resume, not both"); }
            var match = resumeByLabel.FirstOrDefault(r => r.Key == wantedResume || r.Value.Id == wantedResume);
            if (match.Key is null) { return (null, null, $"No resumable session '{wantedResume}' on this machine - it is not on its resume list"); }
            resumeLabel = match.Key;
            var theirs = offered.Agents.FirstOrDefault(x => x.Id == match.Value.Launcher);
            if (agentName.Length > 0 && agent?.Id != match.Value.Launcher)
            {
                var theirLabel = AgentLabel(offered, match.Value.Launcher);
                return (null, null, $"'{resumeLabel}' is a {theirLabel} session, so it resumes in {theirLabel}, not {agent?.Label}");
            }
            if (theirs is null) { return (null, null, $"{AgentLabel(offered, match.Value.Launcher)} is not installed here, so that session can't be resumed"); }
            agent = theirs;
            resume = match.Value.Id;
        }

        string? workspace = null;
        var wantedWorkspace = Get("workspace");
        if (wantedWorkspace.Length > 0)
        {
            if (!offered.Workspaces.Any(w => w.Label == wantedWorkspace))
            {
                return (null, null, $"Unknown workspace '{wantedWorkspace}' - this machine has {string.Join(", ", offered.Workspaces.Select(w => w.Label))}");
            }
            workspace = wantedWorkspace;
        }

        string? profile = null;
        var wantedProfile = Get("profile");
        var profiles = Agency(offered)?.Profiles ?? [];
        if (agent?.Id == "agency" && profiles.Count > 0)
        {
            if (wantedProfile.Length > 0)
            {
                if (!profiles.Contains(wantedProfile, StringComparer.Ordinal)) { return (null, null, $"Unknown profile '{wantedProfile}' - this machine has {string.Join(", ", profiles)}"); }
                profile = wantedProfile;
            }
        }
        else if (wantedProfile.Length > 0)
        {
            var why = agent?.Id != "agency" ? $"a profile applies only to Agency, and this launch is {agent?.Label}" : "this machine's Agency has no profiles";
            return (null, null, $"Profile '{wantedProfile}' refused: {why}");
        }

        var tuning = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var (axis, label, _) in HaContract.TuningAxes)
        {
            var wanted = Get(axis);
            if (wanted.Length == 0) { tuning[axis] = null; continue; }
            if (wanted == HaContract.TuningDefault) { tuning[axis] = ""; continue; }
            IReadOnlyList<string> values = agent is null ? [] : axis switch { "model" => agent.Models, "effort" => agent.Efforts, _ => agent.Contexts };
            if (!values.Contains(wanted, StringComparer.Ordinal))
            {
                var all = (IReadOnlyList<string>)[HaContract.TuningDefault, .. values];
                var name = label.ToLowerInvariant();
                return (null, null, all.Count > 1 ? $"Unknown {name} '{wanted}' for {agent?.Label} - it offers {string.Join(", ", all)}"
                    : $"{agent?.Label} has no {name} to choose here, so '{wanted}' can't be used");
            }
            tuning[axis] = wanted;
        }

        string? permissions = null;
        var wantedPermissions = Get("permissions");
        if (wantedPermissions.Length > 0)
        {
            if (!HaContract.PermissionOptions.Contains(wantedPermissions, StringComparer.Ordinal))
            {
                return (null, null, $"Unknown permissions '{wantedPermissions}' - use '{string.Join("' or '", HaContract.PermissionOptions)}'");
            }
            permissions = wantedPermissions == HaContract.PermissionAllowAll ? LaunchProtocol.AllowAll : LaunchProtocol.Ask;
        }

        var request = new LaunchRequest
        {
            Request = RequestId(Slug, "launch", $"{read.Id}#{pressed}"),
            Agent = agent?.Id,
            Workspace = workspace,
            Resume = resume,
            Profile = profile,
            Model = tuning["model"],
            Effort = tuning["effort"],
            Context = tuning["context"],
            Permissions = permissions,
            Prompt = read.Text.Length > 0 ? read.Text : null,
        };
        return (request, Launched(offered, request, resumeLabel), null);
    }

    // What a launch is about, before the host has filled in its defaults; the host's own account
    // replaces it once it answers.
    JsonObject Launched(LaunchOptions offered, LaunchRequest request, string? resumeLabel)
    {
        var agent = offered.Agents.FirstOrDefault(a => a.Id == (request.Agent ?? offered.Defaults.Agent));
        return new JsonObject
        {
            ["agent"] = agent?.Label ?? request.Agent ?? "",
            ["workspace"] = request.Resume is null ? request.Workspace ?? offered.Defaults.Workspace ?? "" : "",
            ["resume"] = resumeLabel ?? "",
            ["profile"] = request.Profile ?? "",
            ["model"] = request.Model ?? "",
            ["effort"] = request.Effort ?? "",
            ["context"] = request.Context ?? "",
            ["permissions"] = (request.Permissions ?? offered.Defaults.Permissions) == LaunchProtocol.AllowAll ? HaContract.PermissionAllowAll : HaContract.PermissionAsk,
            ["resumed"] = request.Resume is not null,
        };
    }

    // What Start-DaemonLaunch says while it starts.
    static string StartingNote(JsonObject launched)
    {
        if (launched["resumed"]!.GetValue<bool>()) { return $"Resuming {launched["resume"]!.GetValue<string>()}..."; }
        var profile = launched["profile"]!.GetValue<string>();
        return $"Starting {launched["agent"]!.GetValue<string>()} in {launched["workspace"]!.GetValue<string>()}{(profile.Length > 0 ? $" ({profile})" : "")}...";
    }

    // The launch, beside the passes: the host follows it to registration, up to a minute.
    void Dispatch(LaunchRequest request, string? requestId, string pressed, bool fromCard, bool hadPrompt, string driver, JsonObject? launched)
    {
        launchBusy = true;
        var cancel = replyStop.Token;
        log($"Home Assistant connector: launch {request.Request[..8]} handed to the host ({request.Agent ?? "default agent"}{(request.Resume is null ? $" in {request.Workspace ?? "the default workspace"}" : ", resuming")})");
        Track(Task.Run(async () =>
        {
            LaunchResult result;
            try { result = await launcher!.Launch(request, cancel); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { result = new LaunchResult(LaunchOutcome.Unconfirmed, "the host stopped while the launch was starting; check the machine"); }
            catch (Exception e) { result = new LaunchResult(LaunchOutcome.Failed, $"Launch failed: {e.GetType().Name}"); }
            launchesDone.Enqueue(new LaunchDone(request, requestId, pressed, fromCard, hadPrompt, driver, launched, result));
        }, CancellationToken.None));
    }

    // What came of each launch: the card's note and the request's outcome.
    async Task LaunchesDoneAsync(DateTimeOffset now, CancellationToken ct)
    {
        while (launchesDone.TryPeek(out var done))
        {
            var result = done.Result;
            var outcome = result.Outcome switch
            {
                LaunchOutcome.Started => "started",
                LaunchOutcome.Busy => "busy",
                LaunchOutcome.Refused => "refused",
                LaunchOutcome.Failed => "failed",
                _ => "unconfirmed",
            };
            var launched = done.Launched;
            if (result.Launched is { } with && launchOptions is { } offered)
            {
                launched = new JsonObject
                {
                    ["agent"] = AgentLabel(offered, with.Agent),
                    ["workspace"] = with.Resume is null ? with.Workspace ?? "" : "",
                    ["resume"] = done.Launched?["resume"]?.GetValue<string>() ?? "",
                    ["profile"] = with.Profile ?? "",
                    ["model"] = with.Model ?? "",
                    ["effort"] = with.Effort ?? "",
                    ["context"] = with.Context ?? "",
                    ["permissions"] = with.Permissions == LaunchProtocol.AllowAll ? HaContract.PermissionAllowAll : HaContract.PermissionAsk,
                    ["resumed"] = with.Resume is not null,
                };
            }
            // Started: the note goes, as it does once the daemon sees the session register.
            var note = result.Outcome == LaunchOutcome.Started ? "" : result.Reason ?? (outcome == "failed" ? "Launch failed." : "Launch refused.");
            await LaunchNoteAsync(note, ct);
            if (done.RequestId is { } id)
            {
                await OutcomeAsync(id, outcome, note, session: result.Session ?? "", launched: launched, ct: ct);
            }
            if (result.Outcome is LaunchOutcome.Started or LaunchOutcome.Unconfirmed)
            {
                // The prompt that started a session is not left to start the next one; an agent's
                // was taken from the payload already, and the box is a person's.
                if (done.FromCard && done.HadPrompt) { await ClearPromptsAsync(ct); }
                // The selectors were re-read on the next refresh: what is resumable has changed.
                launchOptionsAt = DateTimeOffset.MinValue;
                if (done.Driver == "agent" && result.Session is { Length: > 0 } session) { ArmLaunchedDriver(session, now); }
            }
            launchesDone.TryDequeue(out _);
            launchBusy = false;
            log($"Home Assistant connector: launch {done.Request.Request[..8]} {outcome}{(result.Session is { } sid ? $" as {sid[..Math.Min(8, sid.Length)]}" : "")}{(note.Length > 0 ? $": {note}" : "")}");
        }
    }

    void ArmLaunchedDriver(string sessionId, DateTimeOffset now)
    {
        lock (gate)
        {
            if (published.TryGetValue(sessionId, out var p)) { p.ArmDriver("agent", now); return; }
            launchedByAgent[sessionId] = now;
        }
    }

    // The launch card's rows, for the capability flags; none when it is not served here.
    HaPayloads.LaunchRows? LaunchCapabilities()
    {
        if (!launchServing) { return null; }
        var offered = launchOptions;
        return new HaPayloads.LaunchRows(
            Profile: offered is not null && (Agency(offered)?.Profiles?.Count ?? 0) > 0,
            Resume: offered is not null && offered.Agents.Count > 0,
            Agent: offered is not null && offered.Agents.Count > 1);
    }

    // Called under gate when a session is first published.
    void ApplyLaunchedDriver(string sessionId, Published p, DateTimeOffset now)
    {
        if (launchedByAgent.Remove(sessionId, out var at) && now - at < options.DriverArm) { p.ArmDriver("agent", at); }
    }

    // Set-CopilotMqttNewSessionResult.
    async Task LaunchNoteAsync(string text, CancellationToken ct) =>
        await ha.PublishAsync(HaContract.LaunchResultTopic(Slug), HaContract.LaunchNote(text), true, ct);

    // Set-DaemonLaunchOutcome: the latest few, newest first; never throws, since the launch it
    // describes has happened either way.
    async Task OutcomeAsync(string request, string outcome, string note, string session = "", JsonObject? launched = null, string pressed = "", CancellationToken ct = default)
    {
        var previous = launchOutcomes.FirstOrDefault(o => o["request"]!.GetValue<string>() == request);
        if (previous is not null)
        {
            launched ??= previous["launched"]?.DeepClone().AsObject();
            if (pressed.Length == 0) { pressed = previous["pressed"]!.GetValue<string>(); }
            launchOutcomes.Remove(previous);
        }
        launchOutcomes.Insert(0, new JsonObject
        {
            ["request"] = request,
            ["outcome"] = outcome,
            ["session"] = session,
            ["note"] = note,
            ["pressed"] = pressed,
            ["at"] = time.GetUtcNow().ToString("o", CultureInfo.InvariantCulture),
            ["launched"] = launched?.DeepClone(),
        });
        while (launchOutcomes.Count > HaContract.LaunchOutcomesKept) { launchOutcomes.RemoveAt(launchOutcomes.Count - 1); }
        try
        {
            await PublishAsync(HaContract.LaunchOutcomeTopic(Slug), new JsonObject { ["recent"] = new JsonArray([.. launchOutcomes.Select(o => (JsonNode)o.DeepClone())]) }, true, ct);
        }
        catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning))
        {
            log($"Home Assistant connector: the launch outcome for {request} could not be published: {Describe(e)}");
        }
    }

    // Format-DaemonInstant.
    static string Instant(DateTimeOffset at) => at.UtcDateTime.ToString("o", CultureInfo.InvariantCulture);

    // Clear-CopilotMqttNewSessionPrompt.
    async Task ClearPromptPayloadAsync(CancellationToken ct)
    {
        try { await ha.PublishAsync(HaContract.PromptPayloadTopic(Slug), "{}", true, ct); }
        catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { log($"Home Assistant connector could not clear the launch prompt payload: {Describe(e)}"); }
    }

    // Clear-DaemonLaunchPrompt: both boxes.
    async Task ClearPromptsAsync(CancellationToken ct)
    {
        try { await ha.CallServiceAsync("text", "set_value", new JsonObject { ["entity_id"] = LaunchEntity("text", "new_prompt"), ["value"] = HaDecision.ReplyBlank }, ct); }
        catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { }
        await ClearPromptPayloadAsync(ct);
    }

    // Clear-DaemonStaleNote: a note is what the last press came to; once old, it only sits there.
    async Task ExpireLaunchNoteAsync(CancellationToken ct)
    {
        var state = await StateOrNullAsync(LaunchEntity("sensor", "new_session_result"), ct);
        if (state is null || Stale(state.State) || state.LastChanged is not { } changed) { return; }
        if (time.GetUtcNow() - changed < HaContract.LaunchNoteExpiry) { return; }
        await LaunchNoteAsync("", ct);
        log($"Home Assistant connector cleared the launch note: {state.State}");
    }
}
