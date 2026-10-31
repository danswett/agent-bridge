using AgentBridge.HomeAssistant;
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

    [Fact]
    public async Task Only_the_release_a_check_here_found_and_downloaded_is_downloaded_or_applied()
    {
        var stopped = false;
        var updates = new VelopackHostUpdates(root, "default", () => stopped = true, _ => { });
        // Same version, but not the one a check here found (another feed's, say): refused.
        var stranger = new UpdateRelease("0.2.0", null, null);
        await Assert.ThrowsAsync<InvalidOperationException>(() => updates.DownloadAsync(stranger, _ => { }, CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => updates.ApplyAndRestart(stranger));
        Assert.Throws<InvalidOperationException>(() => updates.ApplyAndRestart(stranger with { Handle = new object() }));
        Assert.False(stopped);
    }

    [Fact]
    public void Pre_releases_follow_the_apps_setting_for_the_profile_read_as_the_app_reads_it()
    {
        var updates = new VelopackHostUpdates(root, "work", () => { }, _ => { });
        Assert.True(updates.Prereleases());
        var file = Path.Combine(root, "app", "work-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, """{"prereleases": false}""");
        Assert.False(updates.Prereleases());
        File.WriteAllText(file, """{"Prereleases": false}""");
        Assert.False(updates.Prereleases());
        File.WriteAllText(file, """{"prereleases": true}""");
        Assert.True(updates.Prereleases());
        File.WriteAllText(file, "not json");
        Assert.True(updates.Prereleases());
        // Another profile's choice is its own.
        File.WriteAllText(Path.Combine(root, "app", "default-settings.json"), """{"prereleases": false}""");
        File.WriteAllText(file, "{}");
        Assert.True(updates.Prereleases());
    }

    [Fact]
    public async Task A_feed_that_is_configured_but_cannot_be_read_fails_the_check_rather_than_falling_back_to_github()
    {
        // A file another process holds without sharing: Windows refuses the read; elsewhere such
        // locks are only advisory.
        if (!OperatingSystem.IsWindows()) { return; }
        Directory.CreateDirectory(Path.Combine(root, "app"));
        var feed = Path.Combine(root, "app", "feed.txt");
        File.WriteAllText(feed, Path.Combine(root, "releases"));
        var updates = new VelopackHostUpdates(root, "default", () => { }, _ => { });
        using (new FileStream(feed, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsAsync<IOException>(() => updates.CheckAsync(CancellationToken.None));
            Assert.Null(updates.ReleasesUrl);
        }
    }
}
