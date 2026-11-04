using System.Globalization;
using AgentBridge.Hooks;
using AgentBridge.Host.Core.Intake;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// agent-bridge-host hooks status: how hook handling stands on this machine (docs/host-hooks.md).
// Read-only: it reads the profile's intake record, the heartbeat's time and the hook log, and
// counts the spool only on a profile that has taken it over.
public static class HookCommands
{
    internal static bool? ClaudeModeFor(IntakeState.AgentHomes? saved, SessionSourceOptions current, bool setup)
    {
        if (saved is null) { return current.ClaudeConfigDirExplicit; }
        return saved.ClaudeConfigDirExplicit ?? (setup && Folders.Same(Path.GetFullPath(saved.Claude), Path.GetFullPath(current.ClaudeHome))
            ? current.ClaudeConfigDirExplicit : null);
    }

    public static async Task<int> SetupAsync(IReadOnlyList<string> args, string profileRoot, SessionSourceOptions defaults,
        TextWriter output, TextWriter error, Func<bool> refuseWrite, CancellationToken ct = default)
    {
        var rest = args.Skip(2).ToList();
        if (args.ElementAtOrDefault(1) == "cleanup")
        {
            if (rest.Count != 0 && (rest.Count != 2 || rest[0] != "--profile" || rest[1].StartsWith('-') || !Identity.IsValidProfile(rest[1])))
            {
                error.WriteLine("usage: agent-bridge-host hooks cleanup [--profile <name>]");
                return 2;
            }
            if (refuseWrite()) { return 4; }
            if (!OperatingSystem.IsWindows()) { error.WriteLine("Fresh hook cleanup is Windows-only for now."); return 2; }
            var cleanupProfile = Path.Combine(profileRoot, rest.Count == 0 ? "default" : rest[1]);
            return await CleanupAsync(cleanupProfile, homes => CreateSetup(cleanupProfile, homes), output, error, ct);
        }
        var profile = "default";
        var at = rest.IndexOf("--profile");
        if (at >= 0)
        {
            if (at + 1 >= rest.Count || rest[at + 1].StartsWith('-') || !Identity.IsValidProfile(rest[at + 1])) { error.WriteLine("--profile needs a valid profile name."); return 2; }
            profile = rest[at + 1];
            rest.RemoveRange(at, 2);
        }
        var homes = new Dictionary<string, string>
        {
            ["--copilot-home"] = defaults.CopilotHome, ["--claude-home"] = defaults.ClaudeHome, ["--codex-home"] = defaults.CodexHome,
        };
        var claudeConfigDirExplicit = defaults.ClaudeConfigDirExplicit;
        foreach (var option in homes.Keys.ToArray())
        {
            at = rest.IndexOf(option);
            if (at < 0) { continue; }
            if (at + 1 >= rest.Count || !Path.IsPathFullyQualified(rest[at + 1])) { error.WriteLine($"{option} needs an absolute path."); return 2; }
            homes[option] = rest[at + 1];
            if (option == "--claude-home") { claudeConfigDirExplicit = true; }
            rest.RemoveRange(at, 2);
        }
        var dryRun = rest.Remove("--dry-run");
        if (args.ElementAtOrDefault(1) is not ("install" or "remove") || rest.Count == 0
            || rest.Any(a => a is not ("copilot" or "claude" or "codex")))
        {
            error.WriteLine("usage: agent-bridge-host hooks install|remove <copilot|claude|codex> [...] [--dry-run] [--profile <name>]");
            return 2;
        }
        // Authorization and execution use the same parsed value: an option consumed as another
        // option's value must never make a real write look like a dry run to the guard.
        if (!dryRun && refuseWrite()) { return 4; }
        if (!OperatingSystem.IsWindows()) { error.WriteLine("Fresh hook setup is Windows-only for now."); return 2; }
        var folder = Path.Combine(profileRoot, profile);
        var agentHomes = new IntakeState.AgentHomes(Path.GetFullPath(homes["--copilot-home"]), Path.GetFullPath(homes["--claude-home"]), Path.GetFullPath(homes["--codex-home"]))
        {
            ClaudeConfigDirExplicit = claudeConfigDirExplicit,
        };
        return await RunSetupAsync(CreateSetup(folder, agentHomes), rest.Distinct(), args[1] == "install", dryRun, output, error, ct);
    }

    static StandaloneHooks CreateSetup(string folder, IntakeState.AgentHomes homes)
    {
        if (!OperatingSystem.IsWindows()) { throw new PlatformNotSupportedException("Fresh hook setup is Windows-only for now."); }
        var environment = new Dictionary<string, string> { ["CODEX_HOME"] = homes.Codex };
        return new StandaloneHooks(folder, homes, Path.Combine(AppContext.BaseDirectory, "agent-bridge-host-hook.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agent-ha-bridge"),
            new AgentBridge.Host.Delivery.Windows.AgentHaBridgeProbe().Active, new AgentLocator(),
            new BatchFileRunner(new CommandRunner(environment), environment));
    }

    internal static async Task<int> CleanupAsync(string profile, Func<IntakeState.AgentHomes, StandaloneHooks> create,
        TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        try
        {
            var (state, problem) = IntakeState.Load(profile);
            if (problem is not null) { throw new InvalidOperationException(problem); }
            if (state is null)
            {
                output.WriteLine("No standalone hook intake is recorded; no automatic cleanup was attempted. Profile data is retained for repair.");
                return 0;
            }
            if (!state.Standalone)
            {
                output.WriteLine("Migrated hooks were left alone; use migration status or rollback. Profile data is retained.");
                return 0;
            }
            if (state.Homes is not { } homes) { throw new InvalidDataException("Standalone hook intake has no saved agent homes; inspect it before cleanup."); }
            var setup = create(homes);
            var code = 0;
            foreach (var agent in new[] { "copilot", "claude", "codex" })
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    if (setup.OwnsAgent(agent) && await RunSetupAsync(setup, [agent], false, false, output, error, ct) != 0) { code = 1; }
                }
                catch (Exception e) when (ExpectedSetupFailure(e))
                {
                    error.WriteLine($"Hook cleanup incomplete for {agent}: {e.Message}");
                    code = 1;
                }
            }
            output.WriteLine(code == 0 ? "Owned hook cleanup finished. Profile data and private runtime were retained."
                : "Owned hook cleanup is incomplete. Profile data and failed-agent ownership were retained for repair.");
            return code;
        }
        catch (Exception e) when (ExpectedSetupFailure(e))
        {
            error.WriteLine($"Hook cleanup incomplete: {e.Message}");
            return 1;
        }
    }

    internal static async Task<int> RunSetupAsync(StandaloneHooks setup, IEnumerable<string> agents, bool install,
        bool dryRun, TextWriter output, TextWriter error, CancellationToken ct = default)
    {
        try
        {
            foreach (var agent in agents)
            {
                if (install) { await setup.InstallAsync(agent, dryRun, output, ct); }
                else { await setup.RemoveAsync(agent, dryRun, output, ct); }
            }
            return 0;
        }
        catch (Exception e) when (ExpectedSetupFailure(e))
        {
            error.WriteLine($"Hook setup incomplete: {e.Message}");
            return 1;
        }
    }

    static bool ExpectedSetupFailure(Exception e) =>
        e is IOException or InvalidDataException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException;

    public static int Status(string profileFolder, string defaultRuntime, TextWriter output, DateTimeOffset now)
    {
        var (state, problem) = Directory.Exists(profileFolder) ? IntakeState.Load(profileFolder) : (null, null);
        var runtime = state?.RuntimeRoot ?? defaultRuntime;
        if (state is not null)
        {
            output.WriteLine(state.Standalone
                ? $"hook intake: on, for this fresh installation (runtime {state.RuntimeRoot}), since {state.MigratedAt.ToLocalTime():yyyy-MM-dd HH:mm}"
                : $"hook intake: on, for agent-ha-bridge installation {state.InstallationId} (runtime {state.RuntimeRoot}), since {state.MigratedAt.ToLocalTime():yyyy-MM-dd HH:mm}");
        }
        else
        {
            output.WriteLine(problem ?? "hook intake: off; fresh setup (Windows only): hooks install copilot claude codex; existing agent-ha-bridge installation: migrate");
        }

        var heartbeat = SpoolContract.HeartbeatPath(runtime);
        if (File.Exists(heartbeat))
        {
            var age = now.UtcDateTime - File.GetLastWriteTimeUtc(heartbeat);
            var writer = Safe(() => Shared(heartbeat).Trim()) ?? "?";
            var fresh = age < SpoolContract.HeartbeatMaxAge;
            var whose = writer == Environment.ProcessId.ToString(CultureInfo.InvariantCulture) ? "this process" : $"pid {writer}";
            output.WriteLine($"heartbeat: {(fresh ? "fresh" : "stale")}, {(int)age.TotalSeconds} s old, written by {whose}{(fresh ? "; hooks are spooling" : "; hooks give their fixed reply (or agent-ha-bridge's run PowerShell)")}");
        }
        else
        {
            output.WriteLine("heartbeat: none; hooks give their fixed reply (or agent-ha-bridge's run PowerShell)");
        }

        if (state is not null)
        {
            var spool = SpoolContract.SpoolDirectory(runtime);
            var waiting = Safe(() => Directory.Exists(spool) ? Directory.GetFiles(spool, "*.json").Count(f => f.EndsWith(".json", StringComparison.Ordinal)).ToString(CultureInfo.InvariantCulture) : "0") ?? "?";
            output.WriteLine($"spool: {waiting} event(s) waiting");
        }

        var window = TimeSpan.FromHours(24);
        output.WriteLine($"runs: {HookStats.Read(runtime, now, window).Describe(window)}");
        return 0;
    }

    // Sharing write and delete, as the intake's own reads do: File.ReadAllText would refuse the
    // intake its next heartbeat, or its deletion on a clean stop, for as long as this read lasts.
    static string Shared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    static string? Safe(Func<string> read)
    {
        try { return read(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
