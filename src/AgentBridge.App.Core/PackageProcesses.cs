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
    internal sealed record Observed(int Pid, string Name, string? Image, string? CommandLine)
    {
        public DateTimeOffset? StartedAt { get; init; }
    }

    public static string? Others(string profile, int? ownHost) =>
        Others(AppContext.BaseDirectory, profile, Environment.ProcessId, ownHost, Running());

    internal static string? Others(string appFolder, string profile, int ownApp, int? ownHost, IEnumerable<Observed> running)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(appFolder)) + Path.DirectorySeparatorChar;
        foreach (var p in running)
        {
            // Only our still-running app has a PID that cannot have been recycled.
            if (p.Pid == ownApp) { continue; }
            var kind = p.Name switch { "AgentBridge" => "app", "agent-bridge-host" => "host", "agent-bridge-hostw" => "launcher", _ => null };
            if (kind is null) { continue; }
            if (p.Image is not null && !p.Image.StartsWith(root, StringComparison.OrdinalIgnoreCase)) { continue; }
            var other = Profile(p.CommandLine, kind, out var demo);
            // ownHost is a cached PID, not identity. Only fresh, readable process identity can
            // establish our profile; demo apps have independent instances even with its name.
            if (p.StartedAt is not null && p.Image is not null && !demo && string.Equals(other, profile, StringComparison.OrdinalIgnoreCase)) { continue; }
            if (demo) { kind = "demo app"; }
            var who = other is null ? "unknown" : $"'{other}'";
            return $"bridge profile {who} {kind} (pid {p.Pid}) "
                + (p.Image is null || p.StartedAt is null ? "runs and cannot be looked at" : "runs from this installation");
        }
        return null;
    }

    static string? Profile(string? commandLine, string kind, out bool demo)
    {
        demo = false;
        if (PlainArguments(commandLine) is not { } args) { return null; }
        demo = kind == "app" && args.Any(a => a is "--demo" or "--self-test" or "--self-test-history-scroll" or "--self-test-live-list");
        if (kind != "app" && args.FirstOrDefault() != "serve") { return null; }
        var options = args.Select((a, i) => (a, i)).Where(x => x.a.StartsWith("--profile", StringComparison.Ordinal)).ToArray();
        if (options.Length == 0) { return "default"; }
        if (options is not [{ a: "--profile", i: var at }] || at + 1 >= args.Length || !ProfileFolders.IsValidProfile(args[at + 1])) { return null; }
        return args[at + 1];
    }

    // The arguments after the program, read exactly as Windows would only in the plain form our
    // own launches use: no quotes after the program, separated by spaces and tabs. CommandLine.Split
    // is only close to Windows (`"C:\dir\\"`, `"--pro""file"`, other whitespace), and a near miss
    // could read another profile as ours, so any other form is unreadable identity.
    static string[]? PlainArguments(string? commandLine)
    {
        if (string.IsNullOrEmpty(commandLine) || commandLine[0] is ' ' or '\t') { return null; }
        var quoted = commandLine[0] == '"';
        var end = quoted ? commandLine.IndexOf('"', 1) + 1 : commandLine.IndexOfAny([' ', '\t']);
        if (quoted && end == 0) { return null; }
        if (end < 0) { end = commandLine.Length; }
        var rest = commandLine.AsSpan(end);
        if ((!quoted && commandLine.AsSpan(0, end).Contains('"')) || rest.Contains('"')
            || (rest.Length > 0 && rest[0] is not (' ' or '\t'))) { return null; }
        foreach (var c in rest) { if (char.IsWhiteSpace(c) && c is not (' ' or '\t')) { return null; } }
        return rest.ToString().Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
    }

    static IEnumerable<Observed> Running()
    {
        foreach (var name in new[] { "AgentBridge", "agent-bridge-host", "agent-bridge-hostw" })
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                using (p)
                {
                    DateTimeOffset? started = null;
                    string? path;
                    try
                    {
                        // Pin this generation's PID while the command-line reader opens it.
                        _ = p.SafeHandle;
                        started = new DateTimeOffset(p.StartTime.ToUniversalTime(), TimeSpan.Zero);
                        path = p.MainModule?.FileName;
                    }
                    catch (InvalidOperationException) { continue; }
                    catch (Win32Exception) { path = null; }
                    yield return new(p.Id, name, path, SystemProcessInspector.ReadCommandLine(p.Id)) { StartedAt = started };
                }
            }
        }
    }

    // Only the detector's bounded, path-free identity report may bypass the generic UI wording.
    internal static string? RefusalDetail(string why) => Refusal().IsMatch(why) ? why : null;

    [GeneratedRegex(@"\Abridge profile (?:'[A-Za-z0-9_.-]{1,64}'|unknown) (?:app|demo app|host|launcher) \(pid [1-9][0-9]{0,9}\) runs (?:from this installation|and cannot be looked at)\z")]
    private static partial Regex Refusal();
}
