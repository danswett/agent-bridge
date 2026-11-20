using System.Security.Cryptography;
using AgentBridge.App.Core;
using Xunit;

// #3343: Windows' telemetry service (DiagTrack) held current\mscordaccore.dll, so Velopack could
// not rename current and every update failed until a reboot. Offline fakes only: "held" is what
// the probe says, never a real handle.
public sealed class InPlaceApplyTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abinplace-" + Guid.NewGuid().ToString("N")[..8]);
    string Current => Path.Combine(root, "current");
    string Staged => Path.Combine(root, "staged");
    string Backup => Path.Combine(root, "current-inplace");

    public InPlaceApplyTests()
    {
        Write(Current, "mscordaccore.dll", "dac 10.0");
        Write(Current, "AgentBridge.dll", "app 1");
        Write(Current, @"lib\old.dll", "dropped in 2");
        Write(Staged, "mscordaccore.dll", "dac 10.0");
        Write(Staged, "AgentBridge.dll", "app 2");
        Write(Staged, @"lib\new.dll", "added in 2");
    }

    public void Dispose()
    {
        if (!Directory.Exists(root)) { return; }
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) { File.SetAttributes(f, FileAttributes.Normal); }
        Directory.Delete(root, recursive: true);
    }

    static void Write(string dir, string rel, string text)
    {
        var path = Path.Combine(dir, rel.Replace('\\', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    string Held(string rel) => Path.Combine(Current, rel.Replace('\\', Path.DirectorySeparatorChar));

    static Dictionary<string, string> Snapshot(string dir) =>
        Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(dir, f), f => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))));

    bool Install(Func<bool> apply, params string[] held) =>
        InPlaceApply.Install(apply, () => held.Select(Held).ToList(), Staged, Current, Backup);

    [Fact]
    public void A_refused_rename_installs_in_place_and_leaves_a_held_identical_file_untouched()
    {
        // Read-only: any write to the held file would throw, so passing proves it was skipped.
        File.SetAttributes(Held("mscordaccore.dll"), FileAttributes.ReadOnly);
        // Applied once and never retried (where an elevated "release and update" would go): the
        // in-place copy runs in this process with the rights it already has.
        var applies = 0;

        Assert.True(Install(() => { applies++; return false; }, "mscordaccore.dll"));

        Assert.Equal(1, applies);
        Assert.Equal(Snapshot(Staged), Snapshot(Current));
        Assert.False(Directory.Exists(Backup));
    }

    [Fact]
    public void A_held_file_that_differs_fails_naming_it_and_leaves_current_unchanged()
    {
        Write(Staged, "mscordaccore.dll", "dac 10.1");
        var before = Snapshot(Current);

        var e = Assert.Throws<InPlaceApplyException>(() => Install(() => false, "mscordaccore.dll"));

        Assert.Contains("mscordaccore.dll", e.Message);
        Assert.Contains("nothing was installed", e.Message);
        Assert.Equal(before, Snapshot(Current));
        Assert.False(Directory.Exists(Backup));
    }

    [Fact]
    public void A_held_file_the_new_version_drops_stays_in_place_with_a_warning()
    {
        // Chief, Nov 19: not a failure; a later apply removes it once nothing holds it.
        File.Delete(Path.Combine(Staged, "mscordaccore.dll"));
        var expected = new Dictionary<string, string>(Snapshot(Staged)) { ["mscordaccore.dll"] = Snapshot(Current)["mscordaccore.dll"] };
        var log = new List<string>();

        Assert.True(InPlaceApply.Install(() => false, () => [Held("mscordaccore.dll")], Staged, Current, Backup, log.Add));

        Assert.Equal(expected, Snapshot(Current));
        Assert.Contains(log, l => l.Contains("mscordaccore.dll") && l.Contains("not in the new version"));
    }

    [Fact]
    public void When_the_existing_apply_succeeds_current_is_left_to_it()
    {
        var before = Snapshot(Current);
        var probed = false;

        var inPlace = InPlaceApply.Install(() => true, () => { probed = true; return []; }, Staged, Current, Backup);

        Assert.False(inPlace);
        Assert.False(probed);
        Assert.Equal(before, Snapshot(Current));
    }

    [Fact]
    public void A_refused_apply_with_nothing_held_is_not_retried_in_place()
    {
        var before = Snapshot(Current);

        Assert.Throws<InPlaceApplyException>(() => Install(() => false));

        Assert.Equal(before, Snapshot(Current));
    }

    // A held file the new version replaces with a folder: the copy into it fails last (files are
    // copied in name order), after old files moved aside and new ones were written.
    void FailLast()
    {
        Write(Current, "x", "held, a folder in 2");
        Write(Staged, @"x\y.dll", "added in 2");
    }

    [Fact]
    public void A_file_that_cannot_be_written_mid_way_puts_the_old_version_back()
    {
        FailLast();
        var before = Snapshot(Current);

        var e = Assert.Throws<InPlaceApplyException>(() => Install(() => false, "x"));

        Assert.Contains("old version was put back", e.Message);
        Assert.Equal(before, Snapshot(Current));
        Assert.False(Directory.Exists(Backup));
    }

    [Fact]
    public void A_read_only_new_file_is_removed_when_the_old_version_is_put_back()
    {
        Write(Staged, "a.dll", "added in 2");
        File.SetAttributes(Path.Combine(Staged, "a.dll"), FileAttributes.ReadOnly);
        FailLast();
        var before = Snapshot(Current);

        var e = Assert.Throws<InPlaceApplyException>(() => Install(() => false, "x"));

        Assert.Contains("old version was put back", e.Message);
        Assert.Equal(before, Snapshot(Current));
    }

    [Fact]
    public void A_read_only_old_file_does_not_leave_the_backup_behind()
    {
        File.SetAttributes(Held("AgentBridge.dll"), FileAttributes.ReadOnly);

        Assert.True(Install(() => false, "mscordaccore.dll"));

        Assert.Equal(Snapshot(Staged), Snapshot(Current));
        Assert.False(Directory.Exists(Backup));
    }

    [Fact]
    public void A_folder_the_new_version_replaces_with_a_file_installs()
    {
        Write(Current, @"lib\new.dll\inner.txt", "a folder in 1");

        Assert.True(Install(() => false, "mscordaccore.dll"));

        Assert.Equal(Snapshot(Staged), Snapshot(Current));
    }

    [Fact]
    public void A_file_the_new_version_replaces_with_a_folder_is_put_back_on_failure()
    {
        Write(Current, "plugins", "a file in 1");
        Write(Staged, @"plugins\p.dll", "a folder in 2");
        FailLast();
        var before = Snapshot(Current);

        var e = Assert.Throws<InPlaceApplyException>(() => Install(() => false, "x"));

        Assert.Contains("old version was put back", e.Message);
        Assert.Equal(before, Snapshot(Current));
    }

    [Fact]
    public void A_leftover_backup_whose_files_are_all_the_same_in_current_is_cleared_and_said()
    {
        Write(Backup, "AgentBridge.dll", "app 1");
        File.SetAttributes(Path.Combine(Backup, "AgentBridge.dll"), FileAttributes.ReadOnly);
        var log = new List<string>();

        Assert.True(InPlaceApply.Install(() => false, () => [Held("mscordaccore.dll")], Staged, Current, Backup, log.Add));

        Assert.Equal(Snapshot(Staged), Snapshot(Current));
        Assert.False(Directory.Exists(Backup));
        Assert.Contains(log, l => l.Contains("removed") && l.Contains(Backup));
    }

    [Fact]
    public void A_leftover_backup_whose_file_differs_in_current_is_kept_when_the_retry_fails_too()
    {
        Write(Backup, "AgentBridge.dll", "app 1");
        Write(Current, "AgentBridge.dll", "app");
        FailLast();
        var e = Assert.Throws<InPlaceApplyException>(() => Install(() => false, "x"));

        var kept = Assert.Single(Directory.GetDirectories(root, "current-inplace-*"));
        Assert.Equal("app 1", File.ReadAllText(Path.Combine(kept, "AgentBridge.dll")));
        Assert.Contains(kept, e.Message);
    }

    [Fact]
    public void A_leftover_backup_holding_the_only_copy_of_a_file_is_kept_aside_and_said()
    {
        Write(Backup, "only.dll", "nowhere else");
        var log = new List<string>();

        Assert.True(InPlaceApply.Install(() => false, () => [Held("mscordaccore.dll")], Staged, Current, Backup, log.Add));

        Assert.Equal(Snapshot(Staged), Snapshot(Current));
        Assert.False(Directory.Exists(Backup));
        var kept = Assert.Single(Directory.GetDirectories(root, "current-inplace-*"));
        Assert.Equal("nowhere else", File.ReadAllText(Path.Combine(kept, "only.dll")));
        Assert.Contains(log, l => l.Contains("kept") && l.Contains(kept));
    }
}
