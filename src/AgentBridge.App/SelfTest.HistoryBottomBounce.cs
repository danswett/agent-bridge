using System.Text.Json;
using AgentBridge.App.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

static partial class SelfTest
{
    static async Task CheckHistoryBottomBounce(MainWindow window, MainViewModel main, DemoHost demo,
        Action<bool, string> check, string outFile)
    {
        window.ShowPage("sessions");
        Resize(window, 1000, 700);
        const string id = "demo-history-bottom-bounce";
        var fixture = DemoHost.Heavy(DateTimeOffset.UtcNow).Single(s => s.Id == DemoHost.HeavyId)
            with { Id = id, Title = "Bottom bounce regression", Subagents = null };
        var trace = new List<object>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var phase = "setup";
        await demo.PutAsync(fixture);
        try
        {
            if (!await Until(() => main.AllCards.Any(c => c.Id == id), TimeSpan.FromSeconds(10))
                || main.AllCards.Single(c => c.Id == id).Stored is not { } stored)
            { check(false, "bottom bounce setup: receives an isolated demo history"); return; }
            await stored.LoadNewestAsync();
            for (var n = 1; n <= 300; n++) { stored.AddLive(StoredTurn(n)); }
            var view = await Choose(window, main, id, "Response");
            if (view is null || ConversationScroller(view) is not { } scroll
                || ById<ListView>(view, "Conversation") is not { } list
                || ById<ItemsRepeater>(view, "History") is not { } rows)
            { check(false, "bottom bounce setup: has the conversation scroller and history"); return; }
            var wheelSteps = 0;
            var moving = false;
            void Changed(object? sender, ScrollViewerViewChangedEventArgs e) => moving = e.IsIntermediate;
            void Record() => trace.Add(new
            {
                ms = clock.ElapsedMilliseconds, phase, offset = scroll.VerticalOffset,
                extent = scroll.ScrollableHeight, viewport = scroll.ViewportHeight,
                width = scroll.ViewportWidth, contentHeight = scroll.ExtentHeight,
                firstBlock = (list.ItemsPanelRoot as ItemsStackPanel)?.FirstVisibleIndex,
                moving, wheelSteps, rows = rows.ItemsSourceView?.Count,
            });
            async Task Frame()
            {
                var drawn = new TaskCompletionSource<bool>();
                EventHandler<object> rendered = (_, _) => drawn.TrySetResult(true);
                CompositionTarget.Rendering += rendered;
                try { await drawn.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
                finally { CompositionTarget.Rendering -= rendered; }
                Record();
            }
            double Gap() => scroll.ScrollableHeight - scroll.VerticalOffset;
            void Wheel(int delta)
            {
                // Control-level wheel transition, not physical input: the routed handler releases
                // the end pin and the ScrollViewer moves relative to its current offset.
                view.ReleasePin();
                wheelSteps++;
                var target = Math.Clamp(scroll.VerticalOffset - delta / 120.0 * 3 * ConversationKeyboard.Line, 0, scroll.ScrollableHeight);
                scroll.ChangeView(null, target, null, disableAnimation: false);
            }
            scroll.ViewChanged += Changed;
            try
            {
                if (!await Until(() => rows.ItemsSourceView?.Count == 300 && scroll.ScrollableHeight > 2 * scroll.ViewportHeight,
                    TimeSpan.FromSeconds(15)))
                { check(false, "bottom bounce setup: draws 300 stored turns and a virtualized live response"); return; }
                phase = "control-wheel:leave-bottom";
                Wheel(960);
                for (var n = 0; n < 60; n++) { await Frame(); }
                var left = !moving && Gap() > 1.5;
                check(left, $"bottom bounce control setup: wheel-up transition leaves the bottom (gap {Gap():0.##})");
                if (!left) { return; }
                var next = 301;
                phase = "control-wheel:update-bursts";
                var startCount = stored.Turns.Count;
                var startExtent = scroll.ScrollableHeight;
                for (var burst = 0; burst < 8; burst++)
                {
                    Wheel(-120);
                    await Frame();
                    for (var n = 0; n < 8; n++) { stored.AddLive(StoredTurn(next++)); }
                    for (var n = 0; n < 4; n++) { await Frame(); }
                }
                check(stored.Turns.Count == startCount + 64, "bottom bounce control: feeds eight update bursts while scrolling");
                check(scroll.ScrollableHeight > startExtent + 1.5, "bottom bounce control: updates really grow the extent without cache eviction");
                phase = "control-wheel:reach-bottom";
                // A fixed input budget, with no retry or programmatic jump to the end.
                for (var n = 0; n < 240; n++)
                {
                    if (n % 6 == 0) { Wheel(-120); }
                    await Frame();
                    if (!moving && Gap() <= 1.5) { break; }
                }
                check(!moving && Gap() <= 1.5, $"bottom bounce control: reaches bottom within the wheel-step budget ({scroll.VerticalOffset:0.##}/{scroll.ScrollableHeight:0.##})");
                phase = "control-wheel:quiet";
                var worst = 0.0;
                var minOffset = scroll.VerticalOffset;
                var maxOffset = minOffset;
                var minExtent = scroll.ScrollableHeight;
                var maxExtent = minExtent;
                for (var n = 0; n < 120; n++)
                {
                    await Frame();
                    worst = Math.Max(worst, Gap());
                    minOffset = Math.Min(minOffset, scroll.VerticalOffset);
                    maxOffset = Math.Max(maxOffset, scroll.VerticalOffset);
                    minExtent = Math.Min(minExtent, scroll.ScrollableHeight);
                    maxExtent = Math.Max(maxExtent, scroll.ScrollableHeight);
                }
                check(worst <= 1.5 && maxOffset - minOffset <= 1.5 && maxExtent - minExtent <= 1.5,
                    $"bottom bounce control: stays settled without extent feedback (gap {worst:0.##}, travel {maxOffset - minOffset:0.##}, extent change {maxExtent - minExtent:0.##} DIP)");
                check(Selected(list) is null, "bottom bounce control: wheel transitions do not select conversation items");
            }
            finally
            {
                scroll.ViewChanged -= Changed;
            }
        }
        finally
        {
            await File.WriteAllTextAsync(Path.ChangeExtension(outFile, ".history-bottom-bounce.json"), JsonSerializer.Serialize(trace));
            Away(window, main);
            await demo.RemoveSessionAsync(id);
        }
    }
}
