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
        var id = order.Groups.Create("Work");
        order.Groups.SetGroup(K("a"), id);
        var again = new SessionOrder(FilePath);
        Assert.Equal(id, again.Groups.GroupOf(K("a")));
        again.Groups.SetGroup(K("a"), null);
        Assert.Null(new SessionOrder(FilePath).Groups.GroupOf(K("a")));
        Assert.Throws<ArgumentOutOfRangeException>(() => again.Groups.SetGroup(K("a"), "missing"));
    }

    [Fact]
    public void Deleting_a_group_returns_every_member_in_place_without_deleting_sessions()
    {
        var order = new SessionOrder(FilePath);
        OrderEntry[] rows = [E("a"), E("b"), E("c")];
        var before = order.Arrange(rows).Sessions.Select(e => e.Key).ToArray();
        var id = order.Groups.Create("Work");
        order.Groups.SetGroup(K("a"), id);
        order.Groups.SetGroup(K("c"), id);
        order.Groups.Delete(id);
        var again = new SessionOrder(FilePath);
        Assert.Empty(again.Groups.Items);
        Assert.All(rows, e => Assert.Null(again.Groups.GroupOf(e.Key)));
        Assert.Equal(before, again.Arrange(rows).Sessions.Select(e => e.Key));
    }

    [Fact]
    public void Pins_keep_membership_and_keyboard_and_drag_moves_cannot_leave_the_group()
    {
        var order = new SessionOrder(null);
        OrderEntry[] rows = [E("a"), E("b"), E("c")];
        order.Arrange(rows);
        var id = order.Groups.Create("Work");
        order.Groups.SetGroup(K("a"), id);
        order.Groups.SetGroup(K("c"), id);
        Assert.True(order.Move(K("a"), 1));
        Assert.Equal(["b", "c", "a"], order.Arrange(rows).Sessions.Select(e => e.Session.Id));
        Assert.False(order.Move(K("a"), 1));
        Assert.False(order.Place(K("a"), K("b"), false));
        order.SetPinned(K("a"), true);
        order.Arrange(rows);
        order.SetPinned(K("a"), false);
        order.Arrange(rows);
        Assert.Equal(id, order.Groups.GroupOf(K("a")));
        Assert.False(order.Move(K("a"), -1));
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

    [Fact]
    public void Incomplete_snapshots_do_not_expire_members_that_have_not_arrived_yet()
    {
        var clock = new FakeTimeProvider(Make.T0);
        var order = new SessionOrder(null, clock);
        var id = order.Groups.Create("Work");
        order.Groups.SetGroup(K("a"), id);
        order.Groups.SetGroup(K("b"), id);
        clock.Advance(TimeSpan.FromDays(31));
        order.Arrange([E("a")], settled: _ => false);
        order.Arrange([E("a"), E("b")]);
        Assert.Equal(id, order.Groups.GroupOf(K("b")));
    }

    [Fact]
    public void Retention_uses_the_true_last_listing_even_within_the_daily_save_window()
    {
        var clock = new FakeTimeProvider(Make.T0);
        var order = new SessionOrder(null, clock);
        var id = order.Groups.Create("Work");
        order.Groups.SetGroup(K("a"), id);
        clock.Advance(TimeSpan.FromHours(23));
        order.Arrange([E("a")]);
        clock.Advance(TimeSpan.FromDays(30) - TimeSpan.FromHours(23) + TimeSpan.FromTicks(1));
        order.Arrange([]);
        Assert.Equal(id, order.Groups.GroupOf(K("a")));
    }

    [Fact]
    public void Listing_stamps_save_at_most_once_per_day_rather_than_on_every_arrangement()
    {
        var clock = new FakeTimeProvider(Make.T0);
        var order = new SessionOrder(FilePath, clock);
        var id = order.Groups.Create("Work");
        order.Arrange([E("a")]);
        order.Groups.SetGroup(K("a"), id);
        var before = System.IO.File.ReadAllText(FilePath);
        order.MarkSeen(K("a"), "reply");
        clock.Advance(TimeSpan.FromHours(1));
        order.Arrange([E("a")]);
        Assert.Equal(before, System.IO.File.ReadAllText(FilePath));
        Assert.True(new SessionOrder(FilePath).Unread(K("a"), "reply"));
        clock.Advance(TimeSpan.FromDays(1));
        order.Arrange([E("a")]);
        Assert.NotEqual(before, System.IO.File.ReadAllText(FilePath));
        Assert.False(new SessionOrder(FilePath).Unread(K("a"), "reply"));
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
