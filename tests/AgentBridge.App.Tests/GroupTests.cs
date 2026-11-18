using System.Text.Json;
using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class GroupTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "abg-" + Guid.NewGuid().ToString("N"));
    string FilePath => Path.Combine(folder, "order.json");
    static OrderEntry E(string id) => new("local", Make.Session(id));
    static string K(string id) => E(id).Key;
    public void Dispose() { if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); } }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("Pinned")]
    [InlineData("sessions")]
    [InlineData("ENDED")]
    [InlineData("12345678901234567890123456789012345678901")]
    public void Invalid_names_are_refused_without_changing_groups(string name)
    {
        var groups = new SessionOrder(null).Groups;
        Assert.Throws<ArgumentException>(() => groups.Create(name));
        Assert.Empty(groups.Items);
    }

    [Fact]
    public void Names_are_trimmed_unique_and_limited_to_32_live_groups()
    {
        var groups = new SessionOrder(null).Groups;
        var id = groups.Create(" Work ");
        Assert.Throws<ArgumentException>(() => groups.Create("work"));
        Assert.Throws<ArgumentException>(() => groups.Rename(id, "Ended"));
        groups.Rename(id, "WORK");
        Assert.Equal("WORK", Assert.Single(groups.Items).Name);
        for (var i = 1; i < 32; i++) { groups.Create($"Group {i}"); }
        Assert.Throws<InvalidOperationException>(() => groups.Create("Extra"));
        Assert.Throws<ArgumentException>(() => groups.Rename(id, "group 1"));
        groups.Delete(id);
        groups.Create("Replacement");
        Assert.Equal(32, groups.Items.Count);
    }

    [Fact]
    public void Membership_and_removal_survive_reloading_the_same_order_file()
    {
        var order = new SessionOrder(FilePath);
        OrderEntry[] rows = [E("a"), E("b"), E("c")];
        order.Arrange(rows);
        var id = order.Groups.Create("Work");
        order.Groups.SetGroup(K("c"), id);
        Assert.Equal(["c", "a", "b"], order.Arrange(rows).Sessions.Select(e => e.Session.Id));
        order.Groups.SetGroup(K("a"), id);
        var again = new SessionOrder(FilePath);
        Assert.Equal(id, again.Groups.GroupOf(K("a")));
        again.Arrange(rows);
        again.Groups.SetGroup(K("c"), id);
        Assert.Equal(["a", "c", "b"], again.Arrange(rows).Sessions.Select(e => e.Session.Id));
        again.Groups.SetGroup(K("c"), null);
        Assert.Equal(["c", "a", "b"], again.Arrange(rows).Sessions.Select(e => e.Session.Id));
        again.Groups.SetGroup(K("a"), null);
        Assert.Null(new SessionOrder(FilePath).Groups.GroupOf(K("a")));
        Assert.Throws<ArgumentOutOfRangeException>(() => again.Groups.SetGroup(K("a"), "missing"));
    }

    [Fact]
    public void Deleting_a_group_returns_every_member_in_place_without_deleting_sessions()
    {
        var order = new SessionOrder(FilePath);
        OrderEntry[] rows = [E("a"), E("b"), E("c")];
        order.Arrange(rows);
        var id = order.Groups.Create("Work");
        order.Groups.SetGroup(K("a"), id);
        order.Groups.SetGroup(K("c"), id);
        var before = order.Arrange(rows).Sessions.Select(e => e.Key).ToArray();
        order.Groups.Delete(id);
        var again = new SessionOrder(FilePath);
        Assert.Empty(again.Groups.Items);
        Assert.All(rows, e => Assert.Null(again.Groups.GroupOf(e.Key)));
        Assert.Equal(before, again.Arrange(rows).Sessions.Select(e => e.Key));
    }

    [Fact]
    public void Pin_boundary_drops_keep_membership_while_unpinned_moves_stay_in_their_group()
    {
        var order = new SessionOrder(null);
        OrderEntry[] rows = [E("a"), E("b"), E("c")];
        order.Arrange(rows);
        var id = order.Groups.Create("Work");
        order.Groups.SetGroup(K("a"), id);
        order.SetPinned(K("a"), true);
        order.Groups.SetGroup(K("c"), id);
        order.Arrange(rows);
        Assert.True(order.Place(K("a"), K("b"), false));
        order.Arrange(rows);
        Assert.Equal(id, order.Groups.GroupOf(K("a")));
        Assert.False(order.Move(K("a"), 1));
        Assert.False(order.Place(K("a"), K("b"), false));
        Assert.True(order.Move(K("a"), -1));
        Assert.Equal(["a", "c", "b"], order.Arrange(rows).Sessions.Select(e => e.Session.Id));
        order.SetPinned(K("a"), true);
        order.SetPinned(K("c"), true);
        order.Groups.SetGroup(K("a"), null);
        Assert.Equal(["a", "c"], order.Arrange(rows).Pinned.Select(e => e.Session.Id));
    }

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
        clock.Advance(TimeSpan.FromTicks(1));
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
        clock.Advance(TimeSpan.FromHours(22) + TimeSpan.FromTicks(1));
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

    [Fact]
    public void Counts_include_only_current_unpinned_live_members()
    {
        var order = new SessionOrder(null);
        var id = order.Groups.Create("Work");
        foreach (var key in new[] { K("a"), K("b"), K("c"), K("absent") }) { order.Groups.SetGroup(key, id); }
        order.SetPinned(K("b"), true);
        order.Ended("local", E("c").Session);
        order.Arrange([E("a"), E("b")]);
        Assert.Equal(1, order.Groups.Counts()[id]);
    }

    [Fact]
    public void Registers_raise_counters_above_loaded_names_and_membership_and_keep_the_device_tag()
    {
        Directory.CreateDirectory(folder);
        System.IO.File.WriteAllText(FilePath, """{"groups":{"g":{"value":"Work","counter":90,"deviceTag":"00000000000000000000000000000001"}},"membership":{"other":{"value":"g","counter":100,"deviceTag":"00000000000000000000000000000002"}}}""");
        var order = new SessionOrder(FilePath);
        order.Groups.Rename("g", "Renamed");
        order = new SessionOrder(FilePath);
        order.Groups.SetGroup(K("a"), "g");
        using var json = JsonDocument.Parse(System.IO.File.ReadAllText(FilePath));
        var name = json.RootElement.GetProperty("groups").GetProperty("g");
        var member = json.RootElement.GetProperty("membership").GetProperty(K("a"));
        Assert.Equal(101, name.GetProperty("counter").GetInt64());
        Assert.Equal(102, member.GetProperty("counter").GetInt64());
        Assert.Equal(name.GetProperty("deviceTag").GetString(), member.GetProperty("deviceTag").GetString());
    }
}
