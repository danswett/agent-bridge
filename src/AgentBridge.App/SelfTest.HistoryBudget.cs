using Stopwatch = System.Diagnostics.Stopwatch;
using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AgentBridge.App;

static partial class SelfTest
{
    public static async Task RunHistoryScrollBudgetAsync(MainWindow window, MainViewModel main, DemoHost demo, string outFile, Action<int> exit)
    {
        var result = new Dictionary<string, object?>();
        var passed = new List<string>();
        var failed = new List<string>();
        void Check(bool ok, string what) => (ok ? passed : failed).Add(what);
        try
        {
            if (!await Until(() => window.RootElement.XamlRoot is not null && main.AllCards.Any(c => c.Id == "demo-approval"), TimeSpan.FromSeconds(10))) { throw new InvalidOperationException("The demo conversation window did not load."); }
            await CheckHistoryScrollBudget(window, main, demo, Check, result);
        }
        catch (Exception e) { Check(false, $"history budget crashed: {e}"); }
        await Write(outFile, result, passed, failed, null);
        exit(failed.Count == 0 ? 0 : 1);
    }

    static async Task CheckHistoryScrollBudget(MainWindow window, MainViewModel main, DemoHost demo,
        Action<bool, string> check, Dictionary<string, object?> result)
    {
        CheckHistoryHeaderGrowthDiagnostics(check);
        const string id = "demo-scroll-budget";
        const int turns = 500;
        const double budgetMs = 200;
        var fixture = new Session
        {
            Id = id, Agent = AgentKind.Copilot, Title = "Long conversation scroll budget",
            Status = SessionStatus.Idle, StatusSince = StoredFrom, UpdatedAt = StoredFrom,
            Activity = new Activity { UserMessage = "The latest exchange", Response = "The latest reply.", TurnCount = turns },
        };
        await demo.PutAsync(fixture);
        try
        {
            if (!await Until(() => main.AllCards.Any(c => c.Id == id), TimeSpan.FromSeconds(10)))
            {
                check(false, "receives the long conversation fixture");
                return;
            }
            window.ShowPage("sessions");
            Resize(window, 1000, 700);
            var card = main.AllCards.Single(c => c.Id == id);
            var cold = await Choose(window, main, id, "Response");
            if (cold is null || card.Stored is not { } stored || ById<ItemsRepeater>(cold, "History") is not { } coldRows)
            {
                check(false, "opens a conversation whose first history page has not arrived");
                return;
            }
            await Task.Run(() => stored.AddLive(StoredTurn(-1)));
            check(await Until(() => coldRows.ItemsSourceView?.Count == 1, TimeSpan.FromSeconds(3)),
                "draws a first history turn arriving asynchronously after an empty open");
            stored.AddLive(StoredTurn(-2) with { Prompt = new HistoryPrompt { Text = "The latest exchange" } });
            Away(window, main);
            await Choose(window, main, id, "Response");
            check(await Until(() => coldRows.ItemsSourceView?.Count == 1, TimeSpan.FromSeconds(3)),
                "draws an earlier turn when the newest held prompt is already the live exchange");
            // Initialise the shared pane before timing another session's open.
            var away = await Choose(window, main, "demo-choice", "Response");
            if (away is null || ConversationScroller(away) is not { } scroll
                || ById<ListView>(away, "Conversation") is not { } list)
            {
                check(false, "has the shared conversation scroller and the fixture's history");
                return;
            }
            for (var n = 1; n <= turns; n++)
            {
                var items = string.Join("\n", Enumerable.Range(0, 5).Select(i =>
                    $"- **Step {i + 1}** of reply {n}: change `file{i}.js`, see [the notes](https://example.com/{n}/{i})."));
                var code = string.Join("\n", Enumerable.Range(0, 8).Select(i => $"const value{i} = compute({n}, {i});"));
                stored.AddLive(new HistoryTurn
                {
                    Id = $"budget-{n}", At = StoredFrom.AddMinutes(n),
                    Prompt = new HistoryPrompt { Text = $"Demo prompt {n}" },
                    Parts = [new HistoryPart
                    {
                        Id = $"budget-{n}-reply", Kind = HistoryPartKind.Response, At = StoredFrom.AddMinutes(n).AddSeconds(3),
                        Text = $"Reply {n} starts here, with a few words of *explanation* before the list.\n\n{items}\n\n```js\n{code}\n```\n\nAnd a closing paragraph for reply {n}, with `inline code` and **bold**.",
                    }],
                });
            }
            check(stored.Turns.Count == turns, $"holds all {turns} realistic Markdown turns before opening");
            var watch = new Stopwatch();
            double? receivedMs = null, movedMs = null, from = null, fillingStartedMs = null, fillingInputMs = null;
            double? readerDrift = null, longestFrameMs = null;
            var moving = false;
            var frames = 0;
            var filled = false;
            var method = "none";
            var openedAtBottom = false;
            var done = new TaskCompletionSource<bool>();
            var heard = new KeyEventHandler((_, e) =>
            {
                if (e.Key == VirtualKey.PageUp && from is not null) { receivedMs ??= watch.Elapsed.TotalMilliseconds; }
                if (e.Key == VirtualKey.PageUp && fillingStartedMs is { } sent) { fillingInputMs ??= watch.Elapsed.TotalMilliseconds - sent; }
            });
            void Changed(object? sender, ScrollViewerViewChangedEventArgs e)
            {
                moving = e.IsIntermediate;
                if (receivedMs is not null && from is { } start && scroll.VerticalOffset < start - 0.5)
                {
                    movedMs ??= watch.Elapsed.TotalMilliseconds;
                    done.TrySetResult(true);
                }
            }
            var timer = window.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(16);
            timer.Tick += async (_, _) =>
            {
                if (from is not null || main.Selected != card || scroll.ScrollableHeight <= 0
                    || scroll.VerticalOffset < scroll.ScrollableHeight - 1.5) { return; }
                openedAtBottom = true;
                if (!list.Focus(FocusState.Programmatic)) { return; }
                from = scroll.VerticalOffset;
                try { method = await Press(window, VirtualKey.PageUp, foreground: false); }
                catch (Exception e) { done.TrySetException(e); }
            };
            scroll.AddHandler(UIElement.PreviewKeyDownEvent, heard, true);
            scroll.ViewChanged += Changed;
            try
            {
                await PrepareNativeInput(window, list);
                watch.Start();
                timer.Start();
                window.Sessions.Rows.SelectedItem = card;
                await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10)));
                if (done.Task.IsFaulted) { await done.Task; }
                if (done.Task.IsCompleted && ById<ItemsRepeater>(away, "History") is { } historyRows)
                {
                    check(await Until(() => !moving, TimeSpan.FromSeconds(2)), "finishes the first native scroll");
                    fillingStartedMs = watch.Elapsed.TotalMilliseconds;
                    await Press(window, VirtualKey.PageUp, foreground: false);
                    await Until(() => fillingInputMs is not null, TimeSpan.FromSeconds(2));
                    check(await Until(() => !moving, TimeSpan.FromSeconds(2)), "finishes native scrolling during older drawing");
                    if (Enumerable.Range(0, historyRows.ItemsSourceView?.Count ?? 0).Select(i => historyRows.TryGetElement(i)).OfType<FrameworkElement>()
                        .FirstOrDefault(row => row.TransformToVisual(scroll).TransformPoint(default).Y + row.ActualHeight > 0 && row.TransformToVisual(scroll).TransformPoint(default).Y < scroll.ViewportHeight) is { } anchor)
                    {
                        var top = anchor.TransformToVisual(scroll).TransformPoint(default).Y;
                        var lastFrame = watch.Elapsed.TotalMilliseconds;
                        readerDrift = longestFrameMs = 0;
                        void Frame(object? sender, object args)
                        {
                            frames++;
                            var now = watch.Elapsed.TotalMilliseconds;
                            longestFrameMs = Math.Max(longestFrameMs.Value, now - lastFrame);
                            lastFrame = now;
                            readerDrift = Math.Max(readerDrift.Value, Math.Abs(anchor.TransformToVisual(scroll).TransformPoint(default).Y - top));
                        }
                        CompositionTarget.Rendering += Frame;
                        try { filled = await Until(() => historyRows.ItemsSourceView?.Count == turns && frames > 0, TimeSpan.FromSeconds(20)); }
                        finally { CompositionTarget.Rendering -= Frame; }
                    }
                }
            }
            finally
            {
                timer.Stop();
                scroll.RemoveHandler(UIElement.PreviewKeyDownEvent, heard);
                scroll.ViewChanged -= Changed;
            }
            result["historyScrollBudget"] = new
            {
                turns, budgetMs, method, receivedMs, movedMs, openedAtBottom,
                fillingInputMs, readerDrift, longestFrameMs, frames, filled,
                from, offset = scroll.VerticalOffset, extent = scroll.ScrollableHeight,
            };
            check(openedAtBottom, "opens the 500-turn conversation at the latest before input");
            check(receivedMs is < budgetMs && movedMs is < budgetMs,
                $"handles the first scroll within {budgetMs} ms on {turns} turns: {method}, input {receivedMs?.ToString("0.0") ?? "absent"} ms, movement {movedMs?.ToString("0.0") ?? "absent"} ms");
            check(fillingInputMs is < budgetMs, $"handles queued input while older history draws within {budgetMs} ms ({fillingInputMs?.ToString("0.0") ?? "absent"} ms)");
            check(filled && frames > 0 && readerDrift is <= 1.5 && longestFrameMs is < budgetMs,
                $"draws all {turns} turns without moving the reader or blocking a frame ({readerDrift:0.0} px drift, {longestFrameMs:0.0} ms longest frame)");
            check(Selected(list) is null, "keeps conversation blocks unselected after scrolling while older history draws");
            await CheckPostedInput(window, check, result);
            Away(window, main);
            await Choose(window, main, id, "Response");
            var pinnedTrace = new HistoryPinnedTrace();
            var pinnedWatch = Stopwatch.StartNew();
            var pinnedRows = ById<ItemsRepeater>(away, "History");
            void ObservePinned(string reason, double? target = null, bool? intermediate = null) =>
                pinnedTrace.Record(CapturePinnedSample(pinnedWatch.Elapsed.TotalMilliseconds, reason, scroll, pinnedRows, target, intermediate));
            void PinnedChanging(object? sender, ScrollViewerViewChangingEventArgs e) =>
                ObservePinned("view changing", e.FinalView.VerticalOffset);
            void PinnedChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
                ObservePinned("view changed", intermediate: e.IsIntermediate);
            long? offsetObserver = null, extentObserver = null;
            bool pinned;
            try
            {
                scroll.ViewChanging += PinnedChanging;
                scroll.ViewChanged += PinnedChanged;
                offsetObserver = scroll.RegisterPropertyChangedCallback(ScrollViewer.VerticalOffsetProperty, (_, _) => ObservePinned("offset"));
                extentObserver = scroll.RegisterPropertyChangedCallback(ScrollViewer.ScrollableHeightProperty, (_, _) => ObservePinned("extent"));
                ObservePinned("wait start");
                pinned = await Until(() => ById<ItemsRepeater>(away, "History")?.ItemsSourceView?.Count == turns
                    && scroll.VerticalOffset >= scroll.ScrollableHeight - 1.5, TimeSpan.FromSeconds(30));
                ObservePinned("wait end");
            }
            finally
            {
                pinnedWatch.Stop();
                scroll.ViewChanging -= PinnedChanging;
                scroll.ViewChanged -= PinnedChanged;
                if (offsetObserver is { } offsetToken) { scroll.UnregisterPropertyChangedCallback(ScrollViewer.VerticalOffsetProperty, offsetToken); }
                if (extentObserver is { } extentToken) { scroll.UnregisterPropertyChangedCallback(ScrollViewer.ScrollableHeightProperty, extentToken); }
            }
            var pinnedFill = new { drawn = ById<ItemsRepeater>(away, "History")?.ItemsSourceView?.Count, offset = scroll.VerticalOffset, extent = scroll.ScrollableHeight, pinned };
            var pinnedDiagnostic = pinned ? null : new
            {
                elapsedMs = pinnedWatch.Elapsed.TotalMilliseconds,
                correctionMeaning = "ViewChanging target within 1.5 px of the current end; observed effect, not method entry",
                trace = pinnedTrace, focusAndForeground = PostedInputTarget(window).Detail,
            };
            result["historyPinnedFill"] = pinned ? pinnedFill : new
            {
                pinnedFill.drawn, pinnedFill.offset, pinnedFill.extent, pinnedFill.pinned, diagnostic = pinnedDiagnostic,
            };
            const string pinnedMessage = "finishes older drawing at the end before focus moves away";
            if (!QuarantinePostedInput(result, check, pinnedMessage, new
            {
                outcome = pinnedFill,
                causeLimit = "posts no input: Chief runner-cohort exception, not a proven delivery cause",
            }))
            {
                check(pinned, pinned ? pinnedMessage
                    : $"{pinnedMessage} (history: {pinnedFill.drawn}/{turns}; count500: {pinnedFill.drawn == turns}; at-end: {pinnedFill.offset >= pinnedFill.extent - 1.5}; offset: {pinnedFill.offset:0.###}; extent: {pinnedFill.extent:0.###}; {PostedInputTarget(window).Detail}; trace: {System.Text.Json.JsonSerializer.Serialize(pinnedDiagnostic)})");
            }
            await Still(scroll);
            if (ById<ItemsRepeater>(away, "History") is { } historyList
                && historyList.TryGetElement((historyList.ItemsSourceView?.Count ?? 0) - 1) is { } last
                && ById<Button>(last, "CopyCode") is { } copy && copy.Focus(FocusState.Programmatic))
            {
                var tab = await Press(window, VirtualKey.Tab, foreground: false, extended: false, shift: true);
                bool tookView;
                try
                {
                    tookView = await Until(() => !Equals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(list.XamlRoot), copy)
                        && scroll.VerticalOffset < scroll.ScrollableHeight - 1.5, TimeSpan.FromSeconds(3));
                }
                finally { Unshift(tab); }
                await Still(scroll);
                var top = last.TransformToVisual(scroll).TransformPoint(default).Y;
                var response = card.Response + "\n\n" + string.Join("\n\n", Enumerable.Repeat("A later paragraph grows the live response.", 20));
                await demo.PutAsync(fixture with { Activity = fixture.Activity with { Response = response } });
                var arrived = await Until(() => card.Response == response, TimeSpan.FromSeconds(3));
                await Task.Delay(350);
                var drift = Math.Abs(last.TransformToVisual(scroll).TransformPoint(default).Y - top);
                result["historyFocusAway"] = new { arrived, tookView, offset = scroll.VerticalOffset, extent = scroll.ScrollableHeight, readerDrift = drift };
                check(arrived && tookView && drift < 1.5,
                    $"keeps a view taken by Shift+Tab when the live response grows ({tab}, {drift:0.0} px reader drift)");
            }
            else { check(false, "has the newest historical code block for the focus-away growth check"); }
            Away(window, main);
            await Choose(window, main, id, "Response");
            var followed = await Until(() => scroll.VerticalOffset >= scroll.ScrollableHeight - 1.5, TimeSpan.FromSeconds(3));
            if (ById<Button>(away, "HeaderPin") is { } pin && ById<Button>(away, "Details") is { } details
                && pin.Focus(FocusState.Programmatic) && ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(pin.XamlRoot), pin))
            {
                await Press(window, VirtualKey.Tab, foreground: false, extended: false);
                var tabbed = await Until(() => ReferenceEquals(Microsoft.UI.Xaml.Input.FocusManager.GetFocusedElement(pin.XamlRoot), details), TimeSpan.FromSeconds(3));
                var response = card.Response + "\n\n" + string.Join("\n\n", Enumerable.Repeat("An arriving paragraph while header focus moves.", 20));
                await demo.PutAsync(fixture with { Activity = fixture.Activity with { Response = response } });
                var arrived = await Until(() => card.Response == response, TimeSpan.FromSeconds(3));
                await Task.Delay(350);
                CheckHistoryHeaderGrowth(followed, tabbed, arrived, scroll.VerticalOffset, scroll.ScrollableHeight, check, result);
            }
            else { check(false, "focuses the header pin before Tab to Details"); }
            Away(window, main);
            await Choose(window, main, id, "Response");
            int WheelHistoryCount() => ById<ItemsRepeater>(away, "History")?.ItemsSourceView?.Count ?? 0;
            int? wheelReceivedCount = null;
            var wheelMoving = false;
            void WheelChanged(object? sender, ScrollViewerViewChangedEventArgs e) => wheelMoving = e.IsIntermediate;
            var heardWheel = new PointerEventHandler((_, _) => wheelReceivedCount ??= WheelHistoryCount());
            scroll.ViewChanged += WheelChanged;
            scroll.AddHandler(UIElement.PointerWheelChangedEvent, heardWheel, true);
            try
            {
                // Choose sees the card before its opening layout and queued end correction finish.
                await CaptureFrame(window.RootElement);
                var wheelReady = await Until(() =>
                {
                    window.RootElement.UpdateLayout();
                    return WheelHistoryCount() > 0 && !wheelMoving && scroll.ScrollableHeight > 0
                        && scroll.VerticalOffset >= scroll.ScrollableHeight - 1.5;
                }, TimeSpan.FromSeconds(10));
                var wheelPost = await Wheel(window, list);
                var wheeled = await Until(() => wheelReceivedCount is not null
                    && scroll.VerticalOffset < scroll.ScrollableHeight - 1.5, TimeSpan.FromSeconds(3));
                var wheelObserved = CaptureNativeWheel(window, wheelPost.Target, GetFocus(), GetForegroundWindow());
                var wheelTarget = $"sent: {wheelPost.Sent.Detail}; after receipt wait: {wheelObserved.Detail}";
                AppLog.Write($"self-test: native wheel {(wheelReceivedCount is null ? "not received" : "received")}; {wheelTarget}");
                result["historyWheelInput"] = new
                {
                    x = wheelPost.X, y = wheelPost.Y, received = wheelReceivedCount is not null,
                    sent = wheelPost.Sent, observed = wheelObserved,
                };
                const string tookMessage = "a native wheel takes the view away from the latest";
                var drawn = await Until(() => WheelHistoryCount() == turns, TimeSpan.FromSeconds(30));
                await CaptureFrame(window.RootElement);
                (int Count, bool Intermediate, double Offset, double Extent, bool Stable) snapshot = default;
                (double Offset, double Extent)? previous = null;
                var kept = await Until(() =>
                {
                    window.RootElement.UpdateLayout();
                    var offset = scroll.VerticalOffset;
                    var extent = scroll.ScrollableHeight;
                    var stable = previous is { } was && Math.Abs(offset - was.Offset) < 0.5
                        && Math.Abs(extent - was.Extent) < 0.5;
                    previous = (offset, extent);
                    snapshot = (WheelHistoryCount(), wheelMoving, offset, extent, stable);
                    return snapshot.Count == turns && !snapshot.Intermediate
                        && snapshot.Offset < snapshot.Extent - 1.5 && snapshot.Stable;
                }, TimeSpan.FromSeconds(10));
                var count500 = snapshot.Count == turns;
                var belowEnd = snapshot.Offset < snapshot.Extent - 1.5;
                var receipt = wheelReceivedCount is { } count
                    ? $"{(count < turns ? "before" : "after")} history reached {turns} (count: {count})"
                    : "wheel not received (issues 2471, 2476; quarantine requires missing canary before 2026-11-20T08:00:00Z)";
                var ok = wheelReady && wheeled && drawn && kept && count500 && !snapshot.Intermediate && belowEnd && snapshot.Stable;
                const string keptMessage = "keeps the view taken by a native wheel while older turns draw";
                var quarantined = wheelReceivedCount is null && QuarantinePostedInput(result, check, "native wheel (2471)", new
                {
                    wheelReady, wheeled, drawn, kept, count500, belowEnd, receipt,
                    historyCount = snapshot.Count, intermediate = snapshot.Intermediate,
                    offset = snapshot.Offset, extent = snapshot.Extent, stable = snapshot.Stable,
                    target = wheelTarget,
                });
                result["historyWheelFill"] = new
                {
                    wheelReady, wheeled, drawn, kept, wheelReceivedCount, receipt, quarantined,
                    historyCount = snapshot.Count, intermediate = snapshot.Intermediate,
                    offset = snapshot.Offset, extent = snapshot.Extent, stable = snapshot.Stable,
                    target = wheelTarget,
                };
                if (!quarantined)
                {
                    check(wheelReady && wheeled, wheelReady && wheeled ? tookMessage
                        : $"{tookMessage} ({(wheelReceivedCount is null ? receipt : "wheel received")}; ready: {wheelReady}; moved: {wheeled}; {wheelTarget})");
                    check(ok, ok ? keptMessage
                        : $"{keptMessage} (history: {snapshot.Count}/{turns}; count500: {count500}; intermediate: {snapshot.Intermediate}; below-end: {belowEnd}; stable: {snapshot.Stable}; offset: {snapshot.Offset:0.###}; extent: {snapshot.Extent:0.###}; wheel receipt: {receipt}; ready: {wheelReady}; wheeled: {wheeled}; drawn: {drawn}; kept: {kept}; {wheelTarget})");
                }
            }
            finally
            {
                scroll.ViewChanged -= WheelChanged;
                scroll.RemoveHandler(UIElement.PointerWheelChangedEvent, heardWheel);
            }
        }
        finally
        {
            Away(window, main);
            await demo.RemoveSessionAsync(id);
            await Until(() => main.AllCards.All(c => c.Id != id), TimeSpan.FromSeconds(10));
        }
    }

    static void CheckHistoryHeaderGrowthDiagnostics(Action<bool, string> check)
    {
        foreach (var (followed, tabbed, arrived, offset, part) in new[]
        {
            (false, true, true, 100.0, "followed"),
            (true, false, true, 100.0, "tabbed"),
            (true, true, false, 100.0, "arrived"),
            (true, true, true, 98.0, "scroll offset"),
            (false, false, false, 98.0, "followed, tabbed, arrived, scroll offset"),
        })
        {
            bool? passed = null;
            string? message = null;
            CheckHistoryHeaderGrowth(followed, tabbed, arrived, offset, 100,
                (ok, what) => { passed = ok; message = what; }, new Dictionary<string, object?>());
            check(passed == false && message?.EndsWith($"(failed: {part})", StringComparison.Ordinal) == true,
                $"names {part} when the growing-reply check fails ({message})");
        }
        foreach (var offset in new[] { 98.5, 98.4 })
        {
            var result = new Dictionary<string, object?>();
            bool? passed = null;
            var atBottom = offset >= 98.5;
            CheckHistoryHeaderGrowth(true, true, true, offset, 100,
                (ok, _) => passed = ok, result);
            var evidence = JsonSerializer.SerializeToElement(result);
            check(passed == atBottom
                && evidence.TryGetProperty("historyHeaderGrowth", out var growth)
                && growth.GetProperty("followed").GetBoolean()
                && growth.GetProperty("tabbed").GetBoolean()
                && growth.GetProperty("arrived").GetBoolean()
                && growth.GetProperty("atBottom").GetBoolean() == atBottom
                && growth.GetProperty("offset").GetDouble() == offset
                && growth.GetProperty("extent").GetDouble() == 100,
                $"records all growing-reply values on {(atBottom ? "pass at the scroll tolerance" : "failure outside the scroll tolerance")}");
        }
    }

    static void CheckHistoryHeaderGrowth(bool followed, bool tabbed, bool arrived, double offset, double extent,
        Action<bool, string> check, Dictionary<string, object?> result)
    {
        var atBottom = offset >= extent - 1.5;
        result["historyHeaderGrowth"] = new { followed, tabbed, arrived, atBottom, offset, extent };
        var failed = new List<string>();
        if (!followed) { failed.Add("followed"); }
        if (!tabbed) { failed.Add("tabbed"); }
        if (!arrived) { failed.Add("arrived"); }
        if (!atBottom) { failed.Add("scroll offset"); }
        const string message = "keeps following a growing reply after Tab outside the conversation has not scrolled it";
        check(followed && tabbed && arrived && atBottom,
            failed.Count == 0 ? message : $"{message} (failed: {string.Join(", ", failed)})");
    }

    static HistoryPinnedSample CapturePinnedSample(double ms, string reason, ScrollViewer scroll, ItemsRepeater? rows,
        double? target, bool? intermediate)
    {
        var realised = 0;
        int? firstVisible = null;
        var count = rows?.ItemsSourceView?.Count;
        for (var i = 0; rows is not null && i < (count ?? 0); i++)
        {
            if (rows.TryGetElement(i) is not FrameworkElement row) { continue; }
            realised++;
            if (firstVisible is not null) { continue; }
            var top = row.TransformToVisual(scroll).TransformPoint(default).Y;
            if (row.ActualHeight > 0 && top + row.ActualHeight > 0 && top < scroll.ViewportHeight) { firstVisible = i; }
        }
        return new(ms, reason, scroll.VerticalOffset, scroll.ScrollableHeight, count, realised, firstVisible, target, intermediate);
    }

    static async Task<(nint Target, int X, int Y, NativeWheelSnapshot Sent)> Wheel(MainWindow window, ListView list)
    {
        await PrepareNativeInput(window, list);
        var point = list.TransformToVisual(window.RootElement).TransformPoint(new Windows.Foundation.Point(list.ActualWidth / 2, list.ActualHeight / 2));
        var scale = list.XamlRoot.RasterizationScale;
        var mouse = new WheelPoint { X = (int)(point.X * scale), Y = (int)(point.Y * scale) };
        if (ClientToScreen(window.Handle, ref mouse) == 0) { throw new InvalidOperationException("The wheel target could not be located."); }
        var focus = GetFocus();
        var target = VerifiedNativeTarget(window);
        var sent = CaptureNativeWheel(window, target, focus, GetForegroundWindow());
        if (VerifiedNativeTarget(window) != target) { throw NativeInputRefused(window, "native focus changed before the wheel post"); }
        if (!PostMessageW(target, 0x020A, (nint)(120 << 16), (mouse.Y << 16) | (mouse.X & 0xffff))) { throw new InvalidOperationException($"The native wheel message could not be posted ({sent.Detail})."); }
        return (target, mouse.X, mouse.Y, sent);
    }

    struct WheelPoint { public int X, Y; }
    [System.Runtime.InteropServices.LibraryImport("user32.dll")]
    private static partial int ClientToScreen(nint window, ref WheelPoint point);
}
