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

    public SessionSourceOptions Options(long tailBytes = 4 * 1024 * 1024, bool watch = false, long maxSkim = 512L * 1024 * 1024) => new()
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
    };

    public SessionSource Source(long tailBytes = 4 * 1024 * 1024, long maxSkim = 512L * 1024 * 1024) =>
        new(Options(tailBytes, maxSkim: maxSkim), Processes, Time);

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

    public static string ToolDone(string id, int s, bool success = true) =>
        success ? Event("tool.execution_complete", new { toolCallId = id, success, result = new { content = "ok" } }, s)
                : Event("tool.execution_complete", new { toolCallId = id, success, error = new { message = "no", code = "failure" } }, s);

    public static string Answered(string id, int s) =>
        Event("human_response.recorded", new { toolCallId = id, requestId = "r", actor = "human_response", response = new { responseKind = "ask_user", content = new { choice = "a" } } }, s);
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
    public static JsonObject ToolUse(string id, string name, JsonObject input) => new() { ["type"] = "tool_use", ["id"] = id, ["name"] = name, ["input"] = input };

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
