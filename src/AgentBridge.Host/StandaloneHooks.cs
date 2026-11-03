using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Hooks;
using AgentBridge.Host.Core.Intake;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Migration;

namespace AgentBridge.Host;

// Explicit opt-in for machines with no legacy installation. Migration keeps its own
// registrations and shared runtime; this path never writes anything under the legacy home.
public sealed class StandaloneHooks(
    string profile, IntakeState.AgentHomes homes, string hookSource, string legacyHome,
    Func<string?> active, IAgentLocator locator, ICommandRunner codex)
{
    const string OwnerFile = "agent-bridge-owner.json";
    const string Marketplace = "agent-bridge";
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };
    string HookFolder => Path.Combine(profile, "hooks");
    string Hook => Path.Combine(HookFolder, "agent-bridge-host-hook.exe");
    string Runtime => Path.Combine(profile, "runtime");
    string CodexMarketplace => Path.Combine(HookFolder, "codex");

    string Home(string agent) => agent switch
    {
        "copilot" => homes.Copilot,
        "claude" => homes.Claude,
        "codex" => homes.Codex,
        _ => throw new ArgumentException("Choose copilot (also Agency), claude or codex."),
    };

    static JsonObject Read(string path) => JsonNode.Parse(Registrations.Decode(File.ReadAllBytes(path)),
        documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
        ?? throw new InvalidDataException($"{path} is not a JSON object; it was left alone");

    static byte[] Bytes(JsonNode value) => Encoding.UTF8.GetBytes(value.ToJsonString(Pretty) + Environment.NewLine);

    static void Write(string path, JsonNode value) => DurableFile.Replace(path, Bytes(value), MigrationRecord.Hash(path));

    void Preflight(string agent, bool removing)
    {
        _ = Home(agent);
        if (active() is { } why) { throw new InvalidOperationException($"agent-ha-bridge is active or cannot be checked ({why}); use Migrate instead"); }
        // Even a stopped legacy install must go through its reversible migration, not
        // acquire a second set of hooks when its task next starts.
        if (Directory.Exists(legacyHome)) { throw new InvalidOperationException("an agent-ha-bridge directory exists and may retain legacy state; inspect it for migration, not fresh hook setup"); }
        var (state, problem) = IntakeState.Load(profile);
        if (problem is not null) { throw new InvalidOperationException(problem); }
        if (state is { Standalone: false } || File.Exists(Path.Combine(profile, MigrationRecord.FileName)))
        {
            throw new InvalidOperationException("this profile belongs to a migration; use migration status or rollback");
        }
        if (state is not null && (state.Homes != homes || !Same(state.RuntimeRoot, Runtime) || !Same(state.BridgeHome, profile)))
        {
            throw new InvalidOperationException("this profile already uses different agent homes or a different runtime");
        }
        var owner = Path.Combine(Home(agent), OwnerFile);
        if (File.Exists(owner))
        {
            var record = Read(owner);
            if ((int?)record["version"] != 1 || (string?)record["profile"] is not { } owned || !Same(owned, profile))
            {
                throw new InvalidOperationException($"{owner} belongs to another installation; it was left alone");
            }
        }
        else if (removing) { throw new InvalidOperationException($"{agent} has no hook registration owned by this profile"); }
        if (!removing && !File.Exists(hookSource)) { throw new FileNotFoundException("The native hook is missing beside the host; reinstall the app.", hookSource); }
        foreach (var path in new[] { Path.Combine(homes.Copilot, "hooks", "decision-notifier.json"), Path.Combine(homes.Claude, "ha-bridge"),
            Path.Combine(homes.Codex, "agent-ha-bridge-owner.json") })
        {
            if (File.Exists(path) || Directory.Exists(path)) { throw new InvalidOperationException($"{path} is a legacy registration; use Migrate"); }
        }
    }

    static bool Same(string left, string right)
    {
        static string Normal(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            OperatingSystem.IsWindows() && path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path));
        return string.Equals(Normal(left), Normal(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    public async Task InstallAsync(string agent, bool dryRun, TextWriter output, CancellationToken ct = default)
    {
        Preflight(agent, removing: false);
        using var held = dryRun ? null : Lock();
        Preflight(agent, removing: false);
        var launcher = Launchers.Find(agent)!;
        var executable = locator.Find(launcher, AgentConfig.Default);
        if (agent == "copilot") { executable ??= locator.Find(Launchers.Find("agency")!, AgentConfig.Default); }
        if (executable is null) { throw new InvalidOperationException($"{agent} is not installed; no hooks were registered"); }
        var owner = Path.Combine(Home(agent), OwnerFile);
        var ownerBefore = MigrationRecord.Hash(owner);
        var owned = ownerBefore is not null;
        var marketplacePresent = false;
        var path = agent == "copilot" ? Path.Combine(homes.Copilot, "hooks", "agent-bridge.json") : Path.Combine(homes.Claude, "settings.json");
        JsonObject? root = null;
        string? before = null;
        if (agent != "codex")
        {
            before = MigrationRecord.Hash(path);
            root = before is null ? new JsonObject() : Read(path);
            if (agent == "copilot" && before is not null && !owned) { throw new InvalidOperationException($"{path} has no ownership record; it was left alone"); }
            if (agent == "claude" && !owned && HasBridgeCommands(root))
            {
                throw new InvalidOperationException($"{path} already mentions bridge hooks without this profile's ownership record; it was left alone");
            }
            Register(root, agent);
        }
        else
        {
            if (Hook.Any(char.IsWhiteSpace)) { throw new InvalidOperationException("Codex cannot run a hook from a path with spaces; choose a profile root without spaces"); }
            // Codex refuses a CODEX_HOME that does not exist yet. There can be no configured
            // marketplace there; writing ownership below creates it before the first CLI write.
            marketplacePresent = Directory.Exists(homes.Codex) && await CheckMarketplaceAsync(executable, owned, ct);
        }
        output.WriteLine($"{(dryRun ? "Would register" : "Registering")} {agent} hooks for {profile}; runtime {Runtime}.");
        if (dryRun) { return; }
        if (agent == "claude" && before is not null)
        {
            var backup = Path.Combine(HookFolder, "backups", "claude-settings.json");
            if (!File.Exists(backup)) { DurableFile.Replace(backup, File.ReadAllBytes(path), null); }
        }
        // Save ownership first: an interrupted install can be safely retried or removed.
        DurableFile.Replace(owner, Bytes(new JsonObject { ["version"] = 1, ["profile"] = profile }), ownerBefore);
        Refresh(profile, hookSource);
        if (root is not null) { DurableFile.Replace(path, Bytes(root), before); }
        else
        {
            WriteCodexPlugin();
            if (!marketplacePresent) { await RunCodexAsync(executable, ["plugin", "marketplace", "add", CodexMarketplace], ct); }
            await RunCodexAsync(executable, ["plugin", "add", $"{Marketplace}@{Marketplace}"], ct);
        }
        var (state, _) = IntakeState.Load(profile);
        (state ?? new IntakeState(Runtime, profile, Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow)
        {
            Standalone = true, Homes = homes,
        }).Save(profile);
        output.WriteLine("Registered. Restart the host from the Agent Bridge app, then start new agent sessions to load the hooks.");
        if (agent == "codex") { output.WriteLine("Start Codex at this machine and approve its hook trust prompt once; until then Codex silently skips its hooks."); }
    }

    public async Task RemoveAsync(string agent, bool dryRun, TextWriter output, CancellationToken ct = default)
    {
        Preflight(agent, removing: true);
        using var held = dryRun ? null : Lock();
        Preflight(agent, removing: true);
        string? executable = null;
        var present = false;
        JsonObject? root = null;
        string? before = null;
        var path = agent == "copilot" ? Path.Combine(homes.Copilot, "hooks", "agent-bridge.json") : Path.Combine(homes.Claude, "settings.json");
        if (agent == "codex")
        {
            executable = locator.Find(Launchers.Find("codex")!, AgentConfig.Default)
                ?? throw new InvalidOperationException("Codex is unavailable; its plugin and ownership record were preserved");
            present = await CheckMarketplaceAsync(executable, owned: true, ct);
        }
        else
        {
            before = MigrationRecord.Hash(path);
            if (before is not null) { root = Read(path); Unregister(root, agent); }
        }
        output.WriteLine($"{(dryRun ? "Would remove" : "Removing")} only {agent} hooks owned by {profile}.");
        if (dryRun) { return; }
        if (agent == "codex")
        {
            if (present)
            {
                await RunCodexAsync(executable!, ["plugin", "remove", $"{Marketplace}@{Marketplace}"], ct);
                await RunCodexAsync(executable!, ["plugin", "marketplace", "remove", Marketplace], ct);
            }
        }
        else if (root is not null)
        {
            if (agent == "copilot" && root.All(p => p.Key == "version")) { DurableFile.Remove(path, before); }
            else { DurableFile.Replace(path, Bytes(root), before); }
        }
        var owner = Path.Combine(Home(agent), OwnerFile);
        // A dedicated Copilot file with somebody else's additions must remain attributable,
        // otherwise reinstall would have to claim an unowned file or permanently refuse it.
        if (agent != "copilot" || !File.Exists(path)) { DurableFile.Remove(owner, MigrationRecord.Hash(owner)); }
        // Keep the private runtime and binary: sessions already open retain their commands.
        output.WriteLine("Removed. Existing sessions keep their hooks until they end; other settings and Codex hook trust were left alone.");
    }

    FileStream Lock()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(profile)!);
        return new FileStream(Path.Combine(Path.GetDirectoryName(profile)!, "migration.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
    }

    public static void Refresh(string profile, string source)
    {
        var folder = Path.Combine(profile, "hooks");
        var hook = Path.Combine(folder, "agent-bridge-host-hook.exe");
        if (MigrationRecord.Hash(hook) != MigrationRecord.Hash(source))
        {
            DurableFile.Write(hook, File.ReadAllBytes(source));
        }
        Write(Path.Combine(folder, HookRunner.PointerFile), new JsonObject { ["version"] = 1, ["runtimeRoot"] = Path.Combine(profile, "runtime") });
    }

    void Register(JsonObject root, string agent)
    {
        Unregister(root, agent);
        if (agent == "copilot") { root["version"] = 1; }
        var hooks = Object(root, "hooks");
        if (agent == "copilot")
        {
            Add(hooks, "preToolUse", new JsonObject { ["type"] = "command", ["matcher"] = "ask_user", ["timeoutSec"] = 120, ["exec"] = Hook, ["args"] = new JsonArray("copilot", "ask_user") });
            Add(hooks, "agentStop", new JsonObject { ["type"] = "command", ["timeoutSec"] = 30, ["exec"] = Hook, ["args"] = new JsonArray("copilot", "agent_stop") });
            Add(hooks, "notification", new JsonObject { ["type"] = "command", ["matcher"] = "permission_prompt", ["timeoutSec"] = 15, ["exec"] = Hook, ["args"] = new JsonArray("copilot", "permission") });
        }
        else
        {
            foreach (var (name, matcher, hook, timeout) in new (string, string, string, int)[]
            {
                ("PreToolUse", "AskUserQuestion", "ask", 30), ("Notification", "", "notification", 30),
                ("Stop", "", "stop", 30), ("SessionStart", "", "register", 15), ("UserPromptSubmit", "", "register", 15),
            })
            {
                Add(hooks, name, new JsonObject { ["matcher"] = matcher, ["hooks"] = new JsonArray(new JsonObject
                {
                    ["type"] = "command", ["command"] = $"\"{Hook}\" claude {hook}", ["timeout"] = timeout,
                }) });
            }
        }
    }

    void Unregister(JsonObject root, string agent)
    {
        if (root["hooks"] is null) { return; }
        var hooks = Object(root, "hooks");
        foreach (var (name, value) in hooks.ToList())
        {
            if (value is not JsonArray entries) { throw new InvalidDataException($"hooks.{name} is not an array; it was left alone"); }
            foreach (var entry in entries.OfType<JsonObject>().ToList())
            {
                if (agent == "copilot")
                {
                    if ((string?)entry["exec"] is { } exec && Same(exec, Hook)) { entries.Remove(entry); }
                }
                else if (entry["hooks"] is JsonArray inner)
                {
                    foreach (var hook in inner.OfType<JsonObject>().ToList())
                    {
                        if ((string?)hook["command"] is { } command && new[] { "ask", "notification", "stop", "register" }.Any(n => command == $"\"{Hook}\" claude {n}")) { inner.Remove(hook); }
                    }
                    if (inner.Count == 0) { entries.Remove(entry); }
                }
            }
            if (entries.Count == 0) { hooks.Remove(name); }
        }
        if (hooks.Count == 0) { root.Remove("hooks"); }
    }

    static JsonObject Object(JsonObject parent, string key)
    {
        if (parent[key] is null) { parent[key] = new JsonObject(); }
        return parent[key] as JsonObject ?? throw new InvalidDataException($"{key} is not an object; it was left alone");
    }

    static bool HasBridgeCommands(JsonObject root)
    {
        if (root["hooks"] is not JsonObject hooks) { return false; }
        return hooks.Select(p => p.Value).OfType<JsonArray>().SelectMany(a => a.OfType<JsonObject>())
            .Select(g => g["hooks"]).OfType<JsonArray>().SelectMany(a => a.OfType<JsonObject>())
            .Select(h => h["command"]).OfType<JsonValue>()
            .Any(v => v.TryGetValue<string>(out var command) && (
                command.Contains("agent-bridge", StringComparison.OrdinalIgnoreCase)
                || command.Contains("ha-bridge", StringComparison.OrdinalIgnoreCase)));
    }

    static void Add(JsonObject hooks, string name, JsonObject entry)
    {
        if (hooks[name] is null) { hooks[name] = new JsonArray(); }
        if (hooks[name] is not JsonArray entries) { throw new InvalidDataException($"hooks.{name} is not an array; it was left alone"); }
        entries.Add(entry);
    }

    async Task<bool> CheckMarketplaceAsync(string executable, bool owned, CancellationToken ct)
    {
        var text = await RunCodexAsync(executable, ["plugin", "marketplace", "list", "--json"], ct);
        var listing = JsonNode.Parse(text) as JsonObject;
        if (listing?["marketplaces"] is not JsonArray marketplaces) { throw new InvalidDataException("Codex returned an unreadable marketplace list; nothing was registered"); }
        var matches = marketplaces.OfType<JsonObject>().Where(m => (string?)m["name"] == Marketplace).ToList();
        if (matches.Count > 1 || (matches.Count == 1 && (!owned || (string?)matches[0]["root"] is not { } root || !Same(root, CodexMarketplace))))
        {
            throw new InvalidOperationException("the Codex agent-bridge marketplace belongs to another installation; it was left alone");
        }
        return matches.Count == 1;
    }

    async Task<string> RunCodexAsync(string executable, IReadOnlyList<string> args, CancellationToken ct)
    {
        var result = await codex.RunAsync(executable, args, TimeSpan.FromSeconds(30), ct);
        if (!result.Ran || result.TimedOut || result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Codex {string.Join(' ', args.Take(3))} failed ({(result.TimedOut ? "timed out" : $"exit {result.ExitCode}")}); the hook operation is incomplete; fix the Codex installation and retry");
        }
        return result.Output;
    }

    void WriteCodexPlugin()
    {
        var plugin = Path.Combine(CodexMarketplace, "plugins", Marketplace);
        Write(Path.Combine(plugin, ".codex-plugin", "plugin.json"), new JsonObject
        {
            ["name"] = Marketplace, ["version"] = "1.0.0", ["description"] = "Agent Bridge hooks", ["hooks"] = "./hooks.json",
        });
        var hooks = new JsonObject();
        foreach (var name in new[] { "SessionStart", "UserPromptSubmit", "PermissionRequest", "PreToolUse", "Stop", "SessionEnd" })
        {
            Add(hooks, name, new JsonObject { ["hooks"] = new JsonArray(new JsonObject
            {
                ["type"] = "command", ["command"] = $"{Hook} codex hook", ["timeout"] = name == "SessionEnd" ? 3 : 20,
            }) });
        }
        Write(Path.Combine(plugin, "hooks.json"), new JsonObject { ["description"] = "Agent Bridge", ["hooks"] = hooks });
        Write(Path.Combine(CodexMarketplace, ".agents", "plugins", "marketplace.json"), new JsonObject
        {
            ["name"] = Marketplace, ["interface"] = new JsonObject { ["displayName"] = "Agent Bridge" },
            ["plugins"] = new JsonArray(new JsonObject
            {
                ["name"] = Marketplace, ["source"] = new JsonObject { ["source"] = "local", ["path"] = "./plugins/agent-bridge" },
                ["policy"] = new JsonObject { ["installation"] = "AVAILABLE", ["authentication"] = "ON_USE" }, ["category"] = "Productivity",
            }),
        });
    }
}
