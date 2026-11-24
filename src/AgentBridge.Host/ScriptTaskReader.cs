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

    // The files the task's one program action runs, the program first: null when it is not exactly one program action, or names a
    // relative path or an environment variable the user could redirect, or a command line (not a file) to run.
    internal static IReadOnlyList<string>? ActionFiles(string xml)
    {
        if (Root(xml) is not { } task || Part(task, "Actions")?.Elements().ToList() is not [{ } exec] || exec.Name.LocalName != "Exec") { return null; }
        var program = exec.Elements().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value.Trim().Trim('"');
        if (!Pinnable(program)) { return null; }
        var files = new List<string> { program! };
        var args = Tokens(exec.Elements().FirstOrDefault(e => e.Name.LocalName == "Arguments")?.Value ?? "");
        // pwsh and powershell abbreviate their parameters: -f is -File; -c and -e (and longer) are a command line or an encoded one.
        var inline = args.Any(a => Prefix(a, "-command", 2) || Prefix(a, "-encodedcommand", 3) || a.Equals("-e", StringComparison.OrdinalIgnoreCase) || a.Equals("-ec", StringComparison.OrdinalIgnoreCase));
        if (inline) { return null; }
        var at = args.FindIndex(a => Prefix(a, "-file", 2));
        if (at >= 0)
        {
            var script = at + 1 < args.Count ? args[at + 1] : null;
            if (!Pinnable(script)) { return null; }
            files.Add(script!);
        }
        return files;
    }

    static bool Prefix(string token, string full, int min) => token.Length >= min && full.StartsWith(token, StringComparison.OrdinalIgnoreCase);

    static bool Pinnable(string? path) => !string.IsNullOrWhiteSpace(path) && Path.IsPathFullyQualified(path) && !path.Contains('%') && !path.Contains('$');

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
