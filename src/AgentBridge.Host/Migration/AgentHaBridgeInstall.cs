using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Hooks;

namespace AgentBridge.Host.Migration;

// One agent-ha-bridge installation, read from what its install.ps1 left on disk
// (hooks/bridge-install-context.ps1, Resolve-BridgeInstallContext):
//
//   <bridgeHome>\installation.json   schemaVersion 1, a 32-hex id, bridgeHome, configPath and
//                                    the agents' homes (copilotHome, claudeHome, codexHome)
//   <bridgeHome>\config.json         its settings (and, in some installs, secrets)
//   <bridgeHome>\hooks\              the PowerShell hooks, daemon and supervisor
//   <bridgeHome>\bin\                agent-bridge-hook(.exe), the Go hook
//   <bridgeHome>\runtime\            spool, heartbeat, registrations, process receipts
//
// Only read. Nothing in it is ever written by a migration except the Codex plugin's
// hooks.json, which is where Codex reads agent-ha-bridge's hook command from, and which is
// restored byte for byte on rollback.
public sealed record AgentHaBridgeInstall(
    string Id,
    string BridgeHome,
    string ConfigPath,
    string Home,
    string CopilotHome,
    string ClaudeHome,
    string CodexHome,
    JsonObject Config)
{
    public string MetadataPath => Path.Combine(BridgeHome, "installation.json");
    public string HooksDir => Path.Combine(BridgeHome, "hooks");
    public string RuntimeRoot => Path.Combine(BridgeHome, "runtime");
    public string GoHook => Path.Combine(BridgeHome, "bin", "agent-bridge-hook.exe");
    public string TaskName => $"AgentBridgeDaemon_{Id}";
    public string DevBoxTaskName => $"AgentBridgeDevBoxKeepAwake_{Id}";

    // The files each agent reads agent-ha-bridge's hook commands from.
    public string CopilotHooksFile => Path.Combine(CopilotHome, "hooks", "decision-notifier.json");
    public string ClaudeSettingsFile => Path.Combine(ClaudeHome, "settings.json");
    public string ClaudeAdapterDir => Path.Combine(ClaudeHome, "ha-bridge");
    public string CodexPluginRoot => Path.Combine(BridgeHome, "codex-bridge", "plugins", "agent-ha-bridge");
    public string CodexHooksFile => Path.Combine(CodexPluginRoot, "hooks.json");
    public string CodexHookScript => Path.Combine(CodexPluginRoot, "hooks", "codex-bridge-hook.ps1");

    // Its owned instruction files and blocks: left in place by a migration, and checked on
    // rollback (Set-BridgeManagedInstructionBlock, Install-BridgeAgentInstructions).
    public IReadOnlyList<string> InstructionFiles =>
    [
        Path.Combine(CopilotHome, "instructions", "agent-ha-bridge.instructions.md"),
        Path.Combine(ClaudeHome, "CLAUDE.md"),
        Path.Combine(CodexHome, "AGENTS.md"),
        Path.Combine(CodexHome, "AGENTS.override.md"),
    ];

    // Where its daemon staged attachments (Get-BridgeAttachmentRootForInstallation).
    public string AttachmentRoot(string? localAppData) =>
        !string.IsNullOrEmpty(localAppData)
            ? Path.Combine(localAppData, "agent-ha-bridge", "attachments", $"install-{Id}")
            : Path.Combine(BridgeHome, "attachments", $"install-{Id}");

    public string WorktreeRoot() =>
        Config["newSession"] is JsonObject s && s["worktreeRoot"] is JsonValue v && v.TryGetValue<string>(out var root) && !string.IsNullOrWhiteSpace(root)
            ? (root.StartsWith('~') ? Path.Combine(Home, root.TrimStart('~').TrimStart('/', '\\')) : root)
            : Path.Combine(Home, "repos", "wt");

    // The installation under bridgeHome (by default ~/.agent-ha-bridge), or why it cannot be
    // migrated. A legacy install without installation.json is refused: migration starts from
    // the 1.33 layout.
    public static (AgentHaBridgeInstall? Install, string? Problem) Read(string bridgeHome)
    {
        // Absolute from here on: the hook pointer and intake.json written from it are refused by
        // their readers otherwise, and only once the daemon has been stopped.
        bridgeHome = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bridgeHome));
        var metadata = Path.Combine(bridgeHome, "installation.json");
        JsonElement record;
        try
        {
            if (!File.Exists(metadata)) { return (null, $"no agent-ha-bridge installation at {bridgeHome} (no installation.json); nothing to migrate"); }
            using var doc = JsonDocument.Parse(File.ReadAllBytes(metadata));
            record = doc.RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, $"{metadata} could not be read ({e.Message}); nothing was changed");
        }
        string? Str(string name) => record.ValueKind == JsonValueKind.Object && record.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var id = Str("id");
        if (record.ValueKind != JsonValueKind.Object || !record.TryGetProperty("schemaVersion", out var schema) || schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1
            || id is not { Length: 32 } || !id.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
        {
            return (null, $"{metadata} is not an installation record this migration knows (schemaVersion 1 with a 32-hex id); nothing was changed");
        }
        if (Str("bridgeHome") is not { } recordedHome || !SamePath(recordedHome, bridgeHome))
        {
            return (null, $"{metadata} belongs to an installation at another path; nothing was changed");
        }
        if (record.TryGetProperty("isolated", out var isolated) && isolated.ValueKind == JsonValueKind.True)
        {
            return (null, $"{bridgeHome} is an isolated (test) installation; only a machine's own installation is migrated");
        }
        var configPath = Path.GetFullPath(Str("configPath") ?? "config.json", bridgeHome);
        JsonObject config;
        try
        {
            // Names case-insensitively, as agent-ha-bridge's Get-BridgeSetting reads them: a writer's
            // "Dashboard"/"Publication" must not look absent to the G7 check. Two names differing
            // only in case are refused, as ConvertFrom-Json refuses them.
            config = JsonNode.Parse(File.ReadAllText(configPath), new JsonNodeOptions { PropertyNameCaseInsensitive = true },
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                ?? throw new JsonException("not a JSON object");
            // A JsonObject builds its lookup lazily; touching it here makes a duplicate fail now.
            Touch(config);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return (null, $"agent-ha-bridge's configuration {configPath} could not be read ({e.Message}), so its publication policy cannot be checked; nothing was changed");
        }
        var home = Str("home") ?? Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(bridgeHome))!;
        return (new AgentHaBridgeInstall(
            id, Path.TrimEndingDirectorySeparator(bridgeHome), configPath, home,
            Str("copilotHome") ?? Path.Combine(home, ".copilot"),
            Str("claudeHome") ?? Path.Combine(home, ".claude"),
            Str("codexHome") ?? Path.Combine(home, ".codex"),
            config), null);
    }

    // Design G7: the publication writer never migrates (it migrates last, at P7, once the
    // dashboard no longer needs it), and neither does a machine whose policy cannot be read,
    // since it cannot tell whether it is the writer. As Get-BridgePublicationSettings. A machine
    // with no dashboard.publication at all publishes nothing shared (that function refuses it),
    // so it is not a writer.
    public string? PublicationProblem()
    {
        if (Config["dashboard"] is not { } dashboard) { return null; }
        if (dashboard is not JsonObject d) { return "agent-ha-bridge's dashboard setting is not an object, so whether this machine is its publication writer cannot be told; migration is refused (G7)"; }
        if (d["publication"] is not { } publication) { return null; }
        if (publication is not JsonObject p) { return "dashboard.publication is not an object, so whether this machine is the publication writer cannot be told; migration is refused (G7)"; }
        string? Identity(string key) => p[key] is JsonValue v && v.TryGetValue<string>(out var s) && System.Text.RegularExpressions.Regex.IsMatch(s, "^[A-Za-z0-9][A-Za-z0-9._:-]{0,79}$") ? s : null;
        var authority = Identity("authority");
        var participant = Identity("participant");
        var writer = Identity("writer");
        var generation = p["generation"] is JsonValue g && g.TryGetValue<long>(out var n) ? n : 0;
        if (authority is null || participant is null || writer is null || generation is < 1 or > int.MaxValue)
        {
            return "dashboard.publication cannot be read (it needs authority, participant, writer and a positive generation), so whether this machine is the publication writer cannot be told; migration is refused (G7)";
        }
        if (string.Equals(participant, writer, StringComparison.Ordinal))
        {
            return $"this machine is the dashboard's publication writer ({writer}); it migrates last, once the dashboard no longer needs it, so migration is refused (G7)";
        }
        return null;
    }

    static void Touch(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                _ = o.ContainsKey("");
                foreach (var (_, child) in o) { Touch(child); }
                break;
            case JsonArray a:
                foreach (var child in a) { Touch(child); }
                break;
        }
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public string Heartbeat => SpoolContract.HeartbeatPath(RuntimeRoot);
}
