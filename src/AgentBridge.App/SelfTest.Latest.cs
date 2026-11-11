using System.Runtime.InteropServices;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
        Action? restoreFocus = null;
        var caret = false;
        try
        {
            if (card.Stored is not { } stored) { check(false, "latest: has stored history"); return; }
            await Task.Delay(500);
            for (var n = 1; n <= 300; n++) { stored.AddLive(StoredTurn(1000 + n)); }
            var scroll = ConversationScroller(view);
            if (scroll is null || !await Until(() => scroll.ScrollableHeight > 2000, TimeSpan.FromSeconds(10)))
            { check(false, "latest: lays out 300 stored turns"); return; }
            var tabStop = scroll.IsTabStop;
            scroll.IsTabStop = true;
            restoreFocus = () => scroll.IsTabStop = tabStop;
            var next = 1400;
            bool Bottom() => scroll.ScrollableHeight - scroll.VerticalOffset <= 1.5;
            Task Jump() => LatestJump(window, view, scroll, check);
            async Task Grow(bool pinned, string input)
            {
                var before = scroll.VerticalOffset;
                var extent = scroll.ScrollableHeight;
                var bad = new List<string>();
                for (var i = 0; i < 20; i++)
                {
                    stored.AddLive(StoredTurn(next++));
                    await Task.Delay(100);
                    AppLog.Write($"latest {input} {i}: {scroll.VerticalOffset:0.##}/{scroll.ScrollableHeight:0.##}");
                    if (pinned ? !Bottom() : Math.Abs(scroll.VerticalOffset - before) > 1.5) { bad.Add($"{i}: {scroll.VerticalOffset:0}/{scroll.ScrollableHeight:0}"); }
                }
                check(scroll.ScrollableHeight > extent + 1.5, $"latest: twenty appends grow the extent after {input} ({extent:0}/{scroll.ScrollableHeight:0})");
                check(bad.Count == 0, $"latest: {(pinned ? "stays pinned" : "stays scrolled back")} through twenty appends after {input} ({string.Join("; ", bad)})");
            }
            foreach (var input in new[] { "wheel", "arrow", "thumb", "key" })
            {
                if (input != "wheel")
                {
                    card = new SessionCardViewModel(original.Session, main.Local.Cards);
                    view.Card = card;
                    stored = card.Stored ?? throw new InvalidOperationException("latest: input case has no fresh history");
                    check(await Until(() => stored.State != HistoryLoad.Loading, TimeSpan.FromSeconds(5)), "latest: fresh input history finishes loading");
                    for (var n = 0; n < 300; n++) { stored.AddLive(StoredTurn(next++)); }
                    await Still(scroll);
                }
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
            foreach (var (modifier, down, up) in new[] {
                (VirtualKey.Control, VirtualKey.PageDown, VirtualKey.Up),
                (VirtualKey.Shift, VirtualKey.Down, VirtualKey.PageUp),
                (VirtualKey.None, VirtualKey.Down, VirtualKey.Up) })
            {
                var input = modifier == VirtualKey.None ? "consumed caret" : $"inert {modifier}";
                card = new SessionCardViewModel(original.Session, main.Local.Cards);
                view.Card = card;
                stored = card.Stored ?? throw new InvalidOperationException("latest: modifier case has no fresh history");
                check(await Until(() => stored.State != HistoryLoad.Loading, TimeSpan.FromSeconds(5)), "latest: fresh modifier history finishes loading");
                for (var n = 0; n < 40; n++) { stored.AddLive(StoredTurn(next++)); }
                var paragraph = modifier == VirtualKey.None ? string.Join(" ", Enumerable.Repeat("Selectable caret text.", 30)) : "A long reply paragraph.";
                card.Update(card.Session with { Activity = (card.Session.Activity ?? new Activity()) with { Response = string.Join("\n\n", Enumerable.Repeat(paragraph, 30)) } });
                await Still(scroll);
                check(stored.Turns.Count + 40 < HistoryLimits.Kept, "latest: modifier appends remain below the history retention cap");
                if (modifier == VirtualKey.None) { await LatestCaretMode(window, enabled: true); caret = true; }
                await Jump();
                check(await LatestModifiedKey(window, scroll, up, modifier), $"latest: receives {input}+{up}");
                if (modifier == VirtualKey.None)
                {
                    var extent = scroll.ScrollableHeight;
                    card.Update(card.Session with { Activity = (card.Session.Activity ?? new Activity()) with { Response = string.Join("\n\n", Enumerable.Repeat(paragraph, 31)) } });
                    check(await Until(() => scroll.ScrollableHeight > extent + 1.5, TimeSpan.FromSeconds(3)), "latest: response extent grows after consumed caret Up");
                    check(await Until(Bottom, TimeSpan.FromSeconds(3)), "latest: consumed caret Up keeps pin through response growth before history appends");
                }
                await Grow(pinned: true, $"{input}+{up}");
                await Jump();
                var end = scroll.VerticalOffset;
                check(await LatestInput(window, scroll, "wheel", up: true), "latest: native wheel reaches modifier release setup");
                await Still(scroll);
                var before = scroll.VerticalOffset;
                check(before < end - 1.5, "latest: modifier setup really scrolls away from the end");
                check(await LatestModifiedKey(window, scroll, down, modifier), $"latest: receives {input}+{down}");
                check(Math.Abs(scroll.VerticalOffset - before) <= 0.5, $"latest: {input}+{down} does not scroll");
                card.Update(card.Session with { Activity = (card.Session.Activity ?? new Activity()) with { Response = "A shorter reply." } });
                check(await Until(() => scroll.ScrollableHeight < before - 1.5, TimeSpan.FromSeconds(3)), "latest: shorter reply shrinks below the released offset");
                scroll.ChangeView(null, before, null, disableAnimation: true);
                await Still(scroll);
                check(Bottom() && scroll.ScrollableHeight < before - 1.5, $"latest: shorter re-render clamps the released view after {input}+{down} ({before:0} to {scroll.VerticalOffset:0}/{scroll.ScrollableHeight:0})");
                await Grow(pinned: false, $"{input}+{down}, then shorter re-render");
            }
        }
        finally
        {
            try { if (caret) { await LatestCaretMode(window, enabled: false); } }
            finally { restoreFocus?.Invoke(); view.Card = original; }
        }
    }

    static async Task LatestCaretMode(MainWindow window, bool enabled)
    {
        ContentDialog? Open() => VisualTreeHelper.GetOpenPopupsForXamlRoot(window.RootElement.XamlRoot)
            .SelectMany(p => p.Child is ContentDialog d ? new[] { d } : p.Child is { } child ? Find<ContentDialog>(child).ToArray() : []).FirstOrDefault();
        if (Open() is not null) { throw new InvalidOperationException("latest: a dialog already blocks caret-mode setup"); }
        Press(window.Handle, VirtualKey.F7, foreground: false, extended: false);
        ContentDialog? dialog = null;
        if (!await Until(() => (dialog = Open()) is not null, TimeSpan.FromSeconds(3)))
        { throw new InvalidOperationException("latest: F7 did not open the native caret-browsing dialog"); }
        if (dialog!.Title is not TextBlock || dialog.Content is not StackPanel content
            || (enabled ? content.FindName("notPopAgainCheckBox") is not CheckBox || content.FindName("content") is not TextBlock : content.Children.Count != 0))
        { throw new InvalidOperationException("latest: unexpected dialog, not the requested native caret-mode toggle"); }
        var primary = Find<Button>(dialog).FirstOrDefault(b => b.Name == "PrimaryButton");
        if (primary is null || !primary.Focus(FocusState.Keyboard)) { throw new InvalidOperationException("latest: cannot focus the native caret-mode choice"); }
        var accepted = false;
        dialog.PrimaryButtonClick += (_, _) => accepted = true;
        Press(window.Handle, VirtualKey.Enter, foreground: false);
        if (!await Until(() => accepted && Open() is null, TimeSpan.FromSeconds(3)))
        { throw new InvalidOperationException("latest: native caret-mode choice did not complete"); }
    }

    static async Task LatestJump(MainWindow window, SessionView view, ScrollViewer scroll, Action<bool, string> check)
    {
        var button = ById<Button>(view, "JumpToLatest");
        check(button is { IsTabStop: true, Visibility: Visibility.Visible } && AutomationProperties.GetName(button) == "Jump to latest",
            "latest: offers a keyboard-reachable, screen-reader-named Jump to latest");
        if (button is null) { return; }
        var activated = false;
        RoutedEventHandler clicked = (_, _) => activated = true;
        button.Click += clicked;
        try
        {
            check(button.Focus(FocusState.Keyboard), "latest: keyboard can focus Jump to latest");
            Press(window.Handle, VirtualKey.Enter, foreground: false);
            check(await Until(() => activated, TimeSpan.FromSeconds(3)), "latest: Enter activates Jump to latest");
            check(await Until(() => scroll.ScrollableHeight - scroll.VerticalOffset <= 1.5, TimeSpan.FromSeconds(3)),
                $"latest: Enter reaches the bottom ({scroll.VerticalOffset:0}/{scroll.ScrollableHeight:0})");
        }
        finally { button.Click -= clicked; }
    }

    static async Task<bool> LatestModifiedKey(MainWindow window, ScrollViewer scroll, VirtualKey key, VirtualKey modifier)
    {
        RichTextBlock? text = null;
        if (modifier == VirtualKey.None)
        {
            text = Find<RichTextBlock>(scroll).FirstOrDefault(t => t.IsTextSelectionEnabled && t.ActualHeight > 60
                && t.TransformToVisual(scroll).TransformPoint(new Point()).Y >= 1
                && t.TransformToVisual(scroll).TransformPoint(new Point()).Y + t.ActualHeight < scroll.ViewportHeight - 1);
            if (text is null || !text.Focus(FocusState.Programmatic) || !ReferenceEquals(FocusManager.GetFocusedElement(scroll.XamlRoot), text))
            { throw new InvalidOperationException("latest: cannot focus fully visible selectable conversation text"); }
            var middle = text.ContentStart.GetPositionAtOffset(300, LogicalDirection.Forward) ?? throw new InvalidOperationException("latest: selectable text has no interior caret position");
            text.Select(middle, middle);
        }
        else { LatestFocus(scroll); }
        var position = text?.SelectionStart.Offset;
        var saved = new byte[256];
        if (!GetKeyboardState(saved)) { throw new InvalidOperationException("latest: cannot read keyboard modifiers"); }
        var held = saved.ToArray();
        if (modifier != VirtualKey.None) { held[(int)modifier] = held[modifier == VirtualKey.Control ? 0xA2 : 0xA0] = 0x80; }
        var seen = false;
        var finished = false;
        var taken = false;
        var observer = text is null ? null : VisualTreeHelper.GetParent(text) as UIElement;
        if (text is not null && (observer is null || observer == scroll)) { throw new InvalidOperationException("latest: no child-level key observation point"); }
        // After the text's class handler, but before the conversation handler can set Handled.
        var consumed = new KeyEventHandler((_, e) => { if (e.Key == key) { taken = e.Handled; } });
        var heard = new KeyEventHandler((_, e) => { if (e.Key == key) { seen = text is not null ? ReferenceEquals(e.OriginalSource, text) : InputKeyboardSource.GetKeyStateForCurrentThread(modifier).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down); } });
        var released = new KeyEventHandler((_, e) => { if (e.Key == key) { finished = true; } });
        scroll.AddHandler(UIElement.PreviewKeyDownEvent, heard, true);
        scroll.AddHandler(UIElement.KeyUpEvent, released, true);
        observer?.AddHandler(UIElement.KeyDownEvent, consumed, true);
        try
        {
            if (!SetKeyboardState(held)) { throw new InvalidOperationException("latest: cannot hold the keyboard modifier"); }
            Press(window.Handle, key, foreground: false);
            return await Until(() => seen && finished && (text is null || taken && text.SelectionStart.Offset != position), TimeSpan.FromSeconds(2));
        }
        finally
        {
            AppLog.Write($"latest {modifier}+{key}: source={seen}, keyUp={finished}, handled={taken}, caret={position}->{text?.SelectionStart.Offset}");
            scroll.RemoveHandler(UIElement.PreviewKeyDownEvent, heard);
            scroll.RemoveHandler(UIElement.KeyUpEvent, released);
            observer?.RemoveHandler(UIElement.KeyDownEvent, consumed);
            if (!SetKeyboardState(saved)) { throw new InvalidOperationException("latest: cannot release the keyboard modifier"); }
            if (text is not null) { LatestFocus(scroll); }
        }
    }

    static void LatestFocus(ScrollViewer scroll)
    {
        if (!scroll.Focus(FocusState.Programmatic) || !ReferenceEquals(FocusManager.GetFocusedElement(scroll.XamlRoot), scroll))
        { throw new InvalidOperationException("latest: cannot focus the transcript input target"); }
    }

    static async Task<bool> LatestInput(MainWindow window, ScrollViewer scroll, string input, bool up)
    {
        LatestFocus(scroll);
        if (input == "key")
        {
            var seen = false;
            var finished = false;
            var key = up ? VirtualKey.PageUp : VirtualKey.PageDown;
            var heard = new KeyEventHandler((_, e) => { if (e.Key == key) { seen = true; } });
            var released = new KeyEventHandler((_, e) => { if (e.Key == key) { finished = true; } });
            scroll.AddHandler(UIElement.PreviewKeyDownEvent, heard, true);
            scroll.AddHandler(UIElement.KeyUpEvent, released, true);
            try { Press(window.Handle, key, foreground: false); return await Until(() => seen && finished, TimeSpan.FromSeconds(2)); }
            finally { scroll.RemoveHandler(UIElement.PreviewKeyDownEvent, heard); scroll.RemoveHandler(UIElement.KeyUpEvent, released); }
        }
        window.Activate();
        TrayIcon.SetForegroundWindow(window.Handle);
        if (!await Until(() => GetForegroundWindow() == window.Handle, TimeSpan.FromSeconds(3))) { throw new InvalidOperationException("latest: native mouse target is not foreground"); }
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
        if (input == "thumb") { target = bar is null ? throw new InvalidOperationException("latest: no vertical scrollbar") : Find<Thumb>(bar).FirstOrDefault(t => t.Name == "VerticalThumb" && t.ActualWidth > 0 && t.ActualHeight > 0) ?? throw new InvalidOperationException("latest: no actual vertical scroll thumb"); }
        var at = PointAt(target);
        await Task.Delay(100);
        void Mouse(uint flags, int data = 0)
        {
            var packet = new[] { new LatestMouse { Type = 0, Flags = flags, Data = unchecked((uint)data) } };
            if (SendLatestMouse(1, packet, Marshal.SizeOf<LatestMouse>()) != 1) { throw new InvalidOperationException("latest: SendInput refused mouse input"); }
        }
        var received = false;
        var operated = input == "wheel";
        ScrollEventHandler operatedBar = (_, e) =>
        {
            if (input == "thumb" ? e.ScrollEventType == ScrollEventType.ThumbTrack
                : e.ScrollEventType == (up ? ScrollEventType.SmallDecrement : ScrollEventType.SmallIncrement)) { operated = true; }
        };
        if (bar is not null) { bar.Scroll += operatedBar; }
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
                    if (!await Until(() => received, TimeSpan.FromSeconds(2))) { throw new InvalidOperationException($"latest: native {input} pointer press was not routed"); }
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
            await Until(() => received && operated, TimeSpan.FromSeconds(3));
            AppLog.Write($"latest {input}: foreground={GetForegroundWindow() == window.Handle}, target={target.Name}, pointer={received}, operated={operated}");
            return received && operated;
        }
        finally { scroll.RemoveHandler(routed, pointer); if (bar is not null) { bar.Scroll -= operatedBar; } }
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
