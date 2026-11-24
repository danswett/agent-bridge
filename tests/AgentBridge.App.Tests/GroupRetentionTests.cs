using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// G4b (#1617): membership lasts 30 days past the last listing of its session; listing times are
// kept exactly in memory but saved in batches.
public sealed class GroupRetentionTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "abg-" + Guid.NewGuid().ToString("N"));
    string FilePath => Path.Combine(folder, "order.json");
    static OrderEntry E(string id) => new("local", Make.Session(id));
    static string K(string id) => E(id).Key;
    public void Dispose() { if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); } }
    [Fact]
    public void Membership_expires_only_after_30_days_unlisted_even_after_order_pruning()
    {
        var clock = new FakeTimeProvider(Make.T0);
        var order = new SessionOrder(FilePath, clock);
        var id = order.Groups.Create("Work");
        order.Arrange([E("a")]);
        order.Groups.SetGroup(K("a"), id);
        order.Arrange(Enumerable.Range(0, 301).Select(i => E($"row-{i}")).ToArray());
        clock.Advance(TimeSpan.FromDays(30));
        order.Arrange([]);
        Assert.Equal(id, order.Groups.GroupOf(K("a")));
        clock.Advance(TimeSpan.FromMilliseconds(1));
        order = new SessionOrder(FilePath, clock);
        order.Arrange([]);
        Assert.Null(new SessionOrder(FilePath, clock).Groups.GroupOf(K("a")));
        Assert.Single(order.Groups.Items);
    }

    [Fact]
    public void Live_members_keep_their_group_and_refresh_retention_after_30_days()
    {
        var clock = new FakeTimeProvider(Make.T0);
        var order = new SessionOrder(FilePath, clock);
        var id = order.Groups.Create("Work");
        order.Groups.SetGroup(K("a"), id);
        clock.Advance(TimeSpan.FromDays(31));
        order.Arrange([E("a")]);
        Assert.Equal(id, order.Groups.GroupOf(K("a")));
        clock.Advance(TimeSpan.FromDays(29));
        order = new SessionOrder(FilePath, clock);
        order.Arrange([]);
        Assert.Equal(id, order.Groups.GroupOf(K("a")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Listing_stamps_throttle_saves_but_keep_exact_retention_in_memory_and_after_flush(bool reload)
    {
        var clock = new FakeTimeProvider(Make.T0);
        var order = new SessionOrder(FilePath, clock);
        var id = order.Groups.Create("Work");
        order.Arrange([E("a")]);
        order.Groups.SetGroup(K("a"), id);
        var before = System.IO.File.ReadAllText(FilePath);
        order.MarkSeen(K("a"), "reply");
        clock.Advance(TimeSpan.FromHours(23));
        order.Arrange([E("a")]);
        Assert.Equal(before, System.IO.File.ReadAllText(FilePath));
        Assert.True(new SessionOrder(FilePath).Unread(K("a"), "reply"));
        clock.Advance(TimeSpan.FromHours(2));
        order.Arrange([E("a")]);
        Assert.NotEqual(before, System.IO.File.ReadAllText(FilePath));
        Assert.False(new SessionOrder(FilePath).Unread(K("a"), "reply"));
        before = System.IO.File.ReadAllText(FilePath);
        clock.Advance(TimeSpan.FromHours(23));
        order.Arrange([E("a")]);
        Assert.Equal(before, System.IO.File.ReadAllText(FilePath));
        if (reload) { order.Flush(); order = new SessionOrder(FilePath, clock); }
        clock.Advance(TimeSpan.FromDays(29) + TimeSpan.FromHours(2));
        order.Arrange([]);
        Assert.Equal(id, order.Groups.GroupOf(K("a")));
        clock.Advance(TimeSpan.FromHours(22) + TimeSpan.FromMilliseconds(1));
        order.Arrange([]);
        Assert.Null(order.Groups.GroupOf(K("a")));
    }

    [Fact]
    public async Task Filtered_machines_refresh_membership_without_affecting_visible_counts()
    {
        var clock = new FakeTimeProvider(Make.T0);
        var ui = new LockedDispatcher();
        await using var local = new HostConnection(_ => Task.FromException<AgentBridge.Local.LocalChannel>(new InvalidOperationException("Offline test")), ui);
        await using var remote = new HostConnection(_ => Task.FromException<AgentBridge.Local.LocalChannel>(new InvalidOperationException("Offline test")), ui);
        Machine M(string key, HostConnection host) => new(key, host, new CardContext { Host = host, Deliveries = new Deliveries(host, ui), Folds = new FoldStore(null), Capabilities = () => host.Capabilities });
        var main = new MainViewModel(M(Machine.LocalKey, local), new UsageViewModel(clock), order: new SessionOrder(null, clock));
        main.Add(M("remote", remote));
        local.Sessions.BeginSnapshot();
        remote.Sessions.BeginSnapshot();
        local.Sessions.Upsert(Make.Session("a"));
        remote.Sessions.Upsert(Make.Session("b"));
        local.Sessions.EndSnapshot();
        remote.Sessions.EndSnapshot();
        var id = main.CreateGroup("Work");
        var key = SessionOrder.KeyOf("remote", "b");
        var card = main.AllCards.Single(c => c.Id == "b");
        card.Editing = true;
        main.MoveToGroup(card, id);
        Assert.Equal(1, main.GroupCounts[id]);
        Assert.False(main.CanMove(card, -1));
        main.RenameGroup(id, "Renamed");
        Assert.Equal("Renamed", Assert.Single(main.Groups).Name);
        main.SelectedMachine = main.Machines.Single(m => m.Key == Machine.LocalKey);
        clock.Advance(TimeSpan.FromDays(31));
        remote.Sessions.Upsert(Make.Session("b"));
        Assert.Equal(id, main.Order.Groups.GroupOf(key));
        Assert.Equal(0, main.GroupCounts[id]);
        Assert.Equal("a", Assert.Single(main.Sessions).Id);
        main.SelectedMachine = main.Machines[0];
        main.RemoveFromGroup(card);
        Assert.Equal(["b", "a"], main.Sessions.Select(c => c.Id));
        Assert.False(main.Move(card, -1));
        main.DeleteGroup(id);
        Assert.Empty(main.Groups);
    }

}
