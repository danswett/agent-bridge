using System.Text.Json.Nodes;
using AgentBridge.Host;
using Xunit;

// The host half of the launch-folder editor (#2694): remove an approved folder, save only against the list the person saw, keep everything else.
public sealed class LaunchSettingsTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "abls-" + Guid.NewGuid().ToString("N")[..8]);
    readonly string dirs = Path.Combine(AppContext.BaseDirectory, "abls-" + Guid.NewGuid().ToString("N")[..8]);
    string ConfigPath => Path.Combine(folder, HostSettings.FileName);
    string Dir(string name) => Directory.CreateDirectory(Path.Combine(dirs, name)).FullName;

    public LaunchSettingsTests() { Directory.CreateDirectory(folder); }

    public void Dispose()
    {
        Directory.Delete(folder, recursive: true);
        if (Directory.Exists(dirs)) { Directory.Delete(dirs, recursive: true); }
    }

    void Write(object config) => File.WriteAllText(ConfigPath, System.Text.Json.JsonSerializer.Serialize(config));

    [Fact]
    public void Removing_a_folder_drops_only_that_entry_and_keeps_everything_else()
    {
        string a = Dir("a"), b = Dir("b");
        Write(new { version = 1, retained = 7, launch = new { permissions = "ask", workspaces = new object[] { new { path = a, label = "A", isolate = true, fleetWorkers = true }, new { path = b, label = "B", extra = "kept" } } } });
        var baseline = WorkspaceApproval.List(folder).Baseline;

        var result = WorkspaceApproval.Remove(folder, a, baseline);

        Assert.Equal((WorkspaceApproval.Removed, "A"), (result.Code, result.Label));
        var saved = JsonNode.Parse(File.ReadAllText(ConfigPath))!;
        Assert.Equal(7, saved["retained"]!.GetValue<int>());
        Assert.Equal("ask", saved["launch"]!["permissions"]!.GetValue<string>());
        var left = saved["launch"]!["workspaces"]!.AsArray();
        Assert.Single(left);
        Assert.Equal(("B", "kept"), (left[0]!["label"]!.GetValue<string>(), left[0]!["extra"]!.GetValue<string>()));
    }

    [Fact]
    public void A_baseline_that_no_longer_matches_is_stale_and_nothing_is_written()
    {
        string a = Dir("a"), b = Dir("b");
        Write(new { launch = new { workspaces = new[] { new { path = a, label = "A" } } } });
        var seen = WorkspaceApproval.List(folder).Baseline;
        Assert.Equal(WorkspaceApproval.Added, WorkspaceApproval.Add(folder, b, "B", false, _ => false).Code); // someone approves a folder after the person looked
        var before = File.ReadAllText(ConfigPath);

        var result = WorkspaceApproval.Remove(folder, a, seen);

        Assert.Equal(WorkspaceApproval.Stale, result.Code);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void A_folder_that_is_not_on_the_list_is_refused_and_a_subfolder_of_an_approved_one_is_not_approved()
    {
        string a = Dir("a");
        var below = Dir(Path.Combine("a", "below"));
        Write(new { launch = new { workspaces = new[] { new { path = a, label = "A" } } } });
        var baseline = WorkspaceApproval.List(folder).Baseline;
        var before = File.ReadAllText(ConfigPath);

        Assert.Equal(WorkspaceApproval.NotApproved, WorkspaceApproval.Remove(folder, below, baseline).Code);
        Assert.Equal(WorkspaceApproval.NotApproved, WorkspaceApproval.Remove(folder, Dir("other"), baseline).Code);
        Assert.Equal(WorkspaceApproval.NotAPath, WorkspaceApproval.Remove(folder, "relative\folder", baseline).Code);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }

    [Fact]
    public void An_approved_folder_that_has_gone_away_can_still_be_removed_and_the_list_still_shows_it()
    {
        var gone = Path.Combine(dirs, "gone");
        Directory.CreateDirectory(gone);
        Write(new { launch = new { workspaces = new[] { new { path = gone, label = "Gone" } } } });
        Directory.Delete(gone);
        var listed = WorkspaceApproval.List(folder);
        Assert.Equal(gone, Assert.Single(listed.Workspaces).Path);

        Assert.Equal(WorkspaceApproval.Removed, WorkspaceApproval.Remove(folder, gone, listed.Baseline).Code);
        Assert.Empty(WorkspaceApproval.List(folder).Workspaces);
    }

    [Fact]
    public void A_string_entry_is_found_by_its_folder_and_the_last_removal_leaves_an_empty_list()
    {
        string a = Dir("a");
        Write(new { launch = new { workspaces = new[] { a } } });
        var listed = WorkspaceApproval.List(folder);

        Assert.Equal(WorkspaceApproval.Removed, WorkspaceApproval.Remove(folder, a, listed.Baseline).Code);
        Assert.Empty(JsonNode.Parse(File.ReadAllText(ConfigPath))!["launch"]!["workspaces"]!.AsArray());
    }

    [Fact]
    public async Task A_held_gate_makes_the_remove_busy_not_a_lost_write()
    {
        string a = Dir("a");
        Write(new { launch = new { workspaces = new[] { new { path = a, label = "A" } } } });
        var baseline = WorkspaceApproval.List(folder).Baseline;
        using var held = new Mutex(false, WorkspaceApproval.GateName(folder));
        var other = Task.Run(() => { held.WaitOne(); Thread.Sleep(1500); held.ReleaseMutex(); });
        await Task.Delay(200);

        Assert.Equal(WorkspaceApproval.Busy, WorkspaceApproval.Remove(folder, a, baseline, TimeSpan.FromMilliseconds(100)).Code);
        await other;
        Assert.Single(WorkspaceApproval.List(folder).Workspaces);
    }
}
