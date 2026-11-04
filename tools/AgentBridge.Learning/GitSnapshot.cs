using System.Diagnostics;

namespace AgentBridge.Learning;

sealed record TreeFile(string Path, string Blob, string Mode);
sealed record PinnedNote(Note Note, string Blob);

sealed class GitSnapshot(string repository)
{
    public const string NotesFolder = "quality/learning/notes";

    public string Commit(string sha)
    {
        Note.CommitId(sha);
        var resolved = Read("rev-parse", "--verify", sha + "^{commit}").Trim();
        if (resolved != sha) throw new FormatException("The object id must name a commit, not a tag.");
        return resolved;
    }

    public IReadOnlyList<PinnedNote> Notes(string commit)
    {
        Commit(commit);
        var files = Files(commit, NotesFolder).Where(f => f.Path.EndsWith(".json", StringComparison.Ordinal)).ToList();
        if (files.Count > 500) throw new FormatException("The experiment is limited to 500 note files.");
        var notes = files.Select(f => new PinnedNote(Note.Parse(Blob(f)), f.Blob)).ToList();
        var duplicate = notes.GroupBy(n => n.Note.Id).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new FormatException($"Duplicate note id '{duplicate.Key}'.");
        return notes;
    }

    public string? ReferenceProblem(Note note, string codeCommit)
    {
        Commit(note.VerifiedCommit);
        foreach (var path in note.References)
        {
            var previous = Files(note.VerifiedCommit, path).SingleOrDefault(f => f.Path == path);
            var current = Files(codeCommit, path).SingleOrDefault(f => f.Path == path);
            if (previous is null || current is null || !Regular(previous) || !Regular(current))
                return $"Source reference is missing or is not a regular file: {path}";
            if (previous.Blob != current.Blob || previous.Mode != current.Mode)
                return $"Source reference changed since verification: {path}";
        }
        return null;
    }

    IReadOnlyList<TreeFile> Files(string commit, string path)
    {
        var rows = Read("ls-tree", "-r", "-z", commit, "--", path).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        return rows.Select(row =>
        {
            var tab = row.IndexOf('\t');
            var fields = row[..tab].Split(' ');
            return new TreeFile(row[(tab + 1)..], fields[2], fields[0]);
        }).ToList();
    }

    string Blob(TreeFile file)
    {
        if (!Regular(file)) throw new FormatException($"Notes must be regular Git files: {file.Path}");
        var size = long.Parse(Read("cat-file", "-s", file.Blob), System.Globalization.CultureInfo.InvariantCulture);
        if (size > 32_768) throw new FormatException($"Note exceeds 32 KiB: {file.Path}");
        return Read("cat-file", "blob", file.Blob);
    }

    static bool Regular(TreeFile file) => file.Mode is "100644" or "100755";

    string Read(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // Literal paths and raw blobs avoid pathspec expansion, checkout filters and working-tree content.
        start.ArgumentList.Add("--no-optional-locks");
        start.ArgumentList.Add("--no-replace-objects");
        start.ArgumentList.Add("--literal-pathspecs");
        start.Environment["GIT_NO_LAZY_FETCH"] = "1";
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("git did not start.");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new IOException("Reading the Git snapshot timed out.");
        }
        if (process.ExitCode != 0)
            throw new IOException($"git {args[0]} failed ({process.ExitCode}): {stderr.GetAwaiter().GetResult().Trim()}");
        return stdout.GetAwaiter().GetResult();
    }
}
