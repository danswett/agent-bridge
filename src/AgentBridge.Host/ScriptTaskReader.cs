using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using AgentBridge.Host.Migration;

namespace AgentBridge.Host;

// What a task's XML says, and nothing else: the pure half of the Windows reads behind a script credential bound to a scheduled task
// (docs/design/script-credentials.md section 6 rule 3, docs/protocol/local-pipe.md).
internal static class ScriptTaskXml
{
    static XElement? Root(string xml)
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(xml), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            return XDocument.Load(reader).Root is { } root && root.Name.LocalName == "Task" ? root : null;
        }
        catch (XmlException) { return null; }
    }

    static XElement? Part(XElement task, string name) => task.Elements().FirstOrDefault(e => e.Name.LocalName == name);

    static string Canon(XElement e) => "<" + e.Name.LocalName
        + string.Concat(e.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.LocalName, StringComparer.Ordinal).Select(a => $" {a.Name.LocalName}=\"{a.Value}\""))
        + ">" + (e.HasElements ? string.Concat(e.Elements().Select(Canon)) : e.Value.Trim()) + "</" + e.Name.LocalName + ">";

    // SHA-256 of the task's actions, principal and triggers (not its registration date or settings), or null when the XML is not a task.
    internal static string? Definition(string xml)
    {
        if (Root(xml) is not { } task) { return null; }
        var parts = new[] { "Triggers", "Principals", "Actions" }.Select(n => Part(task, n) is { } part ? Canon(part) : $"<{n}/>");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))));
    }

    // The files the task's one program action runs, the program first. An allow-list: only a locked, fully qualified program on its own, or
    // pwsh / powershell with plain switches and -File <fully qualified script> (a - or / prefix), is understood. Every other way to name a
    // script (a positional one, -Command, -EncodedCommand, cmd /c, wscript, an unknown switch, a relative path or an environment variable)
    // is null, so no file the task runs can go unpinned.
    internal static IReadOnlyList<string>? ActionFiles(string xml)
    {
        if (Root(xml) is not { } task || Part(task, "Actions")?.Elements().ToList() is not [{ } exec] || exec.Name.LocalName != "Exec") { return null; }
        var program = exec.Elements().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value.Trim().Trim('"');
        if (program is null || !Pinnable(program)) { return null; }
        if (Launcher.Equals(program[(program.LastIndexOfAny(['\\', '/']) + 1)..], StringComparison.OrdinalIgnoreCase)) { return LauncherFiles(task, exec, program); }
        if (Tokens(exec.Elements().FirstOrDefault(e => e.Name.LocalName == "Arguments")?.Value ?? "") is not { } args) { return null; }
        if (args.Count == 0) { return [program]; }
        if (!Interpreters.Contains(program[(program.LastIndexOfAny(['\\', '/']) + 1)..])) { return null; }
        for (var i = 0; i < args.Count; i++)
        {
            if (args[i].Length < 2 || args[i][0] is not ('-' or '/')) { return null; }
            var name = args[i][1..].ToLowerInvariant();
            if (name.Length <= 4 && "file".StartsWith(name, StringComparison.Ordinal))
            {
                return i + 1 < args.Count && Pinnable(args[i + 1]) ? [program, args[i + 1]] : null;
            }
            if (ValuedSwitches.Contains(name)) { i++; }
            else if (!PlainSwitches.Contains(name)) { return null; }
        }
        return null;
    }

    const string Launcher = "script-run.exe";

    // The action of a task bound through the admin-locked launcher (design/script-credentials-run.md sections 2, 3 and 4):
    // `script-run <name> -- <absolute program> [-NoProfile -NonInteractive -File <absolute script>]`. It pins the launcher, the child program AND the script, never
    // optionally, and only when the task is logged-on-only (the owner's answer to Q-run-2: no stored password). Anything else, inline code included, is null.
    static IReadOnlyList<string>? LauncherFiles(XElement task, XElement exec, string launcher)
    {
        if (Part(task, "Principals")?.Elements().ToList() is not [{ } principal]
            || principal.Elements().Where(e => e.Name.LocalName == "LogonType").ToList() is not [{ } logon] || logon.Value.Trim() != "InteractiveToken") { return null; }
        if (Tokens(exec.Elements().FirstOrDefault(e => e.Name.LocalName == "Arguments")?.Value ?? "") is not { } args) { return null; }
        if (args.Count < 3 || args[1] != "--" || !ScriptName.IsMatch(args[0]) || args[0] is "." or ".." || !Pinnable(args[2])) { return null; }
        var child = args[2];
        if (!Interpreters.Contains(child[(child.LastIndexOfAny(['\\', '/']) + 1)..])) { return args.Count == 3 ? [launcher, child] : null; }
        bool noProfile = false, nonInteractive = false;
        for (var i = 3; i < args.Count; i++)
        {
            if (args[i].Length < 2 || args[i][0] is not ('-' or '/')) { return null; }
            var name = args[i][1..].ToLowerInvariant();
            if (name.Length <= 4 && "file".StartsWith(name, StringComparison.Ordinal))
            {
                return noProfile && nonInteractive && i + 1 < args.Count && Pinnable(args[i + 1]) ? [launcher, child, args[i + 1]] : null;
            }
            if (name == "noprofile") { noProfile = true; }
            else if (name == "noninteractive") { nonInteractive = true; }
            else if (ValuedSwitches.Contains(name)) { i++; }
            else if (!PlainSwitches.Contains(name)) { return null; }
        }
        return null;
    }

    static readonly System.Text.RegularExpressions.Regex ScriptName = new(@"^[A-Za-z0-9._-]{1,64}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    static readonly HashSet<string> Interpreters = new(["pwsh.exe", "powershell.exe"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> PlainSwitches = ["noprofile", "nologo", "noninteractive", "mta", "sta"];
    static readonly HashSet<string> ValuedSwitches = ["executionpolicy", "windowstyle"];

    // A Windows drive path with no environment variable, wildcard, stream, control character or relative segment. Written here, not
    // Path.IsPathFullyQualified, so it decides the same on every platform.
    static bool Pinnable(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length is > 3 and <= 260 && char.IsAsciiLetter(path[0]) && path[1] == ':'
        && path[2] is '\\' or '/' && path.IndexOfAny(['%', '$', '*', '?', '<', '>', '|', '"'], 2) < 0 && !path.Any(char.IsControl) && path.IndexOf(':', 2) < 0
        && !path[3..].Split('\\', '/').Any(s => s is "." or "..");

    // The argv Windows' CommandLineToArgvW (which pwsh uses) would hand the program, or null when this reading could differ from it. A backslash-quote is a literal
    // quote there but a toggle here, so it is refused; a quote may only wrap a WHOLE token (opening at its start, closing at its end), and anything else is refused, so
    // what the reader pinned is exactly what the program runs (#4524).
    static List<string>? Tokens(string line)
    {
        if (line.Contains("\\\"", StringComparison.Ordinal)) { return null; }
        var tokens = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i])) { i++; continue; }
            if (line[i] == '"')
            {
                var close = line.IndexOf('"', i + 1);
                if (close < 0 || (close + 1 < line.Length && !char.IsWhiteSpace(line[close + 1]))) { return null; }
                tokens.Add(line[(i + 1)..close]);
                i = close + 1;
                continue;
            }
            var start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i]))
            {
                if (line[i] == '"') { return null; }
                i++;
            }
            tokens.Add(line[start..i]);
        }
        return tokens;
    }
}

// The host's Task Scheduler and ACL reads on Windows (IScriptTaskReader). Every read answers null, empty or false when it cannot be made,
// so a bound credential is refused rather than accepted on a guess. Registering and changing the task takes an elevated user: the
// task's own descriptor and those of its files and their folders are what hold it, so each is read as this user's token would be checked.
[SupportedOSPlatform("windows")]
public sealed class WindowsScriptTaskReader : IScriptTaskReader
{
    public string? Definition(string task) => Task(task, t => Get(t, "Xml") as string) is { } xml ? ScriptTaskXml.Definition(xml) : null;

    public IReadOnlyList<string>? ActionFiles(string task) => Task(task, t => Get(t, "Xml") as string) is { } xml ? ScriptTaskXml.ActionFiles(xml) : null;

    // Each running instance's engine and when that process started (UTC); an engine whose start cannot be read is left out.
    public IReadOnlyList<ScriptTaskEngine> RunningEngines(string task)
    {
        var engines = new List<ScriptTaskEngine>();
        Scheduler<object>(service =>
        {
            foreach (var running in (IEnumerable?)Call(service, "GetRunningTasks", 1) ?? Array.Empty<object>())
            {
                try
                {
                    if (!string.Equals(Get(running, "Path") as string, task, StringComparison.OrdinalIgnoreCase)) { continue; }
                    using var engine = Process.GetProcessById(Convert.ToInt32(Get(running, "EnginePID")));
                    engines.Add(new(engine.Id, new DateTimeOffset(engine.StartTime.ToUniversalTime())));
                }
                catch (Exception e) when (e is InvalidOperationException or ArgumentException or COMException or TargetInvocationException or System.ComponentModel.Win32Exception) { }
            }
            return null;
        });
        return engines;
    }

    // Owner and discretionary list, as the descriptor of a file: Task Scheduler checks a task's as one.
    public bool TaskLocked(string task) => Task(task, t => Call(t, "GetSecurityDescriptor", 5) as string) is { } sddl && TaskAccess.Unchangeable(sddl, folder: false) == true;

    // The file and every folder above it, none of them reached through a link, none changeable by this user.
    public bool FileLocked(string path)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || new FileInfo(path) is not { Exists: true } file || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) { return false; }
            var chain = new List<(bool Folder, string Sddl)> { (false, file.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access)) };
            for (var dir = file.Directory; dir is not null; dir = dir.Parent)
            {
                if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) { return false; }
                chain.Add((true, dir.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access)));
            }
            return AllLocked(chain);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException) { return false; }
    }

    // The file's own descriptor and each folder's above it, all of which must say this user cannot change them.
    internal static bool AllLocked(IEnumerable<(bool Folder, string Sddl)> chain) => chain.All(c => TaskAccess.Unchangeable(c.Sddl, c.Folder) == true);

    public string? FileSha256(string path)
    {
        try { return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    static T? Task<T>(string name, Func<object, T?> use) where T : class =>
        Scheduler(service =>
        {
            object? folder = null, task = null;
            try
            {
                folder = Call(service, "GetFolder", @"\");
                task = Call(folder!, "GetTask", name);
                return task is null ? null : use(task);
            }
            finally { Release(task); Release(folder); }
        });

    static T? Scheduler<T>(Func<object, T?> use) where T : class
    {
        object? service = null;
        try
        {
            service = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service") ?? throw new IOException("Task Scheduler is not available"));
            Call(service!, "Connect");
            return use(service!);
        }
        catch (Exception e) when (e is IOException or COMException or TargetInvocationException or InvalidCastException or MissingMethodException or ArgumentException) { return null; }
        finally { Release(service); }
    }

    static void Release(object? o) { if (o is not null && Marshal.IsComObject(o)) { Marshal.ReleaseComObject(o); } }

    static object? Call(object target, string name, params object?[] args) => target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args);

    static object? Get(object target, string name) => target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null);
}
