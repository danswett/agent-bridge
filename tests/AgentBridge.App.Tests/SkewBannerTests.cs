using AgentBridge.App.Core;
using Xunit;

// #2869 G2 (problem 3, owner: "No, banner only"): the app and the host on different releases is said in the app's
// update banner, in the slot the Ready banner has, one at a time. It only says it: nothing installs because of a
// skew, in any mode; the person's press is Restart to update, as for a release that is ready.
public sealed class SkewBannerTests
{
    sealed class Rig
    {
        public string? App = "0.1.7684", Host = "0.1.7522", Ready;
        public int Restarts;
        public AppUpdateViewModel Bar;
        public int Said;

        public Rig()
        {
            Bar = new AppUpdateViewModel(() => Ready, _ => { Restarts++; return Task.FromResult<string?>(null); }, null, () => (App, Host));
            Bar.Announced += () => Said++;
        }
    }

    [Theory]
    [InlineData("0.1.7684", "0.1.7684")]
    [InlineData("v0.1.7684", "0.1.7684+abc")]
    public void Matching_releases_say_nothing(string app, string host)
    {
        var rig = new Rig { App = app, Host = host };

        Assert.False(rig.Bar.Shown);
        Assert.Equal("", rig.Bar.Message);
    }

    [Theory]
    [InlineData(null, "0.1.7522")]
    [InlineData("0.1.7684", null)]
    [InlineData("0.0.0", "0.1.7522")]
    [InlineData("0.1.7684", "0.1.7522-dev")]
    [InlineData("0.1.7684", "dev")]
    public void A_version_that_is_not_known_is_not_skew(string? app, string? host)
    {
        var rig = new Rig { App = app, Host = host };

        Assert.False(rig.Bar.Shown);
    }

    [Fact]
    public void A_host_older_than_the_app_says_so_and_points_to_Settings_with_no_button()
    {
        var rig = new Rig();

        Assert.True(rig.Bar.Shown);
        Assert.Equal("The bridge host is on version 0.1.7522 and this app on 0.1.7684. Restart the host in Settings to bring it to 0.1.7684.", rig.Bar.Message);
        // Text only: no verified package to install, so no button that would do something else.
        Assert.False(rig.Bar.Offered);
        Assert.False(rig.Bar.CanRestart);
        Assert.False(rig.Bar.Failed);
    }

    [Fact]
    public void A_host_newer_than_the_app_says_so_and_points_to_the_check_for_updates()
    {
        var rig = new Rig { App = "0.1.7522", Host = "0.1.7684" };

        Assert.Equal("The bridge host is on version 0.1.7684 and this app on 0.1.7522. Check for updates in Settings to bring the app to 0.1.7684.", rig.Bar.Message);
        Assert.False(rig.Bar.Offered);
    }

    [Theory]
    [InlineData("0.1.7700", "the release is newer than both")]
    [InlineData("0.1.7600", "the release is newer than the app and older than the host: still the Ready banner")]
    public void A_release_that_is_ready_is_the_one_banner_whatever_the_skew(string ready, string because)
    {
        var rig = new Rig { Ready = ready };

        Assert.Equal(AppUpdateViewModel.Ready(ready), rig.Bar.Message);
        Assert.True(rig.Bar.Offered, because);
        Assert.Equal("Restart to update", rig.Bar.ActionLabel);
    }

    [Fact]
    public void The_skew_note_gives_way_to_a_release_that_becomes_ready_and_comes_back_with_it_gone()
    {
        var rig = new Rig();
        Assert.Contains("Restart the host in Settings", rig.Bar.Message, StringComparison.Ordinal);

        rig.Ready = "0.1.7700";
        rig.Bar.Changed();
        Assert.Equal(AppUpdateViewModel.Ready("0.1.7700"), rig.Bar.Message);

        rig.Ready = null;
        rig.Bar.Changed();
        Assert.Contains("Restart the host in Settings", rig.Bar.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void The_skew_is_said_once_to_a_screen_reader_not_with_every_look()
    {
        var rig = new Rig();
        rig.Bar.Changed();
        Assert.Equal(1, rig.Said);

        for (var i = 0; i < 20; i++) { rig.Bar.Changed(); }
        Assert.Equal(1, rig.Said);

        // The host restarted onto the app's version: the note goes, and it says nothing new.
        rig.Host = rig.App;
        rig.Bar.Changed();
        Assert.False(rig.Bar.Shown);
        Assert.Equal(1, rig.Said);
    }

    [Fact]
    public void A_skew_alone_never_restarts_anything_however_often_it_is_looked_at()
    {
        var rig = new Rig();

        for (var i = 0; i < 50; i++) { rig.Bar.Changed(); _ = rig.Bar.Message; _ = rig.Bar.Shown; }

        Assert.Equal(0, rig.Restarts);
    }

    [Fact]
    public async Task Only_the_persons_press_restarts_and_it_is_the_one_restart_a_ready_release_has()
    {
        var rig = new Rig { Ready = "0.1.7700" };
        for (var i = 0; i < 10; i++) { rig.Bar.Changed(); }
        Assert.Equal(0, rig.Restarts);

        Assert.Null(await rig.Bar.RestartAsync(null));

        Assert.Equal(1, rig.Restarts);
    }

    [Fact]
    public void A_banner_made_without_versions_never_shows_a_skew()
    {
        var bar = new AppUpdateViewModel(() => null, _ => Task.FromResult<string?>(null));

        Assert.False(bar.Shown);
    }
}
