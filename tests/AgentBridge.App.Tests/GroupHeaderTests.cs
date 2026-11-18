using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Transport;
using Xunit;

public sealed class GroupHeaderTests : IAsyncLifetime
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "abgh-" + Guid.NewGuid().ToString("N"));
    readonly HostConnection connection = new(_ => Task.FromException<IClientChannel>(new InvalidOperationException("No host connections")), InlineDispatcher.Instance);
    MainViewModel main = null!;
    string group = "";
    string FilePath => Path.Combine(folder, "order.json");
    SessionSection Work => main.Sections.Single(s => s.Id == group);
    SessionCardViewModel Card(string id) => main.AllCards.Single(c => c.Id == id);

    public Task InitializeAsync()
    {
        main = new(connection, new CardContext { Deliveries = new(connection, InlineDispatcher.Instance), Folds = new(null), Capabilities = () => [] },
            new UsageViewModel(), order: new SessionOrder(FilePath));
        foreach (var id in new[] { "a", "b", "c", "pin", "loose" })
        { connection.Sessions.Upsert(Make.Session(id, question: id is "a" or "b" ? Make.Choice(id) : null, title: id == "c" ? "Quiet" : id)); }
        main.SetPinned(Card("pin"), true);
        group = main.CreateGroup("Work");
        foreach (var id in new[] { "a", "b", "c", "pin" }) { main.MoveToGroup(Card(id), group); }
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await connection.DisposeAsync();
        if (Directory.Exists(folder)) { Directory.Delete(folder, recursive: true); }
    }

    [Fact]
    public void Headers_and_members_follow_pins_then_groups_then_ungrouped_rows()
    {
        Assert.Equal(["Pinned", "Work", "Sessions"], main.Sections.Select(s => s.Name));
        Assert.Equal(["a", "b", "c"], Work.Rows.Select(c => c.Id));
        Assert.Equal("Work \u00b7 3", Work.Header);
        Assert.True(Work.Expanded);
        Assert.Equal(group, main.Order.Groups.GroupOf(main.KeyOf(Card("pin"))));
        Assert.Equal(["pin"], main.Sections[0].Rows.Select(c => c.Id));
    }

    [Fact]
    public void Collapse_keeps_questions_live_without_closing_the_selected_conversation()
    {
        main.Selected = Card("c");
        Work.Expanded = false;
        Assert.Equal(["a", "b"], Work.Rows.Select(c => c.Id));
        Assert.Equal("2 need you", Work.NeedsText);
        Assert.Equal(2, main.Waiting);
        Assert.Same(Card("c"), main.Selected);
        connection.Sessions.Upsert(Make.Session("b"));
        Assert.Equal(["a"], Work.Rows.Select(c => c.Id));
        Assert.Equal("1 needs you", Work.NeedsText);
        Assert.Equal(1, main.Waiting);
    }

    [Fact]
    public void Collapse_survives_restart_without_changing_roaming_registers()
    {
        using var before = JsonDocument.Parse(File.ReadAllText(FilePath));
        Work.Expanded = false;
        using var after = JsonDocument.Parse(File.ReadAllText(FilePath));
        foreach (var key in new[] { "groups", "membership" })
        { Assert.Equal(before.RootElement.GetProperty(key).GetRawText(), after.RootElement.GetProperty(key).GetRawText()); }
        Assert.True(new SessionOrder(FilePath).IsGroupCollapsed(group));
        Assert.False(new SessionOrder(null).IsGroupCollapsed(group));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Matching_filters_force_open_without_overwriting_saved_collapse(bool search)
    {
        Work.Expanded = false;
        if (search) { main.ListQuery = "Quiet"; } else { main.NeedsOnly = true; }
        Assert.True(Work.Expanded);
        Assert.Equal(search ? ["c"] : ["a", "b"], Work.Rows.Select(c => c.Id));
        if (!search)
        {
            connection.Sessions.Upsert(Make.Session("a"));
            connection.Sessions.Upsert(Make.Session("b"));
            Assert.Equal(["a", "b"], Work.Rows.Select(c => c.Id));
            Assert.True(main.HasNeedsYou);
            Assert.Empty(main.NeedsYou);
        }
        Work.Expanded = false;
        Assert.True(Work.Expanded);
        Assert.True(new SessionOrder(FilePath).IsGroupCollapsed(group));
        main.ListQuery = "";
        main.NeedsOnly = false;
        Assert.False(Work.Expanded);
    }

    [Fact]
    public void Empty_groups_show_the_move_hint_but_not_when_no_rows_match_a_filter()
    {
        var empty = main.CreateGroup("Empty");
        var section = main.Sections.Single(s => s.Id == empty);
        Assert.Equal("Empty \u00b7 0", section.Header);
        Assert.Equal("Nothing here yet. Open a session's menu and choose Move to group.", section.EmptyNote);
        main.ListQuery = "does not match";
        Assert.Empty(main.Sections);
    }
}
