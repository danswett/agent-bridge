using System.Text;

namespace AgentBridge.Learning.Tests;

[Trait("Category", "Process")]
public class SnapshotTests
{
    [Fact]
    public void Only_approved_notes_at_the_pinned_revision_are_returned()
    {
        using var repo = new ScratchRepo();
        var commit = repo.WithNotes(Fixture.Note(), Fixture.Note("K-fixture-draft", "draft"),
            Fixture.Note("K-fixture-retired", "retired"), Fixture.Note("K-fixture-stale", "needs-verification"));
        var result = Search(repo, commit);
        Assert.Equal("K-fixture-one", Assert.Single(result.Returned).Id);
        Assert.Empty(result.Withheld);
        Assert.Contains("returned-only", result.Accounting);
    }

    [Fact]
    public void Working_tree_edits_and_later_commits_cannot_replace_the_pinned_guidance()
    {
        using var repo = new ScratchRepo();
        var commit = repo.WithNotes(Fixture.Note());
        var original = Search(repo, commit);
        var changed = Fixture.Note() with { Guidance = Fixture.Note().Guidance with { Do = "A later unreviewed instruction." } };
        repo.Write($"{GitSnapshot.NotesFolder}/{changed.Id}.json", JsonData.Write(changed));
        var status = repo.Git("status", "--porcelain");
        Assert.Equal(original.Returned, Search(repo, commit).Returned);
        Assert.Equal(status, repo.Git("status", "--porcelain"));
        repo.Commit();
        Assert.Equal(original.Returned, Search(repo, commit).Returned);
    }

    [Fact]
    public void Source_changes_are_withheld_but_unrelated_changes_do_not_expire_a_note()
    {
        using var repo = new ScratchRepo();
        var notes = repo.WithNotes(Fixture.Note());
        repo.Write("unrelated.txt", "new work on main");
        var unrelated = repo.Commit();
        Assert.Single(Search(repo, notes, unrelated).Returned);
        repo.Write("src/caption.cs", "changed upstream behavior");
        var changed = repo.Commit();
        var result = Search(repo, notes, changed);
        Assert.Empty(result.Returned);
        Assert.Contains("changed since verification", Assert.Single(result.Withheld).Reason);
    }

    [Fact]
    public void Local_git_replacement_refs_cannot_change_an_immutable_notes_snapshot()
    {
        using var repo = new ScratchRepo();
        var notes = repo.WithNotes(Fixture.Note());
        var original = Search(repo, notes).Returned;
        var changed = Fixture.Note() with { Guidance = Fixture.Note().Guidance with { Do = "Unreviewed replacement." } };
        var replacement = repo.WithNotes(changed);
        repo.Git("replace", notes, replacement);
        Assert.Equal(original, Search(repo, notes).Returned);
    }

    [Fact]
    public void Missing_source_references_and_expired_invariants_are_withheld()
    {
        using var repo = new ScratchRepo();
        var notes = repo.WithNotes(
            Fixture.Note("K-fixture-missing") with { References = ["src/missing.cs"] },
            Fixture.Note("K-fixture-invariant") with { Kind = "invariant", RecheckBy = Fixture.Today.AddDays(-1) });
        var result = Search(repo, notes);
        Assert.Empty(result.Returned);
        Assert.Equal(2, result.Withheld.Length);
    }

    [Fact]
    public void Globs_match_the_root_file_and_descendants_without_matching_neighbor_folders()
    {
        using var repo = new ScratchRepo();
        var notes = repo.WithNotes(Fixture.Note());
        Assert.Single(Search(repo, notes, paths: ["src/caption.cs"]).Returned);
        Assert.Single(Search(repo, notes, paths: ["src/nested/caption.cs"]).Returned);
        Assert.Empty(Search(repo, notes, paths: ["src-neighbor/caption.cs"]).Returned);
    }

    [Fact]
    public void Twenty_matching_notes_return_twelve_and_name_the_other_eight_without_crediting_them()
    {
        using var repo = new ScratchRepo();
        var set = Enumerable.Range(1, 20).Select(i => Fixture.Note($"K-fixture-{i:00}")).ToArray();
        set[19] = set[19] with { Kind = "invariant" };
        var notes = repo.WithNotes(set);
        var result = Search(repo, notes);
        Assert.Equal(12, result.Returned.Length);
        Assert.Equal(8, result.Pending.Length);
        Assert.Equal("K-fixture-20", result.Returned[0].Id);
        Assert.Empty(result.Returned.Select(n => n.Id).Intersect(result.Pending.Select(n => n.Id)));
        Assert.Equal(20, Assert.Single(result.Overlaps).NoteIds.Length);
        Assert.Equal(result.Returned.Sum(n => Encoding.UTF8.GetByteCount(n.Text)), result.PayloadBytes);
    }

    [Fact]
    public void The_byte_cap_never_truncates_a_note_or_marks_an_omitted_note_as_returned()
    {
        using var repo = new ScratchRepo();
        var notes = repo.WithNotes(Fixture.Note());
        var result = Retrieval.Search(repo.Snapshot, repo.CodeCommit, notes, ["src/caption.cs"], Fixture.Today, maxBytes: 1);
        Assert.Empty(result.Returned);
        Assert.Single(result.Pending);
        Assert.Equal(0, result.PayloadBytes);
    }

    [Fact]
    public void Duplicate_note_ids_are_rejected_even_when_one_is_a_draft()
    {
        using var repo = new ScratchRepo();
        repo.WithNotes(Fixture.Note());
        repo.Write($"{GitSnapshot.NotesFolder}/duplicate.json", JsonData.Write(Fixture.Note(status: "draft")));
        var notes = repo.Commit();
        Assert.Throws<FormatException>(() => Search(repo, notes));
    }

    [Fact]
    public void Git_symlinks_are_not_followed_as_notes()
    {
        using var repo = new ScratchRepo();
        var notes = repo.WithNotes(Fixture.Note());
        var blob = repo.Git("rev-parse", $"{notes}:{GitSnapshot.NotesFolder}/K-fixture-one.json").Trim();
        repo.Git("update-index", "--cacheinfo", $"120000,{blob},{GitSnapshot.NotesFolder}/K-fixture-one.json");
        repo.Git("commit", "--quiet", "-m", "Synthetic symlink mode");
        var symlinkCommit = repo.Git("rev-parse", "HEAD").Trim();
        Assert.Throws<FormatException>(() => Search(repo, symlinkCommit));
        repo.Git("update-index", "--cacheinfo", $"100644,{blob},{GitSnapshot.NotesFolder}/K-fixture-one.json");
    }

    [Fact]
    public void A_checkpoint_pins_notes_tasks_and_tool_and_rejects_drift()
    {
        using var repo = new ScratchRepo();
        var notes = repo.WithNotes(Fixture.Note());
        var tasks = Encoding.UTF8.GetBytes("held-out task set");
        var checkpoint = Checkpoint.Create(repo.Snapshot, repo.CodeCommit, notes, tasks, "tool-hash",
            "fixture-harness", "1", "fixture-model", "1");
        var restored = JsonData.Read<Checkpoint>(JsonData.Write(checkpoint));
        restored.Verify(repo.Snapshot, tasks, "tool-hash");
        Assert.Throws<InvalidOperationException>(() => restored.Verify(repo.Snapshot, Encoding.UTF8.GetBytes("changed tasks"), "tool-hash"));
        Assert.Throws<InvalidOperationException>(() => restored.Verify(repo.Snapshot, tasks, "changed-tool"));
        Assert.Throws<InvalidOperationException>(() => (restored with { ApprovedNotes = [] }).Verify(repo.Snapshot, tasks, "tool-hash"));
    }

    [Fact]
    public void Draft_only_snapshots_cannot_start_an_evaluation()
    {
        using var repo = new ScratchRepo();
        var notes = repo.WithNotes(Fixture.Note(status: "draft"));
        Assert.Throws<InvalidOperationException>(() => Checkpoint.Create(repo.Snapshot, repo.CodeCommit, notes,
            [1], "tool-hash", "fixture", "1", "fixture", "1"));
    }

    [Fact]
    public void The_cli_checkpoint_and_search_leave_git_and_working_files_unchanged()
    {
        using var repo = new ScratchRepo();
        var notes = repo.WithNotes(Fixture.Note() with { RecheckBy = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1) });
        repo.Write("tasks.txt", "Synthetic held-out task");
        var checkpointOutput = new StringWriter();
        Assert.Equal(0, Program.Run(["checkpoint", "--repo", repo.Root, "--code-commit", repo.CodeCommit,
            "--notes-commit", notes, "--tasks", Path.Combine(repo.Root, "tasks.txt"),
            "--harness", "fixture", "--harness-version", "1", "--model", "fixture", "--model-version", "1"], checkpointOutput));
        repo.Write("checkpoint.json", checkpointOutput.ToString());
        var before = repo.Git("status", "--porcelain");
        var output = new StringWriter();
        Assert.Equal(0, Program.Run(["search", "--repo", repo.Root, "--checkpoint", Path.Combine(repo.Root, "checkpoint.json"),
            "--tasks", Path.Combine(repo.Root, "tasks.txt"), "--path", "src/caption.cs"], output));
        Assert.Contains("K-fixture-one", output.ToString());
        Assert.Equal(before, repo.Git("status", "--porcelain"));
        Assert.Equal(notes, repo.Git("rev-parse", "HEAD").Trim());
    }

    static SearchResult Search(ScratchRepo repo, string notes, string? code = null, string[]? paths = null) =>
        Retrieval.Search(repo.Snapshot, code ?? repo.CodeCommit, notes, paths ?? ["src/caption.cs"], Fixture.Today);
}
