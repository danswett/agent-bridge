using AgentBridge.Local;

namespace AgentBridge.Host;

public sealed record ScriptTaskFile(string Path, string Sha256);

// What `scripts add <name> --task <path>` binds a script credential to (design/script-credentials.md
// §6, rule 3): a task, a hash of its definition and of every file it runs. Ancestry alone proves
// nothing, as any user process can register a task; what holds is that only an elevated user can
// change these, and that the Scheduler itself reports the instance the caller descends from.
public sealed record ScriptTaskBinding(string Task, string Definition, ScriptTaskFile[] Files)
{
    // At mint: everything must already be locked against this user; it is pinned as it is now.
    public static ScriptTaskBinding Pin(string task, IReadOnlyList<string> extra, IScriptTaskReader reader)
    {
        if (!ValidTask(task)) { throw new ArgumentException("a task is named by its full path, such as \\Folder\\Task", nameof(task)); }
        if (!reader.TaskLocked(task)) { throw new InvalidOperationException($"refused: the task {task} can be changed without elevation"); }
        var definition = reader.Definition(task) ?? throw new InvalidOperationException($"refused: the task {task} could not be read");
        // The program the action runs comes first: an accepted engine must be that program.
        var paths = reader.ActionFiles(task) ?? throw new InvalidOperationException($"refused: the task {task} runs something that can't be pinned");
        var files = new List<ScriptTaskFile>();
        foreach (var path in paths.Concat(extra).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!System.IO.Path.IsPathFullyQualified(path) || !reader.FileLocked(path))
            { throw new InvalidOperationException($"refused: {path} can be changed without elevation"); }
            files.Add(new(path, reader.FileSha256(path) ?? throw new InvalidOperationException($"refused: {path} could not be read")));
        }
        return files.Count > 0 ? new(task, definition, [.. files]) : throw new InvalidOperationException($"refused: the task {task} runs no file");
    }

    // At accept: why this ancestry may not use the credential, or null when it may. Every read is
    // fresh, so an ACL loosened or a file swapped since the mint refuses.
    public string? Refusal(ProcessAncestry ancestry, IScriptTaskReader? reader)
    {
        if (reader is null) { return "the bound task cannot be checked on this host"; }
        if (!reader.TaskLocked(Task) || Files.Any(f => !reader.FileLocked(f.Path))) { return "the bound task or a file it runs can be changed without elevation"; }
        if (!Same(reader.Definition(Task), Definition) || Files.Any(f => !Same(reader.FileSha256(f.Path), f.Sha256)))
        { return "the bound task or a file it runs has changed since it was bound"; }
        var engines = reader.RunningEngines(Task);
        var top = ancestry.Processes.ToList().FindIndex(p => p.Id > 0 && engines.Contains(p.Id));
        if (top < 0) { return "no running instance of the bound task started the caller"; }
        // Above the engine is the Scheduler's, which the user can't open; up to it must be read in full.
        if (ancestry.Processes.Take(top + 1).Any(Unread)) { return "the script caller's ancestry could not be traced in full"; }
        return Same(ancestry.Processes[top].Image, Files[0].Path) ? null : "the task's engine is not the program the bound task runs";
    }

    internal static bool Unread(PeerProcess p) => p.Id <= 0 || p.StartedAt is null || string.IsNullOrWhiteSpace(p.Image);

    static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    static bool ValidTask(string? task) => task is { Length: > 1 and <= 260 } && task[0] == '\\' && !task.EndsWith('\\')
        && !task.Any(char.IsControl) && !task.Split('\\').Skip(1).Any(p => p is "" or "." or "..");

    internal bool Valid() => ValidTask(Task) && Hex(Definition) && Files is { Length: > 0 and <= 16 }
        && Files.All(f => f is not null && System.IO.Path.IsPathFullyQualified(f.Path ?? "") && Hex(f.Sha256));

    static bool Hex(string? s) => s is { Length: 64 } && s.All(Uri.IsHexDigit);
}

// The host's Task Scheduler and ACL reads: Windows' in production, fakes in tests. Each answers
// null, empty or false when it cannot read, so the caller refuses (fails closed).
public interface IScriptTaskReader
{
    // SHA-256 of the task's canonical definition (actions, principal, triggers), or null.
    string? Definition(string task);
    // Every file the task's one program action runs, that program first; null when its action isn't
    // a program, or names a relative path or an environment variable the user could redirect.
    IReadOnlyList<string>? ActionFiles(string task);
    IReadOnlyList<int> RunningEngines(string task);
    // True only when the unelevated user can't change, delete or replace it (write is SYSTEM/Administrators only).
    bool TaskLocked(string task);
    // The same for a file and the folders above it.
    bool FileLocked(string path);
    string? FileSha256(string path);
}
