using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
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

    // Escaped, so text that looks like markup can never hash the same as markup.
    static string Canon(XElement e) => "<" + e.Name.LocalName
        + string.Concat(e.Attributes().Where(a => !a.IsNamespaceDeclaration).OrderBy(a => a.Name.LocalName, StringComparer.Ordinal).Select(a => $" {a.Name.LocalName}=\"{SecurityElement.Escape(a.Value)}\""))
        + ">" + (e.HasElements ? string.Concat(e.Elements().Select(Canon)) : SecurityElement.Escape(e.Value.Trim())) + "</" + e.Name.LocalName + ">";

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
        var args = Tokens(exec.Elements().FirstOrDefault(e => e.Name.LocalName == "Arguments")?.Value ?? "");
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

    static readonly HashSet<string> Interpreters = new(["pwsh.exe", "powershell.exe"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> PlainSwitches = ["noprofile", "nologo", "noninteractive", "mta", "sta"];
    static readonly HashSet<string> ValuedSwitches = ["executionpolicy", "windowstyle"];

    // A Windows drive path with no environment variable, wildcard, stream, control character or relative segment. Written here, not
    // Path.IsPathFullyQualified, so it decides the same on every platform.
    static bool Pinnable(string? path) => !string.IsNullOrWhiteSpace(path) && path.Length is > 3 and <= 260 && char.IsAsciiLetter(path[0]) && path[1] == ':'
        && path[2] is '\\' or '/' && path.IndexOfAny(['%', '$', '*', '?', '<', '>', '|', '"'], 2) < 0 && !path.Any(char.IsControl) && path.IndexOf(':', 2) < 0
        && !path[3..].Split('\\', '/').Any(s => s is "." or "..");

    // A task is named by its full path, such as \Folder\Task: no traversal, wildcard, control character or empty segment.
    internal static bool ValidTaskName(string? task) => task is { Length: > 1 and <= 260 } && task[0] == '\\' && !task.EndsWith('\\')
        && task.IndexOfAny(['*', '?', '<', '>', '|', '"', '/']) < 0 && !task.Any(char.IsControl) && !task.Split('\\').Skip(1).Any(s => s is "" or "." or "..");

    static List<string> Tokens(string line)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"') { quoted = !quoted; }
            else if (char.IsWhiteSpace(c) && !quoted) { if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); } }
            else { current.Append(c); }
        }
        if (current.Length > 0) { tokens.Add(current.ToString()); }
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
        if (!ScriptTaskXml.ValidTaskName(task)) { return engines; }
        Scheduler<object>(service =>
        {
            var running = Call(service, "GetRunningTasks", 1);
            try
            {
                foreach (var one in (IEnumerable?)running ?? Array.Empty<object>())
                {
                    try
                    {
                        if (!string.Equals(Get(one, "Path") as string, task, StringComparison.OrdinalIgnoreCase)) { continue; }
                        using var engine = Process.GetProcessById(Convert.ToInt32(Get(one, "EnginePID")));
                        engines.Add(new(engine.Id, new DateTimeOffset(engine.StartTime.ToUniversalTime())));
                    }
                    catch (Exception e) when (e is InvalidOperationException or ArgumentException or COMException or TargetInvocationException or InvalidCastException
                        or FormatException or OverflowException or System.ComponentModel.Win32Exception) { }
                    finally { Release(one); }
                }
            }
            finally { Release(running); }
            return null;
        });
        return engines;
    }

    // Owner and discretionary list, as the descriptor of a file: Task Scheduler checks a task's as one.
    public bool TaskLocked(string task) => Task(task, t => Call(t, "GetSecurityDescriptor", 5) as string) is { } sddl && TaskAccess.Unchangeable(sddl, folder: false) == true;

    // The file and every folder above it, none of them reached through a link, none changeable by this user.
    public bool FileLocked(string path) => LockedChain(path, e => e switch
    {
        FileInfo f => f.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access),
        DirectoryInfo d => d.GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access),
        _ => throw new NotSupportedException(),
    });

    // The walk from a file up to the volume root, with `descriptor` giving each level's owner and discretionary list.
    internal static bool LockedChain(string path, Func<FileSystemInfo, string> descriptor)
    {
        try
        {
            if (!Path.IsPathFullyQualified(path) || new FileInfo(path) is not { Exists: true } file || file.Attributes.HasFlag(FileAttributes.ReparsePoint)) { return false; }
            var chain = new List<(bool Folder, string Sddl)> { (false, descriptor(file)) };
            for (var dir = file.Directory; dir is not null; dir = dir.Parent)
            {
                if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) { return false; }
                chain.Add((true, descriptor(dir)));
            }
            return AllLocked(chain);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or InvalidOperationException) { return false; }
    }

    // The file's own descriptor and each folder's above it, all of which must say this user cannot change them. The first folder in the
    // chain is the one holding the file, which must also take no additions.
    internal static bool AllLocked(IEnumerable<(bool Folder, string Sddl)> chain)
    {
        var beside = true;
        foreach (var (folder, sddl) in chain)
        {
            // Additions are refused only in the folder holding the file (lead-approved): adding higher up puts nothing beside the program, and a parent's delete-child, delete and re-permission are still checked.
            if (TaskAccess.Unchangeable(sddl, folder, folder && beside) != true) { return false; }
            if (folder) { beside = false; }
        }
        return true;
    }

    public string? FileSha256(string path)
    {
        try { return Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    // The name is checked here as well as by the binding: a path with traversal or a wildcard reads as no task.
    static T? Task<T>(string name, Func<object, T?> use) where T : class =>
        !ScriptTaskXml.ValidTaskName(name) ? null : Scheduler(service =>
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
