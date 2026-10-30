using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Hooks;
using AgentBridge.Host.Core.Discovery;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Intake;

// What agent-ha-bridge's hook functions record locally, without the Home Assistant half: the
// Claude and Codex session registrations and the Codex approval marker, in the files and
// shapes its daemon and this host's discovery read (RegistrationDiscovery, ApprovalMarkerRead).
//
//   claude/register      Invoke-ClaudeRegisterHook        claude/hooks/claude-hooks.ps1
//   claude/stop          Invoke-ClaudeStopHook
//   claude/ask           Invoke-ClaudeAskHook
//   claude/notification  Invoke-ClaudeNotificationHook
//   codex/hook           Invoke-CodexHook                 codex/hooks/codex-hooks.ps1
//   copilot/*            nothing to record: Copilot's state is its own session folder
//
// agent-ha-bridge wrote the Codex approval marker only after reaching Home Assistant, because
// only its card could answer one. Here a client of this host answers it, so it is written for
// every approval Codex asks for.
internal sealed class HookHandlers(string runtimeRoot, string claudeHome, IProcessInspector processes, TimeProvider time)
{
    public string ClaudeRoot { get; } = Path.Combine(runtimeRoot, SpoolContract.ClaudeFolder);
    public string CodexRoot { get; } = Path.Combine(runtimeRoot, SpoolContract.CodexFolder);

    // A Codex approval asked at or before this may have been answered at the terminal while no
    // one took events (HookIntake.ReconcileApprovals), so it arms no marker.
    public DateTimeOffset? ApprovalsSettledBefore { get; set; }

    static readonly string[] ClaudeBlocking = ["permission_prompt", "worker_permission_prompt", "elicitation_dialog", "elicitation_url_dialog", "agent_needs_input"];

    // Throws when the event cannot be handled, so the intake tries it once more.
    public HookNotice Handle(SpoolEvent spooled)
    {
        var e = spooled.Event;
        var eventName = Str(e, "hook_event_name");
        var sessionId = Str(e, "session_id");
        var notice = new HookNotice(spooled.Agent, spooled.Hook, eventName, string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
            null, null, null, e, spooled.Ancestors, spooled.ReceivedAt, time.GetUtcNow());
        return spooled.Key switch
        {
            "claude/register" or "claude/stop" or "claude/ask" or "claude/notification" => Claude(spooled, notice),
            "codex/hook" => Codex(spooled, notice),
            "copilot/ask_user" or "copilot/agent_stop" or "copilot/permission" => notice,
            _ => throw new InvalidDataException($"no handler for {spooled.Key}"),
        };
    }

    HookNotice Claude(SpoolEvent spooled, HookNotice notice)
    {
        var e = spooled.Event;
        if (notice.SessionId is not { } sessionId) { return notice; }
        if (Superseded(ClaudePath(sessionId), spooled)) { return notice; }
        var known = Str(e, "transcript_path");
        var cwd = Str(e, "cwd") ?? "";
        string? status;
        string? transcript;
        switch (spooled.Hook)
        {
            case "register":
                // At SessionStart the transcript may not exist yet, so the path Claude reports is
                // kept as it is rather than verified and discarded.
                transcript = string.IsNullOrWhiteSpace(known) ? ClaudeTranscript(sessionId, null) : known;
                // A submitted prompt is the start of a turn; SessionStart leaves status alone.
                status = notice.EventName == "UserPromptSubmit" ? "working" : null;
                break;
            case "stop":
                transcript = ClaudeTranscript(sessionId, known);
                status = "idle";
                break;
            case "ask":
                if (Str(e, "tool_name") != "AskUserQuestion") { return notice; }
                transcript = ClaudeTranscript(sessionId, known);
                status = null;
                break;
            default:
                transcript = ClaudeTranscript(sessionId, known);
                // Only a notification Claude is actually blocked on marks it waiting; the idle
                // reminder after a finished turn only keeps the registration fresh.
                status = ClaudeNeedsUser(e) ? "waiting" : null;
                break;
        }
        var owner = ClaudeOwner(spooled.Ancestors);
        WriteClaudeRegistration(sessionId, transcript, cwd, owner, status, At(spooled));
        return notice with { OwnerPid = owner > 0 ? owner : null, Status = status };
    }

    HookNotice Codex(SpoolEvent spooled, HookNotice notice)
    {
        var e = spooled.Event;
        if (notice.SessionId is not { } sessionId) { return notice; }
        if (Superseded(CodexPath(sessionId), spooled)) { return notice; }
        var eventName = notice.EventName ?? "";
        // A later lifecycle event retires the prompt, whichever input answered it. SessionStart
        // too: a Codex that died waiting for an approval never sent SessionEnd, and its resumed
        // session must not offer the dead run's approval to be answered into the new process.
        if (eventName is "SessionStart" or "PreToolUse" or "UserPromptSubmit" or "Stop" or "SessionEnd")
        {
            Withdraw(sessionId, spooled.ReceivedAt ?? time.GetUtcNow());
        }

        string status;
        var activity = "";
        var approval = false;
        switch (eventName)
        {
            case "SessionStart":
                (status, activity) = ("idle", "Session started");
                break;
            case "UserPromptSubmit":
                status = "working";
                var prompt = Str(e, "prompt") ?? "";
                if (prompt.Length > 160) { prompt = prompt[..157] + "..."; }
                activity = prompt.Length > 0 ? $"Prompt: {prompt}" : "Working";
                break;
            case "PermissionRequest":
                (status, approval) = ("waiting", true);
                activity = $"Needs approval: {Str(e, "tool_name") ?? ""}";
                if (Command(e) is { Length: > 0 } asked) { activity = $"Needs approval: {Cut(asked, 300)}"; }
                break;
            case "PreToolUse":
                status = "working";
                var tool = Str(e, "tool_name");
                activity = string.IsNullOrEmpty(tool) ? "Running a tool" : $"Running: {tool}";
                if (Command(e) is { Length: > 0 } command) { activity = $"{activity} - {Cut(command, 200)}"; }
                break;
            case "Stop":
                status = "idle";
                activity = "Idle";
                var response = Str(e, "last_assistant_message") ?? "";
                var first = response.Split('\n').Select(l => l.TrimEnd('\r')).FirstOrDefault(l => l.Trim().Length > 0);
                if (first is not null) { activity = Cut(first, 200); }
                break;
            case "SessionEnd":
                status = "ended";
                break;
            default:
                return notice;
        }

        var owner = CodexOwner(sessionId, spooled.Ancestors);
        WriteCodexRegistration(sessionId, Str(e, "transcript_path"), Str(e, "cwd"), Str(e, "model"), status, activity, owner, ended: eventName == "SessionEnd", At(spooled));

        string? approvalId = null;
        if (approval && ApprovalsSettledBefore is { } settled && (spooled.ReceivedAt ?? time.GetUtcNow()) <= settled) { approval = false; }
        if (approval)
        {
            // agent-ha-bridge's generation: the session's node id and the time, so an answer
            // made for one approval is never typed into the next.
            approvalId = $"{NodeId(sessionId)}-{NextDecisionMs()}";
            WriteApprovalMarker(sessionId, approvalId, activity, At(spooled));
        }
        return notice with { OwnerPid = owner > 0 ? owner : null, Status = status, ApprovalId = approvalId };
    }

    // ---------------------------------------------------------------- registrations

    public string ClaudePath(string sessionId) => Path.Combine(ClaudeRoot, RegistrationDiscovery.SafeKey(sessionId) + ".json");

    public string CodexPath(string sessionId) => Path.Combine(CodexRoot, RegistrationDiscovery.SafeKey(sessionId) + ".json");

    public string ApprovalPath(string sessionId) => Path.Combine(CodexRoot, RegistrationDiscovery.SafeKey(sessionId) + ".approval.json");

    // As Write-ClaudeSessionRegistration: a later event without a pid keeps the one known, and
    // one that sets no status keeps the last recorded, with its time.
    void WriteClaudeRegistration(string sessionId, string? transcript, string cwd, int pid, string? status, string at)
    {
        var path = ClaudePath(sessionId);
        using var existing = ReadJson(path);
        var old = existing?.RootElement;
        if (pid <= 0 && old is { } o && Json.Int(o, "ProcessId") is { } kept) { pid = kept; }
        string hookStatus = "", hookStatusAt = "";
        if (!string.IsNullOrEmpty(status)) { (hookStatus, hookStatusAt) = (status, at); }
        else if (old is { } p && p.TryGetProperty("HookStatus", out _))
        {
            (hookStatus, hookStatusAt) = (Json.Str(p, "HookStatus") ?? "", Json.Str(p, "HookStatusAt") ?? "");
        }
        WriteJson(path, w =>
        {
            w.WriteString("SessionId", sessionId);
            w.WriteNumber("ProcessId", pid);
            w.WriteString("TranscriptPath", transcript ?? "");
            w.WriteString("WorkingDirectory", cwd);
            w.WriteString("Updated", at);
            w.WriteString("HookStatus", hookStatus);
            w.WriteString("HookStatusAt", hookStatusAt);
        });
    }

    // As Write-CodexSessionRegistration: later events carry less than SessionStart did, so
    // anything already known is kept rather than blanked.
    void WriteCodexRegistration(string sessionId, string? transcript, string? cwd, string? model, string status, string activity, int pid, bool ended, string at)
    {
        var path = CodexPath(sessionId);
        using var existing = ReadJson(path);
        var old = existing?.RootElement;
        string Keep(string? value, string field) =>
            !string.IsNullOrWhiteSpace(value) ? value : old is { } o && o.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        if (pid <= 0 && old is { } p && Json.Int(p, "ProcessId") is { } kept) { pid = kept; }
        WriteJson(path, w =>
        {
            w.WriteString("SessionId", sessionId);
            w.WriteNumber("ProcessId", pid);
            w.WriteString("TranscriptPath", Keep(transcript, "TranscriptPath"));
            w.WriteString("WorkingDirectory", Keep(cwd, "WorkingDirectory"));
            w.WriteString("Model", Keep(model, "Model"));
            w.WriteString("Status", ended ? "ended" : Keep(status, "Status"));
            w.WriteString("Activity", Keep(activity, "Activity"));
            w.WriteBoolean("Ended", ended);
            w.WriteString("Updated", at);
        });
    }

    // As Write-CodexApprovalMarker: the gate an answer is typed through (AnswerService reads it
    // afresh before typing, and agent-ha-bridge's Set-CodexApprovalAttempt claims it).
    void WriteApprovalMarker(string sessionId, string decisionId, string question, string at) =>
        WriteJson(ApprovalPath(sessionId), w =>
        {
            w.WriteString("SessionId", sessionId);
            w.WriteString("DecisionId", decisionId);
            w.WriteString("Question", question);
            w.WriteString("Created", at);
        });


    // Approval markers a lifecycle event retired but that could not be removed yet (a reader held
    // one without sharing its deletion), by session, with the time of the latest event that
    // retired it. Kept apart from the event, which may be dropped after its retries, and retried
    // every pass (RetryWithdrawals); every Codex answer is refused while any is pending.
    readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _withdrawals = new(StringComparer.Ordinal);

    public bool WithdrawalsPending => !_withdrawals.IsEmpty;

    void Withdraw(string sessionId, DateTimeOffset upTo)
    {
        try
        {
            File.Delete(ApprovalPath(sessionId));
            _withdrawals.TryRemove(sessionId, out _);
        }
        catch (DirectoryNotFoundException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _withdrawals.AddOrUpdate(sessionId, upTo, (_, earlier) => earlier > upTo ? earlier : upTo);
        }
    }

    // True once none is pending. A marker created after the event that retired its session's
    // last one is a new approval, and stays.
    public bool RetryWithdrawals()
    {
        foreach (var (sessionId, upTo) in _withdrawals.ToArray())
        {
            DateTimeOffset? created;
            using (var marker = ReadJson(ApprovalPath(sessionId)))
            {
                if (marker is null && !File.Exists(ApprovalPath(sessionId))) { _withdrawals.TryRemove(sessionId, out _); continue; }
                created = marker is null ? null : Json.Time(marker.RootElement, "Created");
            }
            if (created is { } c && c > upTo) { _withdrawals.TryRemove(sessionId, out _); continue; }
            Withdraw(sessionId, upTo);
        }
        return _withdrawals.IsEmpty;
    }

    // The generation in a DecisionId: the time in ms, as agent-ha-bridge's, but never the same
    // twice. Two approvals handled in one millisecond would otherwise share one, and an answer
    // made for the first would pass the answer path's check for the second.
    long _lastDecisionMs;

    long NextDecisionMs()
    {
        var ms = Math.Max(time.GetUtcNow().ToUnixTimeMilliseconds(), _lastDecisionMs + 1);
        _lastDecisionMs = ms;
        return ms;
    }

    // An event older than what its session's registration already records, by when the hooks
    // ran: a replay (a spooled file whose delete failed, handled again after a restart) or one
    // that arrived late. Applying it would roll the session back, and a replayed
    // PermissionRequest would arm an approval its PreToolUse has already settled.
    static bool Superseded(string registration, SpoolEvent spooled)
    {
        if (spooled.ReceivedAt is not { } received) { return false; }
        using var existing = ReadJson(registration);
        return existing is not null && Json.Time(existing.RootElement, "Updated") is { } updated && received < updated;
    }

    // When the hook ran, not when the event was handled: an event that waited in the spool (the
    // host was down) must not read as newer than what the transcript or a lost hook said since.
    // A clock ahead of this one is not trusted past now.
    string At(SpoolEvent spooled)
    {
        var now = time.GetLocalNow();
        return (spooled.ReceivedAt is { } received && received < now ? received.ToOffset(now.Offset) : now).ToString("o");
    }

    internal static JsonDocument? ReadJson(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 1024 * 1024) { return null; }
            return JsonDocument.Parse(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    // Written whole and renamed into place: discovery reads these every second, and
    // agent-ha-bridge's own reader treats a half-written file as no file.
    static void WriteJson(string path, Action<Utf8JsonWriter> body)
    {
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        var partial = Path.Combine(directory, $".{Path.GetFileName(path)}.{Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(4))}.tmp");
        try
        {
            using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartObject();
                body(writer);
                writer.WriteEndObject();
            }
            File.Move(partial, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(partial); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    // ---------------------------------------------------------------- owning processes

    // As Find-BridgeAgentAncestor with a recorded chain: the nearest ancestor that is a
    // running claude; one that has exited is skipped, not the end of the walk.
    int ClaudeOwner(IReadOnlyList<int> ancestors)
    {
        if (ancestors.Count == 0) { return 0; }
        var running = processes.GetAgentProcesses(AgentKind.Claude).Select(p => p.Id).ToHashSet();
        return ancestors.Take(SpoolContract.MaxAncestors).FirstOrDefault(running.Contains);
    }

    // As Get-CodexOwningProcessId: newer Codex runs hooks under a shared `codex app-server`,
    // which has no console to type into, so the walk goes past it to the interactive codex
    // that owns the window. A window above the app-server can belong to another session, so it
    // is taken only if no other live session has claimed it; failing that, the window this
    // session recorded while it runs, then the newest unclaimed window, then the app-server.
    int CodexOwner(string sessionId, IReadOnlyList<int> ancestors)
    {
        if (ancestors.Count == 0) { return 0; }
        var running = processes.GetAgentProcesses(AgentKind.Codex).ToDictionary(p => p.Id);
        var fallback = 0;
        foreach (var id in ancestors.Take(SpoolContract.MaxAncestors))
        {
            if (!running.TryGetValue(id, out var process)) { continue; }
            if (!IsAppServer(process))
            {
                if (fallback == 0 || !CodexClaimed(id, sessionId, running)) { return id; }
                break;
            }
            if (fallback == 0) { fallback = id; }
        }
        if (fallback == 0) { return 0; }
        var windows = running.Values.Where(p => !IsAppServer(p)).ToList();
        using (var mine = ReadJson(CodexPath(sessionId)))
        {
            if (mine is not null && Json.Int(mine.RootElement, "ProcessId") is { } recorded && recorded > 0
                && windows.FirstOrDefault(w => w.Id == recorded) is { } window && Bound(mine.RootElement, window) && !CodexClaimed(recorded, sessionId, running))
            {
                return recorded;
            }
        }
        var free = windows.Where(w => !CodexClaimed(w.Id, sessionId, running)).OrderByDescending(w => w.StartedAt ?? DateTimeOffset.MinValue).FirstOrDefault();
        return free?.Id ?? fallback;
    }

    // Test-BridgeCodexAppServer: Codex runs it from ~\.codex\packages\app-server-daemon, or as
    // `codex app-server`.
    internal static bool IsAppServer(AgentProcess process) =>
        process.CommandLine is { } line
        && (line.Contains("\\app-server-daemon\\", StringComparison.OrdinalIgnoreCase)
            || line.Contains("/app-server-daemon/", StringComparison.Ordinal)
            || System.Text.RegularExpressions.Regex.IsMatch(line, @"\sapp-server(\s|$)"));

    // Whether a registration's pid still means the process running under it: one written before
    // that process started names an earlier process whose pid was reused. As discovery allows,
    // a second of clock slack.
    static bool Bound(JsonElement registration, AgentProcess process) =>
        process.StartedAt is not { } started || Json.Time(registration, "Updated") is not { } updated || updated >= started - TimeSpan.FromSeconds(1);

    bool CodexClaimed(int pid, string sessionId, IReadOnlyDictionary<int, AgentProcess> running)
    {
        var own = RegistrationDiscovery.SafeKey(sessionId) + ".json";
        foreach (var file in SafeFiles(CodexRoot))
        {
            var name = Path.GetFileName(file);
            if (name.Equals(own, StringComparison.OrdinalIgnoreCase) || name.EndsWith(".approval.json", StringComparison.OrdinalIgnoreCase)) { continue; }
            using var doc = ReadJson(file);
            if (doc is null || doc.RootElement.ValueKind != JsonValueKind.Object) { continue; }
            if (Json.Bool(doc.RootElement, "Ended") == true) { continue; }
            if (Json.Int(doc.RootElement, "ProcessId") == pid && (!running.TryGetValue(pid, out var process) || Bound(doc.RootElement, process))) { return true; }
        }
        return false;
    }

    internal static IEnumerable<string> SafeFiles(string root)
    {
        try { return Directory.Exists(root) ? Directory.GetFiles(root, "*.json").Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase)).ToArray() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    // ---------------------------------------------------------------- the rest

    // Resolve-ClaudeTranscriptPath: the path the event named while it exists, else the
    // session's transcript in its project folder, else none.
    string? ClaudeTranscript(string sessionId, string? known)
    {
        if (!string.IsNullOrWhiteSpace(known) && File.Exists(known)) { return known; }
        var projects = Path.Combine(claudeHome, "projects");
        var name = RegistrationDiscovery.SafeKey(sessionId) + ".jsonl";
        try
        {
            if (!Directory.Exists(projects)) { return null; }
            foreach (var project in Directory.EnumerateDirectories(projects))
            {
                var candidate = Path.Combine(project, name);
                if (File.Exists(candidate)) { return candidate; }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return null;
    }

    // Test-ClaudeNotificationNeedsUser: the type when Claude sends one; otherwise anything but
    // the idle reminder's fixed wording is blocking, so a genuine prompt is never missed.
    internal static bool ClaudeNeedsUser(JsonElement e)
    {
        if (Str(e, "notification_type") is { } type && !string.IsNullOrWhiteSpace(type)) { return ClaudeBlocking.Contains(type); }
        return !System.Text.RegularExpressions.Regex.IsMatch(Str(e, "message") ?? "", @"^\s*Claude is waiting for your input");
    }

    // Get-CopilotMqttNodeId: agent-ha-bridge's per-session node, which its decision ids begin with.
    internal static string NodeId(string sessionId)
    {
        var clean = new string(sessionId.Where(char.IsAsciiLetterOrDigit).ToArray());
        if (clean.Length == 0) { clean = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))[..12]; }
        if (clean.Length > 16) { clean = clean[..16]; }
        return "agent_bridge_" + clean.ToLowerInvariant();
    }

    static string? Command(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("tool_input", out var input) && input.ValueKind == JsonValueKind.Object
            ? Json.Str(input, "command") : null;

    static string Cut(string text, int max) => text.Length > max ? text[..(max - 3)] + "..." : text;

    // PowerShell's [string] of a property: a string as it is, anything else as its text.
    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString(),
                JsonValueKind.Null or JsonValueKind.Undefined => null,
                JsonValueKind.Object or JsonValueKind.Array => null,
                _ => v.GetRawText(),
            }
            : null;
}
