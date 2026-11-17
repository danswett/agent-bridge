using AgentBridge.App.Core;
using AgentBridge.Host.Core.Updates;
using Xunit;

public sealed class UpdaterHaltPolicyTests
{
    sealed class Updater : IUpdater
    {
        public string? InstalledVersion => "0.1.0";
        public string? Ready { get; set; }
        public HaltPolicySnapshot? HaltPolicy { get; set; }
        public int Downloads;
        public bool Applied;
        public Task<string?> CheckAsync() => Task.FromResult<string?>("0.2.0");
        public Task DownloadAsync(Action<int> progress)
        {
            Downloads++;
            Ready = "0.2.0";
            return Task.CompletedTask;
        }
        public void ApplyAndRestart(string version, Action? handedOver = null) => Applied = true;
    }

    static HaltPolicySnapshot Halted(bool fresh = true) =>
        new(new(1, """{"v":1,"halted":["0.2.0"]}""", ["0.2.0"]), DateTimeOffset.UtcNow, fresh);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_known_halt_is_not_offered_even_when_the_policy_is_stale(bool fresh)
    {
        var inner = new Updater { HaltPolicy = Halted(fresh), Ready = "0.2.0" };
        var observed = new ObservedUpdater(inner);

        Assert.Null(await observed.CheckAsync());
        Assert.Null(observed.Found);
        Assert.Null(observed.Ready);
    }

    [Fact]
    public async Task A_new_halt_hides_a_previously_offered_and_downloaded_release()
    {
        var inner = new Updater { Ready = "0.2.0" };
        var observed = new ObservedUpdater(inner);
        Assert.Equal("0.2.0", await observed.CheckAsync());
        Assert.Equal("0.2.0", observed.Ready);

        inner.HaltPolicy = Halted();

        Assert.Null(observed.Found);
        Assert.Null(observed.Ready);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_halt_after_a_check_refuses_download_without_touching_the_inner_updater(bool ready)
    {
        var inner = new Updater();
        var observed = new ObservedUpdater(inner);
        await observed.CheckAsync();
        inner.Ready = ready ? "0.2.0" : null;
        inner.HaltPolicy = Halted();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => observed.DownloadAsync(_ => { }));

        Assert.Contains("halted", error.Message);
        Assert.Equal(0, inner.Downloads);
        Assert.Null(observed.RetryAt);
    }

    [Fact]
    public void A_halt_after_download_refuses_install_without_applying_the_package()
    {
        var inner = new Updater { Ready = "0.2.0", HaltPolicy = Halted(false) };
        var observed = new ObservedUpdater(inner);

        var error = Assert.Throws<InvalidOperationException>(() => observed.ApplyAndRestart("0.2.0"));

        Assert.Contains("halted", error.Message);
        Assert.False(inner.Applied);
    }

    [Fact]
    public async Task An_unknown_policy_keeps_existing_manual_update_behavior()
    {
        var inner = new Updater();
        var observed = new ObservedUpdater(inner);

        Assert.Equal("0.2.0", await observed.CheckAsync());
        await observed.DownloadAsync(_ => { });
        observed.ApplyAndRestart("0.2.0");

        Assert.Equal(1, inner.Downloads);
        Assert.True(inner.Applied);
    }
}
