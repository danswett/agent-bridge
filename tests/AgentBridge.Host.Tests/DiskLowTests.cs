using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

// #2771 (child of #1448): under 1 GiB free on the profile's disk the fleet grant is refused and so are fleet launches, with code diskLow
// (docs/design/agent-launch-scope.md section 11, "No fallback once HA launch is retired"), and host.status shows it. Free space that cannot be
// read never refuses a launch: it is allowed, said once in the log, and host.status says "disk unknown".
public sealed class DiskLowTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("ab-disklow-").FullName;
    readonly List<string> logged = [];
    static readonly DateTimeOffset Now = new(2026, 11, 24, 0, 0, 0, TimeSpan.Zero);
    static readonly ProcessAncestry Outside = new([new PeerProcess(123, Now, "pwsh.exe")], true);
    static AgentCredential Script => AgentCredential.Script("fixture", "machine", "dispatcher", Now) with { FleetLaunch = true };
    static LaunchRequest Request => new() { Request = Guid.NewGuid().ToString("D"), Agent = "claude", Workspace = "workers", Permissions = LaunchProtocol.AllowAll };
    long? free = 5L << 30;
    ProfileDisk Disk => new(folder, _ => free, logged.Add);
    public void Dispose() => Directory.Delete(folder, true);

    void Config() => File.WriteAllText(Path.Combine(folder, HostSettings.FileName), JsonSerializer.Serialize(new
    {
        agents = new { enabled = true, fleetLaunch = new { enabled = true }, fleet = new { workspaces = new[] { "workers" } } },
        launch = new { workspaces = new[] { new { label = "workers", path = folder, isolate = true } } },
    }));

    [Fact]
    public void Under_1_GiB_free_refuses_the_grant_and_fleet_launches_with_diskLow()
    {
        Config();
        var disk = Disk;
        var grant = new FleetLaunchGrant(folder, disk);
        free = (1L << 30) - 1;
        Assert.Contains("diskLow", grant.CheckGrant(Script));
        Assert.Contains("diskLow", grant.CheckLaunch(Script, Outside, Request));
        Assert.Equal("low", disk.State());
        free = 1L << 30;   // exactly 1 GiB is enough
        Assert.Null(grant.CheckGrant(Script));
        Assert.Null(grant.CheckLaunch(Script, Outside, Request));
        Assert.Equal("ok", disk.State());
    }

    [Fact]
    public void Free_space_that_cannot_be_read_allows_the_launch_says_so_once_and_shows_disk_unknown()
    {
        Config();
        free = null;
        var disk = Disk;
        var grant = new FleetLaunchGrant(folder, disk);
        Assert.Null(grant.CheckGrant(Script));
        Assert.Null(grant.CheckLaunch(Script, Outside, Request));
        Assert.Null(grant.CheckLaunch(Script, Outside, Request));
        Assert.Equal("unknown", disk.State());
        Assert.Single(logged, line => line.Contains("free space", StringComparison.Ordinal));
        free = 10L << 30;
        Assert.Equal("ok", disk.State());
    }

    [Fact]
    public void The_real_reader_reads_this_disk_and_a_path_it_cannot_read_is_unknown_not_low()
    {
        Assert.True(new ProfileDisk(folder).FreeBytes() > 0);
        Assert.True(new ProfileDisk(Path.Combine(folder, "not", "yet", "made")).FreeBytes() > 0);   // a folder that does not exist yet reads its nearest parent
        Assert.Null(new ProfileDisk("\0bad").FreeBytes());
    }

    [Fact]
    public void host_status_shows_diskLow_and_disk_unknown_and_nothing_when_it_is_fine()
    {
        string? shown = null;
        var control = new HostControl(new HostControlOptions
        {
            Profile = "test", Folder = folder, Version = "0.1.0",
            Machine = new MachineInfo("test", "Test", "windows", "0.1.0"),
            Running = new HostSettingsView(true),
            SignIn = (_, _, _) => throw new NotSupportedException(), SignedIn = (_, _) => throw new NotSupportedException(),
            Stop = () => throw new NotSupportedException(), Restart = () => throw new NotSupportedException(),
            Disk = () => shown,
        }, logged.Add);
        Assert.Null(control.Status().Disk);
        shown = ProfileDisk.Describe((1L << 30) - 1);
        Assert.Contains("diskLow", control.Status().Disk);
        shown = ProfileDisk.Describe(null);
        Assert.Equal("disk unknown", control.Status().Disk);
    }
}
