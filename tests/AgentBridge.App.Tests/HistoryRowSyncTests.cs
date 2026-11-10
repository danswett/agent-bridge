using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Xunit;

// The rows on screen change in place, never as a new list (#1635): a new list collapses the History pane
// and the conversation jumps to the top.
public sealed class HistoryRowSyncTests
{
    static readonly DateTimeOffset T0 = new(2026, 11, 9, 8, 31, 0, TimeSpan.Zero);

    static HistoryRow[] Rows(params (int n, string text)[] turns) => HistoryDraw.Rows(
        [.. turns.Select(t => new HistoryTurn
        {
            Id = $"t{t.n}",
            At = T0.AddMinutes(t.n),
            Prompt = new HistoryPrompt { Text = $"prompt {t.n}" },
            Parts = [new HistoryPart { Id = $"p{t.n}", Kind = HistoryPartKind.Response, At = T0.AddMinutes(t.n), Text = t.text }],
        })], null).ToArray();

    static (ObservableCollection<HistoryRow> shown, List<NotifyCollectionChangedEventArgs> changes) Shown(HistoryRow[] rows)
    {
        var shown = new ObservableCollection<HistoryRow>(rows);
        var changes = new List<NotifyCollectionChangedEventArgs>();
        shown.CollectionChanged += (_, e) => changes.Add(e);
        return (shown, changes);
    }

    [Fact]
    public void A_new_reply_is_appended_and_the_rows_before_it_are_not_touched()
    {
        var (shown, changes) = Shown(Rows((1, "one"), (2, "two")));

        HistoryRowSync.Apply(shown, Rows((1, "one"), (2, "two"), (3, "three")));

        Assert.Equal(["t1", "t2", "t3"], shown.Select(r => r.Id));
        var add = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Add, add.Action);
        Assert.Equal(2, add.NewStartingIndex);
    }

    [Fact]
    public void An_older_page_is_inserted_above()
    {
        var (shown, changes) = Shown(Rows((3, "three"), (4, "four")));

        HistoryRowSync.Apply(shown, Rows((1, "one"), (2, "two"), (3, "three"), (4, "four")));

        Assert.Equal(["t1", "t2", "t3", "t4"], shown.Select(r => r.Id));
        Assert.All(changes, c => Assert.Equal(NotifyCollectionChangedAction.Add, c.Action));
    }

    [Fact]
    public void The_same_rows_drawn_again_change_nothing()
    {
        var (shown, changes) = Shown(Rows((1, "one"), (2, "two")));

        HistoryRowSync.Apply(shown, Rows((1, "one"), (2, "two")));

        Assert.Empty(changes);
    }

    [Fact]
    public void A_row_whose_text_changed_is_replaced_and_its_neighbours_are_not()
    {
        var (shown, changes) = Shown(Rows((1, "one"), (2, "two"), (3, "three")));

        HistoryRowSync.Apply(shown, Rows((1, "one"), (2, "two, edited"), (3, "three")));

        var replace = Assert.Single(changes);
        Assert.Equal(NotifyCollectionChangedAction.Replace, replace.Action);
        Assert.Equal(1, replace.NewStartingIndex);
    }

    [Fact]
    public void Another_session_replaces_every_row_and_fewer_rows_remove_the_rest()
    {
        var (shown, _) = Shown(Rows((1, "one"), (2, "two"), (3, "three")));

        HistoryRowSync.Apply(shown, Rows((7, "seven")));
        Assert.Equal(["t7"], shown.Select(r => r.Id));

        HistoryRowSync.Apply(shown, []);
        Assert.Empty(shown);
    }
}
