using System.Text.RegularExpressions;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Delivery.Staging;

namespace AgentBridge.Rehearsal;

// Invariant 9 and the reclaimer, read from the disk as the host leaves them. Counts and
// states only: never a staged file's name, nor a path inside a worktree.
internal static partial class Disk
{
    [GeneratedRegex("^[0-9a-f]{32}-")]
    private static partial Regex StagedName();

    public static Outcome Staging(string profileFolder, int? expectMinFiles, string? tempRoot = null)
    {
        tempRoot ??= Path.GetTempPath();
        var folder = Path.Combine(profileFolder, "attachments");
        if (!Directory.Exists(folder)) { return Outcome.Error($"{folder} does not exist yet: nothing has been staged on this profile"); }
        if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint)) { return Outcome.Fail("the staging folder is a link"); }
        var isPrivate = PrivateFolder.IsPrivate(folder);
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly).Select(f => new FileInfo(f)).ToList();
        var oddlyNamed = files.Count(f => !StagedName().IsMatch(f.Name));
        var data = new
        {
            folder,
            isPrivate,
            files = files.Count,
            kib = files.Sum(f => f.Length) / 1024,
            oddlyNamed,
            temp = folder.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase),
        };
        if (!isPrivate) { return Outcome.Fail("the staging folder can be opened by someone other than this user", data); }
        if (data.temp) { return Outcome.Fail("the staging folder is under TEMP", data); }
        if (oddlyNamed > 0) { return Outcome.Fail($"{oddlyNamed} file(s) in the staging folder are not named as the host names them", data); }
        if (expectMinFiles is { } min && files.Count < min) { return Outcome.Fail($"{files.Count} staged file(s), expected at least {min}", data); }
        return Outcome.Pass($"private to this user, {files.Count} file(s)", data);
    }

    // Each worktree the repository registers, and whose marker it carries.
    public static Outcome Worktrees(string repository, int? expectHostTrees, int? expectMaxHostTrees, int? expectOldTrees)
    {
        var list = Git.Run(repository, ["worktree", "list", "--porcelain"]);
        if (list.ExitCode != 0) { return Outcome.Error($"git worktree list failed in {repository}"); }
        var trees = new List<(string Name, string Owner, bool Locked)>();
        string? current = null;
        var locked = false;
        foreach (var line in (list.Output + "\n").Split('\n'))
        {
            if (line.StartsWith("worktree ", StringComparison.Ordinal)) { current = line["worktree ".Length..].Trim(); locked = false; continue; }
            if (line.StartsWith("locked", StringComparison.Ordinal)) { locked = true; continue; }
            if (line.Trim().Length == 0 && current is not null)
            {
                trees.Add((Path.GetFileName(current.TrimEnd('/', '\\')), Owner(current), locked));
                current = null;
            }
        }
        var host = trees.Count(t => t.Owner == "agent-bridge-host");
        var data = new
        {
            repository,
            worktrees = trees.Select(t => new { name = t.Name, owner = t.Owner, locked = t.Locked }),
            host,
            agentHaBridge = trees.Count(t => t.Owner == "agent-ha-bridge"),
        };
        if (expectHostTrees is { } n && host != n) { return Outcome.Fail($"{host} worktree(s) carry the host's marker, expected {n}", data); }
        if (expectMaxHostTrees is { } max && host > max) { return Outcome.Fail($"{host} worktree(s) carry the host's marker, expected at most {max}", data); }
        if (expectOldTrees is { } o && data.agentHaBridge != o) { return Outcome.Fail($"{data.agentHaBridge} worktree(s) carry agent-ha-bridge's marker, expected {o}", data); }
        return Outcome.Pass($"{trees.Count} worktree(s): {host} the host's, {data.agentHaBridge} agent-ha-bridge's", data);
    }

    static string Owner(string worktree)
    {
        var dir = Git.Run(worktree, ["rev-parse", "--absolute-git-dir"]);
        if (dir.ExitCode != 0) { return "unknown"; }
        var admin = dir.Output.Trim();
        if (File.Exists(Path.Combine(admin, AgentBridge.Host.Delivery.Launch.Worktrees.Marker))) { return "agent-bridge-host"; }
        if (File.Exists(Path.Combine(admin, "agent-bridge-created"))) { return "agent-ha-bridge"; }
        return "other";
    }
}
