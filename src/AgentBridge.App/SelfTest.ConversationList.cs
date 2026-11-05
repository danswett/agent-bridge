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
        // In the middle of the response before its run of linked paragraphs (DemoHost.HeavyLinkedRun):
        // the block it always was.
        var index = (list.Items.Count - DemoHost.HeavyLinkedRun) / 2;
        if (index < 1) { return null; }
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
    // peer the search found, as a reader does. Null when it can. Each step is logged with where the
    // view is and what has focus; on a failure, the same is tried once more with focus out of the
    // conversation and logged, to tell what took the view away, the verdict unchanged.
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
        if (foundPeer.GetPattern(PatternInterface.VirtualizedItem) is not IVirtualizedItemProvider virtualised)
        {
            problems.Add("the last block realisable");
            return string.Join(", ", problems);
        }
        // Where the view goes, step by step, and every move of it, so a failure says whether the
        // list's own anchoring of the view (AnchorCorrections) or the list took it away (#829).
        var scroller = SessionView.Shallowest<ScrollViewer>(list);
        var moves = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var watched = new EventHandler<ScrollViewerViewChangedEventArgs>((_, e) => { if (moves.Count < 32 && scroller is not null) { moves.Add($"{clock.ElapsedMilliseconds} ms {scroller.VerticalOffset:0}{(e.IsIntermediate ? "~" : "")}"); } });
        if (scroller is not null) { scroller.ViewChanged += watched; }
        try
        {
            ReaderSaw(list, "to begin with");
            var brought = await Brought();
            if (brought is null) { return problems.Count > 0 ? string.Join(", ", problems) : null; }
            problems.Add(brought);
            // Once, to tell the causes apart, with focus out of the conversation: the verdict
            // stands whatever this finds.
            var outside = list.XamlRoot?.Content is { } root && FocusManager.FindFirstFocusableElement(root) is { } first && !InList(list, first) ? first : null;
            var away = outside is not null && (await FocusManager.TryFocusAsync(outside, FocusState.Programmatic)).Succeeded;
            ReaderSaw(list, away ? $"focus moved out of the conversation, to {outside!.GetType().Name}, and again" : "focus not moved out of the conversation, and again");
            var again = await Brought();
            AppLog.Write($"self-test: reader, again: {again ?? "the last block brought into view and said as an item"}");
            return string.Join(", ", problems);
        }
        finally { if (scroller is not null) { scroller.ViewChanged -= watched; } }

        async Task<string?> Brought()
        {
            moves.Clear();
            var view = FindAncestor<SessionView>(list);
            var correctedBefore = view?.AnchorCorrections ?? 0;
            clock.Restart();
            virtualised.Realize();
            var realised = await Until(() => list.ContainerFromIndex(count - 1) is ListViewItem, TimeSpan.FromSeconds(5));
            ReaderSaw(list, $"realised by its peer at {clock.ElapsedMilliseconds} ms");
            if (!realised) { return "the last block realised"; }
            if (foundPeer.GetPattern(PatternInterface.ScrollItem) is IScrollItemProvider scrolled) { scrolled.ScrollIntoView(); }
            ReaderSaw(list, "scrolled into view by its peer");
            // Until it is in view, 3 s at most, rather than a fixed wait; still checked below.
            var shown = await Until(() => list.ContainerFromIndex(count - 1) is ListViewItem l && SessionView.Shallowest<ScrollViewer>(list) is { } s
                && l.TransformToVisual(s).TransformPoint(default).Y is var y && y < s.ViewportHeight && y + l.ActualHeight > 0, TimeSpan.FromSeconds(3));
            ReaderSaw(list, $"{(shown ? "in view" : "not in view")} at {clock.ElapsedMilliseconds} ms, {(view?.AnchorCorrections ?? 0) - correctedBefore} anchor corrections since it was asked; the view's moves: {(moves.Count > 0 ? string.Join(", ", moves) : "none")}");
            if (ItemSaid(peer, list.ContainerFromIndex(count - 1) as ListViewItem, count - 1, count) is { } wrong) { return wrong; }
            if (list.ContainerFromIndex(count - 1) is ListViewItem lastShown && SessionView.Shallowest<ScrollViewer>(list) is { } scroll
                && lastShown.TransformToVisual(scroll).TransformPoint(default).Y is var top && (top >= scroll.ViewportHeight || top + lastShown.ActualHeight <= 0))
            {
                return $"the last block brought into view: at {top:0.#} px of {scroll.ViewportHeight:0.#}";
            }
            return null;
        }
    }

    // Where the conversation's view is for a screen reader's check, logged with what has focus.
    static void ReaderSaw(ListView list, string step)
    {
        var scroll = SessionView.Shallowest<ScrollViewer>(list);
        var panel = list.ItemsPanelRoot as ItemsStackPanel;
        var focused = FocusManager.GetFocusedElement(list.XamlRoot);
        AppLog.Write($"self-test: reader, {step}: the view at {scroll?.VerticalOffset:0} of {scroll?.ScrollableHeight:0} px, {scroll?.ViewportHeight:0} shown; "
            + $"blocks {panel?.FirstVisibleIndex + 1} to {panel?.LastVisibleIndex + 1} in view, {panel?.FirstCacheIndex + 1} to {panel?.LastCacheIndex + 1} laid out; "
            + $"the last block {(list.ContainerFromIndex(list.Items.Count - 1) is null ? "not laid out" : "laid out")}; "
            + $"focus on {focused?.GetType().Name ?? "nothing"}{(BlockOf(list, focused) is { } b ? $" in block {b + 1}" : "")}; "
            + $"{FindAncestor<SessionView>(list)?.AnchorCorrections ?? 0} anchor corrections in all");
    }

    // Whether an element, a link in a text block included, is in the list.
    static bool InList(ListView list, DependencyObject element)
    {
        for (var node = element is TextElement t ? t.ContentStart?.VisualParent : element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == list) { return true; }
        }
        return false;
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
