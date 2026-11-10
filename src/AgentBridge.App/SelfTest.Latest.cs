using System.Runtime.InteropServices;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace AgentBridge.App;

static partial class SelfTest
{
    static async Task CheckLatest(MainWindow window, MainViewModel main, Action<bool, string> check)
    {
        Resize(window, 1200, 900);
        var view = await Choose(window, main, "demo-choice", "Response");
        if (view?.Card is not { } original) { check(false, "latest: opens the demo transcript"); return; }
        // A separate history cache: the 300 turns must not change later demo checks.
        var card = new SessionCardViewModel(original.Session, main.Local.Cards);
        view.Card = card;
        try
        {
            if (card.Stored is not { } stored) { check(false, "latest: has stored history"); return; }
            await Task.Delay(500);
            for (var n = 1; n <= 300; n++) { stored.AddLive(StoredTurn(1000 + n)); }
            var scroll = ConversationScroller(view);
            if (scroll is null || !await Until(() => scroll.ScrollableHeight > 2000, TimeSpan.FromSeconds(10)))
            { check(false, "latest: lays out 300 stored turns"); return; }
            var next = 1400;
            bool Bottom() => scroll.ScrollableHeight - scroll.VerticalOffset <= 1.5;
            async Task Jump()
            {
                var button = ById<Button>(view, "JumpToLatest");
                check(button is { IsTabStop: true, Visibility: Visibility.Visible } && AutomationProperties.GetName(button) == "Jump to latest",
                    "latest: offers a keyboard-reachable, screen-reader-named Jump to latest");
                if (button is null) { return; }
                check(button.Focus(FocusState.Keyboard), "latest: keyboard can focus Jump to latest");
                Press(window.Handle, VirtualKey.Enter, foreground: false);
                check(await Until(Bottom, TimeSpan.FromSeconds(3)), $"latest: Enter reaches the bottom ({scroll.VerticalOffset:0}/{scroll.ScrollableHeight:0})");
            }
            async Task Grow(bool pinned, string input)
            {
                var before = scroll.VerticalOffset;
                var bad = new List<string>();
                for (var i = 0; i < 20; i++)
                {
                    stored.AddLive(StoredTurn(next++));
                    await Task.Delay(100);
                    AppLog.Write($"latest {input} {i}: {scroll.VerticalOffset:0.##}/{scroll.ScrollableHeight:0.##}");
                    if (pinned ? !Bottom() : Math.Abs(scroll.VerticalOffset - before) > 1.5) { bad.Add($"{i}: {scroll.VerticalOffset:0}/{scroll.ScrollableHeight:0}"); }
                }
                check(bad.Count == 0, $"latest: {(pinned ? "stays pinned" : "stays scrolled back")} through twenty appends after {input} ({string.Join("; ", bad)})");
            }
            foreach (var input in new[] { "wheel", "arrow", "thumb", "key" })
            {
                // Setup only. The action being tested below always travels through the real input path.
                await LatestInput(window, scroll, input, up: true);
                view.ReleasePin();
                scroll.ChangeView(null, scroll.ScrollableHeight / 2, null, disableAnimation: true);
                await Still(scroll);
                check(await LatestInput(window, scroll, input, up: false), $"latest: real {input} reaches the transcript input handler");
                await Jump();
                await Grow(pinned: true, input);
                card.Update(card.Session with { Activity = (card.Session.Activity ?? new Activity()) with { Response = "A plain reply." } });
                await Task.Delay(150);
                card.Update(card.Session with { Activity = (card.Session.Activity ?? new Activity()) with { Response = "# Formatted reply\n\n**More text**, a paragraph and `code`.\n\n" + string.Join("\n\n", Enumerable.Repeat("Another paragraph.", 20)) } });
                await Task.Delay(250);
                check(Bottom(), $"latest: stays pinned through plain-text to formatted re-render after {input}");
                var before = scroll.VerticalOffset;
                await LatestInput(window, scroll, input, up: true);
                check(await Until(() => scroll.VerticalOffset < before - 1.5, TimeSpan.FromSeconds(3)), $"latest: {input} scrolls up out of pin mode");
                await Still(scroll);
                await Grow(pinned: false, input);
                scroll.ChangeView(null, scroll.ScrollableHeight - 8, null, disableAnimation: true);
                await Still(scroll);
                await LatestInput(window, scroll, input, up: false);
                check(await Until(Bottom, TimeSpan.FromSeconds(3)), $"latest: real {input} reaches the bottom again");
                await Grow(pinned: true, $"returning by {input}");
            }
            await Jump();
            var departures = 0;
            void Layout(object? sender, object args)
            {
                AppLog.Write($"latest quiet layout: {scroll.VerticalOffset:0.##}/{scroll.ScrollableHeight:0.##}");
                if (!Bottom()) { departures++; }
            }
            scroll.LayoutUpdated += Layout;
            await Task.Delay(3000);
            scroll.LayoutUpdated -= Layout;
            check(Bottom() && departures == 0, $"latest: stays at the bottom for three seconds ({departures} layout departures)");
        }
        finally { view.Card = original; }
    }

    static async Task<bool> LatestInput(MainWindow window, ScrollViewer scroll, string input, bool up)
    {
        scroll.Focus(FocusState.Keyboard);
        if (input == "key")
        {
            var seen = false;
            var key = up ? VirtualKey.PageUp : VirtualKey.PageDown;
            var heard = new KeyEventHandler((_, e) => { if (e.Key == key) { seen = true; } });
            scroll.AddHandler(UIElement.PreviewKeyDownEvent, heard, true);
            try { Press(window.Handle, key, foreground: false); return await Until(() => seen, TimeSpan.FromSeconds(2)); }
            finally { scroll.RemoveHandler(UIElement.PreviewKeyDownEvent, heard); }
        }
        TrayIcon.SetForegroundWindow(window.Handle);
        var bar = Find<ScrollBar>(scroll).FirstOrDefault(b => b.Orientation == Orientation.Vertical && b.ActualHeight > 0);
        LatestPoint PointAt(FrameworkElement element)
        {
            var point = element.TransformToVisual(window.RootElement).TransformPoint(new Point(element.ActualWidth / 2, element.ActualHeight / 2));
            var scale = element.XamlRoot.RasterizationScale;
            var at = new LatestPoint { X = (int)(point.X * scale), Y = (int)(point.Y * scale) };
            if (!ClientToScreen(window.Handle, ref at) || !SetCursorPos(at.X, at.Y)) { throw new InvalidOperationException("latest: cannot position the input pointer"); }
            return at;
        }
        if (input is "arrow" or "thumb" && bar is not null) { PointAt(bar); await Task.Delay(350); }
        FrameworkElement target = scroll;
        if (input == "arrow")
        {
            target = bar is null ? throw new InvalidOperationException("latest: no vertical scrollbar")
                : Find<RepeatButton>(bar).FirstOrDefault(b => b.Name == (up ? "VerticalSmallDecrease" : "VerticalSmallIncrease") && b.ActualHeight > 0)
                    ?? throw new InvalidOperationException("latest: no actual scrollbar arrow");
        }
        if (input == "thumb") { target = bar is null ? throw new InvalidOperationException("latest: no vertical scrollbar") : Find<Thumb>(bar).FirstOrDefault(t => t.ActualHeight > 0) ?? throw new InvalidOperationException("latest: no actual scroll thumb"); }
        var at = PointAt(target);
        await Task.Delay(100);
        void Mouse(uint flags, int data = 0)
        {
            var packet = new[] { new LatestMouse { Type = 0, Flags = flags, Data = unchecked((uint)data) } };
            if (SendLatestMouse(1, packet, Marshal.SizeOf<LatestMouse>()) != 1) { throw new InvalidOperationException("latest: SendInput refused mouse input"); }
        }
        var received = false;
        var routed = input == "wheel" ? UIElement.PointerWheelChangedEvent : UIElement.PointerPressedEvent;
        var pointer = new PointerEventHandler((_, _) => received = true);
        scroll.AddHandler(routed, pointer, true);
        try
        {
            if (input == "wheel") { Mouse(0x0800, up ? 120 : -120); }
            else
            {
                Mouse(0x0002);
                try
                {
                    if (input == "thumb")
                    {
                        for (var i = 1; i <= 8; i++)
                        {
                            if (!SetCursorPos(at.X, at.Y + (up ? -1 : 1) * i * 12)) { throw new InvalidOperationException("latest: cannot drag the scroll thumb"); }
                            await Task.Delay(30);
                        }
                    }
                    else { await Task.Delay(180); }
                }
                finally { Mouse(0x0004); }
            }
            await Task.Delay(400);
            return received;
        }
        finally { scroll.RemoveHandler(routed, pointer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LatestPoint { public int X, Y; }

    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct LatestMouse
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(16)] public uint Data;
        [FieldOffset(20)] public uint Flags;
    }

    [LibraryImport("user32.dll", EntryPoint = "SendInput")]
    private static partial uint SendLatestMouse(uint count, [In] LatestMouse[] inputs, int size);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ClientToScreen(nint window, ref LatestPoint point);
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);
}
