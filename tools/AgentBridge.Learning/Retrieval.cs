using System.Text;
using AgentBridge.Quality;

namespace AgentBridge.Learning;

sealed record ReturnedNote(string Id, string Blob, string Text);
sealed record WithheldNote(string Id, string Reason);
sealed record PendingNote(string Id, string Blob);
sealed record Overlap(string Path, string[] NoteIds);
sealed record SearchResult(
    int Version, string CodeCommit, string NotesCommit, string[] Paths,
    ReturnedNote[] Returned, PendingNote[] Pending, WithheldNote[] Withheld,
    Overlap[] Overlaps, int PayloadBytes, string Accounting);

static class Retrieval
{
    public static SearchResult Search(GitSnapshot git, string codeCommit, string notesCommit, string[] paths,
        DateOnly today, int maxNotes = 12, int maxBytes = 12_000)
    {
        git.Commit(codeCommit);
        if (paths.Length == 0) throw new ArgumentException("Supply at least one --path.");
        foreach (var path in paths) Note.Path(path);
        if (maxNotes is < 1 or > 12 || maxBytes is < 1 or > 12_000)
            throw new ArgumentException("The caps are 1-12 notes and 1-12000 UTF-8 bytes.");
        var targets = paths.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var matches = git.Notes(notesCommit)
            .Where(n => n.Note.Status == "approved" && targets.Any(p => n.Note.Paths.Any(g => new Glob(g).IsMatch(p))))
            .OrderBy(n => n.Note.Kind == "invariant" ? 0 : 1)
            .ThenBy(n => n.Note.Id, StringComparer.Ordinal).ToList();
        var returned = new List<ReturnedNote>();
        var pending = new List<PendingNote>();
        var withheld = new List<WithheldNote>();
        var bytes = 0;
        foreach (var pinned in matches)
        {
            var note = pinned.Note;
            var problem = note.RecheckBy < today ? "Recheck date passed." : git.ReferenceProblem(note, codeCommit);
            if (problem is not null)
            {
                withheld.Add(new(note.Id, problem));
                continue;
            }
            var text = note.Render();
            var size = Encoding.UTF8.GetByteCount(text);
            if (returned.Count >= maxNotes || bytes + size > maxBytes)
                pending.Add(new(note.Id, pinned.Blob));
            else
            {
                returned.Add(new(note.Id, pinned.Blob, text));
                bytes += size;
            }
        }
        // Overlap is a review signal, not proof of contradiction or permission to prefer one rule.
        var overlaps = targets.Select(path => new Overlap(path,
                matches.Where(n => n.Note.Paths.Any(g => new Glob(g).IsMatch(path))).Select(n => n.Note.Id).ToArray()))
            .Where(o => o.NoteIds.Length > 1).ToArray();
        return new(1, codeCommit, notesCommit, targets, returned.ToArray(), pending.ToArray(), withheld.ToArray(),
            overlaps, bytes, "returned-only; no claim of prompt delivery, application or pre-edit coverage");
    }
}
