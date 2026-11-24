using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

// The one check a folder passes before it is approved for launching sessions, shared by `workspace add` and the app's Add folder. Every
// outcome is one closed code, and a refusal writes nothing. A person decides this at the machine; no other device reaches it.
internal static class WorkspaceApproval
{
    public const string Added = "added";
    public const string NotAPath = "not-a-path";
    public const string Missing = "missing";
    public const string SystemFolder = "system-folder";
    public const string NotGit = "not-git";
    public const string AlreadyApproved = "already-approved";
    public const string LabelInUse = "label-in-use";
    public const string TooMany = "too-many";
    public const string Unwritable = "unwritable";
    public const string NotLocal = "not-local";
    public const string Busy = "busy";
    static readonly TimeSpan GateWait = TimeSpan.FromSeconds(10);

    // Path is as it would be stored; Label is the new one (the existing one for AlreadyApproved); Detail is for the command line only.
    public sealed record Result(string Code, string? Path = null, string? Label = null, string? Detail = null);

    // The folder must be an absolute path (or ~): a relative one would be resolved against whatever folder this process happens to run in.
    // A device path (\\?\ or \\.\) is refused too, since the system-folder check compares plain paths. Throws JsonException when
    // config.json is not JSON, as the command line reports it. Other I/O trouble is Unwritable.
    internal static string GateName(string configFolder) => "AgentBridgeHostConfig-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configFolder.ToLowerInvariant())))[..16];

    // A fixed drive of this machine (Windows): not a network share (UNC or mapped), removable or optical drive, or one that is not there.
    internal static bool IsLocalFixed(string path)
    {
        if (!OperatingSystem.IsWindows()) { return true; }
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) { return false; }
        try { return new DriveInfo(System.IO.Path.GetPathRoot(path) ?? "").DriveType == DriveType.Fixed; }
        catch (Exception e) when (e is ArgumentException or IOException) { return false; }
    }

    public static Result Add(string configFolder, string folder, string? label, bool isolate, Func<string, bool> isSystemDirectory, TimeSpan? wait = null)
    {
        string full;
        try
        {
            if (string.IsNullOrWhiteSpace(folder) || Folders.Expand(folder) is not { } expanded) { return new(NotAPath); }
            full = System.IO.Path.GetFullPath(expanded);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return new(NotAPath); }
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal) || full.StartsWith(@"\\.\", StringComparison.Ordinal)) { return new(NotAPath); }
        // Only a folder on this machine's own fixed drives: a network share can hang the call and be changed by someone else after it is approved.
        if (!IsLocalFixed(full)) { return new(NotLocal, full); }
        if (Folders.Physical(full) is not { } physical) { return new(Missing, full); }
        if (!IsLocalFixed(physical)) { return new(NotLocal, full); }
        if (isSystemDirectory(physical)) { return new(SystemFolder, full); }
        if (isolate && !Git.Run(physical, ["rev-parse", "--show-toplevel"]).Ok) { return new(NotGit, full); }

        // One approval at a time across processes (the host and the command line), so two cannot each read the file and the later write drop the earlier.
        using var gate = new Mutex(false, GateName(configFolder));
        bool held;
        try { held = gate.WaitOne(wait ?? GateWait); } catch (AbandonedMutexException) { held = true; }
        if (!held) { return new(Busy, full); }
        var path = System.IO.Path.Combine(configFolder, HostSettings.FileName);
        try
        {
            var root = ConfigCommands.Read(path);
            var launch = ConfigCommands.Object(root, "launch");
            var workspaces = launch["workspaces"] as JsonArray ?? [];
            launch["workspaces"] = workspaces;
            var (current, _) = HostSettings.Load(configFolder);
            if (current.Launch.Workspaces.FirstOrDefault(w => Folders.Physical(w.Path) is { } p && Folders.Same(p, physical)) is { } existing)
            {
                return new(AlreadyApproved, full, existing.Label);
            }
            label = string.IsNullOrWhiteSpace(label) ? Folders.DefaultLabel(full) : label.Trim();
            if (current.Launch.Workspaces.Any(w => w.Label == label)) { return new(LabelInUse, full, label); }
            if (workspaces.Count >= LaunchProtocol.MaxWorkspaces) { return new(TooMany, full); }
            var entry = new JsonObject { ["path"] = full, ["label"] = label };
            if (isolate) { entry["isolate"] = true; }
            workspaces.Add(entry);
            var problems = new StringWriter();
            return ConfigCommands.Save(path, root, problems) ? new(Added, full, label) : new(Unwritable, full, label, problems.ToString().Trim());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(Unwritable, full, label, $"{path}: {e.Message}"); }
        finally { gate.ReleaseMutex(); }
    }
}
