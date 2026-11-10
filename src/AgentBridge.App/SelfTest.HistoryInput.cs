using System.Runtime.InteropServices;
using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

static partial class SelfTest
{
    static async Task CheckHistoryInput(MainWindow window, MainViewModel main, Action<bool, string> check, string outFile)
    {
        window.ShowPage("sessions");
        Resize(window, 1000, 700);
        var view = await Choose(window, main, "demo-approval", "Response");
        if (view is null) { check(false, "native history input: a visible session"); return; }
        window.Activate();
        if (!await Until(() => GetForegroundWindow() == window.Handle, TimeSpan.FromSeconds(2)))
        {
            check(false, "native history input: an interactive foreground desktop (no posted-message or programmatic fallback)");
            return;
        }
        var endpoint = LocalEndpoint.For(Path.Combine(Path.GetTempPath(), $"ab-history-input-{Guid.NewGuid():N}"));
        await using var server = LocalServer.Start(endpoint, (_, op, data) =>
        {
            if (op == Ops.Hello)
            {
                return Task.FromResult<object?>(new HelloResult(1, new MachineInfo("history-input", "History input fixture", "windows", "0.1.0"), [HistoryProtocol.Capability]));
            }
            if (op != HistoryProtocol.Op) { throw new InvalidOperationException($"Unexpected history fixture request: {op}"); }
            var end = data.TryGetProperty("before", out var before) && before.ValueKind == JsonValueKind.String ? int.Parse(before.GetString()![1..]) : 40;
            return Task.FromResult<object?>(new HistoryResult
            {
                Turns = [.. Enumerable.Range(end - 19, 20).Select(StoredTurn)],
                Before = end > 0 ? $"b{end - 20}" : null,
                Start = end > 0 ? null : new HistoryStart { At = StoredFrom.AddDays(-1) },
            });
        });
        var ui = new QueueDispatcher(view.DispatcherQueue);
        await using var host = new HostConnection(ct => LocalClient.ConnectAsync(endpoint, TimeSpan.FromSeconds(2), ct), ui) { Subscribes = false };
        host.Start();
        await host.WhenConnectedAsync().WaitAsync(TimeSpan.FromSeconds(10));
        if (!await Until(() => host.Hello is not null, TimeSpan.FromSeconds(5))) { check(false, "native history input: fixture hello"); return; }
        var original = view.Card;
        var trace = new List<object>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var phase = "setup";
        var wheels = 0;
        var clicks = 0;
        var starts = 0;
        var ends = 0;
        try
        {
            foreach (var route in new[] { "wheel", "arrow", "thumb" })
            {
                var session = DemoHost.Heavy(DateTimeOffset.UtcNow).Single(s => s.Id == DemoHost.HeavyId);
                var plain = session.Activity.Response + "\n\nNative input plain row.";
                session = session with { Id = $"native-history-{route}", Subagents = null, Activity = session.Activity with { Response = plain, UserMessage = "Native history input" } };
                var card = new SessionCardViewModel(session, new CardContext
                {
                    Host = host, Ui = ui, Deliveries = new Deliveries(host, ui),
                    Folds = new FoldStore(null), Capabilities = () => host.Capabilities,
                });
                view.Card = card;
                var stored = card.Stored!;
                try
                {
                    if (!await Until(() => stored.Turns.Count == 20 && ConversationScroller(view) is { ScrollableHeight: > 0 }, TimeSpan.FromSeconds(10)))
                    {
                        check(false, $"native {route}: initial page and scrollable conversation");
                        continue;
                    }
                    var list = ById<ListView>(view, "Conversation")!;
                    var scroll = ConversationScroller(view)!;
                    var mouse = new HistoryPointer(window);
                    var wheel = new PointerEventHandler((_, _) => wheels++);
                    scroll.AddHandler(UIElement.PointerWheelChangedEvent, wheel, true);
                    FrameworkElement? anchor = null;
                    object? anchored = null;
                    var top = 0.0;
                    double? AnchorY() => anchor is ListViewItem item && item.IsLoaded && ReferenceEquals(item.Content, anchored)
                        ? item.TransformToVisual(scroll).TransformPoint(default).Y : null;
                    void Record()
                    {
                        if (trace.Count < 6000) { trace.Add(new { ms = clock.ElapsedMilliseconds, phase, offset = scroll.VerticalOffset, extent = scroll.ScrollableHeight, anchor = AnchorY(), wheels, clicks, starts, ends }); }
                    }
                    void LaidOut(object? _, object __) => Record();
                    scroll.LayoutUpdated += LaidOut;
                    RepeatButton? increase = null;
                    RepeatButton? decrease = null;
                    Thumb? thumb = null;
                    RoutedEventHandler clicked = (_, _) => clicks++;
                    DragStartedEventHandler begun = (_, _) => starts++;
                    DragCompletedEventHandler ended = (_, _) => ends++;
                    try
                    {
                        mouse.Move(scroll, scroll.ActualWidth - 8, scroll.ActualHeight / 2);
                        await Task.Delay(700);
                        var bar = Find<ScrollBar>(scroll).FirstOrDefault(b => b.Orientation == Orientation.Vertical && b.ActualHeight > 0);
                        increase = bar is null ? null : Find<RepeatButton>(bar).FirstOrDefault(b => b.Name == "VerticalSmallIncrease");
                        decrease = bar is null ? null : Find<RepeatButton>(bar).FirstOrDefault(b => b.Name == "VerticalSmallDecrease");
                        thumb = bar is null ? null : Find<Thumb>(bar).FirstOrDefault(t => t.Name == "VerticalThumb");
                        if (increase is not { ActualWidth: > 0, ActualHeight: > 0, IsHitTestVisible: true }
                            || decrease is not { ActualWidth: > 0, ActualHeight: > 0, IsHitTestVisible: true }
                            || thumb is not { ActualWidth: > 0, ActualHeight: > 0, IsHitTestVisible: true } || bar is null)
                        { check(false, $"native {route}: laid-out, hit-testable vertical arrows and thumb"); continue; }
                        increase.Click += clicked;
                        decrease.Click += clicked;
                        thumb.DragStarted += begun;
                        thumb.DragCompleted += ended;
                        async Task Bottom()
                        {
                            phase = $"{route}:input-bottom";
                            if (route == "thumb") { await mouse.Drag(thumb, bar); }
                            else
                            {
                                for (var n = 0; n < 80 && (n == 0 || scroll.ScrollableHeight - scroll.VerticalOffset > 1.5); n++)
                                {
                                    if (route == "wheel") { mouse.Move(scroll, scroll.ActualWidth / 2, scroll.ActualHeight / 2); mouse.Wheel(-1200); }
                                    else { await mouse.Click(increase); }
                                    await Task.Delay(50);
                                }
                            }
                            await Still(scroll);
                        }
                        async Task Observe(string label, bool atBottom)
                        {
                            phase = $"{route}:{label}";
                            var worst = 0.0;
                            for (var n = 0; n < 20; n++)
                            {
                                await Task.Delay(50);
                                Record();
                                var drift = atBottom ? scroll.ScrollableHeight - scroll.VerticalOffset : AnchorY() is { } y ? Math.Abs(y - top) : double.PositiveInfinity;
                                worst = Math.Max(worst, drift);
                            }
                            check(worst <= 1.5, $"native {route}: {label}, worst visible drift {worst:0.##} DIP ({scroll.VerticalOffset:0.##}/{scroll.ScrollableHeight:0.##})");
                        }
                        void Capture()
                        {
                            anchor = Enumerable.Range(0, list.Items.Count).Select(i => list.ContainerFromIndex(i)).OfType<ListViewItem>()
                                .FirstOrDefault(i => i.TransformToVisual(scroll).TransformPoint(default).Y is var y && y + i.ActualHeight > 0 && y < scroll.ViewportHeight);
                            anchored = (anchor as ListViewItem)?.Content;
                            top = AnchorY() ?? double.NaN;
                            check(anchor is not null, $"native {route}: visible reading anchor");
                        }
                        var receipts = (wheels, clicks, starts, ends);
                        await Bottom();
                        check(route == "wheel" ? wheels > receipts.wheels : route == "arrow" ? clicks > receipts.clicks : starts > receipts.starts && ends > receipts.ends,
                            $"native {route}: actual input receipts (wheel {wheels - receipts.wheels}, arrow {clicks - receipts.clicks}, drag {starts - receipts.starts}/{ends - receipts.ends})");
                        await Observe("settles at bottom and stays", atBottom: true);
                        stored.AddLive(StoredTurn(1000));
                        await Observe("bottom follows new rows", atBottom: true);
                        phase = $"{route}:one-notch-up";
                        mouse.Move(scroll, scroll.ActualWidth / 2, scroll.ActualHeight / 2);
                        var heard = wheels;
                        mouse.Wheel(120);
                        await Still(scroll);
                        check(wheels > heard && scroll.ScrollableHeight - scroll.VerticalOffset > 1.5, $"native {route}: one real wheel notch leaves the bottom");
                        Capture();
                        for (var n = 1001; n <= 1008; n++) { stored.AddLive(StoredTurn(n)); await Observe($"up holds arrival {n - 1000}", atBottom: false); }
                        for (var n = 1; n <= 2; n++)
                        {
                            phase = $"{route}:older-page-{n}";
                            var count = stored.Turns.Count;
                            await stored.LoadOlderAsync();
                            check(stored.Turns.Count == count + 20, $"native {route}: older page {n} arrives through session.history");
                            await Observe($"up holds older page {n}", atBottom: false);
                        }
                        phase = $"{route}:formatted-row";
                        card.Update(session with { Activity = session.Activity with { Response = session.Activity.Response!.Replace("Native input plain row.", "Native input **formatted** row.", StringComparison.Ordinal) } });
                        await Observe("up holds plain-to-formatted row", atBottom: false);
                        anchor = null;
                        receipts = (wheels, clicks, starts, ends);
                        await Bottom();
                        check(route == "wheel" ? wheels > receipts.wheels : route == "arrow" ? clicks > receipts.clicks : starts > receipts.starts && ends > receipts.ends,
                            $"native {route}: re-engages through its own input route");
                        stored.AddLive(StoredTurn(1009));
                        await Observe("re-engaged bottom follows new rows", atBottom: true);
                        if (route == "arrow")
                        {
                            heard = clicks;
                            await mouse.Click(decrease);
                            await Still(scroll);
                            check(clicks > heard && scroll.ScrollableHeight - scroll.VerticalOffset > 1.5, "native arrow: one real upward arrow step leaves the bottom");
                            Capture();
                            stored.AddLive(StoredTurn(1010));
                            await Observe("one upward arrow step holds new rows", atBottom: false);
                        }
                        check(Selected(list) is null, $"native {route}: scroll input does not select conversation items");
                    }
                    finally
                    {
                        scroll.RemoveHandler(UIElement.PointerWheelChangedEvent, wheel);
                        scroll.LayoutUpdated -= LaidOut;
                        if (increase is not null) { increase.Click -= clicked; }
                        if (decrease is not null) { decrease.Click -= clicked; }
                        if (thumb is not null) { thumb.DragStarted -= begun; thumb.DragCompleted -= ended; }
                    }
                }
                finally { view.Card = original; card.Detach(); }
            }
        }
        finally
        {
            view.Card = original;
            await File.WriteAllTextAsync(Path.ChangeExtension(outFile, ".history-input.json"), JsonSerializer.Serialize(trace, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    sealed class HistoryPointer(MainWindow window)
    {
        void Own()
        {
            if (GetForegroundWindow() != window.Handle) { throw new InvalidOperationException("Native history input lost its own foreground window; refusing input."); }
        }

        public void Move(FrameworkElement element, double x, double y)
        {
            Own();
            var point = element.TransformToVisual(window.RootElement).TransformPoint(new Windows.Foundation.Point(x, y));
            var scale = element.XamlRoot.RasterizationScale;
            var origin = new HistoryPoint();
            if (!HistoryClientToScreen(window.Handle, ref origin)) { throw new InvalidOperationException("Cannot locate native history input window."); }
            var target = new HistoryPoint { X = origin.X + (int)Math.Round(point.X * scale), Y = origin.Y + (int)Math.Round(point.Y * scale) };
            HistoryGetWindowThreadProcessId(HistoryWindowFromPoint(target), out var pid);
            if (pid != Environment.ProcessId || !HistorySetCursorPos(target.X, target.Y)) { throw new InvalidOperationException("Native history pointer target is not the self-test app."); }
        }

        void Send(uint flags, uint data = 0)
        {
            // Release our own held button even if activation was lost during the gesture.
            if (flags != 0x0004) { Own(); }
            var inputs = new[] { new HistoryMouseInput { Flags = flags, Data = data } };
            if (HistorySendInput(1, inputs, Marshal.SizeOf<HistoryMouseInput>()) != 1) { throw new InvalidOperationException($"Native history SendInput failed: {Marshal.GetLastPInvokeError()}"); }
        }

        public void Wheel(int delta) => Send(0x0800, unchecked((uint)delta));

        public async Task Click(FrameworkElement element)
        {
            Move(element, element.ActualWidth / 2, element.ActualHeight / 2);
            Send(0x0002);
            try { await Task.Delay(50); }
            finally { Send(0x0004); }
            await Task.Delay(50);
        }

        public async Task Drag(Thumb thumb, ScrollBar bar)
        {
            Move(thumb, thumb.ActualWidth / 2, thumb.ActualHeight / 2);
            Send(0x0002);
            try
            {
                await Task.Delay(80);
                for (var n = 1; n <= 6; n++) { Move(bar, bar.ActualWidth / 2, bar.ActualHeight - 1 - (6 - n) * 8); await Task.Delay(60); }
            }
            finally { Send(0x0004); }
            await Task.Delay(150);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HistoryPoint { public int X, Y; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct HistoryMouseInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(16)] public uint Data;
        [FieldOffset(20)] public uint Flags;
    }

    [LibraryImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)]
    private static partial uint HistorySendInput(uint count, [In] HistoryMouseInput[] inputs, int size);
    [LibraryImport("user32.dll", EntryPoint = "SetCursorPos")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HistorySetCursorPos(int x, int y);
    [LibraryImport("user32.dll", EntryPoint = "ClientToScreen")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool HistoryClientToScreen(nint window, ref HistoryPoint point);
    [LibraryImport("user32.dll", EntryPoint = "WindowFromPoint")]
    private static partial nint HistoryWindowFromPoint(HistoryPoint point);
    [LibraryImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")]
    private static partial uint HistoryGetWindowThreadProcessId(nint window, out uint pid);
}
