namespace AgentBridge.Host.Delivery.Launch;

// Where each agent's executable is, or null when it is not installed. Faked in tests, so no
// test depends on what this machine has installed.
public interface IAgentLocator
{
    string? Find(Launcher launcher, AgentConfig config);
}

// The machine's own, as agent-ha-bridge finds them (Get-BridgeCopilotPath and its siblings):
// the configured path if it exists, else the command on PATH, else where its installer puts
// it. PATH is read afresh from the registry as well as from this process, at most once a
// minute, so an agent installed after the host started is found without a restart.
public sealed class AgentLocator(TimeProvider? time = null) : IAgentLocator
{
    readonly TimeProvider time = time ?? TimeProvider.System;
    readonly Lock gate = new();
    IReadOnlyList<string> path = [];
    DateTimeOffset readAt = DateTimeOffset.MinValue;

    public string? Find(Launcher launcher, AgentConfig config)
    {
        // A configured path that does not exist means the agent is not here, never "look
        // elsewhere": the person said where it is.
        if (config.Path is { } configured) { return File.Exists(configured) && Runnable(configured) ? Path.GetFullPath(configured) : null; }
        foreach (var dir in SearchPath())
        {
            foreach (var ext in Extensions())
            {
                var candidate = Path.Combine(dir, launcher.Command + ext);
                if (File.Exists(candidate)) { return candidate; }
            }
        }
        if (!OperatingSystem.IsWindows()) { return null; }
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string? fallback = launcher.Id switch
        {
            "copilot" => Path.Combine(localAppData, "Microsoft", "WinGet", "Packages", "GitHub.Copilot_Microsoft.Winget.Source_8wekyb3d8bbwe", "copilot.exe"),
            "agency" => Path.Combine(appData, "agency", "CurrentVersion", "agency.exe"),
            "claude" => Path.Combine(home, ".local", "bin", "claude.exe"),
            "codex" => Path.Combine(appData, "npm", "codex.cmd"),
            _ => null,
        };
        return fallback is not null && File.Exists(fallback) ? fallback : null;
    }

    // Only what CreateProcess can start: an executable, or a batch file through cmd.exe. A
    // PowerShell shim beside an npm install is never chosen.
    static IEnumerable<string> Extensions() => OperatingSystem.IsWindows() ? [".exe", ".cmd", ".bat"] : [""];

    static bool Runnable(string file) => !OperatingSystem.IsWindows() || Path.GetExtension(file).ToLowerInvariant() is ".exe" or ".cmd" or ".bat";

    IReadOnlyList<string> SearchPath()
    {
        lock (gate)
        {
            if (time.GetUtcNow() - readAt < TimeSpan.FromMinutes(1)) { return path; }
            var entries = new List<string>();
            var sources = new List<string?> { Environment.GetEnvironmentVariable("PATH") };
            if (OperatingSystem.IsWindows())
            {
                sources.Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
                sources.Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
            }
            foreach (var source in sources)
            {
                foreach (var raw in (source ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var dir = Environment.ExpandEnvironmentVariables(raw.Trim('"'));
                    // A relative entry would search wherever the host was started from.
                    if (!Path.IsPathFullyQualified(dir) || entries.Contains(dir, StringComparer.OrdinalIgnoreCase)) { continue; }
                    entries.Add(dir);
                }
            }
            path = entries;
            readAt = time.GetUtcNow();
            return path;
        }
    }
}
