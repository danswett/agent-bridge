using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AgentBridge.App;

static partial class SelfTest
{
    static async Task CheckHistoryOneNotchUpArrivals(MainWindow window, MainViewModel main, DemoHost demo,
        Action<bool, string> check, Dictionary<string, object?> result)
    {
        const string id = "demo-one-notch-up";
        const int turns = 300, blocks = 304;
        const string method = "window-targeted WM_MOUSEWHEEL (PostMessage; not physical SendInput)";
        var previous = main.Selected?.Id;
        var samples = new List<object>();
        var receipts = new List<int>();
        var phase = "setup";
        var arrival = 0;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        result["historyOneNotchUpArrivals"] = new { method, turns, blocks, samples, receipts };
        var fixture = new Session
        {
            Id = id, Agent = AgentKind.Copilot, Title = "One notch up with arriving history",
            Status = SessionStatus.Idle, StatusSince = StoredFrom, UpdatedAt = StoredFrom,
            Activity = new Activity
            {
                UserMessage = "Keep this reading position.",
                Response = string.Join("\n\n", Enumerable.Range(1, blocks).Select(n => $"Live block {n}.")),
                TurnCount = turns + 1,
            },
        };
        await demo.PutAsync(fixture);
        try
        {
            if (!await Until(() => main.AllCards.Any(c => c.Id == id), TimeSpan.FromSeconds(10)))
            { check(false, "one-notch arrivals setup: receives the synthetic session"); return; }
            window.ShowPage("sessions");
            Resize(window, 1000, 700);
            var card = main.AllCards.Single(c => c.Id == id);
            if (card.Stored is not { } stored)
            { check(false, "one-notch arrivals setup: has stored history"); return; }
            for (var n = 1; n <= turns; n++) { stored.AddLive(StoredTurn(n)); }
            var view = await Choose(window, main, id, "Conversation");
            if (view is null || ConversationScroller(view) is not { } scroll
                || ById<ListView>(view, "Conversation") is not { } list
                || ById<ItemsRepeater>(view, "History") is not { } rows)
            { check(false, "one-notch arrivals setup: has the conversation and history scrollers"); return; }
            int Drawn() => rows.ItemsSourceView?.Count ?? 0;
            if (!await Until(() => stored.Turns.Count == turns && Drawn() == turns && list.Items.Count == blocks
                && scroll.ScrollableHeight > scroll.ViewportHeight
                && scroll.VerticalOffset >= scroll.ScrollableHeight - 1.5, TimeSpan.FromSeconds(30)))
            {
                check(false, $"one-notch arrivals setup: draws {turns} stored turns / {blocks} live blocks at the bottom"
                    + $" ({stored.Turns.Count} held, {Drawn()} rows, {list.Items.Count} blocks, {scroll.VerticalOffset:0.##}/{scroll.ScrollableHeight:0.##} DIP)");
                return;
            }
            await Still(scroll);
            var moving = false;
            var layouts = 0;
            double kept = 0, worst = 0;
            void Record(string edge)
            {
                var offset = scroll.VerticalOffset;
                if (arrival > 0) { worst = Math.Max(worst, Math.Abs(offset - kept)); }
                samples.Add(new
                {
                    ms = clock.ElapsedMilliseconds, phase, arrival, edge, layouts,
                    offset, expected = arrival > 0 ? (double?)kept : null,
                    extent = scroll.ScrollableHeight, drawn = Drawn(),
                });
            }
            void LaidOut(object? sender, object args) { layouts++; Record("layout"); }
            void Changed(object? sender, ScrollViewerViewChangedEventArgs args)
            { moving = args.IsIntermediate; Record(args.IsIntermediate ? "view-intermediate" : "view-final"); }
            var wheel = new PointerEventHandler((_, e) => receipts.Add(e.GetCurrentPoint(scroll).Properties.MouseWheelDelta));
            scroll.AddHandler(UIElement.PointerWheelChangedEvent, wheel, true);
            scroll.LayoutUpdated += LaidOut;
            scroll.ViewChanged += Changed;
            try
            {
                phase = "one-notch-up";
                var bottom = scroll.VerticalOffset;
                var point = scroll.TransformToVisual(window.RootElement).TransformPoint(
                    new Windows.Foundation.Point(scroll.ViewportWidth / 2, scroll.ViewportHeight / 2));
                var scale = scroll.XamlRoot.RasterizationScale;
                var mouse = new WheelPoint { X = (int)Math.Round(point.X * scale), Y = (int)Math.Round(point.Y * scale) };
                var (target, inWindow, detail) = PostedInputTarget(window);
                // Post directly to our XAML window: the shared input helper requires a foreground desktop.
                if (!inWindow || ClientToScreen(window.Handle, ref mouse) == 0)
                { check(false, $"one-notch arrivals setup: owned native wheel target ({detail})"); return; }
                AppLog.Write($"self-test: one-notch arrivals input: {method}; delta 120; {detail}");
                Record("before-wheel");
                if (!PostMessageW(target, 0x020A, (nint)(120 << 16), (mouse.Y << 16) | (mouse.X & 0xffff)))
                { check(false, $"one-notch arrivals setup: posts {method}"); return; }
                var delivered = await Until(() => receipts.Count > 0 && !moving && scroll.VerticalOffset < bottom,
                    TimeSpan.FromSeconds(5));
                await Still(scroll);
                kept = scroll.VerticalOffset;
                var leftBottom = delivered && receipts.Count == 1 && receipts[0] == 120
                    && kept < bottom && scroll.ScrollableHeight - kept > 1.5 && !moving;
                check(leftBottom, $"one-notch arrivals input: one {method} notch leaves the bottom"
                    + $" (receipts [{string.Join(", ", receipts)}], {bottom:0.##} -> {kept:0.##}/{scroll.ScrollableHeight:0.##} DIP)");
                if (!leftBottom) { return; }

                phase = "arrivals";
                for (arrival = 1; arrival <= 8; arrival++)
                {
                    var before = layouts;
                    Record("before-arrival");
                    stored.AddLive(StoredTurn(turns + arrival));
                    var drawn = await Until(() => Drawn() == turns + arrival && layouts > before,
                        TimeSpan.FromSeconds(5));
                    await Task.Delay(700);
                    Record("after-arrival");
                    check(drawn && worst == 0,
                        $"one-notch arrivals: offset unchanged after every layout pass through arrival {arrival}"
                        + $" (expected {kept:0.###}, actual {scroll.VerticalOffset:0.###}, worst drift {worst:0.###} DIP;"
                        + $" {layouts - before} layouts, {Drawn()}/{turns + arrival} rows)");
                }
            }
            finally
            {
                scroll.RemoveHandler(UIElement.PointerWheelChangedEvent, wheel);
                scroll.LayoutUpdated -= LaidOut;
                scroll.ViewChanged -= Changed;
            }
        }
        finally
        {
            if (previous is not null) { await Choose(window, main, previous, "Conversation"); }
            await demo.RemoveSessionAsync(id);
        }
    }
}
