using AgentBridge.App.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class LiveRosterListTests : IAsyncLifetime
{
    readonly LockedDispatcher ui = new();
    readonly HostConnection local;
    readonly HostConnection remote;
    readonly MainViewModel main;

    public LiveRosterListTests()
    {
        local = Connection();
        remote = Connection();
        main = new MainViewModel(local, Context(local), new UsageViewModel());
        main.Add(new Machine("other-host", remote, Context(remote)));
    }

    HostConnection Connection() => new(
        (Func<CancellationToken, Task<IClientChannel>>)(_ => throw new InvalidOperationException("No HA or network source belongs in this projection test")),
        ui);

    CardContext Context(HostConnection host) => new()
    {
        Deliveries = new Deliveries(host, ui),
        Folds = new FoldStore(null),
        Capabilities = () => [Capabilities.SessionsRead],
        Host = host,
    };

    static void Snapshot(HostConnection host, params string[] ids)
    {
        host.Sessions.BeginSnapshot();
        foreach (var id in ids) { host.Sessions.Upsert(Make.Session(id)); }
        host.Sessions.EndSnapshot();
    }

    [Fact]
    public void The_list_uses_each_hosts_snapshot_without_a_Home_Assistant_client()
    {
        Snapshot(local, "here");
        Snapshot(remote, "there");
        Assert.Equal(["here", "there"], main.AllCards.Select(c => c.Id).Order());
        Assert.Empty(main.Ended);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_ended_session_leaves_every_list_group_immediately(bool replacementSnapshot)
    {
        Snapshot(local, "ended", "live");
        var ended = main.Sessions.Single(c => c.Id == "ended");
        main.SetPinned(ended, true);
        main.Selected = ended;
        if (replacementSnapshot) { Snapshot(local, "live"); }
        else { local.Sessions.Remove("ended"); }
        Assert.Equal("live", Assert.Single(main.Sessions).Id);
        Assert.DoesNotContain(main.AllCards, c => c.Id == "ended");
        Assert.Null(main.Selected);
        Assert.Empty(main.Ended);
        Assert.False(main.HasEnded);
    }

    [Fact]
    public void One_hosts_replacement_snapshot_preserves_a_silent_hosts_stale_rows()
    {
        Snapshot(local, "old");
        Snapshot(remote, "there");
        remote.Sessions.BeginSnapshot();
        Snapshot(local, "new");
        Assert.Equal(["new", "there"], main.AllCards.Select(c => c.Id).Order());
        Assert.False(main.MachineOf("other-host")!.Current);
        Assert.NotNull(main.MachineOf("other-host")!.Away(Make.T0));
        Assert.DoesNotContain(remote.Sessions.All, s => s.Id == "old");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var card in main.AllCards.ToList()) { card.Detach(); }
        await remote.DisposeAsync();
        await local.DisposeAsync();
    }
}
