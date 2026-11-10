using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

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
        await CheckHistoryBottom(window, main, check);
    }

    static async Task CheckHistoryBottom(MainWindow window, MainViewModel main, Action<bool, string> check)
    {
        var before = window.Sessions.Rows.SelectedItem;
        // A separate session keeps these 300 turns out of the existing burst/retention checks.
        var view = await Choose(window, main, "demo-choice", "Conversation");
        var stored = view?.Card?.Stored;
        var scroll = view is null ? null : ConversationScroller(view);
        if (view?.Card is not { } card || stored is null || scroll is null)
        {
            check(false, "has an isolated session for the bottom-settle check");
            window.Sessions.Rows.SelectedItem = before;
            return;
        }
        var session = card.Session;
        var selected = window.Sessions.Rows.SelectedItem;
        try
        {
            card.Update(session with
            {
                Activity = session.Activity with
                {
                    Response = string.Join("\n\n", Enumerable.Range(1, 80).Select(i => $"Bottom-settle paragraph {i}. A [reading link](https://example.test/) in a growing transcript must stay at its latest reply after scrolling to the end.")),
                },
            });
            for (var i = 2000; i < 2300; i++) { stored.AddLive(StoredTurn(i)); }
            await Task.Delay(1200);
            var next = 2300;
            var list = ById<ListView>(view, "Conversation")!;
            foreach (var keyed in new[] { false, true })
            {
                view.ReleasePin();
                if (keyed)
                {
                    list.ScrollIntoView(list.Items[20], ScrollIntoViewAlignment.Leading);
                    await Still(scroll);
                    var stop = list.ContainerFromIndex(20) is { } container ? FocusManager.FindFirstFocusableElement(container) : null;
                    check(stop is not null && (await FocusManager.TryFocusAsync(stop, FocusState.Keyboard)).Succeeded, "focuses a reading link before pressing End");
                }
                else { scroll.ChangeView(null, scroll.ScrollableHeight / 2, null, disableAnimation: true); }
                await Task.Delay(700);
                stored.AddLive(StoredTurn(next++));
                await Task.Delay(700);

                var name = keyed ? "End key" : "scroll bar";
                var cue = ById<Button>(view, "NewMessages");
                check(cue is { Visibility: Visibility.Visible }, $"has new stored history below the reader before reaching the bottom by {name}");
                var reached = false;
                var departures = new List<string>();
                var changes = new List<long>();
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var previous = scroll.VerticalOffset;
                var away = false;
                void Record(object? sender, object args)
                {
                    var offset = scroll.VerticalOffset;
                    var gap = scroll.ScrollableHeight - offset;
                    AppLog.Write($"self-test: bottom {name} layout {watch.ElapsedMilliseconds} ms: offset {offset:0.##} of {scroll.ScrollableHeight:0.##}, cue {cue?.Visibility}");
                    if (gap <= 1.5) { reached = true; away = false; }
                    else if (reached && !away) { departures.Add($"{watch.ElapsedMilliseconds} ms: gap {gap:0.##}"); away = true; }
                    if (reached && Math.Abs(offset - previous) > 0.5) { changes.Add(watch.ElapsedMilliseconds); }
                    previous = offset;
                }
                list.LayoutUpdated += Record;
                try
                {
                    if (keyed) { AppLog.Write($"self-test: bottom End pressed by {Press(window.Handle, VirtualKey.End, foreground: false)}"); }
                    else { ((IScrollProvider)new ScrollViewerAutomationPeer(scroll).GetPattern(PatternInterface.Scroll)).SetScrollPercent(-1, 100); }
                    await Until(() => reached, TimeSpan.FromSeconds(5));
                    for (var step = 0; step < 30; step++)
                    {
                        await Task.Delay(100);
                        Record(null, EventArgs.Empty);
                    }
                    var most = changes.Count == 0 ? 0 : changes.Max(at => changes.Count(other => other >= at && other < at + 500));
                    check(reached && departures.Count == 0 && most <= 2 && cue is { Visibility: Visibility.Collapsed },
                        $"a growing history reaches the bottom by {name} and stays after the cue disappears (reached {reached}; departures {string.Join("; ", departures)}; most changes in 500 ms {most})");

                    departures.Clear();
                    var growth = new List<string>();
                    for (var i = 0; i < 20; i++)
                    {
                        stored.AddLive(StoredTurn(next++));
                        await Task.Delay(100);
                        Record(null, EventArgs.Empty);
                        if (scroll.ScrollableHeight - scroll.VerticalOffset > 1.5) { growth.Add($"{i}: gap {scroll.ScrollableHeight - scroll.VerticalOffset:0.##}"); }
                    }
                    check(growth.Count == 0 && departures.Count == 0,
                        $"stays at the bottom through 20 stored events arriving every 100 ms after {name} ({string.Join("; ", growth)}; departures {string.Join("; ", departures)})");
                }
                finally { list.LayoutUpdated -= Record; }
            }
            check(ReferenceEquals(selected, window.Sessions.Rows.SelectedItem), "keeps the selected session while scrolling a growing history to the bottom");
        }
        finally { card.Update(session); window.Sessions.Rows.SelectedItem = before; }
    }
}
