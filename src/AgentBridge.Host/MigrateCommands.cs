using AgentBridge.Host.Migration;

namespace AgentBridge.Host;

// agent-bridge-host migrate | rollback | migration status (docs/migration.md): moving this
// machine's hooks and settings from agent-ha-bridge to this host, and back. At the machine only,
// and Windows only for now: what migration stops and starts are agent-ha-bridge's scheduled
// tasks, and what tells it agent-ha-bridge has stopped is the Windows probe (G5).
public static class MigrateCommands
{
    public static async Task<int> RunAsync(IReadOnlyList<string> args, string profileFolder, TextWriter output, TextWriter error, Func<MigrationOptions, Migrator> create, CancellationToken cancellationToken = default)
    {
        string? Option(string name)
        {
            for (var i = 0; i < args.Count - 1; i++) { if (args[i] == name) { return args[i + 1]; } }
            return null;
        }
        var dryRun = args.Contains("--dry-run");
        var verb = args.Count > 0 ? args[0] : "";
        if (verb == "migration" && args.ElementAtOrDefault(1) != "status") { verb = ""; }
        // Every other word must be one this verb takes: a mistyped --dry-run must never run the
        // real thing.
        var problem = verb switch
        {
            "migrate" => Unknown(args.Skip(1).ToList(), ["--dry-run"], ["--bridge-home", "--hook"]),
            "rollback" => Unknown(args.Skip(1).ToList(), ["--dry-run"], []),
            "migration" => Unknown(args.Skip(2).ToList(), [], []),
            _ => "",
        };
        if (problem is not null)
        {
            if (problem.Length > 0) { error.WriteLine(problem); }
            error.WriteLine("usage: agent-bridge-host migrate [--dry-run] [--bridge-home <dir>] [--hook <file>] | rollback [--dry-run] | migration status");
            return 2;
        }
        if (!OperatingSystem.IsWindows())
        {
            error.WriteLine("Migration from agent-ha-bridge is Windows-only for now (docs/migration.md).");
            return 2;
        }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var options = new MigrationOptions
        {
            ProfileFolder = profileFolder,
            BridgeHome = Option("--bridge-home") ?? Path.Combine(home, ".agent-ha-bridge"),
            HookFolder = Path.Combine(local, "AgentBridge", "bin"),
            HookSource = Option("--hook") ?? Path.Combine(AppContext.BaseDirectory, "agent-bridge-host-hook.exe"),
            LocalAppData = local,
        };
        var migrator = create(options);
        if (verb == "migration")
        {
            output.WriteLine(migrator.Status());
            return 0;
        }
        var report = verb == "migrate" ? await migrator.MigrateAsync(dryRun, cancellationToken) : await migrator.RollbackAsync(dryRun, cancellationToken);
        output.WriteLine(report.ToString());
        return report.Ok ? 0 : 1;
    }

    // Null when every word is a flag or option this verb takes, each option with a value.
    static string? Unknown(IReadOnlyList<string> rest, string[] flags, string[] options)
    {
        for (var i = 0; i < rest.Count; i++)
        {
            if (flags.Contains(rest[i])) { continue; }
            if (options.Contains(rest[i]))
            {
                if (i + 1 >= rest.Count || rest[i + 1].StartsWith("--", StringComparison.Ordinal) || string.IsNullOrWhiteSpace(rest[i + 1])) { return $"{rest[i]} needs a value."; }
                i++;
                continue;
            }
            return $"Unknown argument: {rest[i]}";
        }
        return null;
    }
}