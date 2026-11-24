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

    public const string Removed = "removed";
    public const string Stale = "stale";
    public const string NotApproved = "not-approved";

    public sealed record Entry(string Path, string Label, bool Isolate);

    // The approved folders as the config file holds them, with a baseline: a hash of the raw list, so any added, removed or changed entry
    // changes it. A folder that has gone away is still listed. Throws JsonException when config.json is not JSON.
    public sealed record Listing(IReadOnlyList<Entry> Workspaces, string Baseline);

    public static Listing List(string configFolder) => Read(ConfigCommands.Read(System.IO.Path.Combine(configFolder, HostSettings.FileName)));

    static string? Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static Listing Read(JsonObject root)
    {
        var workspaces = root["launch"]?["workspaces"] as JsonArray;
        var entries = new List<Entry>();
        foreach (var node in workspaces ?? [])
        {
            var obj = node as JsonObject;
            var written = obj is null ? Text(node) : Text(obj["path"]);
            if (written is null) { continue; }
            var path = Folders.Expand(written) ?? written;
            var label = obj is not null && Text(obj["label"]) is { Length: > 0 } named ? named : Folders.DefaultLabel(path);
            entries.Add(new(path, label, obj?["isolate"] is JsonValue i && i.TryGetValue<bool>(out var iso) && iso));
        }
        return new(entries, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(workspaces?.ToJsonString() ?? "[]"))));
    }

    // Removes one exact approved folder, only against the list the person saw (the baseline): a list that has changed since is Stale and
    // nothing is written. A folder below an approved one is not approved, so it is NotApproved. Everything else in the file is kept as it was.
    public static Result Remove(string configFolder, string folder, string baseline, TimeSpan? wait = null)
    {
        string? expanded;
        try { expanded = string.IsNullOrWhiteSpace(folder) ? null : Folders.Expand(folder); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { expanded = null; }
        if (expanded is null) { return new(NotAPath); }
        using var gate = new Mutex(false, GateName(configFolder));
        bool held;
        try { held = gate.WaitOne(wait ?? GateWait); } catch (AbandonedMutexException) { held = true; }
        if (!held) { return new(Busy, expanded); }
        var path = System.IO.Path.Combine(configFolder, HostSettings.FileName);
        try
        {
            var root = ConfigCommands.Read(path);
            var listing = Read(root);
            if (!string.Equals(listing.Baseline, baseline, StringComparison.Ordinal)) { return new(Stale, expanded); }
            var workspaces = (JsonArray?)root["launch"]?["workspaces"] ?? [];
            string? label = null;
            for (var i = workspaces.Count - 1; i >= 0; i--)
            {
                var written = workspaces[i] is JsonObject o ? Text(o["path"]) : Text(workspaces[i]);
                if (written is null || !Folders.Same(Folders.Expand(written) ?? written, expanded)) { continue; }
                label ??= workspaces[i] is JsonObject n && Text(n["label"]) is { Length: > 0 } named ? named : Folders.DefaultLabel(expanded);
                workspaces.RemoveAt(i);
            }
            if (label is null) { return new(NotApproved, expanded); }
            return ConfigCommands.Save(path, root, new StringWriter()) ? new(Removed, expanded, label) : new(Unwritable, expanded, label);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new(Unwritable, expanded, null, $"{path}: {e.Message}"); }
        finally { gate.ReleaseMutex(); }
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
