using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;

// Synthetic agent homes under a temp folder. Every transcript here is invented to
// reproduce the shapes the agents write; none is copied from a real session.
internal sealed class Fixture : IDisposable
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 28, 4, 0, 0, TimeSpan.Zero);

    public Fixture()
    {
        Root = Path.Combine(Path.GetTempPath(), "abhc-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public string Root { get; }
    public FakeProcesses Processes { get; } = new();
    public FakeTimeProvider Time { get; } = new(T0.AddHours(1));
    public string CopilotHome => Path.Combine(Root, "home", ".copilot");
    public string ClaudeHome => Path.Combine(Root, "home", ".claude");
    public string CodexHome => Path.Combine(Root, "home", ".codex");
    public string Runtime => Path.Combine(Root, "home", ".agent-ha-bridge", "runtime");
    public string ClaudeRegistrations => Path.Combine(Runtime, "agent-bridge-claude");
    public string CodexRegistrations => Path.Combine(Runtime, "agent-bridge-codex");

    public SessionSourceOptions Options(long tailBytes = 4 * 1024 * 1024, bool watch = false, long maxSkim = 512L * 1024 * 1024, bool remoteApproval = true) => new()
    {
        CopilotHome = CopilotHome,
        ClaudeHome = ClaudeHome,
        CodexHome = CodexHome,
        ClaudeRegistrationRoots = [ClaudeRegistrations],
        CodexRegistrationRoots = [CodexRegistrations],
        UseFileSystemWatcher = watch,
        InitialTailBytes = tailBytes,
        MaxSkimBytes = maxSkim,
        PollInterval = TimeSpan.FromMilliseconds(200),
        CopilotRemoteApproval = remoteApproval,
    };

    public SessionSource Source(long tailBytes = 4 * 1024 * 1024, long maxSkim = 512L * 1024 * 1024, bool remoteApproval = true) =>
        new(Options(tailBytes, maxSkim: maxSkim, remoteApproval: remoteApproval), Processes, Time);

    // session-state/<id>/ with workspace.yaml and, optionally, inuse.<pid>.lock. Returns
    // the events.jsonl path (not created).
    public string CopilotSession(string id, int? lockPid, string? name = "Fixture session", DateTimeOffset? lockWrite = null)
    {
        var dir = Path.Combine(CopilotHome, "session-state", id);
        Directory.CreateDirectory(dir);
        var yaml = $"id: {id}\ncwd: /work/repo\ngit_root: /work/repo\nrepository: octo/repo\nbranch: main\n" +
            (name is null ? "" : $"name: {name}\n") + "created_at: 2026-10-28T03:00:00.000Z\nupdated_at: 2026-10-28T03:30:00.000Z\n";
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), yaml);
        if (lockPid is { } pid)
        {
            var lockFile = Path.Combine(dir, $"inuse.{pid}.lock");
            File.WriteAllText(lockFile, pid.ToString(System.Globalization.CultureInfo.InvariantCulture));
            File.SetLastWriteTimeUtc(lockFile, (lockWrite ?? T0).UtcDateTime);
        }
        return Path.Combine(dir, "events.jsonl");
    }

    public string ClaudeTranscript(string id) => Path.Combine(ClaudeHome, "projects", "-work-repo", id + ".jsonl");

    // Claude Code's own sessions/<pid>.json, as 2.1.312 writes it for every interactive CLI.
    public void ClaudeSessionFile(int pid, string id, DateTimeOffset? startedAt = null, string kind = "interactive", string? fileName = null)
    {
        var dir = Path.Combine(ClaudeHome, "sessions");
        Directory.CreateDirectory(dir);
        var at = (startedAt ?? T0).ToUnixTimeMilliseconds();
        var json = new JsonObject
        {
            ["pid"] = pid,
            ["sessionId"] = id,
            ["cwd"] = "/work/repo",
            ["startedAt"] = at,
            ["kind"] = kind,
            ["entrypoint"] = "cli",
            ["status"] = "idle",
            ["updatedAt"] = at,
        };
        File.WriteAllText(Path.Combine(dir, fileName ?? pid.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".json"), json.ToJsonString());
    }

    public void ClaudeRegistration(string id, int pid, string? transcript, string? hookStatus = null, DateTimeOffset? hookAt = null, string? fileName = null)
    {
        Directory.CreateDirectory(ClaudeRegistrations);
        var json = new JsonObject
        {
            ["SessionId"] = id,
            ["ProcessId"] = pid,
            ["TranscriptPath"] = transcript ?? "",
            ["WorkingDirectory"] = "/work/repo",
            ["Updated"] = T0.ToString("o"),
            ["HookStatus"] = hookStatus ?? "",
            ["HookStatusAt"] = hookAt?.ToString("o") ?? "",
        };
        File.WriteAllText(Path.Combine(ClaudeRegistrations, fileName ?? id + ".json"), json.ToJsonString());
    }

    // Codex's thread-writer-locks/<id>.lock, held open by whichever process writes the thread.
    public string CodexLock(string id)
    {
        var dir = Path.Combine(CodexHome, "thread-writer-locks");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, id + ".lock");
        File.WriteAllText(path, "");
        return path;
    }

    public string CodexTranscript(string id) => Path.Combine(CodexHome, "sessions", "2026", "10", "28", $"rollout-2026-10-28T04-00-00-{id}.jsonl");

    public void CodexRegistration(string id, int pid, string? transcript, string status = "working", bool ended = false, DateTimeOffset? updated = null)
    {
        Directory.CreateDirectory(CodexRegistrations);
        var json = new JsonObject
        {
            ["SessionId"] = id,
            ["ProcessId"] = pid,
            ["TranscriptPath"] = transcript ?? "",
            ["WorkingDirectory"] = "/work/codex",
            ["Model"] = "gpt-fixture",
            ["Status"] = status,
            ["Activity"] = "",
            ["Ended"] = ended,
            ["Updated"] = (updated ?? T0).ToString("o"),
        };
        File.WriteAllText(Path.Combine(CodexRegistrations, id + ".json"), json.ToJsonString());
    }

    // agent-ha-bridge's <id>.approval.json, as Write-CodexApprovalMarker writes it, and as
    // Set-CodexApprovalAttempt rewrites it once it has typed into the approval.
    public string CodexApprovalMarker(string id, string decisionId, string question = "Needs approval: git push", DateTimeOffset? created = null, JsonNode? attempted = null, string? owner = null)
    {
        Directory.CreateDirectory(CodexRegistrations);
        var json = new JsonObject
        {
            ["SessionId"] = owner ?? id,
            ["DecisionId"] = decisionId,
            ["Question"] = question,
            ["Created"] = (created ?? T0).ToString("o"),
        };
        if (attempted is not null)
        {
            json["DeliveryAttempted"] = attempted;
            json["DeliveryChoice"] = "Approve";
            json["DeliveryOutcome"] = "unconfirmed";
        }
        var path = Path.Combine(CodexRegistrations, id + ".approval.json");
        File.WriteAllText(path, json.ToJsonString());
        return path;
    }

    public static void Append(string path, params string[] lines)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.AppendAllText(path, string.Concat(lines.Select(l => l + "\n")));
    }

    // Every file and folder under the root with its size, write time and content hash.
    public SortedDictionary<string, string> Listing()
    {
        var listing = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(Root, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(Root, entry);
            if (Directory.Exists(entry)) { listing[relative] = "dir " + Directory.GetLastWriteTimeUtc(entry).Ticks; continue; }
            var info = new FileInfo(entry);
            var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry)));
            listing[relative] = $"{info.Length} {info.LastWriteTimeUtc.Ticks} {hash}";
        }
        return listing;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}

internal sealed class FakeProcesses : IProcessInspector
{
    readonly Dictionary<AgentKind, List<AgentProcess>> _live = [];

    public void Run(AgentKind agent, int pid, string? commandLine = null, DateTimeOffset? startedAt = null)
    {
        if (!_live.TryGetValue(agent, out var list)) { _live[agent] = list = []; }
        list.RemoveAll(p => p.Id == pid);
        list.Add(new AgentProcess(pid, startedAt ?? Fixture.T0.AddHours(-1), commandLine));
    }

    public void Exit(AgentKind agent, int pid)
    {
        if (_live.TryGetValue(agent, out var list)) { list.RemoveAll(p => p.Id == pid); }
    }

    public IReadOnlyList<AgentProcess> GetAgentProcesses(AgentKind agent) => _live.TryGetValue(agent, out var list) ? [.. list] : [];

    // Who has each file open, by full path, and how often the OS was asked.
    public Dictionary<string, int[]> Holders { get; } = new(StringComparer.OrdinalIgnoreCase);
    public int HolderQueries { get; private set; }

    public IReadOnlyList<int> FileHolders(string path)
    {
        HolderQueries++;
        return Holders.TryGetValue(path, out var pids) ? pids : [];
    }
}

// Line builders, one per agent, reproducing each transcript's shape.
internal static class Copilot
{
    public static string At(int second) => Fixture.T0.AddSeconds(second).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    public static string Event(string type, object data, int second, string? agentId = null)
    {
        var line = new JsonObject { ["type"] = type, ["data"] = JsonSerializer.SerializeToNode(data) };
        line["id"] = Guid.NewGuid().ToString();
        line["timestamp"] = At(second);
        line["parentId"] = null;
        if (agentId is not null) { line["agentId"] = agentId; }
        return line.ToJsonString();
    }

    public static string User(string text, int s, string? agentId = null) => Event("user.message", new { content = text, attachments = Array.Empty<object>(), delivery = "steering" }, s, agentId);
    public static string TurnStart(int s, string? agentId = null) => Event("assistant.turn_start", new { turnId = "t" + s }, s, agentId);
    public static string TurnEnd(int s, string? agentId = null) => Event("assistant.turn_end", new { turnId = "t" + s }, s, agentId);

    public static string Assistant(string content, int s, string model = "fixture-model", string? reasoning = null, string[]? tools = null, string? agentId = null) =>
        Event("assistant.message", new
        {
            messageId = "m" + s,
            model,
            content,
            reasoningText = reasoning,
            toolRequests = (tools ?? []).Select(t => new { toolCallId = t, name = "powershell", arguments = new { } }).ToArray(),
        }, s, agentId);

    public static string ToolStart(string id, string name, object arguments, int s, string? agentId = null) =>
        Event("tool.execution_start", new { toolCallId = id, toolName = name, arguments }, s, agentId);

    // permission.requested as Copilot CLI 1.0.10x writes it: what is asked (permissionRequest)
    // and what its prompt draws (promptRequest), each naming the tool call it gates. The
    // prompts are synthesized with the field sets real transcripts carry, never copied.
    public static string Permission(string requestId, string toolCallId, JsonObject prompt, int s, string? agentId = null)
    {
        var drawn = prompt.DeepClone().AsObject();
        drawn["toolCallId"] = toolCallId;
        var asked = drawn.DeepClone().AsObject();
        // The shell command and path prompts both come from a shell permission request.
        if (drawn["kind"]?.GetValue<string>() is "commands" or "path")
        {
            asked = new JsonObject
            {
                ["kind"] = "shell",
                ["toolCallId"] = toolCallId,
                ["fullCommandText"] = drawn["fullCommandText"]?.DeepClone() ?? "Get-Content C:\\elsewhere\\notes.txt",
                ["intention"] = drawn["intention"]?.DeepClone() ?? "Read notes",
                ["commands"] = new JsonArray(new JsonObject { ["identifier"] = "git push", ["readOnly"] = false }),
                ["possiblePaths"] = new JsonArray(),
                ["possibleUrls"] = new JsonArray(),
                ["hasWriteFileRedirection"] = false,
                ["canOfferSessionApproval"] = true,
            };
        }
        var line = JsonNode.Parse(Event("permission.requested", new { requestId }, s, agentId))!.AsObject();
        line["data"]!["permissionRequest"] = asked;
        line["data"]!["promptRequest"] = drawn;
        return line.ToJsonString();
    }

    public static string PermissionDone(string requestId, string toolCallId, string kind, int s, string? feedback = null) =>
        Event("permission.completed", new { requestId, toolCallId, result = feedback is null ? (object)new { kind } : new { kind, feedback } }, s);

    // One of each kind of prompt this host drives.
    public static readonly IReadOnlyDictionary<string, JsonObject> Prompts = new Dictionary<string, JsonObject>
    {
        ["commands"] = new() { ["kind"] = "commands", ["fullCommandText"] = "git push origin main --force-with-lease", ["intention"] = "Push the branch", ["commandIdentifiers"] = new JsonArray("git push"), ["canOfferSessionApproval"] = true },
        ["path"] = new() { ["kind"] = "path", ["accessKind"] = "read", ["paths"] = new JsonArray("C:\\elsewhere\\notes.txt", "C:\\elsewhere\\more") },
        ["write"] = new() { ["kind"] = "write", ["intention"] = "Edit file", ["fileName"] = "src\\app.js", ["diff"] = "--- a/src/app.js\n+++ b/src/app.js\n@@ -1 +1 @@\n-old()\n+fresh()", ["newFileContents"] = "fresh()\n", ["canOfferSessionApproval"] = true },
        ["read"] = new() { ["kind"] = "read", ["intention"] = "Read the settings", ["path"] = "C:\\Users\\someone\\settings.json" },
        ["url"] = new() { ["kind"] = "url", ["url"] = "https://example.com/docs/page", ["intention"] = "Fetch the docs" },
        ["mcp"] = new() { ["kind"] = "mcp", ["serverName"] = "github", ["toolName"] = "get_file_contents", ["toolTitle"] = "Get file contents", ["args"] = new JsonObject { ["owner"] = "octo", ["path"] = "README.md" } },
        ["custom-tool"] = new() { ["kind"] = "custom-tool", ["toolName"] = "deploy_site", ["toolDescription"] = "Deploys the site", ["args"] = new JsonObject { ["env"] = "prod" } },
    };

    public static string ToolDone(string id, int s, bool success = true, string? agentId = null) =>
        success ? Event("tool.execution_complete", new { toolCallId = id, success, result = new { content = "ok" } }, s, agentId)
                : Event("tool.execution_complete", new { toolCallId = id, success, error = new { message = "no", code = "failure" } }, s, agentId);

    // A tool call's result as Copilot records it, e.g. a shell tool's marker line.
    public static string ToolResult(string id, string content, int s, string? agentId = null) =>
        Event("tool.execution_complete", new { toolCallId = id, success = true, result = new { content } }, s, agentId);

    public static string Answered(string id, int s) =>
        Event("human_response.recorded", new { toolCallId = id, requestId = "r", actor = "human_response", response = new { responseKind = "ask_user", content = new { choice = "a" } } }, s);

    // A task agent: stamped with its own agentId, keyed by the task tool call that started it.
    public static string SubagentStarted(string callId, string agentId, int s, string mode = "background") =>
        Event("subagent.started", new { toolCallId = callId, agentName = "general-purpose", agentDisplayName = "helper-" + agentId, agentDescription = "Help out", model = "fixture-model", agentType = "general-purpose", executionMode = mode }, s, agentId);

    public static string SubagentCompleted(string callId, string agentId, int s) =>
        Event("subagent.completed", new { toolCallId = callId, agentName = "general-purpose", agentDisplayName = "helper-" + agentId, model = "fixture-model", totalToolCalls = 3, totalTokens = 1000, durationMs = 5000 }, s, agentId);

    // Notifications are the session's own events: the subject is named inside kind.
    public static string AgentIdle(string agentId, int s) =>
        Event("system.notification", new { content = $"<system_notification>\nAgent \"helper-{agentId}\" (general-purpose) has finished processing and is now idle.\n</system_notification>", kind = new { type = "agent_idle", agentId, agentType = "general-purpose", displayName = "helper-" + agentId } }, s);

    public static string ShellCompleted(string shellId, int s) =>
        Event("system.notification", new { content = $"<system_notification>\nShell command \"Fixture\" (shellId: {shellId}) has completed successfully.\n</system_notification>", kind = new { type = "shell_completed", shellId, exitCode = 0, description = "Fixture" } }, s);
}

internal static class Claude
{
    static JsonObject Entry(string type, JsonNode? message, int s, bool sidechain = false) => new()
    {
        ["parentUuid"] = null,
        ["isSidechain"] = sidechain,
        ["type"] = type,
        ["message"] = message,
        ["uuid"] = Guid.NewGuid().ToString(),
        ["timestamp"] = Copilot.At(s),
        ["cwd"] = "/work/repo",
        ["sessionId"] = "claude-session",
        ["gitBranch"] = "feature",
    };

    public static string User(string text, int s, bool sidechain = false) =>
        Entry("user", new JsonObject { ["role"] = "user", ["content"] = text }, s, sidechain).ToJsonString();

    public static string ToolResult(string id, int s, bool error = false) =>
        Entry("user", new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = "result", ["is_error"] = error }),
        }, s).ToJsonString();

    public static string Assistant(int s, string? stopReason, params JsonObject[] blocks) => AssistantMessage(null, s, stopReason, blocks);

    // AskUserQuestion's input: one entry per question, as (text, multiSelect, options).
    public static JsonObject Ask(params (string Question, bool Multi, string[] Options)[] questions) => new()
    {
        ["questions"] = new JsonArray([.. questions.Select(q => (JsonNode)new JsonObject
        {
            ["question"] = q.Question,
            ["header"] = q.Question.Split(' ')[0],
            ["multiSelect"] = q.Multi,
            ["options"] = new JsonArray([.. q.Options.Select(o => (JsonNode)new JsonObject { ["label"] = o, ["description"] = "About " + o })]),
        })]),
    };

    // The answer to an AskUserQuestion as Claude Code records it: the tool_result's text,
    // '"question"="answer"' per question, and the entry's toolUseResult with the answers by
    // question text (left out, as an older build would).
    public static string Answered(string id, int s, IReadOnlyDictionary<string, string> answers, bool structured = true, bool error = false)
    {
        var text = error
            ? "The user doesn't want to proceed with this tool use."
            : $"User has answered your questions: {string.Join(", ", answers.Select(a => $"\"{a.Key}\"=\"{a.Value}\""))}. You can now continue with the user's answers in mind.";
        var e = Entry("user", new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray(new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = id, ["content"] = text, ["is_error"] = error }),
        }, s);
        if (structured && !error)
        {
            var map = new JsonObject();
            foreach (var (k, v) in answers) { map[k] = v; }
            e["toolUseResult"] = new JsonObject { ["questions"] = new JsonArray(), ["answers"] = map };
        }
        return e.ToJsonString();
    }

    // One API message streamed as several entries shares its message id.
    public static string AssistantMessage(string? id, int s, string? stopReason, params JsonObject[] blocks) =>
        Entry("assistant", new JsonObject
        {
            ["id"] = id,
            ["model"] = "claude-fixture",
            ["role"] = "assistant",
            ["content"] = new JsonArray([.. blocks]),
            ["stop_reason"] = stopReason,
        }, s).ToJsonString();

    public static JsonObject Text(string text) => new() { ["type"] = "text", ["text"] = text };
    public static JsonObject Thinking(string text) => new() { ["type"] = "thinking", ["thinking"] = text };
    public static JsonObject ToolUse(string id, string name, JsonObject input) => new() { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input.DeepClone() };

    public static string System(string subtype, int s)
    {
        var e = Entry("system", null, s);
        e.Remove("message");
        e["subtype"] = subtype;
        return e.ToJsonString();
    }
}

internal static class Codex
{
    public static string Line(string type, object payload, int s) =>
        new JsonObject { ["timestamp"] = Copilot.At(s), ["type"] = type, ["payload"] = JsonSerializer.SerializeToNode(payload) }.ToJsonString();

    public static string Event(object payload, int s) => Line("event_msg", payload, s);
    public static string Item(object payload, int s) => Line("response_item", payload, s);
}
