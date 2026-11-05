using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.System;
using Windows.Storage.Streams;

namespace AgentBridge.App;

// The conversation as a list (#765): the block at the top of the viewport stays there through a
// live resize, the conversation drawn alike before the drag and after it, and a screen reader is
// given every block as an item of the list, one off screen included (#707).
static partial class SelfTest
{
    sealed record Shot(int Index, double Top, byte[] Pixels, int Width, int Height);

    // The conversation's own scroller, inside its list.
    static ScrollViewer? ConversationScroller(SessionView view) => ById<ListView>(view, "Conversation") is { } list ? SessionView.Shallowest<ScrollViewer>(list) : null;

    // A block in the middle of the response put at the top of the viewport, a little of it scrolled
    // past, and the conversation as drawn then.
    static async Task<Shot?> Anchor(ListView list, string outFile)
    {
        if (list.Items.Count < 3 || SessionView.Shallowest<ScrollViewer>(list) is not { } scroll) { return null; }
        // In the middle of the response before its tall block and run of linked paragraphs
        // (DemoHost.HeavyMiddle): the block it always was.
        var index = DemoHost.HeavyMiddle;
        if (index >= list.Items.Count) { return null; }
        list.ScrollIntoView(list.Items[index], ScrollIntoViewAlignment.Leading);
        if (!await Until(() => list.ContainerFromIndex(index) is FrameworkElement { ActualHeight: > 0 }, TimeSpan.FromSeconds(5))) { return null; }
        await Task.Delay(300);
        var by = Math.Min(24, ((FrameworkElement)list.ContainerFromIndex(index)).ActualHeight / 2);
        scroll.ChangeView(null, scroll.VerticalOffset + by, null, disableAnimation: true);
        await Task.Delay(700);
        if (list.ContainerFromIndex(index) is not FrameworkElement block) { return null; }
        var top = block.TransformToVisual(scroll).TransformPoint(default).Y;
        var (pixels, width, height) = await Shoot(list, Path.ChangeExtension(outFile, ".resize.before.png"));
        return new Shot(index, top, pixels, width, height);
    }

    // What moved since Anchor: the block's place in the layout (within 0.5 DIP), the proof; or the
    // column drawn differently beyond anti-aliasing (PictureDifference), the backup. Null when
    // neither did.
    static async Task<string?> Anchored(ListView list, Shot before, string outFile)
    {
        if (SessionView.Shallowest<ScrollViewer>(list) is not { } scroll) { return "a scroller"; }
        // Until the block has been where it was for three looks in a row, 3 s at most, rather than a
        // fixed wait: the placement is still checked exactly below.
        var steady = 0;
        await Until(() => (list.ContainerFromIndex(before.Index) is FrameworkElement b && Math.Abs(b.TransformToVisual(scroll).TransformPoint(default).Y - before.Top) <= 0.5 ? ++steady : steady = 0) >= 3, TimeSpan.FromSeconds(3));
        if (list.ContainerFromIndex(before.Index) is not FrameworkElement block) { return $"block {before.Index} still drawn"; }
        var problems = new List<string>();
        var top = block.TransformToVisual(scroll).TransformPoint(default).Y;
        if (Math.Abs(top - before.Top) > 0.5) { problems.Add($"block {before.Index} at {top:0.#} px, not {before.Top:0.#}"); }
        var (pixels, width, height) = await Shoot(list, Path.ChangeExtension(outFile, ".resize.after.png"));
        if (width != before.Width || height != before.Height) { problems.Add($"drawn {width}×{height}, not {before.Width}×{before.Height}"); }
        else
        {
            // The reading column only: the scroll bar at the pane's edge comes and goes with scrolling.
            var scale = width / list.ActualWidth;
            var measure = Math.Min(list.ActualWidth, (double)Application.Current.Resources["AbLayoutReadingMax"]);
            var from = (int)Math.Floor((list.ActualWidth - measure) / 2 * scale);
            var to = (int)Math.Ceiling(Math.Min((list.ActualWidth + measure) / 2, list.ActualWidth - 16) * scale);
            var difference = PictureDifference.Of(before.Pixels, pixels, width, height, from, to);
            AppLog.Write($"self-test: resize, {difference.Differing} pixels of the column more than {PictureDifference.Tolerance} apart in a channel, by up to {difference.Largest}");
            if (!difference.Alike) { problems.Add($"{difference.Differing} pixels of the column more than {PictureDifference.Tolerance} apart in a channel (up to {PictureDifference.MostDiffering} allowed), by up to {difference.Largest} in a channel (up to {PictureDifference.Glaring} allowed)"); }
        }
        return problems.Count > 0 ? string.Join(", ", problems) : null;
    }

    // What a screen reader is given wrong of the response's items: every block a list item numbered
    // 1 to N in a set of N, none -1, and every list item a block's, so the header and the footer are
    // outside the set. The items are walked as a screen reader walks a virtualised list, the list's
    // ItemContainer pattern asked for the item after the last (FindItemByProperty with no property),
    // and one not realised yet realised through the VirtualizedItem pattern of what it found, since
    // WinUI numbers an item only once it has a container (#829, B's realisation contract). Null when
    // it is given all of them right, with how many blocks were realised that way.
    static async Task<(string? Problem, int Realised, double Seconds)> Listed(ListView list)
    {
        var count = list.Items.Count;
        if (count == 0 || FrameworkElementAutomationPeer.CreatePeerForElement(list) is not ItemsControlAutomationPeer peer) { return ("a list to read", 0, 0); }
        if (peer.GetPattern(PatternInterface.ItemContainer) is not IItemContainerProvider items) { return ("the list's items found as a screen reader finds them", 0, 0); }
        var problems = new List<string>();
        var realised = 0;
        var walk = System.Diagnostics.Stopwatch.StartNew();
        var finder = new ProviderPeer();
        IRawElementProviderSimple? after = null;
        var walked = 0;
        for (var i = 0; i < count; i++)
        {
            var found = items.FindItemByProperty(after, null, null);
            if (found is null) { problems.Add($"block {i + 1} found after block {i}"); break; }
            after = found;
            walked++;
            if (finder.Of(found) is not ItemAutomationPeer item || !ReferenceEquals(item.Item, list.Items[i])) { problems.Add($"block {i + 1} found in its place"); break; }
            if (item.GetAutomationControlType() != AutomationControlType.ListItem) { problems.Add($"block {i + 1} a list item: {item.GetAutomationControlType()}"); break; }
            if (list.ContainerFromIndex(i) is null)
            {
                if (item.GetPattern(PatternInterface.VirtualizedItem) is not IVirtualizedItemProvider virtualised) { problems.Add($"block {i + 1} realisable"); break; }
                virtualised.Realize();
                list.UpdateLayout();
                realised++;
                // A frame at a time, not Until's 200 ms, so a walk of some 200 blocks stays short
                // and the list fills its cache ahead between steps.
                var waited = 0;
                while (list.ContainerFromIndex(i) is null && ++waited < 300) { await Task.Delay(16); }
                if (list.ContainerFromIndex(i) is null) { problems.Add($"block {i + 1} realised"); break; }
            }
            if (item.GetPositionInSet() != i + 1 || item.GetSizeOfSet() != count)
            {
                problems.Add($"block {i + 1} {i + 1} of {count}: {item.GetPositionInSet()} of {item.GetSizeOfSet()}");
                break;
            }
            // A hang guard, not a bar: a walk this long is stuck.
            if (walk.Elapsed > TimeSpan.FromSeconds(30)) { problems.Add($"every block numbered within 30 s: block {i + 1} reached"); break; }
        }
        if (problems.Count == 0 && walked == count && items.FindItemByProperty(after, null, null) is { } extra)
        {
            problems.Add($"no item after the last block: \"{Clipped(finder.Of(extra)?.GetName() ?? "?")}\"");
        }
        var listed = (peer.GetChildren() ?? []).Where(c => c.GetAutomationControlType() == AutomationControlType.ListItem).ToList();
        if (listed.FirstOrDefault(c => c is not ItemAutomationPeer { Item: ConversationBlock }) is { } stray)
        {
            problems.Add($"the header and footer outside the set: \"{Clipped(stray.GetName())}\" a list item");
        }
        else if (listed.Count > count || listed.Any(c => c.GetSizeOfSet() != count))
        {
            problems.Add($"{listed.Count} list items for {count} blocks, each of {count}");
        }
        if (list.ItemsPanelRoot is ItemsStackPanel { FirstVisibleIndex: >= 0 } panel && list.ContainerFromIndex(panel.FirstVisibleIndex) is ListViewItem shown)
        {
            if (ItemSaid(peer, shown, panel.FirstVisibleIndex, count) is { } wrong) { problems.Add(wrong); }
        }
        else { problems.Add("a block in view"); }
        return (problems.Count > 0 ? string.Join(", ", problems) : null, realised, walk.Elapsed.TotalSeconds);
    }

    // What a screen reader can't reach of the response: the last block, off screen and unrealised,
    // named as no other block is, found by that name, and realised and brought into view through the
    // peer the search found, as a reader does. Null when it can.
    static async Task<string?> Reachable(ListView list)
    {
        var count = list.Items.Count;
        if (count == 0 || FrameworkElementAutomationPeer.CreatePeerForElement(list) is not ItemsControlAutomationPeer peer) { return "a list to read"; }
        var problems = new List<string>();
        var last = (ConversationBlock)list.Items[count - 1];
        if (list.ContainerFromIndex(count - 1) is not null) { problems.Add("the last block off screen to begin with"); }
        if (list.Items.Count(b => b is ConversationBlock c && c.Name == last.Name) != 1) { problems.Add("the last block named as no other block is"); }
        var found = peer.GetPattern(PatternInterface.ItemContainer) is IItemContainerProvider items
            ? items.FindItemByProperty(null, AutomationElementIdentifiers.NameProperty, last.Name) : null;
        if ((found is null ? null : new ProviderPeer().Of(found)) is not ItemAutomationPeer foundPeer || !ReferenceEquals(foundPeer.Item, last))
        {
            problems.Add("the last block found by its name");
            return string.Join(", ", problems);
        }
        if (foundPeer.GetPattern(PatternInterface.VirtualizedItem) is IVirtualizedItemProvider virtualised) { virtualised.Realize(); }
        else { problems.Add("the last block realisable"); }
        if (!await Until(() => list.ContainerFromIndex(count - 1) is ListViewItem, TimeSpan.FromSeconds(5))) { problems.Add("the last block realised"); }
        else
        {
            if (foundPeer.GetPattern(PatternInterface.ScrollItem) is IScrollItemProvider scrolled) { scrolled.ScrollIntoView(); }
            // Until it is in view, 3 s at most, rather than a fixed wait; still checked below.
            await Until(() => list.ContainerFromIndex(count - 1) is ListViewItem l && SessionView.Shallowest<ScrollViewer>(list) is { } s
                && l.TransformToVisual(s).TransformPoint(default).Y is var y && y < s.ViewportHeight && y + l.ActualHeight > 0, TimeSpan.FromSeconds(3));
            if (ItemSaid(peer, list.ContainerFromIndex(count - 1) as ListViewItem, count - 1, count) is { } wrong) { problems.Add(wrong); }
            else if (list.ContainerFromIndex(count - 1) is ListViewItem lastShown && SessionView.Shallowest<ScrollViewer>(list) is { } scroll
                && lastShown.TransformToVisual(scroll).TransformPoint(default).Y is var top && (top >= scroll.ViewportHeight || top + lastShown.ActualHeight <= 0))
            {
                problems.Add($"the last block brought into view: at {top:0.#} px of {scroll.ViewportHeight:0.#}");
            }
        }
        return problems.Count > 0 ? string.Join(", ", problems) : null;
    }

    // As a screen reader is given it: the list's peer for the item, which works out its place in
    // the set, not the container's own, which knows nothing of the list.
    static string? ItemSaid(ItemsControlAutomationPeer peer, ListViewItem? item, int index, int count)
    {
        var name = item?.Content is ConversationBlock b ? b.Name : null;
        var itemPeer = item?.Content is { } content ? peer.CreateItemAutomationPeer(content) : null;
        return itemPeer is null ? $"block {index + 1} as an item"
            : itemPeer.GetPositionInSet() != index + 1 || itemPeer.GetSizeOfSet() != count || itemPeer.GetName() != name
                ? $"block {index + 1} said as \"{Clipped(itemPeer.GetName())}\", {itemPeer.GetPositionInSet()} of {itemPeer.GetSizeOfSet()}"
                : null;
    }

    static string Clipped(string s) => s.Length > 40 ? s[..40] + "…" : s;

    // From the provider a search hands a screen reader back to the peer it stands for, so the check
    // goes on with what was found. PeerFromProvider is protected: only a peer can ask.
    sealed class ProviderPeer() : FrameworkElementAutomationPeer(new Border())
    {
        public AutomationPeer? Of(IRawElementProviderSimple provider) => PeerFromProvider(provider);
    }
    // What Tab gets wrong in the response: from the last link or Copy button the list has realised,
    // Tab goes on to the first in the next block that has one, which the list had not realised,
    // and lands in the block, never on its container. The list's cache is cut to the viewport while
    // it looks, so the next block with a stop is off the realised end. Null when Tab gets it right.
    static async Task<string?> Tabbed(ListView list)
    {
        if (list.ItemsPanelRoot is not ItemsStackPanel panel || list.ItemsSource is not IList<ConversationBlock> blocks || list.XamlRoot?.Content is not { } root) { return "a list of blocks"; }
        var cache = panel.CacheLength;
        try
        {
            panel.CacheLength = 0;
            await Task.Delay(500);
            // Focus on the last stop realised may scroll it into view and realise more: then again.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                var end = Math.Max(panel.LastVisibleIndex, 0);
                while (end + 1 < blocks.Count && list.ContainerFromIndex(end + 1) is not null) { end++; }
                var from = -1;
                for (var i = end; i >= 0 && from < 0; i--)
                {
                    if (blocks[i].Focusable && list.ContainerFromIndex(i) is { } c && FocusManager.FindLastFocusableElement(c) is not null) { from = i; }
                }
                if (from < 0) { return "a realised block with a link or a Copy button"; }
                var next = Enumerable.Range(from + 1, blocks.Count - from - 1).FirstOrDefault(i => blocks[i].Focusable, -1);
                if (next < 0) { return $"a block with a stop after block {from + 1}"; }
                await FocusManager.TryFocusAsync(FocusManager.FindLastFocusableElement(list.ContainerFromIndex(from)), FocusState.Keyboard);
                await Task.Delay(300);
                if (list.ContainerFromIndex(next) is not null) { continue; }
                FocusManager.TryMoveFocus(FocusNavigationDirection.Next, new FindNextElementOptions { SearchRoot = root });
                // In the block, on a stop inside it. Focus seen on a block's container or the list at
                // any point fails the check, even should it move on into the block after.
                object? container = null;
                var landed = await Until(() =>
                {
                    var f = FocusManager.GetFocusedElement(list.XamlRoot);
                    if (f is ListViewItem or ListView) { container = f; return true; }
                    return BlockOf(list, f) == next;
                }, TimeSpan.FromSeconds(3));
                if (container is not null) { return $"focus on a block's container: {container.GetType().Name}"; }
                if (landed)
                {
                    // On the block's first stop, not merely in it, the block now realised.
                    var on = FocusManager.GetFocusedElement(list.XamlRoot);
                    var first = list.ContainerFromIndex(next) is { } realised ? FocusManager.FindFirstFocusableElement(realised) : null;
                    return first is null ? $"block {next + 1} realised with a stop in it"
                        : ReferenceEquals(on, first) ? null
                        : $"Tab into block {next + 1} on its first stop ({first.GetType().Name}), not {on?.GetType().Name ?? "nothing"}";
                }
                var focused = FocusManager.GetFocusedElement(list.XamlRoot);
                return $"Tab from block {from + 1} to block {next + 1}: it went to {(BlockOf(list, focused) is { } at ? $"block {at + 1}" : focused?.GetType().Name ?? "nothing")}";
            }
            return "the next block with a stop unrealised when Tab was pressed";
        }
        finally { panel.CacheLength = cache; }
    }

    // Tab onto a block not wholly in view brings its start to the leading edge; onto one wholly in
    // view, nothing moves (ui.md §6.4; B and the creative director on #829). For a Copy button, as
    // TabbedLink is for a link: a code block well down the response is put first wholly in view
    // near the bottom of the view, then across its bottom edge, laid out both times; the last stop
    // of the block before it with one is focused with the view held, and Tab pressed as the
    // keyboard presses it. Each step is logged; a view that can't be put so fails with "setup: ...",
    // never skips. Null, and how Tab was pressed, when right.
    static async Task<(string? Problem, string Method)> TabbedInView(MainWindow window, ListView list)
    {
        if (list.ItemsSource is not IList<ConversationBlock> blocks || SessionView.Shallowest<ScrollViewer>(list) is not { } scroll) { return ("setup: a list of blocks with a scroller", "none"); }
        static void Log(string step) => AppLog.Write($"self-test: Copy Tab, {step}");
        static double Top(UIElement e, UIElement view) => e.TransformToVisual(view).TransformPoint(default).Y;
        var code = Enumerable.Range(0, blocks.Count).FirstOrDefault(i => i >= 20 && blocks[i].Block is MdCode && Enumerable.Range(0, i).Any(j => blocks[j].Focusable), -1);
        if (code < 0) { return ("setup: a code block after block 20 with a stop before it", "none"); }
        var from = Enumerable.Range(0, code).Last(j => blocks[j].Focusable);
        var method = "none";
        foreach (var wholly in new[] { true, false })
        {
            var where = wholly ? "wholly in view" : "across the view's bottom edge";
            list.ScrollIntoView(blocks[code], ScrollIntoViewAlignment.Leading);
            await Still(scroll);
            if (list.ContainerFromIndex(code) is not FrameworkElement placed) { return ($"setup: block {code + 1} realised at the leading edge", method); }
            var (height, view) = (placed.ActualHeight, scroll.ViewportHeight);
            var at = scroll.VerticalOffset + Top(placed, scroll) - (wholly ? view - height - 8 : view - height / 2);
            if (height + 16 > view || at < 0) { return ($"setup: block {code + 1} ({height:0.#} px) placed {where} in a {view:0.#} px view", method); }
            scroll.ChangeView(null, at, null, disableAnimation: true);
            await Still(scroll);
            if (list.ContainerFromIndex(code) is not FrameworkElement block || list.ContainerFromIndex(from) is not FrameworkElement before) { return ($"setup: blocks {from + 1} and {code + 1} realised", method); }
            var top = Top(block, scroll);
            if (wholly != ConversationKeyboard.WhollyInView(top, block.ActualHeight, scroll.ViewportHeight) || (!wholly && (top < 0 || top >= scroll.ViewportHeight))) { return ($"setup: block {code + 1} {where}: its top {top:0.#} px, {block.ActualHeight:0.#} px tall, in a {scroll.ViewportHeight:0.#} px view", method); }
            if (FocusManager.FindLastFocusableElement(before) is not DependencyObject stop) { return ($"setup: a stop in block {from + 1}", method); }
            // Focus taken without the view moving to it: the content where it was on screen, measured
            // before and after. The offset the view was asked for is no measure of that, the list
            // re-estimating its blocks' heights as it scrolls (run 41994389656).
            var held = new TypedEventHandler<UIElement, BringIntoViewRequestedEventArgs>((_, e) => e.Handled = true);
            var (offsetBefore, topBefore) = (scroll.VerticalOffset, Top(block, scroll));
            bool focused;
            before.BringIntoViewRequested += held;
            try
            {
                focused = (await FocusManager.TryFocusAsync(stop, FocusState.Keyboard)).Succeeded;
                await Still(scroll);
            }
            finally { before.BringIntoViewRequested -= held; }
            var (offset, topAfter) = (scroll.VerticalOffset, Top(block, scroll));
            Log($"block {code + 1} {where}, its top {topBefore:0.#} px before focus and {topAfter:0.#} px after, focus on the last stop of block {from + 1}: {(focused ? "taken" : "refused")}, the view at {offsetBefore:0.#} px then {offset:0.#} px");
            if (!focused || !ReferenceEquals(FocusManager.GetFocusedElement(list.XamlRoot), stop)) { return ($"setup: focus on the last stop of block {from + 1}", method); }
            if (Math.Abs(offset - offsetBefore) > 0.5 || Math.Abs(topAfter - topBefore) > 0.5) { return ($"setup: block {code + 1} left where it was on screen while block {from + 1} took focus: its top {topBefore:0.#} px, then {topAfter:0.#} px", method); }

            // Tab, pressed as the keyboard presses it.
            var seen = false;
            var heard = new KeyEventHandler((_, e) => seen |= e.Key == VirtualKey.Tab);
            scroll.AddHandler(UIElement.PreviewKeyDownEvent, heard, true);
            try
            {
                method = Press(window.Handle, VirtualKey.Tab, foreground: true, extended: false);
                if (!await Until(() => seen, TimeSpan.FromSeconds(2)) && method == "SendInput")
                {
                    method = Press(window.Handle, VirtualKey.Tab, foreground: false, extended: false);
                    await Until(() => seen, TimeSpan.FromSeconds(2));
                }
            }
            finally { scroll.RemoveHandler(UIElement.PreviewKeyDownEvent, heard); }
            if (!seen) { return ($"Tab reaching the conversation ({method})", method); }
            var landed = await Until(() => BlockOf(list, FocusManager.GetFocusedElement(list.XamlRoot)) == code, TimeSpan.FromSeconds(3));
            await Still(scroll);
            var on = FocusManager.GetFocusedElement(list.XamlRoot);
            var shown = list.ContainerFromIndex(code) as FrameworkElement;
            var first = shown is null ? null : FocusManager.FindFirstFocusableElement(shown);
            var now = shown is null ? double.NaN : Top(shown, scroll);
            Log($"Tab pressed by {method}: focus on {on?.GetType().Name ?? "nothing"} in block {(BlockOf(list, on) is { } b ? b + 1 : 0)}, block {code + 1}'s top {now:0.#} px, the view moved {scroll.VerticalOffset - offset:0.#} px");
            if (!landed || first is not Button || !ReferenceEquals(on, first)) { return ($"Tab from block {from + 1} onto the Copy button of block {code + 1} {where}: on {on?.GetType().Name ?? "nothing"} in {(BlockOf(list, on) is { } to ? $"block {to + 1}" : "no block")}", method); }
            if (wholly && Math.Abs(scroll.VerticalOffset - offset) > 0.5) { return ($"block {code + 1} wholly in view left where it was: the view moved {scroll.VerticalOffset - offset:0.#} px", method); }
            if (!wholly && Math.Abs(now) > 1.5) { return ($"block {code + 1} across the view's bottom edge brought to the leading edge: its top {now:0.#} px from the view's", method); }
        }
        return (null, method);
    }

    // A block wholly in view doesn't move when Shift+Tab lands on its Copy button, even one near the
    // view's top edge (ui.md §6.4; B and Copilot 5783106122 on #829): the focus manager pads a
    // control by the larger of 20 px and its focus visual's overhang each side (BringIntoViewHandler
    // .cpp:13 and 619-656; 20 for Copy), and the button is 12 px down its block, so with the block's
    // top within 8 px of the view's the padding alone scrolled it. A code block well down the
    // response is put 3 px below the top, wholly in view, and Shift+Tab is pressed from the first stop
    // of the block after it with one (focused with the view held, measured before and after).
    // Only Shift+Tab: Tab forward onto that Copy comes from a stop above the view, the case waiting
    // on B and the creative director (6656138093 on #829); and the bottom edge can't be reached at
    // all, since Copy is 32 px with at least 2 + 18 + 8 = 28 px of its block below it (the row
    // spacing, a line of code, the box's bottom padding), so a wholly visible block's Copy is never
    // within 20 px of the view's bottom. Each step is logged; a view that can't be put so fails with
    // "setup: ...", never skips. Null, and how the keys were pressed, when right.
    static async Task<(string? Problem, string Method)> TabbedNearEdge(MainWindow window, ListView list)
    {
        if (list.ItemsSource is not IList<ConversationBlock> blocks || SessionView.Shallowest<ScrollViewer>(list) is not { } scroll) { return ("setup: a list of blocks with a scroller", "none"); }
        static void Log(string step) => AppLog.Write($"self-test: Copy Tab near the edge, {step}");
        static double Top(UIElement e, UIElement view) => e.TransformToVisual(view).TransformPoint(default).Y;
        const string key = "Shift+Tab";
        var code = Enumerable.Range(0, blocks.Count).FirstOrDefault(i => i >= 20 && blocks[i].Block is MdCode, -1);
        if (code < 0) { return ("setup: a code block after block 20", "none"); }
        var by = Enumerable.Range(code + 1, Math.Max(0, blocks.Count - code - 1)).FirstOrDefault(j => blocks[j].Focusable, -1);
        if (by < 0) { return ($"setup: a block with a stop after block {code + 1}", "none"); }
        var method = "none";
        list.ScrollIntoView(blocks[code], ScrollIntoViewAlignment.Leading);
        await Still(scroll);
        if (list.ContainerFromIndex(code) is not FrameworkElement placed) { return ($"setup: block {code + 1} realised at the leading edge", method); }
        scroll.ChangeView(null, Math.Max(0, scroll.VerticalOffset + Top(placed, scroll) - 3), null, disableAnimation: true);
        await Still(scroll);
        if (list.ContainerFromIndex(code) is not FrameworkElement block || list.ContainerFromIndex(by) is not FrameworkElement leaving) { return ($"setup: blocks {by + 1} and {code + 1} realised", method); }
        if (FocusManager.FindFirstFocusableElement(block) is not Button copy) { return ($"setup: a Copy button in block {code + 1}", method); }
        var (top, copyTop) = (Top(block, scroll), Top(copy, scroll));
        // Wholly in view, and near enough the top that the focus manager's padding would scroll it.
        if (!ConversationKeyboard.WhollyInView(top, block.ActualHeight, scroll.ViewportHeight) || copyTop - 20 > -0.5) { return ($"setup: for {key}, block {code + 1} wholly in view with its Copy button within 20 px of the view's top: its top {top:0.#} px, Copy's {copyTop:0.#} px, {block.ActualHeight:0.#} px tall in a {scroll.ViewportHeight:0.#} px view", method); }
        if (FocusManager.FindFirstFocusableElement(leaving) is not { } stop) { return ($"setup: a stop in block {by + 1}", method); }
        var held = new TypedEventHandler<UIElement, BringIntoViewRequestedEventArgs>((_, e) => e.Handled = true);
        var offsetBefore = scroll.VerticalOffset;
        bool focused;
        leaving.BringIntoViewRequested += held;
        try
        {
            focused = (await FocusManager.TryFocusAsync(stop, FocusState.Keyboard)).Succeeded;
            await Still(scroll);
        }
        finally { leaving.BringIntoViewRequested -= held; }
        var (offset, topAfter) = (scroll.VerticalOffset, Top(block, scroll));
        Log($"for {key}, block {code + 1} wholly in view, its top {top:0.#} px before focus and {topAfter:0.#} px after, Copy's {copyTop:0.#} px, focus on the first stop of block {by + 1}: {(focused ? "taken" : "refused")}, the view at {offsetBefore:0.#} px then {offset:0.#} px");
        if (!focused || !ReferenceEquals(FocusManager.GetFocusedElement(list.XamlRoot), stop)) { return ($"setup: focus on a stop in block {by + 1}", method); }
        if (Math.Abs(offset - offsetBefore) > 0.5 || Math.Abs(topAfter - top) > 0.5) { return ($"setup: block {code + 1} left where it was on screen while block {by + 1} took focus: its top {top:0.#} px, then {topAfter:0.#} px", method); }

        // The key, pressed as the keyboard presses it.
        var seen = false;
        var heard = new KeyEventHandler((_, e) => seen |= e.Key == VirtualKey.Tab);
        scroll.AddHandler(UIElement.PreviewKeyDownEvent, heard, true);
        try
        {
            method = Press(window.Handle, VirtualKey.Tab, foreground: true, extended: false, shift: true);
            if (!await Until(() => seen, TimeSpan.FromSeconds(2)) && method == "SendInput")
            {
                method = Press(window.Handle, VirtualKey.Tab, foreground: false, extended: false, shift: true);
                await Until(() => seen, TimeSpan.FromSeconds(2));
            }
        }
        finally
        {
            scroll.RemoveHandler(UIElement.PreviewKeyDownEvent, heard);
            Unshift(method);
        }
        if (!seen) { return ($"{key} reaching the conversation ({method})", method); }
        var landed = await Until(() => BlockOf(list, FocusManager.GetFocusedElement(list.XamlRoot)) == code, TimeSpan.FromSeconds(3));
        await Still(scroll);
        var on = FocusManager.GetFocusedElement(list.XamlRoot);
        var shown = list.ContainerFromIndex(code) as FrameworkElement;
        var now = shown is null ? double.NaN : Top(shown, scroll);
        var moved = scroll.VerticalOffset - offset;
        Log($"{key} pressed by {method}: focus on {on?.GetType().Name ?? "nothing"} in block {(BlockOf(list, on) is { } b ? b + 1 : 0)}, block {code + 1}'s top {now:0.#} px, the view moved {moved:0.#} px");
        if (!landed || shown is null || !ReferenceEquals(on, FocusManager.FindFirstFocusableElement(shown))) { return ($"{key} from block {by + 1} onto the Copy button of block {code + 1}: on {on?.GetType().Name ?? "nothing"} in {(BlockOf(list, on) is { } to ? $"block {to + 1}" : "no block")}", method); }
        if (Math.Abs(moved) > 0.5 || Math.Abs(now - topAfter) > 0.5) { return ($"block {code + 1}, wholly in view near the top, left where it was by {key} onto its Copy button: the view moved {moved:0.#} px, its top {topAfter:0.#} px then {now:0.#} px", method); }
        return (null, method);
    }

    // Tab only ever moves the view for the stop it lands on (ui.md §6.4; the creative director on
    // #829, #881): focus on a stop scrolled out of view, more than the focus manager's 20 px padding
    // above it, and Tab onto the Copy button of a block wholly in view near the top edge leaves the
    // view where it is. The stop focus is leaving must not pull the view back (CI run 42012618845:
    // block 17's Copy 295 px above the view, Tab onto block 28's, the view moved -315 px). A code block
    // well down the response is put 3 px below the top; the last stop before it, in a block above
    // the view, is focused with the view held, measured before and after, and Tab pressed as the
    // keyboard presses it. Every BringIntoViewRequested that reaches the conversation meanwhile is
    // logged (BringIntoViewLog). A code block whose preceding stop is too near is passed over for the
    // next; none right fails with "setup: ...", never skips. Null, and how Tab was pressed, when right.
    static async Task<(string? Problem, string Method)> TabbedFromOffScreen(MainWindow window, ListView list)
    {
        if (list.ItemsSource is not IList<ConversationBlock> blocks || SessionView.Shallowest<ScrollViewer>(list) is not { } scroll) { return ("setup: a list of blocks with a scroller", "none"); }
        static void Log(string step) => AppLog.Write($"self-test: Tab from a stop out of view, {step}");
        static double Top(UIElement e, UIElement view) => e.TransformToVisual(view).TransformPoint(default).Y;
        var candidates = Enumerable.Range(20, Math.Max(0, blocks.Count - 20))
            .Where(i => blocks[i].Block is MdCode && Enumerable.Range(0, i).Any(j => blocks[j].Focusable)).Take(4).ToList();
        if (candidates.Count == 0) { return ("setup: a code block after block 20 with a stop before it", "none"); }
        var method = "none";
        var setup = "";
        foreach (var code in candidates)
        {
            var from = Enumerable.Range(0, code).Last(j => blocks[j].Focusable);
            list.ScrollIntoView(blocks[code], ScrollIntoViewAlignment.Leading);
            await Still(scroll);
            if (list.ContainerFromIndex(code) is not FrameworkElement placed) { setup += $"; block {code + 1} not realised at the leading edge"; continue; }
            scroll.ChangeView(null, Math.Max(0, scroll.VerticalOffset + Top(placed, scroll) - 3), null, disableAnimation: true);
            await Still(scroll);
            if (list.ContainerFromIndex(code) is not FrameworkElement block || list.ContainerFromIndex(from) is not FrameworkElement before) { setup += $"; blocks {from + 1} and {code + 1} not both realised"; continue; }
            if (FocusManager.FindFirstFocusableElement(block) is not Button copy) { setup += $"; no Copy button in block {code + 1}"; continue; }
            if (FocusManager.FindLastFocusableElement(before) is not DependencyObject stop) { setup += $"; no stop in block {from + 1}"; continue; }
            var (top, copyTop) = (Top(block, scroll), Top(copy, scroll));
            var stopBottom = stop is FrameworkElement s ? Top(s, scroll) + s.ActualHeight : Top(before, scroll) + before.ActualHeight;
            Log($"block {code + 1} ({block.ActualHeight:0.#} px tall) top {top:0.#} px, Copy's {copyTop:0.#} px, the last stop of block {from + 1} ({stop.GetType().Name}) ends {stopBottom:0.#} px from the view's top, in a {scroll.ViewportHeight:0.#} px view");
            if (!ConversationKeyboard.WhollyInView(top, block.ActualHeight, scroll.ViewportHeight) || stopBottom > -30) { setup += $"; block {code + 1} wholly in view with the last stop of block {from + 1} 30 px or more above the view: top {top:0.#} px, that stop ends {stopBottom:0.#} px"; continue; }
            // The case is block 28's, 3 px below the top: any other place is a different test.
            if (Math.Abs(top - 3) > 0.5) { setup += $"; block {code + 1} measured at {top:0.#} px from the view's top, not 3 px"; continue; }

            var held = new TypedEventHandler<UIElement, BringIntoViewRequestedEventArgs>((_, e) => e.Handled = true);
            var (offsetBefore, topBefore) = (scroll.VerticalOffset, top);
            bool focused;
            before.BringIntoViewRequested += held;
            try
            {
                focused = (await FocusManager.TryFocusAsync(stop, FocusState.Keyboard)).Succeeded;
                await Still(scroll);
            }
            finally { before.BringIntoViewRequested -= held; }
            var (offset, topAfter) = (scroll.VerticalOffset, Top(block, scroll));
            Log($"focus on the last stop of block {from + 1}: {(focused ? "taken" : "refused")}, its FocusState {SessionView.StateOf(stop)?.ToString() ?? "none"}, the view at {offsetBefore:0.#} px then {offset:0.#} px, block {code + 1}'s top {topBefore:0.#} px then {topAfter:0.#} px");
            if (!focused || !ReferenceEquals(FocusManager.GetFocusedElement(list.XamlRoot), stop)) { return ($"setup: focus on the last stop of block {from + 1}", method); }
            if (Math.Abs(offset - offsetBefore) > 0.5 || Math.Abs(topAfter - topBefore) > 0.5) { return ($"setup: block {code + 1} left where it was on screen while block {from + 1} took focus: its top {topBefore:0.#} px, then {topAfter:0.#} px", method); }

            // Tab, pressed as the keyboard presses it, every request to bring something into view logged.
            var seen = false;
            var heard = new KeyEventHandler((_, e) => seen |= e.Key == VirtualKey.Tab);
            using var requests = new BringIntoViewLog(list, scroll, Log);
            scroll.AddHandler(UIElement.PreviewKeyDownEvent, heard, true);
            requests.Pressing("Tab pressed: every time from here is the time since the press");
            requests.WatchKeys("leaving container", before);
            try
            {
                method = Press(window.Handle, VirtualKey.Tab, foreground: true, extended: false);
                if (!await Until(() => seen, TimeSpan.FromSeconds(2)) && method == "SendInput")
                {
                    method = Press(window.Handle, VirtualKey.Tab, foreground: false, extended: false);
                    await Until(() => seen, TimeSpan.FromSeconds(2));
                }
            }
            finally { scroll.RemoveHandler(UIElement.PreviewKeyDownEvent, heard); }
            if (!seen) { return ($"Tab reaching the conversation ({method})", method); }
            var landed = await Until(() => BlockOf(list, FocusManager.GetFocusedElement(list.XamlRoot)) == code, TimeSpan.FromSeconds(3));
            await Still(scroll);
            var on = FocusManager.GetFocusedElement(list.XamlRoot);
            var shown = list.ContainerFromIndex(code) as FrameworkElement;
            var now = shown is null ? double.NaN : Top(shown, scroll);
            var moved = scroll.VerticalOffset - offset;
            Log($"Tab pressed by {method}: focus on {on?.GetType().Name ?? "nothing"} in block {(BlockOf(list, on) is { } b ? b + 1 : 0)}, its FocusState {SessionView.StateOf(on as DependencyObject)?.ToString() ?? "none"}, block {code + 1}'s top {now:0.#} px, the view moved {moved:0.#} px, {requests.Count} requests reached the conversation");
            if (!landed || !ReferenceEquals(on, copy)) { return ($"Tab from block {from + 1} onto the Copy button of block {code + 1}: on {on?.GetType().Name ?? "nothing"} in {(BlockOf(list, on) is { } to ? $"block {to + 1}" : "no block")}", method); }
            if (Math.Abs(moved) > 0.5 || Math.Abs(now - topAfter) > 0.5) { return ($"block {code + 1}, wholly in view near the top, left where it was by Tab from a stop out of view (block {from + 1}'s, {-stopBottom:0.#} px above the view): the view moved {moved:0.#} px, its top {topAfter:0.#} px then {now:0.#} px", method); }
            return (null, method);
        }
        return ($"setup: a code block wholly in view near the top, the stop before it above the view{setup}", method);
    }

    // TEST-ONLY (#881), made only by the self-test and never in a normal run: every
    // BringIntoViewRequested that reaches the conversation while it is open is logged, never handled.
    // Heard on the list, on its items' panel (below the scroller, so before it takes the request) and
    // on the scroller, with the element that asked, its block, where it is, the rectangle and
    // alignment asked for and whether it was handled by then; and each change of the view.
    sealed class BringIntoViewLog : IDisposable
    {
        readonly List<(UIElement Target, TypedEventHandler<UIElement, BringIntoViewRequestedEventArgs> Handler)> heard = [];
        readonly ScrollViewer scroll;
        readonly ListView list;
        readonly Action<string> log;
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        public int Count { get; private set; }

        public BringIntoViewLog(ListView list, ScrollViewer scroll, Action<string> log)
        {
            (this.list, this.scroll, this.log) = (list, scroll, log);
            Listen("list", list);
            if (list.ItemsPanelRoot is { } panel) { Listen("panel", panel); }
            else { log("the list's ItemsPanelRoot was null: no listener on the panel"); }
            if (SessionView.Shallowest<ScrollContentPresenter>(scroll) is { } presenter) { Listen("scroll content presenter", presenter); }
            else { log("no ScrollContentPresenter found under the scroller"); }
            Listen("scroller", scroll);
            WatchKeys("list", list);
            WatchKeys("scroller", scroll);
            if (list.XamlRoot?.Content is { } content)
            {
                WatchKeys("window's content", content);
                WatchAccelerators("window's content", content);
            }
            else { log("the list's XamlRoot had no content: no key listener on the window's content"); }
            WatchAccelerators("list", list);
            WatchFocus();
            WatchMessages();
            for (var i = 0; i < list.Items.Count; i++)
            {
                if (list.ContainerFromIndex(i) is ListViewItem item) { HookContainer(item); }
            }
            log($"listening on {containers.Count} realised containers");
            list.ContainerContentChanging += OnContainer;
            scroll.ViewChanged += OnViewChanged;
            scroll.ViewChanging += OnViewChanging;
            SessionView.Diagnostics = line => log($"at {clock.ElapsedMilliseconds} ms, {line}");
        }

        // A mark in the log, on the same clock.
        public void Mark(string what) => log($"at {clock.ElapsedMilliseconds} ms, {what}");

        // The key about to be pressed: the clock starts again from here, so every time after it is
        // the time since the press.
        public void Pressing(string what)
        {
            clock.Restart();
            Mark(what);
        }

        readonly List<(UIElement Target, TypedEventHandler<UIElement, ProcessKeyboardAcceleratorEventArgs> Handler)> accelerators = [];

        // Every ProcessKeyboardAccelerators the element raises, on the log's clock: the framework's
        // accelerator pass comes after the preview key events and before KeyDown.
        void WatchAccelerators(string where, UIElement target)
        {
            var handler = new TypedEventHandler<UIElement, ProcessKeyboardAcceleratorEventArgs>((_, e) =>
                log($"ProcessKeyboardAccelerators on the {where} at {clock.ElapsedMilliseconds} ms: {e.Key}, modifiers {e.Modifiers}, handled {e.Handled}, the view at {scroll.VerticalOffset:0.#} px"));
            target.ProcessKeyboardAccelerators += handler;
            accelerators.Add((target, handler));
        }

        TypedEventHandler<UIElement, GettingFocusEventArgs>? listGetting;
        TypedEventHandler<UIElement, LosingFocusEventArgs>? listLosing;
        EventHandler<GettingFocusEventArgs>? anyGetting;
        EventHandler<LosingFocusEventArgs>? anyLosing;

        string FocusMove(string what, string where, DependencyObject? from, DependencyObject? to, FocusState state, FocusInputDeviceKind input, FocusNavigationDirection direction, bool cancelled) =>
            $"{what} {where} at {clock.ElapsedMilliseconds} ms: {from?.GetType().Name ?? "nothing"} in {(BlockOf(list, from) is { } a ? $"block {a + 1}" : "no block")} to {to?.GetType().Name ?? "nothing"} in {(BlockOf(list, to) is { } b ? $"block {b + 1}" : "no block")}, FocusState {state}, input {input}, direction {direction}, cancelled {cancelled}, the view at {scroll.VerticalOffset:0.#} px";

        // Every move of focus out of or into the list (handled or not), and every move the focus
        // manager makes anywhere: whether focus on the block Tab lands in is taken, refused, or undone.
        void WatchFocus()
        {
            listGetting = (_, e) => log(FocusMove("GettingFocus", "on the list", e.OldFocusedElement, e.NewFocusedElement, e.FocusState, e.InputDevice, e.Direction, e.Cancel));
            listLosing = (_, e) => log(FocusMove("LosingFocus", "on the list", e.OldFocusedElement, e.NewFocusedElement, e.FocusState, e.InputDevice, e.Direction, e.Cancel));
            anyGetting = (_, e) => log(FocusMove("GettingFocus", "from the focus manager", e.OldFocusedElement, e.NewFocusedElement, e.FocusState, e.InputDevice, e.Direction, e.Cancel));
            anyLosing = (_, e) => log(FocusMove("LosingFocus", "from the focus manager", e.OldFocusedElement, e.NewFocusedElement, e.FocusState, e.InputDevice, e.Direction, e.Cancel));
            list.AddHandler(UIElement.GettingFocusEvent, listGetting, true);
            list.AddHandler(UIElement.LosingFocusEvent, listLosing, true);
            FocusManager.GettingFocus += anyGetting;
            FocusManager.LosingFocus += anyLosing;
        }

        // The thread's key messages for Tab as it takes them from its queue (WH_GETMESSAGE), before
        // XAML has them: whether the view moved before the key was even read. One log at a time.
        static BringIntoViewLog? hooked;
        nint hook;

        unsafe void WatchMessages()
        {
            if (hooked is not null) { log("another log holds the message hook: no key messages logged"); return; }
            hook = SetWindowsHookExW(WhGetMessage, &OnMessage, 0, GetCurrentThreadId());
            if (hook == 0) { log($"no message hook: error {Marshal.GetLastPInvokeError()}"); return; }
            hooked = this;
        }

        [UnmanagedCallersOnly]
        static nint OnMessage(int code, nint removing, nint message)
        {
            try
            {
                if (code >= 0 && message != 0 && hooked is { } self)
                {
                    // MSG: the window, then the message (padded to a pointer's size), then its wParam.
                    var id = (uint)Marshal.ReadInt32(message, IntPtr.Size);
                    var key = Marshal.ReadIntPtr(message, 2 * IntPtr.Size);
                    if (id is WmKeyDown or WmKeyUp && key == (nint)VirtualKey.Tab)
                    {
                        self.log($"the message {(id == WmKeyDown ? "WM_KEYDOWN" : "WM_KEYUP")} Tab {(removing != 0 ? "taken" : "peeked")} from the queue at {self.clock.ElapsedMilliseconds} ms, the view at {self.scroll.VerticalOffset:0.#} px");
                    }
                }
            }
            catch (Exception ex) { AppLog.Write($"self-test: the message hook failed: {ex.Message}"); }
            return CallNextHookEx(0, code, removing, message);
        }

        readonly HashSet<UIElement> containers = [];

        void HookContainer(ListViewItem item)
        {
            if (containers.Add(item)) { Listen("container", item); }
        }

        void OnContainer(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!args.InRecycleQueue && args.ItemContainer is ListViewItem item) { HookContainer(item); }
        }

        void OnViewChanging(object? sender, ScrollViewerViewChangingEventArgs e)
        {
            var focused = list.XamlRoot is { } root ? FocusManager.GetFocusedElement(root) : null;
            var element = focused is TextElement t ? t.ContentStart?.VisualParent : focused as UIElement;
            var where = element is not null && BlockOf(list, focused) is not null ? $", {element.TransformToVisual(scroll).TransformPoint(default).Y:0.#} px from the view's top" : "";
            log($"the view changing at {clock.ElapsedMilliseconds} ms from {scroll.VerticalOffset:0.#} px to {e.NextView.VerticalOffset:0.#} px, finally {e.FinalView.VerticalOffset:0.#} px, inertial {e.IsInertial}, focus on {focused?.GetType().Name ?? "nothing"} in {(BlockOf(list, focused) is { } at ? $"block {at + 1}" : "no block")}{where}");
        }

        readonly List<(UIElement Target, RoutedEvent Event, KeyEventHandler Handler)> keys = [];

        // Every PreviewKeyDown and KeyDown that reaches the element, handled or not, with the key,
        // whether it was handled by then and the view's offset, on the log's clock.
        public void WatchKeys(string where, UIElement target)
        {
            foreach (var (name, routed) in new[] { ("PreviewKeyDown", UIElement.PreviewKeyDownEvent), ("KeyDown", UIElement.KeyDownEvent) })
            {
                var handler = new KeyEventHandler((_, e) => log($"{name} on the {where} at {clock.ElapsedMilliseconds} ms: {e.Key}, handled {e.Handled}, the view at {scroll.VerticalOffset:0.#} px"));
                target.AddHandler(routed, handler, true);
                keys.Add((target, routed, handler));
            }
        }

        void Listen(string where, UIElement target)
        {
            var handler = new TypedEventHandler<UIElement, BringIntoViewRequestedEventArgs>((_, e) => Heard(where, e));
            target.BringIntoViewRequested += handler;
            heard.Add((target, handler));
        }

        void Heard(string where, BringIntoViewRequestedEventArgs e)
        {
            Count++;
            var source = e.OriginalSource;
            var element = source is TextElement t ? t.ContentStart?.VisualParent : source as UIElement;
            var block = BlockOf(list, source) is { } at ? $"block {at + 1}" : "no block";
            var id = source is DependencyObject d ? AutomationProperties.GetAutomationId(d) : "";
            var where2 = element is null ? "" : $", {element.TransformToVisual(scroll).TransformPoint(default).Y:0.#} px from the view's top";
            var r = e.TargetRect;
            log($"BringIntoViewRequested #{Count} at {clock.ElapsedMilliseconds} ms, seen on the {where}: from {source?.GetType().Name ?? "nothing"} {id} in {block}{where2}, target {e.TargetElement?.GetType().Name ?? "none"}, rectangle x {r.X:0.#} y {r.Y:0.#} {r.Width:0.#}x{r.Height:0.#}, vertical ratio {e.VerticalAlignmentRatio:0.##}, horizontal {e.HorizontalAlignmentRatio:0.##}, offsets {e.HorizontalOffset:0.#},{e.VerticalOffset:0.#}, animated {e.AnimationDesired}, handled {e.Handled}, the view at {scroll.VerticalOffset:0.#} px");
        }

        void OnViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
            log($"the view changed at {clock.ElapsedMilliseconds} ms to {scroll.VerticalOffset:0.#} px{(e.IsIntermediate ? " (intermediate)" : "")}");

        public void Dispose()
        {
            foreach (var (target, handler) in heard) { target.BringIntoViewRequested -= handler; }
            heard.Clear();
            foreach (var (target, routed, handler) in keys) { target.RemoveHandler(routed, handler); }
            keys.Clear();
            foreach (var (target, handler) in accelerators) { target.ProcessKeyboardAccelerators -= handler; }
            accelerators.Clear();
            if (listGetting is not null) { list.RemoveHandler(UIElement.GettingFocusEvent, listGetting); }
            if (listLosing is not null) { list.RemoveHandler(UIElement.LosingFocusEvent, listLosing); }
            FocusManager.GettingFocus -= anyGetting;
            FocusManager.LosingFocus -= anyLosing;
            if (hooked == this)
            {
                UnhookWindowsHookEx(hook);
                hooked = null;
            }
            containers.Clear();
            SessionView.Diagnostics = null;
            list.ContainerContentChanging -= OnContainer;
            scroll.ViewChanged -= OnViewChanged;
            scroll.ViewChanging -= OnViewChanging;
        }
    }

    // A block taller than the view (DemoHost.HeavyTall, a quoted log with a link on its first line
    // and its last; ui.md §6.4, the creative director and B on #829): Tab onto its first link brings
    // its start to the leading edge; Shift+Tab onto its last brings that link wholly into view at the
    // nearest edge, the block's start as close to the leading edge as that allows, and no closer. Each
    // from a stop focused with the view held, measured before and after, and the key pressed as the
    // keyboard presses it. A link is brought in unpadded (BringIntoViewHandler), so it ends at the
    // view's bottom edge; the 20 px a control is padded is allowed, and 1.5 px besides. Each step is
    // logged; a view that can't be put so fails with "setup: ...", never skips. Null, and how the
    // keys were pressed, when right.
    static async Task<(string? Problem, string Method)> TabbedTall(MainWindow window, ListView list)
    {
        if (list.ItemsSource is not IList<ConversationBlock> blocks || SessionView.Shallowest<ScrollViewer>(list) is not { } scroll) { return ("setup: a list of blocks with a scroller", "none"); }
        static void Log(string step) => AppLog.Write($"self-test: tall Tab, {step}");
        static double Top(UIElement e, UIElement view) => e.TransformToVisual(view).TransformPoint(default).Y;
        const int Tall = DemoHost.HeavyTall;
        if (Tall + 1 >= blocks.Count || blocks[Tall].Block is not MdQuote || !blocks[Tall].Focusable || !blocks[Tall + 1].Focusable) { return ($"setup: a quote with stops at block {Tall + 1} and a block with one after it", "none"); }
        var from = Enumerable.Range(0, Tall).LastOrDefault(j => blocks[j].Focusable, -1);
        if (from < 0) { return ($"setup: a block with a stop before block {Tall + 1}", "none"); }
        var method = "none";
        foreach (var forward in new[] { true, false })
        {
            var key = forward ? "Tab" : "Shift+Tab";
            // Forward, the tall block's start in the view's lower part, the block before it with a
            // stop above; back, the block after it wholly in view near the top, the tall block across
            // the view's top edge.
            var by = forward ? from : Tall + 1;
            list.ScrollIntoView(blocks[by], ScrollIntoViewAlignment.Leading);
            await Still(scroll);
            if (list.ContainerFromIndex(Tall) is not FrameworkElement placed || list.ContainerFromIndex(by) is not FrameworkElement leaving) { return ($"setup: blocks {by + 1} and {Tall + 1} realised", method); }
            var view = scroll.ViewportHeight;
            if (placed.ActualHeight <= view) { return ($"setup: block {Tall + 1} taller than the view: {placed.ActualHeight:0.#} px in a {view:0.#} px view", method); }
            var at = forward
                ? scroll.VerticalOffset + Top(placed, scroll) - Math.Min(view - 120, Top(placed, scroll) - Top(leaving, scroll))
                : scroll.VerticalOffset + Top(leaving, scroll) - 120;
            if (at < 0) { return ($"setup: block {Tall + 1} placed for {key}", method); }
            scroll.ChangeView(null, at, null, disableAnimation: true);
            await Still(scroll);
            if (list.ContainerFromIndex(Tall) is not FrameworkElement block || list.ContainerFromIndex(by) is not FrameworkElement before) { return ($"setup: blocks {by + 1} and {Tall + 1} realised", method); }
            var (top, beforeTop) = (Top(block, scroll), Top(before, scroll));
            var placedRight = forward
                ? top >= 24 && top <= scroll.ViewportHeight - 24
                : top < 0 && ConversationKeyboard.WhollyInView(beforeTop, before.ActualHeight, scroll.ViewportHeight);
            if (!placedRight) { return ($"setup: for {key}, block {Tall + 1}'s top {top:0.#} px and block {by + 1}'s {beforeTop:0.#} px ({before.ActualHeight:0.#} px tall) in a {scroll.ViewportHeight:0.#} px view", method); }
            var stop = forward ? FocusManager.FindLastFocusableElement(before) : FocusManager.FindFirstFocusableElement(before);
            if (stop is null) { return ($"setup: a stop in block {by + 1}", method); }
            var held = new TypedEventHandler<UIElement, BringIntoViewRequestedEventArgs>((_, e) => e.Handled = true);
            var offsetBefore = scroll.VerticalOffset;
            bool focused;
            before.BringIntoViewRequested += held;
            try
            {
                focused = (await FocusManager.TryFocusAsync(stop, FocusState.Keyboard)).Succeeded;
                await Still(scroll);
            }
            finally { before.BringIntoViewRequested -= held; }
            var (offset, topAfter) = (scroll.VerticalOffset, Top(block, scroll));
            Log($"for {key}, block {Tall + 1} ({block.ActualHeight:0.#} px in a {scroll.ViewportHeight:0.#} px view), its top {top:0.#} px before focus and {topAfter:0.#} px after, focus on the {(forward ? "last" : "first")} stop of block {by + 1}: {(focused ? "taken" : "refused")}, the view at {offsetBefore:0.#} px then {offset:0.#} px");
            if (!focused || !ReferenceEquals(FocusManager.GetFocusedElement(list.XamlRoot), stop)) { return ($"setup: focus on a stop in block {by + 1}", method); }
            if (Math.Abs(offset - offsetBefore) > 0.5 || Math.Abs(topAfter - top) > 0.5) { return ($"setup: block {Tall + 1} left where it was on screen while block {by + 1} took focus: its top {top:0.#} px, then {topAfter:0.#} px", method); }

            // The key, pressed as the keyboard presses it.
            var seen = false;
            var heard = new KeyEventHandler((_, e) => seen |= e.Key == VirtualKey.Tab);
            scroll.AddHandler(UIElement.PreviewKeyDownEvent, heard, true);
            try
            {
                method = Press(window.Handle, VirtualKey.Tab, foreground: true, extended: false, shift: !forward);
                if (!await Until(() => seen, TimeSpan.FromSeconds(2)) && method == "SendInput")
                {
                    method = Press(window.Handle, VirtualKey.Tab, foreground: false, extended: false, shift: !forward);
                    await Until(() => seen, TimeSpan.FromSeconds(2));
                }
            }
            finally
            {
                scroll.RemoveHandler(UIElement.PreviewKeyDownEvent, heard);
                if (!forward) { Unshift(method); }
            }
            if (!seen) { return ($"{key} reaching the conversation ({method})", method); }
            var landed = await Until(() => BlockOf(list, FocusManager.GetFocusedElement(list.XamlRoot)) == Tall, TimeSpan.FromSeconds(3));
            await Still(scroll);
            var on = FocusManager.GetFocusedElement(list.XamlRoot);
            var shown = list.ContainerFromIndex(Tall) as FrameworkElement;
            var meant = shown is null ? null : forward ? FocusManager.FindFirstFocusableElement(shown) : FocusManager.FindLastFocusableElement(shown);
            var now = shown is null ? double.NaN : Top(shown, scroll);
            var (linkTop, linkBottom) = meant is Hyperlink l && Drawn(l, scroll) is { } r ? (r.Top, r.Bottom) : (double.NaN, double.NaN);
            view = scroll.ViewportHeight;
            Log($"{key} pressed by {method}: focus on {on?.GetType().Name ?? "nothing"} in block {(BlockOf(list, on) is { } b ? b + 1 : 0)}, block {Tall + 1}'s top {now:0.#} px, the link {linkTop:0.#} to {linkBottom:0.#} px in a {view:0.#} px view, the view moved {scroll.VerticalOffset - offset:0.#} px");
            if (!landed || meant is not Hyperlink || !ReferenceEquals(on, meant)) { return ($"{key} from block {by + 1} onto the {(forward ? "first" : "last")} link of block {Tall + 1}: on {on?.GetType().Name ?? "nothing"} in {(BlockOf(list, on) is { } to ? $"block {to + 1}" : "no block")}", method); }
            if (forward && Math.Abs(now) > 1.5) { return ($"block {Tall + 1}, taller than the view, brought to the leading edge by Tab onto its first link: its top {now:0.#} px from the view's", method); }
            if (!forward && (double.IsNaN(linkTop) || linkTop < -1.5 || linkBottom > view + 1.5)) { return ($"the last link of block {Tall + 1}, taller than the view, wholly in view after Shift+Tab onto it: {linkTop:0.#} to {linkBottom:0.#} px in a {view:0.#} px view", method); }
            if (!forward && (now >= 0 || linkBottom < view - 21.5)) { return ($"block {Tall + 1}'s start as close to the leading edge as its last link in view allows: its top {now:0.#} px, the link's bottom {linkBottom:0.#} px in a {view:0.#} px view", method); }
        }
        return (null, method);
    }

    // Where a link is drawn against a view, from its text's first character to its last.
    static Rect? Drawn(Hyperlink link, UIElement view)
    {
        if (link.ContentStart?.VisualParent is not UIElement text || link.ContentEnd is not { } end) { return null; }
        var (first, last) = (link.ContentStart.GetCharacterRect(LogicalDirection.Forward), end.GetCharacterRect(LogicalDirection.Backward));
        var to = text.TransformToVisual(view);
        var (a, b) = (to.TransformPoint(new Point(first.Left, Math.Min(first.Top, last.Top))), to.TransformPoint(new Point(last.Right, Math.Max(first.Bottom, last.Bottom))));
        return new Rect(new Point(Math.Min(a.X, b.X), a.Y), new Point(Math.Max(a.X, b.X), b.Y));
    }

    // A link is a text element in its block's text, not a control (#829): in the window itself, Tab
    // pressed on the last link of the last block the list has realised goes to the first link of the
    // next block, which it had not, and brings that block in at the leading edge, numbered in its
    // set. Where the list ends what it realises is its own business, so the response ends with a run
    // of linked paragraphs longer than twice the most it was seen to realise from a view's top
    // (DemoHost.HeavyLinkedRun): with the run's first block at the top of the view, the last block
    // realised and the one after it are both in the run, wherever the list stops. Should they not be,
    // the check fails with "setup: ...", never skips. Each step is logged, so a failure says which.
    // Null, and how Tab was pressed, when right.
    static async Task<(string? Problem, string Method)> TabbedLink(MainWindow window, ListView list)
    {
        if (list.ItemsPanelRoot is not ItemsStackPanel panel || list.ItemsSource is not IList<ConversationBlock> blocks
            || SessionView.Shallowest<ScrollViewer>(list) is not { } scroll) { return ("setup: a list of blocks with a scroller", "none"); }
        static bool Linked(ConversationBlock block) => block.Block is MdParagraph p && p.Inlines.Any(i => i is MdLink);
        static void Log(string step) => AppLog.Write($"self-test: link Tab, {step}");
        const int Run = DemoHost.HeavyLinkedRun;
        var start = Enumerable.Range(0, Math.Max(0, blocks.Count - Run + 1)).FirstOrDefault(s => Enumerable.Range(s, Run).All(i => Linked(blocks[i])), -1);
        if (start < 0) { return ($"setup: a run of {Run} linked blocks in the response", "none"); }
        // The end of the run the list has realised from the view's first block on, as Tabbed finds
        // it: a block kept far off, such as one an earlier check left focused, is not part of it.
        int End()
        {
            list.UpdateLayout();
            var end = panel.FirstVisibleIndex;
            if (end < 0 || list.ContainerFromIndex(end) is null) { return -1; }
            while (end + 1 < blocks.Count && list.ContainerFromIndex(end + 1) is not null) { end++; }
            return end;
        }
        // Focus taken by the link without the view moving to it, so taking it realises nothing more.
        var held = new TypedEventHandler<UIElement, BringIntoViewRequestedEventArgs>((_, e) => e.Handled = true);
        var cache = panel.CacheLength;
        try
        {
            panel.CacheLength = 0;
            // To the top first, then to the run: a jump that far keeps nothing realised on the way.
            scroll.ChangeView(null, 0, null, disableAnimation: true);
            await Still(scroll);
            await Task.Delay(500);
            list.ScrollIntoView(blocks[start], ScrollIntoViewAlignment.Leading);
            await Still(scroll);
            var a = End();
            Log($"the run of {Run} linked blocks from block {start + 1} at the top of the view: block {panel.FirstVisibleIndex + 1} first in view, block {a + 1} the last realised, block {a + 2} {(a >= 0 && a + 1 < blocks.Count && list.ContainerFromIndex(a + 1) is null ? "not realised" : "realised or none")}");
            if (a < start || a + 1 >= start + Run) { return ($"setup: the last block realised and the next in the run of linked blocks {start + 1} to {start + Run}: block {a + 1} the last realised", "none"); }
            if (list.ContainerFromIndex(a + 1) is not null || list.ContainerFromIndex(a) is not FrameworkElement shown) { return ($"setup: block {a + 1} realised and block {a + 2} not", "none"); }
            if (FocusManager.FindLastFocusableElement(shown) is not Hyperlink link) { return ($"setup: a link last in block {a + 1}, not {FocusManager.FindLastFocusableElement(shown)?.GetType().Name ?? "nothing"}", "none"); }
            bool focused;
            shown.BringIntoViewRequested += held;
            try
            {
                focused = (await FocusManager.TryFocusAsync(link, FocusState.Keyboard)).Succeeded;
                await Still(scroll);
            }
            finally { shown.BringIntoViewRequested -= held; }
            Log($"focus on the last link of block {a + 1}: {(focused ? "taken" : "refused")}, the view at {scroll.VerticalOffset:0.#} px, block {a + 2} {(list.ContainerFromIndex(a + 1) is null ? "not realised" : "realised")}");
            if (!focused || !ReferenceEquals(FocusManager.GetFocusedElement(list.XamlRoot), link)) { return ($"setup: focus on the last link of block {a + 1}", "none"); }
            if (list.ContainerFromIndex(a + 1) is not null) { return ($"setup: block {a + 2} not realised with the link in block {a + 1} focused", "none"); }

            // Tab, pressed as the keyboard presses it.
            var seen = false;
            var heard = new KeyEventHandler((_, e) => seen |= e.Key == VirtualKey.Tab);
            scroll.AddHandler(UIElement.PreviewKeyDownEvent, heard, true);
            string method;
            try
            {
                method = Press(window.Handle, VirtualKey.Tab, foreground: true, extended: false);
                if (!await Until(() => seen, TimeSpan.FromSeconds(2)) && method == "SendInput")
                {
                    method = Press(window.Handle, VirtualKey.Tab, foreground: false, extended: false);
                    await Until(() => seen, TimeSpan.FromSeconds(2));
                }
            }
            finally { scroll.RemoveHandler(UIElement.PreviewKeyDownEvent, heard); }
            Log($"Tab pressed by {method}, {(seen ? "seen" : "not seen")} at the conversation");
            if (!seen) { return ($"Tab reaching the conversation ({method})", method); }

            object? container = null;
            var landed = await Until(() =>
            {
                var f = FocusManager.GetFocusedElement(list.XamlRoot);
                if (f is ListViewItem or ListView) { container = f; return true; }
                return f is Hyperlink && BlockOf(list, f) == a + 1;
            }, TimeSpan.FromSeconds(3));
            await Still(scroll);
            var on = FocusManager.GetFocusedElement(list.XamlRoot);
            Log($"focus on {on?.GetType().Name ?? "nothing"} in block {(BlockOf(list, on) is { } at ? at + 1 : 0)}");
            if (container is not null) { return ($"focus on a block's container: {container.GetType().Name}", method); }
            if (!landed) { return ($"Tab from the link in block {a + 1} to the link in block {a + 2}: it went to {(BlockOf(list, on) is { } to ? $"block {to + 1}" : on?.GetType().Name ?? "nothing")}", method); }
            if (list.ContainerFromIndex(a + 1) is not FrameworkElement realised) { return ($"block {a + 2} realised", method); }
            if (!ReferenceEquals(on, FocusManager.FindFirstFocusableElement(realised))) { return ($"Tab into block {a + 2} on its first link, not {on?.GetType().Name ?? "nothing"}", method); }
            var peer = FrameworkElementAutomationPeer.CreatePeerForElement(list) as ItemsControlAutomationPeer;
            var item = peer?.CreateItemAutomationPeer(blocks[a + 1]);
            var (position, size) = (item?.GetPositionInSet() ?? -1, item?.GetSizeOfSet() ?? -1);
            var top = realised.TransformToVisual(scroll).TransformPoint(default).Y;
            Log($"block {a + 2} realised, {position} of {size}, its top {top:0.#} px from the view's");
            if (position != a + 2 || size != blocks.Count) { return ($"block {a + 2} numbered {a + 2} of {blocks.Count}: {position} of {size}", method); }
            if (Math.Abs(top) > 1.5) { return ($"block {a + 2} in view at the leading edge: its top {top:0.#} px from the view's", method); }
            return (null, method);
        }
        finally { panel.CacheLength = cache; }
    }

    // The block an element is in, a link in its text block included; null outside the blocks.
    static int? BlockOf(ListView list, object? element)
    {
        for (var node = element is TextElement t ? t.ContentStart?.VisualParent : element as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ListViewItem item && ItemsControl.ItemsControlFromItemContainer(item) == list) { return list.IndexFromContainer(item); }
        }
        return null;
    }

    // An element as drawn, its pixels, and a picture of it for review.
    static async Task<(byte[] Pixels, int Width, int Height)> Shoot(UIElement element, string file)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var pixels = (await bitmap.GetPixelsAsync()).ToArray();
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, pixels);
            await encoder.FlushAsync();
            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            await File.WriteAllBytesAsync(file, bytes);
        }
        catch (Exception e) when (e is COMException or ArgumentException or IOException or UnauthorizedAccessException) { AppLog.Write($"self-test: picture {Path.GetFileName(file)} not saved: {e.Message}"); }
        return (pixels, bitmap.PixelWidth, bitmap.PixelHeight);
    }
}
