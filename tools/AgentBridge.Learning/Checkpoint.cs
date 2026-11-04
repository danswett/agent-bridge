namespace AgentBridge.Learning;

sealed record NoteRevision(string Id, string Blob);
sealed record Checkpoint(
    int Version, string CodeCommit, string NotesCommit, NoteRevision[] ApprovedNotes,
    string TaskSetSha256, string ToolSha256, string Harness, string HarnessVersion,
    string Model, string ModelVersion)
{
    public static Checkpoint Create(GitSnapshot git, string codeCommit, string notesCommit, byte[] tasks,
        string toolHash, string harness, string harnessVersion, string model, string modelVersion)
    {
        foreach (var value in new[] { harness, harnessVersion, model, modelVersion })
            Note.Require(value, "harness/model identity and version");
        git.Commit(codeCommit);
        var notes = git.Notes(notesCommit).Where(n => n.Note.Status == "approved")
            .OrderBy(n => n.Note.Id, StringComparer.Ordinal).Select(n => new NoteRevision(n.Note.Id, n.Blob)).ToArray();
        if (notes.Length == 0)
            throw new InvalidOperationException("The selected revision has no approved notes. A draft cannot start an evaluation.");
        if (tasks.Length == 0) throw new FormatException("The task set is empty.");
        return new(1, codeCommit, notesCommit, notes, JsonData.Hash(tasks), toolHash, harness, harnessVersion, model, modelVersion);
    }

    public void Verify(GitSnapshot git, byte[] tasks, string toolHash)
    {
        if (Version != 1) throw new FormatException("Unsupported checkpoint version.");
        var actual = Create(git, CodeCommit, NotesCommit, tasks, toolHash, Harness, HarnessVersion, Model, ModelVersion);
        if (TaskSetSha256 != actual.TaskSetSha256 || ToolSha256 != actual.ToolSha256
            || ApprovedNotes is null || !ApprovedNotes.SequenceEqual(actual.ApprovedNotes))
            throw new InvalidOperationException("Checkpoint mismatch: task set, tool binary or approved-note inventory changed. Start a new evaluation batch.");
    }
}
