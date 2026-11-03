using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;

namespace AgentBridge.AppleInterop;

// Invented transcript data, parsed by the product SessionSource. No process is launched,
// queried or typed into, and every discovery root is inside this run's private directory.
internal sealed class SyntheticSessions : IProcessInspector
{
    public const string SessionId = "88888888-8888-4888-8888-888888888888";
    public const string DelegateId = "interop-helper";
    const int ProcessId = 100;
    readonly DateTimeOffset started = DateTimeOffset.UtcNow.AddMinutes(-1);
    readonly string events;
    readonly string lockFile;
    int sequence;
    bool removed;

    public SessionSourceOptions Options { get; }

    public SyntheticSessions(string root)
    {
        Options = new SessionSourceOptions
        {
            CopilotHome = Path.Combine(root, "copilot"),
            ClaudeHome = Path.Combine(root, "claude"),
            CodexHome = Path.Combine(root, "codex"),
            ClaudeRegistrationRoots = [Path.Combine(root, "registrations", "claude")],
            CodexRegistrationRoots = [Path.Combine(root, "registrations", "codex")],
            UseFileSystemWatcher = false,
            PollInterval = TimeSpan.FromMilliseconds(200),
            CoalesceInterval = TimeSpan.FromMilliseconds(25),
        };
        var dir = Path.Combine(Options.CopilotHome, "session-state", SessionId);
        Directory.CreateDirectory(dir);
        PrivateFiles.WriteNew(Path.Combine(dir, "workspace.yaml"),
            $"id: {SessionId}\ncwd: {root}\nname: Synthetic Apple interoperability session\ncreated_at: {started:O}\nupdated_at: {started:O}\n");
        lockFile = Path.Combine(dir, $"inuse.{ProcessId}.lock");
        PrivateFiles.WriteNew(lockFile, ProcessId.ToString());
        events = Path.Combine(dir, "events.jsonl");
        PrivateFiles.WriteNew(events, "");
        Append("user.message", new { content = "Exercise the bridge using synthetic transcript data." });
        Append("assistant.turn_start", new { turnId = "main-turn" });
        Append("tool.execution_start", new { toolCallId = "delegate-tool", toolName = "task", arguments = new { name = "Interop helper", description = "Inspect an invented transcript", mode = "background" } });
        Append("subagent.started", new { toolCallId = "delegate-tool", agentName = "general-purpose", agentDisplayName = "Interop helper", executionMode = "background" }, DelegateId);
        Append("user.message", new { content = "Read this synthetic fixture." }, DelegateId);
        Append("assistant.turn_start", new { turnId = "helper-turn" }, DelegateId);
        Append("assistant.message", new { messageId = "helper-message", content = "The delegate is connected through the real .NET host.", reasoningText = "This is invented test content." }, DelegateId);
        Append("tool.execution_start", new { toolCallId = "fixture-tool", toolName = "view", arguments = new { path = "synthetic.txt" } }, DelegateId);
        Append("assistant.message", new { messageId = "main-message", content = "## Real protocol, synthetic session\n\nThe .NET host parsed this temporary transcript." });
    }

    public IReadOnlyList<AgentProcess> GetAgentProcesses(AgentKind agent) =>
        agent == AgentKind.Copilot && !Volatile.Read(ref removed) ? [new(ProcessId, started, "synthetic-copilot")] : [];

    public void Advance()
    {
        if (removed) { throw new InvalidOperationException("the fixture session has been removed"); }
        Append("tool.execution_complete", new { toolCallId = "fixture-tool", success = true, result = new { content = "Synthetic file inspected." } }, DelegateId);
        Append("assistant.message", new { messageId = "helper-done", content = "Synthetic delegate work is complete." }, DelegateId);
        Append("assistant.turn_end", new { turnId = "helper-turn" }, DelegateId);
        Append("assistant.message", new { messageId = "advanced-message", content = "Live update received from the real .NET session parser." });
        Append("assistant.turn_end", new { turnId = "main-turn" });
    }

    public void Remove()
    {
        Volatile.Write(ref removed, true);
        File.Delete(lockFile);
    }

    void Append(string type, object data, string? agentId = null)
    {
        var line = new Dictionary<string, object?>
        {
            ["type"] = type, ["data"] = data, ["id"] = Guid.NewGuid().ToString(),
            ["timestamp"] = started.AddMilliseconds(++sequence).ToString("O"), ["parentId"] = null,
        };
        if (agentId is not null) { line["agentId"] = agentId; }
        File.AppendAllText(events, JsonSerializer.Serialize(line) + "\n");
    }
}

internal static class PrivateFiles
{
    public static string CreateRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "abi-" + Guid.NewGuid().ToString("N")[..12]);
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(root); }
        else { Directory.CreateDirectory(root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
        return root;
    }

    public static void WriteNew(string path, string contents)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        using var file = new FileStream(path, options);
        using var writer = new StreamWriter(file);
        writer.Write(contents);
    }
}
