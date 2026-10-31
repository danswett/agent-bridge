using AgentBridge.Host;
using Xunit;

// The host's own updater, as far as it can be tried without an installed package.
public sealed class HostUpdatesTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abhup-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) { Directory.Delete(root, true); }
    }

    [Fact]
    public void The_app_is_restarted_for_the_profile_of_the_host_that_installed_the_update()
    {
        Assert.Equal(["--background", "--profile", "work"], VelopackHostUpdates.RestartArguments("work"));
        Assert.Equal(["--background", "--profile", "default"], VelopackHostUpdates.RestartArguments("default"));
    }

    [Fact]
    public void A_copy_velopack_did_not_install_has_no_installed_version_and_the_feed_decides_the_releases_page()
    {
        var updates = new VelopackHostUpdates(root, "default", () => { }, _ => { });
        Assert.Null(updates.Installed);
        Assert.Equal("https://github.com/danswett/agent-bridge/releases", updates.ReleasesUrl);
        Directory.CreateDirectory(Path.Combine(root, "app"));
        File.WriteAllText(Path.Combine(root, "app", "feed.txt"), Path.Combine(root, "releases"));
        Assert.Null(updates.ReleasesUrl);
    }
}
