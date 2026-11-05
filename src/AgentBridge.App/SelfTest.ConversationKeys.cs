using System.Runtime.InteropServices;
using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AgentBridge.App;

// The conversation read from the keyboard as a page (#765, #707): Page Down and Down, pressed for
// real with focus on a link in a block, scroll the text and leave focus and selection where they
// were; and no block's container is a tab stop, selected, or drawn with a background or a focus
// visual of its own.
static partial class SelfTest
{
    // What the keys get wrong, null when nothing; and how they were pressed: SendInput when the
    // window is in the foreground, else posted to the window that has the keyboard focus, as the
    // keyboard would send them; either way seen arriving at the conversation's scroller.
    static async Task<(string? Problem, string Method)> Keyed(MainWindow window, ListView list)
    {
        if (SessionView.Shallowest<ScrollViewer>(list) is not { } scroll || list.ItemsPanelRoot is not ItemsStackPanel { FirstVisibleIndex: >= 0 } panel) { return ("a scroller with blocks in view", "none"); }
        // Room above and below for a page each way, from a block in the middle of the response
        // before its run of linked paragraphs (DemoHost.HeavyLinkedRun): the block it always was.
        var middle = (list.Items.Count - DemoHost.HeavyLinkedRun) / 2;
        list.ScrollIntoView(list.Items[middle], ScrollIntoViewAlignment.Leading);
        await Task.Delay(500);
        if (scroll.ScrollableHeight - scroll.VerticalOffset < scroll.ViewportHeight + ConversationKeyboard.Line * 2) { return ($"a page below to scroll to: {scroll.ScrollableHeight - scroll.VerticalOffset:0} px of {scroll.ViewportHeight:0}", "none"); }
        // A link or a Copy button in a block in view, focused as the keyboard puts it.
        DependencyObject? stop = null;
        for (var i = panel.FirstVisibleIndex; i <= panel.LastVisibleIndex && stop is null; i++)
        {
            if (list.ContainerFromIndex(i) is { } c) { stop = FocusManager.FindFirstFocusableElement(c); }
        }
        if (stop is null || !(await FocusManager.TryFocusAsync(stop, FocusState.Keyboard)).Succeeded) { return ("a link or a Copy button in view to focus", "none"); }
        await Task.Delay(300);
        var start = scroll.VerticalOffset;
        var seen = new List<VirtualKey>();
        var heard = new KeyEventHandler((_, e) => seen.Add(e.Key));
        scroll.AddHandler(UIElement.PreviewKeyDownEvent, heard, true);
        var methods = new List<string>();
        try
        {
            foreach (var key in new[] { VirtualKey.PageDown, VirtualKey.Down })
            {
                await Still(scroll);
                var focused = FocusManager.GetFocusedElement(list.XamlRoot);
                var from = scroll.VerticalOffset;
                var method = Press(window.Handle, key, foreground: true);
                if (!await Until(() => seen.Contains(key), TimeSpan.FromSeconds(2)) && method == "SendInput")
                {
                    method = Press(window.Handle, key, foreground: false);
                    await Until(() => seen.Contains(key), TimeSpan.FromSeconds(2));
                }
                methods.Add($"{key} by {method}");
                if (!seen.Contains(key)) { return ($"{key} reaching the conversation", string.Join(", ", methods)); }
                if (!await Until(() => Math.Abs(scroll.VerticalOffset - from) >= 0.5, TimeSpan.FromSeconds(2))) { return ($"{key} scrolling the text: still at {from:0.#} px", string.Join(", ", methods)); }
                await Still(scroll);
                var now = FocusManager.GetFocusedElement(list.XamlRoot);
                if (!Equals(now, focused)) { return ($"{key} leaving focus where it was: it moved to {now?.GetType().Name ?? "nothing"}", string.Join(", ", methods)); }
                if (Selected(list) is { } selected) { return ($"{key} selecting nothing: {selected}", string.Join(", ", methods)); }
            }
            return (null, string.Join(", ", methods));
        }
        finally
        {
            scroll.RemoveHandler(UIElement.PreviewKeyDownEvent, heard);
            scroll.ChangeView(null, start, null, disableAnimation: true);
        }
    }

    // Until a scroll under way has stopped: the offset the same 150 ms apart, for 3 s at most.
    static async Task Still(ScrollViewer scroll)
    {
        for (var i = 0; i < 20; i++)
        {
            var at = scroll.VerticalOffset;
            await Task.Delay(150);
            if (Math.Abs(scroll.VerticalOffset - at) < 0.5) { return; }
        }
    }

    // Anything selected in the list, the list's or a container's own.
    static string? Selected(ListView list)
    {
        if (list.SelectedItems.Count > 0 || list.SelectedIndex >= 0) { return $"{list.SelectedItems.Count} items selected"; }
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ContainerFromIndex(i) is ListViewItem { IsSelected: true }) { return $"block {i + 1} selected"; }
        }
        return null;
    }

    // What a realised block's container draws or takes of its own: null when nothing, its content alone.
    static string? Contained(ListView list)
    {
        var realised = 0;
        for (var i = 0; i < list.Items.Count; i++)
        {
            if (list.ContainerFromIndex(i) is not ListViewItem item) { continue; }
            realised++;
            if (item.IsTabStop) { return $"block {i + 1}'s container a tab stop"; }
            if (item.UseSystemFocusVisuals) { return $"block {i + 1}'s container drawing the system focus visual"; }
            if (item.FocusState != FocusState.Unfocused) { return $"block {i + 1}'s container focused ({item.FocusState})"; }
            if (item.IsSelected) { return $"block {i + 1} selected"; }
            if (item.Background is SolidColorBrush { Color.A: > 0 } or (not null and not SolidColorBrush)) { return $"block {i + 1}'s container with a background"; }
            // The template is the content and nothing around it: no focus rectangle, border or fill to draw.
            if (VisualTreeHelper.GetChildrenCount(item) != 1 || VisualTreeHelper.GetChild(item, 0) is not ContentPresenter) { return $"block {i + 1}'s container drawn with chrome of its own"; }
        }
        return realised == 0 ? "a realised block" : null;
    }

    static string Press(nint window, VirtualKey key, bool foreground, bool extended = true)
    {
        var vk = (ushort)key;
        var scan = (ushort)MapVirtualKeyW(vk, 0);
        if (foreground && GetForegroundWindow() == window)
        {
            // The navigation keys are extended keys: without the flag, Page Down is the keypad's 3. Tab is not.
            var flag = extended ? 0x1u : 0u;
            var inputs = new[]
            {
                new KeyInput { Type = 1, Vk = vk, Scan = scan, Flags = flag },
                new KeyInput { Type = 1, Vk = vk, Scan = scan, Flags = flag | 0x2 },
            };
            if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<KeyInput>()) == inputs.Length) { return "SendInput"; }
        }
        var to = GetFocus() is var focus && focus != 0 ? focus : window;
        var extendedBit = extended ? (nint)(1 << 24) : 0;
        var lParam = 1 | ((nint)scan << 16) | extendedBit;
        PostMessageW(to, 0x0100, (nint)vk, lParam);
        PostMessageW(to, 0x0101, (nint)vk, lParam | (1 << 30) | unchecked((nint)(1u << 31)));
        return "PostMessage";
    }

    // INPUT with its KEYBDINPUT, as 64-bit Windows lays it out (the app is x64 and arm64 only).
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    struct KeyInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort Vk;
        [FieldOffset(10)] public ushort Scan;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public nint Extra;
    }

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, [In] KeyInput[] inputs, int size);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetFocus();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    private static partial uint MapVirtualKeyW(uint code, uint type);
}
