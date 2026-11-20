using System.Text.Json;
using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
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
            var monitorArrival = false;
            var arrived = false;
            var peakOffset = 0.0;
            var worstRetreat = 0.0;
            var arrivalGap = 0.0;
            var requiredRows = 0;
            var growthFloor = 0.0;
            var extentFloor = 0.0;
            var invalidSurface = false;
            (bool Valid, int DrawnHistory, int VisibleHistory, int VisibleResponse) Surface()
            {
                bool Drawn(FrameworkElement row) => row.IsLoaded && row.Opacity > 0 && Shown(row, view);
                bool InView(FrameworkElement row)
                {
                    var at = row.TransformToVisual(scroll).TransformPoint(default);
                    return at.Y < scroll.ViewportHeight && at.Y + row.ActualHeight > 0
                        && at.X < scroll.ViewportWidth && at.X + row.ActualWidth > 0;
                }
                bool Written(Inline inline) => inline is Run run ? !string.IsNullOrWhiteSpace(run.Text)
                    : inline is Span span && span.Inlines.Any(Written);
                bool Content(MarkdownView renderer, bool inView) => renderer.Block is not null && Drawn(renderer)
                    && (Find<RichTextBlock>(renderer).Any(text => Drawn(text) && (!inView || InView(text))
                        && text.Blocks.OfType<Paragraph>().Any(p => p.Inlines.Any(Written)))
                    || Find<TextBlock>(renderer).Any(text => AutomationProperties.GetAutomationId(text) == "CodeText"
                        && !string.IsNullOrWhiteSpace(text.Text) && Drawn(text) && (!inView || InView(text))));
                var drawnHistory = 0;
                var visibleHistory = 0;
                var source = rows.ItemsSourceView;
                for (var i = 0; source is not null && i < source.Count; i++)
                {
                    if (source.GetAt(i) is not HistoryRow history || rows.TryGetElement(i) is not FrameworkElement row || !Drawn(row)) { continue; }
                    var content = Find<MarkdownView>(row).Where(m => history.Blocks.Any(b => Equals(b.Block, m.Block))).ToList();
                    if (!content.Any(m => Content(m, false))) { continue; }
                    drawnHistory++;
                    if (content.Any(m => Content(m, true))) { visibleHistory++; }
                }
                var visibleResponse = 0;
                for (var i = 0; i < list.Items.Count; i++)
                {
                    if (list.ContainerFromIndex(i) is ListViewItem row && row.Content is ConversationBlock block
                        && Equals(row.Content, list.Items[i]) && Drawn(row) && ById<MarkdownView>(row, "ResponseBlock") is { } renderer
                        && Equals(renderer.Block, block.Block) && Content(renderer, true)) { visibleResponse++; }
                }
                var height = scroll.ScrollableHeight;
                var valid = double.IsFinite(scroll.ViewportHeight) && scroll.ViewportHeight > 0
                    && double.IsFinite(scroll.ViewportWidth) && scroll.ViewportWidth > 0
                    && double.IsFinite(height) && height > 2 * scroll.ViewportHeight && height >= extentFloor && height > growthFloor
                    && double.IsFinite(scroll.VerticalOffset) && scroll.VerticalOffset >= 0 && Gap() >= -1.5
                    && requiredRows > 0 && stored.Turns.Count == requiredRows && source?.Count == requiredRows
                    && drawnHistory > 0 && visibleHistory + visibleResponse > 0;
                return (valid, drawnHistory, visibleHistory, visibleResponse);
            }
            void Changed(object? sender, ScrollViewerViewChangedEventArgs e)
            {
                moving = e.IsIntermediate;
                Record("view-changed");
            }
            void Record(string kind = "render")
            {
                var offset = scroll.VerticalOffset;
                var surface = Surface();
                if (monitorArrival)
                {
                    invalidSurface |= !surface.Valid;
                    if (!invalidSurface && !arrived && Gap() <= 1.5) { arrived = true; peakOffset = offset; }
                    if (!invalidSurface && arrived)
                    {
                        peakOffset = Math.Max(peakOffset, offset);
                        worstRetreat = Math.Max(worstRetreat, peakOffset - offset);
                        arrivalGap = Math.Max(arrivalGap, Gap());
                    }
                }
                trace.Add(new
                {
                    ms = clock.ElapsedMilliseconds, phase, kind, offset,
                    extent = scroll.ScrollableHeight, viewport = scroll.ViewportHeight,
                    width = scroll.ViewportWidth, contentHeight = scroll.ExtentHeight,
                    firstBlock = (list.ItemsPanelRoot as ItemsStackPanel)?.FirstVisibleIndex,
                    moving, requestIndex = wheelSteps, arrived, worstRetreat, arrivalGap,
                    rows = rows.ItemsSourceView?.Count, storedRows = stored.Turns.Count, requiredRows, extentFloor, growthFloor,
                    surfaceValid = surface.Valid, invalidSurface,
                    drawnHistory = surface.DrawnHistory, visibleHistory = surface.VisibleHistory, visibleResponse = surface.VisibleResponse,
                });
            }
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
            void WheelTo(double requestedTarget)
            {
                // Control-level wheel transition, not physical input: the routed handler releases
                // the end pin; a ChangeView request or return is not evidence of actual movement.
                view.ReleasePin();
                wheelSteps++;
                var target = Math.Clamp(requestedTarget, 0, scroll.ScrollableHeight);
                var changed = scroll.ChangeView(null, target, null, disableAnimation: false);
                trace.Add(new
                {
                    ms = clock.ElapsedMilliseconds, phase, kind = "request",
                    requestIndex = wheelSteps, requestedTarget, target, changeViewReturned = changed,
                });
            }
            void Wheel(int delta) => WheelTo(scroll.VerticalOffset - delta / 120.0 * 3 * ConversationKeyboard.Line);
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
                phase = "control-wheel:rapid-burst";
                var entryGap = Gap();
                requiredRows = startCount + 64;
                growthFloor = startExtent + 1.5;
                // Hosted CI showed drawn history 0 after 60 settled frames: the update bursts leave the
                // viewport over the live response rows (gap ~8000), so the history repeater, which sits
                // above them, realises nothing. Waiting cannot fix that. Start the rapid burst from the
                // top of the history instead, and wait on rendered frames for the unchanged validity rule.
                view.ReleasePin();
                scroll.ChangeView(null, Math.Max(0, scroll.VerticalOffset + rows.TransformToVisual(scroll).TransformPoint(default).Y),
                    null, disableAnimation: true);
                var entry = Surface();
                for (var settle = 0; settle < 60 && !(entry.Valid && Gap() > 1.5); settle++)
                {
                    await Frame();
                    entry = Surface();
                }
                entryGap = Gap();
                Record("reference-prerequisites");
                check(entry.Valid && entryGap > 1.5,
                    $"bottom bounce rapid setup: valid long drawn surface away from end (valid {entry.Valid}, gap {entryGap:0.##}, drawn history {entry.DrawnHistory}, in-view history/response {entry.VisibleHistory}/{entry.VisibleResponse})");
                if (!entry.Valid || entryGap <= 1.5) { return; }
                extentFloor = scroll.ScrollableHeight - 1.5;
                monitorArrival = true;
                Record("burst-entry");
                var requested = scroll.VerticalOffset;
                var endRequests = 0;
                // Accumulate intent, rather than retargeting every request from the same drawn offset.
                for (var frame = 0; frame < 32; frame++)
                {
                    for (var n = 0; n < 8; n++)
                    {
                        requested += 3 * ConversationKeyboard.Line;
                        if (requested >= scroll.ScrollableHeight) { endRequests++; }
                        WheelTo(requested);
                    }
                    await Frame();
                }
                phase = "control-wheel:input-free-tail";
                for (var n = 0; n < 120; n++) { await Frame(); }
                check(endRequests > 1, $"bottom bounce request-budget coverage: continues endpoint requests ({endRequests}), not proof of accepted movement");
                check(!invalidSurface && arrived && !moving && Gap() <= 1.5, $"bottom bounce control reach: reaches bottom within the fixed input-and-tail budget ({scroll.VerticalOffset:0.##}/{scroll.ScrollableHeight:0.##}, arrived {arrived}, invalid surface {invalidSurface})");
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
                check(!invalidSurface, "bottom bounce driver-surface coverage: retains a positive viewport, long extent, drawn history and in-view content throughout burst, tail and quiet");
                check(!invalidSurface && worst <= 1.5 && maxOffset - minOffset <= 1.5 && maxExtent - minExtent <= 1.5,
                    $"bottom bounce control: stays settled without extent feedback (gap {worst:0.##}, travel {maxOffset - minOffset:0.##}, extent change {maxExtent - minExtent:0.##} DIP)");
                check(!invalidSurface && arrived && worstRetreat <= 1.5 && arrivalGap <= 1.5, invalidSurface
                    ? "bottom bounce invalid driver surface: post-arrival bounce remains unproven"
                    : arrived
                    ? $"bottom bounce post-arrival: no reversal through remaining input, tail or quiet (retreat {worstRetreat:0.##}, gap {arrivalGap:0.##} DIP)"
                    : "bottom bounce post-arrival coverage: no actual arrival observed; reversal remains unproven");
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
