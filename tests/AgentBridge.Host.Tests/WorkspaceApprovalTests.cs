using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Host.Delivery.Launch;
using Xunit;

// The one check a folder passes before it is approved, shared by the command line and the app's Add folder: a closed code per refusal.
public sealed class WorkspaceApprovalTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "abwa-" + Guid.NewGuid().ToString("N")[..8]);
    string ConfigPath => Path.Combine(folder, HostSettings.FileName);

    public WorkspaceApprovalTests() => Directory.CreateDirectory(folder);

    public void Dispose() => Directory.Delete(folder, recursive: true);

    string Dir(string name) => Directory.CreateDirectory(Path.Combine(folder, name)).FullName;

    WorkspaceApproval.Result Add(string path, string? label = null, bool isolate = false, Func<string, bool>? system = null) =>
        WorkspaceApproval.Add(folder, path, label, isolate, system ?? (_ => false));

    [Fact]
    public void A_folder_is_approved_exactly_with_a_default_label_and_the_rest_of_the_file_is_kept()
    {
        File.WriteAllText(ConfigPath, """{ "version": 1, "retained": 7, "launch": { "defaultAgent": "copilot" } }""");
        var app = Dir("My Tools");

        var result = Add(app);

        Assert.Equal((WorkspaceApproval.Added, app, "My Tools"), (result.Code, result.Path, result.Label));
        var saved = JsonNode.Parse(File.ReadAllText(ConfigPath))!;
        Assert.Equal(7, saved["retained"]!.GetValue<int>());
        Assert.Equal("copilot", saved["launch"]!["defaultAgent"]!.GetValue<string>());
        var entry = saved["launch"]!["workspaces"]![0]!;
        Assert.Equal((app, "My Tools"), (entry["path"]!.GetValue<string>(), entry["label"]!.GetValue<string>()));
        Assert.False(entry.AsObject().ContainsKey("isolate"));
        var (settings, problem) = HostSettings.Load(folder);
        Assert.Null(problem);
        Assert.Equal([app], settings.Launch.Workspaces.Select(w => w.Path));
    }

    [Fact]
    public void Every_refusal_has_its_own_closed_code_and_writes_nothing()
    {
        var app = Dir("app");
        Assert.Equal(WorkspaceApproval.Added, Add(app, "App").Code);
        var before = File.ReadAllText(ConfigPath);
        var cases = new (string Code, WorkspaceApproval.Result Result)[]
        {
            (WorkspaceApproval.NotAPath, Add("bad\0path")),
            (WorkspaceApproval.Missing, Add(Path.Combine(folder, "missing"))),
            (WorkspaceApproval.SystemFolder, Add(Dir("sys"), system: _ => true)),
            (WorkspaceApproval.NotGit, Add(Dir("plain"), isolate: true)),
            (WorkspaceApproval.AlreadyApproved, Add(app + Path.DirectorySeparatorChar, "Again")),
            (WorkspaceApproval.LabelInUse, Add(Dir("other"), "App")),
        };

        foreach (var (code, result) in cases) { Assert.Equal(code, result.Code); }
        Assert.Equal("App", cases[4].Result.Label);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
        Assert.Equal((WorkspaceApproval.Added, "Mine"), (Add(Dir("t"), "  Mine  ").Code, HostSettings.Load(folder).Settings.Launch.Workspaces[^1].Label));
    }

    [Fact]
    public void Only_an_absolute_path_is_taken_and_never_one_resolved_against_the_hosts_own_folder()
    {
        foreach (var given in new[] { ".", "app", "..", "~nobody", "", "  " })
        {
            Assert.Equal(WorkspaceApproval.NotAPath, Add(given).Code);
        }
    }

    [Fact]
    public void A_device_path_cannot_get_past_the_system_folder_check()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

        Assert.Equal(WorkspaceApproval.SystemFolder, WorkspaceApproval.Add(folder, windows, null, false, Folders.IsSystemDirectory).Code);
        foreach (var prefix in new[] { @"\\?\", @"\\.\" })
        {
            Assert.Equal(WorkspaceApproval.NotAPath, WorkspaceApproval.Add(folder, prefix + windows, null, false, Folders.IsSystemDirectory).Code);
        }
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void A_network_share_is_not_a_local_folder_and_is_refused_before_it_is_touched()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        foreach (var share in new[] { @"\\server\share\work", @"\\localhost\C$\Users", @"\\127.0.0.1\share" })
        {
            Assert.Equal(WorkspaceApproval.NotLocal, Add(share).Code);
        }
        Assert.False(File.Exists(ConfigPath));
    }

    [Fact]
    public void Only_a_local_fixed_drive_is_taken()
    {
        if (!OperatingSystem.IsWindows()) { return; }

        Assert.True(WorkspaceApproval.IsLocalFixed(Dir("here")));
        Assert.False(WorkspaceApproval.IsLocalFixed(@"\\server\share"));
        var unused = Enumerable.Range('D', 23).Select(i => (char)i).First(c => !DriveInfo.GetDrives().Any(d => d.Name[0] == c));
        Assert.False(WorkspaceApproval.IsLocalFixed(unused + @":\none"));
    }

    [Fact]
    public void While_the_config_file_is_being_changed_elsewhere_the_answer_is_busy_after_the_wait_and_nothing_is_written()
    {
        File.WriteAllText(ConfigPath, """{ "version": 1 }""");
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = new Thread(() =>
        {
            using var mutex = new Mutex(false, WorkspaceApproval.GateName(folder));
            mutex.WaitOne();
            held.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        holder.Start();
        held.Wait();
        try
        {
            var started = Environment.TickCount64;

            var result = WorkspaceApproval.Add(folder, Dir("a"), null, false, _ => false, TimeSpan.FromMilliseconds(200));

            Assert.Equal(WorkspaceApproval.Busy, result.Code);
            Assert.InRange(Environment.TickCount64 - started, 150, 5000);
            Assert.Equal("""{ "version": 1 }""", File.ReadAllText(ConfigPath));
        }
        finally { release.Set(); holder.Join(); }
        Assert.Equal(WorkspaceApproval.Added, Add(Dir("b")).Code);
    }

    [Fact]
    public async Task Approvals_made_at_the_same_time_are_all_kept()
    {
        var dirs = Enumerable.Range(0, 8).Select(i => Dir("p" + i)).ToArray();

        var results = await Task.WhenAll(dirs.Select(d => Task.Run(() => Add(d))));

        Assert.All(results, r => Assert.Equal(WorkspaceApproval.Added, r.Code));
        Assert.Equal(dirs.Order(), HostSettings.Load(folder).Settings.Launch.Workspaces.Select(w => w.Path).Order());
    }

    [Fact]
    public void A_config_file_that_cannot_be_replaced_is_unwritable_and_leaves_no_temp_file_behind()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        File.WriteAllText(ConfigPath, """{ "version": 1 }""");
        File.SetAttributes(ConfigPath, FileAttributes.ReadOnly);
        try
        {
            Assert.Equal(WorkspaceApproval.Unwritable, Add(Dir("a")).Code);
            Assert.Equal([ConfigPath], Directory.GetFiles(folder, "*", SearchOption.AllDirectories));
        }
        finally { File.SetAttributes(ConfigPath, FileAttributes.Normal); }
    }

    [Fact]
    public void No_more_than_the_limit_can_be_approved()
    {
        var entries = Enumerable.Range(0, 64).Select(i => new { path = Dir("w" + i), label = "W" + i }).ToArray();
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(new { version = 1, launch = new { workspaces = entries } }));
        var before = File.ReadAllText(ConfigPath);

        Assert.Equal(WorkspaceApproval.TooMany, Add(Dir("one-more")).Code);
        Assert.Equal(before, File.ReadAllText(ConfigPath));
    }
}
