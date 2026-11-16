using System.Runtime.InteropServices;
using System.Text.Json;
using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
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
            if (view is null || view.Card is not { } card || ConversationScroller(view) is not { } scroll
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
            var viewChanges = (Inner: 0, Outer: 0);
            var layoutEvents = (Updated: 0, Sized: 0);
            (int Inner, int Outer, int Calls, int Layouts, int Sizes)? settled = null;
            EventHandler<object> layoutUpdated = (_, _) => layoutEvents.Updated++;
            SizeChangedEventHandler sizeChanged = (_, _) => layoutEvents.Sized++;
            var watchingSettle = false;
            var streaming = false;
            var recovering = false;
            UnhandledExceptionEventHandler xamlFailed = (_, e) => check(false,
                $"bottom bounce {phase}: XAML dispatcher exception 0x{e.Exception.HResult:X8}: {e.Exception}");
            ScrollViewer? outer = null;
            for (var parent = VisualTreeHelper.GetParent(scroll); parent is not null; parent = VisualTreeHelper.GetParent(parent))
            {
                if (parent is ScrollViewer ancestor) { outer = ancestor; break; }
            }
            trace.Add(new { kind = "counter-scope", inner = "Conversation/ListView/ScrollViewer",
                outer = outer is null ? "no ancestor ScrollViewer" : "ancestor ScrollViewer",
                productionChangeViewCalls = (int?)null, productionCallCoverage = "UNKNOWN: no diagnostic seam",
                layoutUpdatedScope = "SessionView event receipt, not layout origin", sizeChangedScope = "inner ScrollViewer" });
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
                viewChanges.Inner++;
                moving = e.IsIntermediate;
                Record("view-changed");
            }
            void OuterChanged(object? sender, ScrollViewerViewChangedEventArgs e)
            {
                viewChanges.Outer++;
                Record("outer-view-changed");
            }
            void QuietCounters()
            {
                if (settled is not { } at) { check(false, $"bottom bounce {phase}: quiescence coverage lacks a first valid settle"); return; }
                check(viewChanges.Inner == at.Inner && viewChanges.Outer == at.Outer && wheelSteps == at.Calls,
                    $"bottom bounce {phase}: no observed view events or fixture ChangeView calls after first settle (inner {viewChanges.Inner - at.Inner}, outer {(outer is null ? "unobserved" : (viewChanges.Outer - at.Outer).ToString())}, fixture calls {wheelSteps - at.Calls}; production calls UNKNOWN)");
                check(layoutEvents.Updated - at.Layouts <= 2 && layoutEvents.Sized == at.Sizes,
                    $"bottom bounce {phase}: bounded quiet layout receipts after first settle (SessionView LayoutUpdated {layoutEvents.Updated - at.Layouts}/2, inner ScrollViewer SizeChanged {layoutEvents.Sized - at.Sizes}/0; receipts are not causal attribution)");
            }
            void Record(string kind = "render")
            {
                var offset = scroll.VerticalOffset;
                var surface = Surface();
                if (monitorArrival)
                {
                    invalidSurface |= !surface.Valid;
                    if (!invalidSurface && !arrived && Gap() <= 1.5) { arrived = true; peakOffset = offset; }
                    if (watchingSettle && settled is null && !invalidSurface && arrived && !moving && Gap() <= 1.5)
                    { settled = (viewChanges.Inner, viewChanges.Outer, wheelSteps, layoutEvents.Updated, layoutEvents.Sized); }
                    if (!invalidSurface && arrived && !recovering)
                    {
                        peakOffset = Math.Max(peakOffset, offset);
                        worstRetreat = Math.Max(worstRetreat, peakOffset - offset);
                        if (!streaming) { arrivalGap = Math.Max(arrivalGap, Gap()); }
                    }
                }
                trace.Add(new
                {
                    ms = clock.ElapsedMilliseconds, phase, kind, offset,
                    extent = scroll.ScrollableHeight, viewport = scroll.ViewportHeight,
                    width = scroll.ViewportWidth, contentHeight = scroll.ExtentHeight,
                    firstBlock = (list.ItemsPanelRoot as ItemsStackPanel)?.FirstVisibleIndex,
                    moving, requestIndex = wheelSteps, arrived, worstRetreat, arrivalGap,
                    innerViewChanged = viewChanges.Inner, outerViewChanged = outer is null ? (int?)null : viewChanges.Outer,
                    sessionLayoutUpdated = layoutEvents.Updated, innerSizeChanged = layoutEvents.Sized,
                    outerOffset = outer?.VerticalOffset, outerExtent = outer?.ScrollableHeight,
                    fixtureChangeViewCalls = wheelSteps, productionChangeViewCalls = (int?)null, settledAtInner = settled?.Inner,
                    watchingSettle, streaming, recovering,
                    rows = rows.ItemsSourceView?.Count, storedRows = stored.Turns.Count, requiredRows, extentFloor, growthFloor,
                    surfaceValid = surface.Valid, invalidSurface,
                    drawnHistory = surface.DrawnHistory, visibleHistory = surface.VisibleHistory, visibleResponse = surface.VisibleResponse,
                });
            }
            async Task IndependentDeadline(Task signal, string requestedPhase, long started, int uiThread)
            {
                try { await signal.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
                catch (TimeoutException error)
                {
                    var failure = $"bottom bounce {requestedPhase}: dispatcher/render progress exceeded 3000ms";
                    await File.WriteAllTextAsync(Path.ChangeExtension(outFile, ".history-bottom-bounce-heartbeat.json"),
                        JsonSerializer.Serialize(new { failed = new[] { failure }, requestedPhase, deadlineMs = 3000,
                            elapsedMs = clock.ElapsedMilliseconds - started, uiThread, failureThread = Environment.CurrentManagedThreadId }))
                        .ConfigureAwait(false);
                    throw new TimeoutException(failure, error);
                }
            }

            async Task Frame()
            {
                var drawn = new TaskCompletionSource<bool>();
                EventHandler<object> rendered = (_, _) => drawn.TrySetResult(true);
                if (!view.DispatcherQueue.TryEnqueue(() => { if (!drawn.Task.IsCompleted) { CompositionTarget.Rendering += rendered; } }))
                { throw new InvalidOperationException($"bottom bounce {phase}: dispatcher rejected the rendered-frame responsiveness probe"); }

                try { await IndependentDeadline(drawn.Task, phase, clock.ElapsedMilliseconds, Environment.CurrentManagedThreadId); }
                finally { drawn.TrySetCanceled(); CompositionTarget.Rendering -= rendered; }
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
            Application.Current.UnhandledException += xamlFailed;
            view.LayoutUpdated += layoutUpdated;
            scroll.SizeChanged += sizeChanged;
            if (outer is not null) { outer.ViewChanged += OuterChanged; }
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
                var entry = Surface();
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
                watchingSettle = true;
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
                QuietCounters();
                if (invalidSurface || !arrived || moving || Gap() > 1.5 || settled is null) { return; }
                phase = "live-response:continuous-growth";
                watchingSettle = false;
                streaming = true;
                var growthStarted = clock.ElapsedMilliseconds;
                var nextGrowth = growthStarted;
                var response = card.Response;
                var updates = 0;
                var drawnGrowths = 0;
                var followedGrowths = 0;
                var awaitingEnd = false;
                var previousExtent = scroll.ScrollableHeight;
                do
                {
                    Task? armedFrame = null;
                    if (clock.ElapsedMilliseconds >= nextGrowth)
                    {
                        response += $"\n\nBottom bounce live update {++updates}: the growing reply remains readable.";
                        var update = card.Session with { UpdatedAt = DateTimeOffset.UtcNow, Activity = card.Session.Activity with { Response = response } };
                        await demo.PutAsync(update);
                        armedFrame = Frame();
                        card.Update(update);
                        nextGrowth = clock.ElapsedMilliseconds + 250;
                    }
                    await (armedFrame ?? Frame());
                    if (scroll.ScrollableHeight > previousExtent + 1.5) { drawnGrowths++; previousExtent = scroll.ScrollableHeight; awaitingEnd = true; }
                    if (awaitingEnd && Gap() <= 1.5 && !invalidSurface) { followedGrowths++; awaitingEnd = false; }
                } while (clock.ElapsedMilliseconds - growthStarted < 10_000);
                check(App.ExitCode == 0, $"bottom bounce growth: no unhandled XAML/layout exception (application exit code {App.ExitCode})");
                check(updates >= 30 && drawnGrowths >= 20 && followedGrowths >= 20 && card.Response == response && !invalidSurface,
                    $"bottom bounce streaming setup: several drawn growths per second while attached for 10s (updates {updates}, drawn growths {drawnGrowths}, followed growths {followedGrowths}, ms {clock.ElapsedMilliseconds - growthStarted}, invalid surface {invalidSurface})");
                phase = "live-response:first-settle";
                settled = null;
                watchingSettle = true;
                for (var n = 0; n < 120 && settled is null; n++) { await Frame(); }
                check(settled is not null && !invalidSurface && Gap() <= 1.5, "bottom bounce streaming: first valid settle within 120 frames after growth");
                streaming = false;
                phase = "live-response:quiet";
                worst = 0;
                minOffset = maxOffset = scroll.VerticalOffset;
                minExtent = maxExtent = scroll.ScrollableHeight;
                for (var n = 0; n < 120; n++)
                {
                    await Frame();
                    worst = Math.Max(worst, Gap());
                    minOffset = Math.Min(minOffset, scroll.VerticalOffset);
                    maxOffset = Math.Max(maxOffset, scroll.VerticalOffset);
                    minExtent = Math.Min(minExtent, scroll.ScrollableHeight);
                    maxExtent = Math.Max(maxExtent, scroll.ScrollableHeight);
                }
                QuietCounters();
                check(!invalidSurface && settled is not null && worst <= 1.5 && maxOffset - minOffset <= 1.5 && maxExtent - minExtent <= 1.5 && worstRetreat <= 1.5 && arrivalGap <= 1.5,
                    $"bottom bounce streaming: remains settled without reversals (gap {worst:0.##}, travel {maxOffset - minOffset:0.##}, extent change {maxExtent - minExtent:0.##}, retreat {worstRetreat:0.##} DIP; invalid surface {invalidSurface})");
                if (invalidSurface || settled is null || moving || Gap() > 1.5) { return; }
                phase = "real-wheel:recover-up";
                watchingSettle = false;
                recovering = true;
                var from = scroll.VerticalOffset;
                var innerWheel = 0;
                var rootWheel = 0;
                var innerHeard = new PointerEventHandler((_, e) => { if (e.GetCurrentPoint(scroll).Properties.MouseWheelDelta > 0) { innerWheel++; } });
                var rootHeard = new PointerEventHandler((_, e) => { if (e.GetCurrentPoint(view).Properties.MouseWheelDelta > 0) { rootWheel++; } });
                scroll.AddHandler(UIElement.PointerWheelChangedEvent, innerHeard, true);
                view.AddHandler(UIElement.PointerWheelChangedEvent, rootHeard, true);
                try
                {
                    var point = scroll.TransformToVisual(window.RootElement).TransformPoint(new Windows.Foundation.Point(scroll.ViewportWidth / 2, scroll.ViewportHeight / 2));
                    var scale = scroll.XamlRoot.RasterizationScale;
                    var mouse = new WheelPoint { X = (int)(point.X * scale), Y = (int)(point.Y * scale) };
                    var located = GetCursorPos(out var cursor) != 0;
                    var ready = located && GetForegroundWindow() == window.Handle && ClientToScreen(window.Handle, ref mouse) != 0 && GetAncestor(WindowFromPoint(mouse), 2) == window.Handle;
                    check(ready, "bottom bounce real-wheel setup: own foreground window and hit-tested conversation target");
                    if (!ready) { return; }
                    try
                    {
                        var positioned = SetCursorPos(mouse.X, mouse.Y) != 0 && GetForegroundWindow() == window.Handle && GetAncestor(WindowFromPoint(mouse), 2) == window.Handle;
                        check(positioned, "bottom bounce real-wheel setup: positions the pointer without activating or retargeting another window");
                        if (!positioned) { return; }
                        var sent = SendBounceWheel(1, [new BounceWheelInput { Type = 0, Data = 120, Flags = 0x0800 }], Marshal.SizeOf<BounceWheelInput>());
                        trace.Add(new { ms = clock.ElapsedMilliseconds, phase, kind = "input-request", mechanism = "SendInput", delta = 120, sent, offset = from });
                        check(sent == 1, "bottom bounce real-wheel delivery: SendInput accepts one upward mouse-wheel event");
                        if (sent != 1) { return; }
                        var movedUp = false;
                        var lowestOffset = from;
                        for (var n = 0; n < 60; n++)
                        {
                            await Frame();
                            movedUp |= scroll.VerticalOffset < from - 1.5;
                            lowestOffset = Math.Min(lowestOffset, scroll.VerticalOffset);
                        }
                        check(innerWheel > 0 && rootWheel > 0, $"bottom bounce real-wheel route coverage: positive wheel reaches inner scroller and outer SessionView (inner {innerWheel}, root {rootWheel})");
                        check(innerWheel > 0 && rootWheel > 0 && movedUp && scroll.VerticalOffset < from - 1.5
                            && scroll.VerticalOffset - lowestOffset <= 1.5 && !moving && !invalidSurface,
                            $"bottom bounce recovery: real wheel moves and retains the actual viewport upward ({from:0.##} -> {scroll.VerticalOffset:0.##}, gap {Gap():0.##}; no programmatic fallback)");
                        check(Selected(list) is null, "bottom bounce recovery: real upward wheel does not select conversation items");
                    }
                    finally { check(SetCursorPos(cursor.X, cursor.Y) != 0, "bottom bounce real-wheel cleanup: restores the original pointer"); }
                }
                finally
                {
                    scroll.RemoveHandler(UIElement.PointerWheelChangedEvent, innerHeard);
                    view.RemoveHandler(UIElement.PointerWheelChangedEvent, rootHeard);
                }
            }
            finally
            {
                scroll.ViewChanged -= Changed;
                Application.Current.UnhandledException -= xamlFailed;
                view.LayoutUpdated -= layoutUpdated;
                scroll.SizeChanged -= sizeChanged;
                if (outer is not null) { outer.ViewChanged -= OuterChanged; }
            }
        }
        finally
        {
            await File.WriteAllTextAsync(Path.ChangeExtension(outFile, ".history-bottom-bounce.json"), JsonSerializer.Serialize(trace));
            Away(window, main);
            await demo.RemoveSessionAsync(id);
        }
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct BounceWheelInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(16)] public uint Data;
        [FieldOffset(20)] public uint Flags;
    }
    [LibraryImport("user32.dll", EntryPoint = "SendInput")]
    private static partial uint SendBounceWheel(uint count, [In] BounceWheelInput[] inputs, int size);
    [LibraryImport("user32.dll")]
    private static partial int GetCursorPos(out WheelPoint point);
    [LibraryImport("user32.dll")]
    private static partial int SetCursorPos(int x, int y);
    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(WheelPoint point);
}
