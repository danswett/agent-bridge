using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// A stored turn arriving (a reply, #1598) leaves the conversation where the person was (#1635):
// at the end it stays at the end, scrolled back it keeps its offset. Before the fix every arrival
// replaced the History rows, the pane collapsed for a moment and the scroller clamped to the top.
static partial class SelfTest
{
    static readonly DateTimeOffset StoredFrom = new(2026, 11, 9, 20, 0, 0, TimeSpan.Zero);

    static HistoryTurn StoredTurn(int n) => new()
    {
        Id = $"scroll{n}",
        At = StoredFrom.AddMinutes(n),
        Prompt = new HistoryPrompt { Text = $"Scroll prompt {n}" },
        Parts = [new HistoryPart { Id = $"scrollp{n}", Kind = HistoryPartKind.Response, At = StoredFrom.AddMinutes(n), Text = $"Reply {n}.\n\nA second paragraph so the turn is a few lines tall." }],
    };

    // Selects another session without waiting for any part of it.
    static void Away(MainWindow window, MainViewModel main)
    {
        if (main.AllCards.FirstOrDefault(c => c.Id == "demo-choice") is { } other) { window.Sessions.Rows.SelectedItem = other; }
    }

    static async Task CheckHistoryScroll(MainWindow window, MainViewModel main, Action<bool, string> check)
    {
        window.ShowPage("sessions");
        Resize(window, 1000, 700);
        var view = await Choose(window, main, "demo-approval", "Response");
        var stored = view?.Card?.Stored;
        if (view is null || stored is null) { check(false, $"has a session whose stored turns can be drawn to check the scroll position ({(view is null ? "no view" : "no history")})"); return; }
        var next = 1;
        for (; next <= 40; next++) { stored.AddLive(StoredTurn(next)); }

        // Opened again from another session: at the end, at the latest reply (#1646), as the first draw
        // and as a reply arrives while it opens.
        Away(window, main);
        view = await Choose(window, main, "demo-approval", "Response");
        var openScroll = view is null ? null : ConversationScroller(view);
        await Task.Delay(1200);
        check(openScroll is { ScrollableHeight: > 0 } o && o.VerticalOffset >= o.ScrollableHeight - 1.5,
            $"opens a long session at its end, on the latest reply ({openScroll?.VerticalOffset:0} of {openScroll?.ScrollableHeight:0} px)");

        Away(window, main);
        view = await Choose(window, main, "demo-approval", "Response");
        stored.AddLive(StoredTurn(next++));
        await Task.Delay(1500);
        openScroll = view is null ? null : ConversationScroller(view);
        check(openScroll is { ScrollableHeight: > 0 } a && a.VerticalOffset >= a.ScrollableHeight - 1.5,
            $"opens a session at its end when a reply arrives as it opens ({openScroll?.VerticalOffset:0} of {openScroll?.ScrollableHeight:0} px)");

        var rows = view is null ? null : ById<ItemsRepeater>(view, "History");
        int Drawn() => rows?.ItemsSourceView?.Count ?? 0;
        var scroll = view is null ? null : ConversationScroller(view);
        if (view is null || rows is null || scroll is null || !await Until(() => Drawn() >= 40 && scroll.ScrollableHeight > 2 * scroll.ViewportHeight, TimeSpan.FromSeconds(10)))
        {
            check(false, $"draws 40 stored turns in a conversation taller than two views ({Drawn()} drawn, {scroll?.ScrollableHeight:0} of {scroll?.ViewportHeight:0} px)");
            return;
        }
        await Task.Delay(700);

        async Task<List<string>> Replies(Func<string?> drifted)
        {
            var problems = new List<string>();
            for (var i = 1; i <= 3; i++)
            {
                var want = Drawn() + 1;
                stored.AddLive(StoredTurn(next++));
                await Until(() => Drawn() >= want, TimeSpan.FromSeconds(5));
                await Task.Delay(700);
                if (drifted() is { } how) { problems.Add($"{how} after reply {i}"); }
            }
            return problems;
        }

        scroll.ChangeView(null, scroll.ScrollableHeight / 2, null, disableAnimation: true);
        view.ReleasePin();
        await Task.Delay(700);
        var kept = scroll.VerticalOffset;
        var moved = await Replies(() => Math.Abs(scroll.VerticalOffset - kept) > 1 ? $"{scroll.VerticalOffset:0} px, not {kept:0}" : null);
        check(moved.Count == 0, $"keeps the scroll position of a conversation scrolled back when stored turns arrive ({(moved.Count == 0 ? $"{kept:0} px" : string.Join("; ", moved))})");
        var cue = ById<Microsoft.UI.Xaml.Controls.Button>(view, "NewMessages");
        check(cue is { Visibility: Microsoft.UI.Xaml.Visibility.Visible, Content: "3 new messages" },
            $"shows a New messages cue with the count when stored turns arrive under a scrolled-back conversation ({cue?.Visibility}, {cue?.Content})");

        scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
        await Task.Delay(700);
        var left = await Replies(() => scroll.VerticalOffset < scroll.ScrollableHeight - 1.5 ? $"{scroll.VerticalOffset:0} of {scroll.ScrollableHeight:0} px" : null);
        check(left.Count == 0, $"keeps a conversation at its end pinned there when stored turns arrive ({(left.Count == 0 ? "at the end" : string.Join("; ", left))})");
        check(cue is { Visibility: Microsoft.UI.Xaml.Visibility.Collapsed }, $"drops the New messages cue once the conversation is back at its end ({cue?.Visibility})");
    }
}
