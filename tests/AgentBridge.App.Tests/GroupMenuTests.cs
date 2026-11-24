using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// #1618 G5b: a row's menu moves its session into a group, makes a new group for it, or takes it out (ui.md Groups).
public sealed class GroupMenuTests
{
    readonly FakeTimeProvider clock = new(Make.T0);
    readonly LockedDispatcher ui = new();

    HostConnection Host() => new(_ => Task.FromException<AgentBridge.Local.LocalChannel>(new InvalidOperationException("Offline test")), ui);

    MainViewModel Show(HostConnection local, params string[] ids)
    {
        var machine = new Machine(Machine.LocalKey, local, new CardContext { Host = local, Deliveries = new Deliveries(local, ui), Folds = new FoldStore(null), Capabilities = () => local.Capabilities });
        var main = new MainViewModel(machine, new UsageViewModel(clock), order: new SessionOrder(null, clock));
        local.Sessions.BeginSnapshot();
        foreach (var id in ids) { local.Sessions.Upsert(Make.Session(id)); }
        local.Sessions.EndSnapshot();
        return main;
    }

    static SessionCardViewModel Card(MainViewModel main, string id) => main.AllCards.Single(c => c.Id == id);

    [Fact]
    public async Task Move_to_group_lists_every_group_with_its_count_and_the_current_one_marked_and_moves_the_row()
    {
        await using var local = Host();
        var main = Show(local, "a", "b", "c");
        var work = main.CreateGroup("Work");
        var home = main.CreateGroup("Home");
        main.MoveToGroup(Card(main, "a"), work);
        main.MoveToGroup(Card(main, "b"), work);
        Assert.Equal([("Work", 2, true), ("Home", 0, false)], Card(main, "a").Row!.GroupChoices.Select(g => (g.Name, g.Count, g.Current)));
        Card(main, "a").Row!.MoveToGroup(home);
        Assert.Equal((1, 1), (main.GroupCounts[work], main.GroupCounts[home]));
        Assert.Equal([("Work", 1, false), ("Home", 1, true)], Card(main, "a").Row!.GroupChoices.Select(g => (g.Name, g.Count, g.Current)));
        Assert.Equal(home, main.Order.Groups.GroupOf(main.KeyOf(Card(main, "a"))));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("work")]
    public async Task A_new_group_with_an_empty_or_duplicate_name_is_refused_with_a_message_and_changes_nothing(string name)
    {
        await using var local = Host();
        var main = Show(local, "a");
        main.CreateGroup("Work");
        var row = Card(main, "a").Row!;
        Assert.False(string.IsNullOrEmpty(row.NewGroupProblem(name)));
        Assert.Throws<ArgumentException>(() => row.NewGroup(name));
        Assert.Equal("Work", Assert.Single(main.Groups).Name);
        Assert.False(row.InGroup);
    }

    [Fact]
    public async Task The_first_group_made_from_the_menu_takes_the_session_with_it()
    {
        await using var local = Host();
        var main = Show(local, "a", "b");
        var row = Card(main, "b").Row!;
        Assert.Empty(row.GroupChoices);
        Assert.Null(row.NewGroupProblem(" Ideas "));
        var id = row.NewGroup(" Ideas ");
        Assert.Equal("Ideas", Assert.Single(main.Groups).Name);
        Assert.Equal(id, main.Order.Groups.GroupOf(main.KeyOf(Card(main, "b"))));
        Assert.Equal(1, main.GroupCounts[id]);
        Assert.True(row.InGroup);
    }

    [Fact]
    public async Task Remove_from_group_is_offered_only_for_a_grouped_row_and_pin_keeps_the_group()
    {
        await using var local = Host();
        var main = Show(local, "a", "b");
        var work = main.CreateGroup("Work");
        Assert.False(Card(main, "a").Row!.CanRemoveFromGroup);
        Card(main, "a").Row!.MoveToGroup(work);
        Assert.True(Card(main, "a").Row!.CanRemoveFromGroup);
        Card(main, "a").Row!.TogglePin();
        Assert.True(Card(main, "a").Row!.CanRemoveFromGroup);
        Assert.Equal(work, main.Order.Groups.GroupOf(main.KeyOf(Card(main, "a"))));
        Card(main, "a").Row!.RemoveFromGroup();
        Assert.False(Card(main, "a").Row!.CanRemoveFromGroup);
        Assert.Equal(0, main.GroupCounts[work]);
    }

    [Fact]
    public async Task The_32_group_limit_is_said_before_a_name_is_taken()
    {
        await using var local = Host();
        var main = Show(local, "a");
        for (var i = 0; i < 32; i++) { main.CreateGroup($"G{i}"); }
        Assert.Contains("32", Card(main, "a").Row!.NewGroupProblem("One more"));
    }
}
