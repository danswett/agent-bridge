using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Transport;

namespace AgentBridge.App.Core;

// Update.exe ends every process in the installation. Identify profiles from running processes,
// never from leftover locks or launcher records; a profile's launcher and host are one profile.
public static partial class PackageProcesses
{
    internal sealed record Observed(int Pid, string Name, string? Image, string? CommandLine);

    public static string? Others(string profile, int? ownHost) =>
        Others(AppContext.BaseDirectory, profile, Environment.ProcessId, ownHost, Running());

    internal static string? Others(string appFolder, string profile, int ownApp, int? ownHost, IEnumerable<Observed> running)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appFolder)) + Path.DirectorySeparatorChar;
        foreach (var p in running)
        {
            if (p.Pid == ownApp || p.Pid == ownHost) { continue; }
            var kind = p.Name switch { "AgentBridge" => "app", "agent-bridge-host" => "host", "agent-bridge-hostw" => "launcher", _ => null };
            if (kind is null) { continue; }
            if (p.Image is not null && !p.Image.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { continue; }
            var other = Profile(p.CommandLine, kind);
            // An unreadable image is still a blocker, even if its command line names our profile.
            if (p.Image is not null && string.Equals(other, profile, StringComparison.OrdinalIgnoreCase)) { continue; }
            var who = other is null ? "unknown" : $"'{other}'";
            return $"bridge profile {who} {kind} (pid {p.Pid}) "
                + (p.Image is null ? "runs and cannot be looked at" : "runs from this installation");
        }
        return null;
    }

    static string? Profile(string? commandLine, string kind)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) { return null; }
        var args = CommandLine.Split(commandLine).Skip(1).ToArray();
        if (kind != "app" && args.FirstOrDefault() != "serve") { return null; }
        var options = args.Select((a, i) => (a, i)).Where(x => x.a.StartsWith("--profile", StringComparison.Ordinal)).ToArray();
        if (options.Length == 0) { return "default"; }
        if (options is not [{ a: "--profile", i: var at }] || at + 1 >= args.Length || !ProfileFolders.IsValidProfile(args[at + 1])) { return null; }
        return args[at + 1];
    }

    static IEnumerable<Observed> Running()
    {
        foreach (var name in new[] { "AgentBridge", "agent-bridge-host", "agent-bridge-hostw" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    string? path;
                    try { path = p.MainModule?.FileName; }
                    catch (InvalidOperationException) { continue; }
                    catch (Win32Exception) { path = null; }
                    yield return new(p.Id, name, path, SystemProcessInspector.ReadCommandLine(p.Id));
                }
            }
        }
    }

    // Only the detector's bounded, path-free identity report may bypass the generic UI wording.
    internal static string? RefusalDetail(string why) => Refusal().IsMatch(why) ? why : null;

    [GeneratedRegex(@"\Abridge profile (?:'[A-Za-z0-9_.-]{1,64}'|unknown) (?:app|host|launcher) \(pid [1-9][0-9]{0,9}\) runs (?:from this installation|and cannot be looked at)\z")]
    private static partial Regex Refusal();
}
