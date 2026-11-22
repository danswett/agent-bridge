using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

static partial class SelfTest
{
    public static async Task RunHistoryScrollPreserveAsync(MainWindow window, MainViewModel main, DemoHost demo, string outFile, Action<int> exit)
    {
        var passed = new List<string>();
        var failed = new List<string>();
        void Check(bool ok, string what)
        {
            (ok ? passed : failed).Add(what);
            if (!ok) { AppLog.Write($"self-test: failed: {what}"); }
        }
        try { await CheckHistoryScrollPreserve(window, main, demo, Check); }
        catch (Exception e) { Check(false, $"scroll-preserve crashed: {e}"); }
        await Write(outFile, new Dictionary<string, object?>(), passed, failed, null);
        exit(failed.Count == 0 ? 0 : 1);
    }

    static async Task CheckHistoryScrollPreserve(MainWindow window, MainViewModel main, DemoHost demo, Action<bool, string> check)
    {
        const string id = "demo-history-scroll-preserve";
        const int turns = 300;
        var fixture = new Session
        {
            Id = id, Agent = AgentKind.Copilot, Title = "Preserve the transcript reading position",
            Status = SessionStatus.Idle, StatusSince = StoredFrom, UpdatedAt = StoredFrom,
            Activity = new Activity { UserMessage = "The live exchange", Response = "Initial latest reply.", TurnCount = turns },
        };
        await demo.PutAsync(fixture);
        try
        {
            var received = await Until(() => main.AllCards.Any(c => c.Id == id), TimeSpan.FromSeconds(10));
            check(received, "scroll-preserve setup: receives the dedicated demo session");
            if (!received) { return; }
            window.ShowPage("sessions");
            Resize(window, 1000, 700);
            var view = await Choose(window, main, id, "Response");
            if (view is null || view.Card is not { Stored: { } stored } card || ConversationScroller(view) is not { } scroll
                || ById<ItemsRepeater>(view, "History") is not { } rows
                || ById<ListView>(view, "Conversation") is not { } list)
            {
                check(false, "scroll-preserve setup: opens the conversation, history and scroller");
                return;
            }
            for (var n = 1; n <= turns; n++) { stored.AddLive(StoredTurn(n)); }
            var drawn = await Until(() => stored.State != HistoryLoad.Loading && rows.ItemsSourceView?.Count == turns
                && scroll.ViewportHeight > 0 && scroll.ScrollableHeight > 3 * scroll.ViewportHeight, TimeSpan.FromSeconds(10));
            check(drawn, "scroll-preserve setup: draws all 300 stored turns in a transcript taller than three viewports");
            if (!drawn) { return; }

            double Top(FrameworkElement element) => element.TransformToVisual(scroll).TransformPoint(default).Y;
            HistoryRow[] ReadRows() => rows.ItemsSourceView is { } items
                ? Enumerable.Range(0, items.Count).Select(i => items.GetAt(i)).OfType<HistoryRow>().ToArray() : [];
            List<(string Id, FrameworkElement Element, double Top)> Visible()
            {
                var visible = new List<(string, FrameworkElement, double)>();
                var items = rows.ItemsSourceView;
                for (var i = 0; items is not null && i < items.Count; i++)
                {
                    if (items.GetAt(i) is HistoryRow row && rows.TryGetElement(i) is FrameworkElement element
                        && element.IsLoaded && element.ActualHeight > 0
                        && Top(element) + element.ActualHeight > 0 && Top(element) < scroll.ViewportHeight)
                    { visible.Add((row.Id, element, Top(element))); }
                }
                return visible;
            }
            var frames = 0;
            var monitoring = false;
            var atBottom = false;
            var before = 0.0;
            var minimum = double.MaxValue;
            var drift = 0.0;
            var rowsKept = true;
            var retained = new List<(string Id, FrameworkElement Element, double Top)>();
            var source = list.ItemsSource;
            var sourceKept = true;
            void Offset()
            {
                if (!monitoring) { return; }
                minimum = Math.Min(minimum, scroll.VerticalOffset);
                drift = Math.Max(drift, Math.Abs(scroll.VerticalOffset - before));
                sourceKept &= ReferenceEquals(source, list.ItemsSource);
            }
            void Changed(object? sender, ScrollViewerViewChangedEventArgs args) => Offset();
            void Layout(object? sender, object args) => Offset();
            void Frame(object? sender, object args)
            {
                frames++;
                Offset();
                if (!monitoring || atBottom) { return; }
                var visible = Visible();
                rowsKept &= visible.Count == retained.Count && visible.Zip(retained).All(pair =>
                    pair.First.Id == pair.Second.Id && ReferenceEquals(pair.First.Element, pair.Second.Element)
                    && pair.First.Top == pair.Second.Top);
            }
            async Task<bool> Rendered()
            {
                var first = frames;
                return await Until(() => frames >= first + 2, TimeSpan.FromSeconds(5));
            }
            async Task<bool> ReachMeasuredBottom(double gap = 0)
            {
                var stable = 0;
                void Seek(object? sender, object args)
                {
                    if (Math.Abs(scroll.VerticalOffset - (scroll.ScrollableHeight - gap)) <= 1.5
                        && (gap > 8 || scroll.VerticalOffset >= scroll.ScrollableHeight - 8)) { stable++; }
                    else { stable = 0; view.ChangeConversationView(scroll, scroll.ScrollableHeight - gap); }
                }
                CompositionTarget.Rendering += Seek;
                try { return await Until(() => stable >= 2 && Math.Abs(scroll.VerticalOffset - (scroll.ScrollableHeight - gap)) <= 1.5
                    && (gap > 8 || scroll.VerticalOffset >= scroll.ScrollableHeight - 8), TimeSpan.FromSeconds(5)); }
                finally { CompositionTarget.Rendering -= Seek; }
            }
            scroll.ViewChanged += Changed;
            scroll.LayoutUpdated += Layout;
            CompositionTarget.Rendering += Frame;
            try
            {
                var update = 0;
                foreach (var bottom in new[] { false, true })
                {
                    monitoring = false;
                    atBottom = bottom;
                    view.ReleasePin();
                    var chosen = bottom ? scroll.ScrollableHeight : Math.Round(scroll.ScrollableHeight / 2);
                    view.ChangeConversationView(scroll, chosen);
                    var reached = bottom ? await ReachMeasuredBottom()
                        : await Until(() => Math.Abs(scroll.VerticalOffset - chosen) <= 1.5, TimeSpan.FromSeconds(5));
                    var settled = reached && (bottom || await Rendered());
                    before = scroll.VerticalOffset;
                    retained = Visible();
                    var ready = settled && before > scroll.ViewportHeight
                        && (bottom ? before >= scroll.ScrollableHeight - 1.5 : retained.Count > 0 && before < scroll.ScrollableHeight - scroll.ViewportHeight);
                    check(ready, $"scroll-preserve setup: stable {(bottom ? "bottom" : "middle")} before updates ({before:R} of {scroll.ScrollableHeight:R} px; visible {string.Join(",", retained.Select(r => r.Id))})");
                    if (!ready) { return; }
                    minimum = before;
                    drift = 0;
                    rowsKept = sourceKept = true;
                    monitoring = true;
                    for (var arrival = 1; arrival <= 3; arrival++)
                    {
                        if (bottom && arrival >= 2)
                        {
                            check(await ReachMeasuredBottom(299), $"scroll-preserve driver: retreats 299 px before reply {update + 1}; {view.PinState()}");
                            check(await ReachMeasuredBottom(arrival == 2 ? 4 : 8), $"scroll-preserve driver: returns near the end before reply {update + 1}; {view.PinState()}");
                        }
                        update++;
                        var prior = fixture.Activity!.Response!;
                        var turn = StoredTurn(turns + update);
                        turn = turn with { Parts = [turn.Parts[0] with { Text = prior }] };
                        var response = $"Received wholly different reply {update}.";
                        check(!MarkdownModel.Parse(prior).Any(a => MarkdownModel.Parse(response).Any(b => ConversationBlocks.Same(a, b))),
                            $"scroll-preserve driver: reply {update} shares no markdown block with its predecessor");
                        fixture = fixture with
                        {
                            UpdatedAt = StoredFrom.AddHours(update),
                            Activity = new Activity { UserMessage = "The live exchange", Response = response, TurnCount = turns + update, Notice = turn },
                        };
                        // Deliver the previous reply's history turn and its replacement through one real session update.
                        await demo.PutAsync(fixture);
                        var delivered = await Until(() => card.Response == response && ReadRows().Any(r => r.Id == turn.Id
                            && r.Blocks.Any(b => b.Text == prior)), TimeSpan.FromSeconds(5));
                        check(delivered, $"scroll-preserve driver: receives reply {update} and stores the previous reply in the same update");
                        if (!delivered) { return; }
                        var rendered = await Rendered();
                        check(rendered, $"scroll-preserve driver: renders two frames after reply {update}");
                        if (!rendered) { return; }
                        check(minimum > 0, $"scroll-preserve behavior: {(bottom ? "bottom" : "middle")} reply {update} never visits offset zero in layout, scrolling or rendered frames (minimum {minimum:R})");
                        if (bottom)
                        {
                            check(scroll.VerticalOffset >= scroll.ScrollableHeight - 1.5,
                                $"scroll-preserve behavior: follows replacement reply {update} at the bottom ({scroll.VerticalOffset:R} of {scroll.ScrollableHeight:R}; {view.PinState()})");
                        }
                        else
                        {
                            check(drift == 0, $"scroll-preserve behavior: reply {update} preserves the exact middle offset {before:R} (now {scroll.VerticalOffset:R}, maximum drift {drift:R})");
                            check(rowsKept, $"scroll-preserve behavior: reply {update} keeps the same first/last visible row ids, elements and exact positions");
                        }
                        check(sourceKept, $"scroll-preserve behavior: reply {update} keeps the bound outer list and history header");
                    }
                    monitoring = false;
                    if (bottom) { continue; }

                    var originalRows = ReadRows();
                    var mark = StoredTurn(1);
                    mark = mark with { Prompt = mark.Prompt! with { From = UserMessageFrom.Agent } };
                    fixture = fixture with
                    {
                        UpdatedAt = fixture.UpdatedAt.AddSeconds(1),
                        Activity = fixture.Activity! with { Notice = mark },
                    };
                    await demo.PutAsync(fixture);
                    var marked = await Until(() => ReadRows().FirstOrDefault(r => r.Id == mark.Id)?.Disclosure is not null, TimeSpan.FromSeconds(5));
                    check(marked && await Rendered(), "scroll-preserve driver: adds a late Sent by an agent mark and collapsed prompt above the viewport");
                    var afterMark = Visible();
                    check(afterMark.Count == retained.Count && afterMark.Zip(retained).All(pair =>
                        pair.First.Id == pair.Second.Id && ReferenceEquals(pair.First.Element, pair.Second.Element)
                        && pair.First.Top == pair.Second.Top),
                        "scroll-preserve behavior: a late mark above the viewport keeps the visible neighbours at their exact reading positions");
                    var currentRows = ReadRows();
                    check(originalRows.Where(r => r.Id != mark.Id).All(old => currentRows.Any(row => ReferenceEquals(row, old))),
                        "scroll-preserve behavior: a late mark changes only its own stored row");
                }
            }
            finally
            {
                CompositionTarget.Rendering -= Frame;
                scroll.LayoutUpdated -= Layout;
                scroll.ViewChanged -= Changed;
            }
        }
        finally
        {
            Away(window, main);
            await demo.RemoveSessionAsync(id);
        }
    }
}
