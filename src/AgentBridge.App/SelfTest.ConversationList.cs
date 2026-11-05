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
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace AgentBridge.App;

// The conversation as a list (#765): the block at the top of the viewport stays there through a
// live resize, the conversation drawn the same before the drag and after it, and a screen reader is
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
        var index = list.Items.Count / 2;
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

    // What moved since Anchor: the block, or any pixel of the column; null when nothing did.
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
            // Anti-aliased edges may be drawn a step or two apart from one frame to the next (arm64's
            // software rasteriser drew two edge pixels of the same text 1 apart in alpha), so a pixel
            // differs only when a channel is more than Tolerance apart: a block moved by even a pixel
            // differs by far more along every edge of its text.
            const int Tolerance = 8;
            var differ = 0;
            var largest = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = Math.Max(0, from); x < Math.Min(width, to); x++)
                {
                    var i = (y * width + x) * 4;
                    var most = 0;
                    for (var c = 0; c < 4; c++) { most = Math.Max(most, Math.Abs(pixels[i + c] - before.Pixels[i + c])); }
                    largest = Math.Max(largest, most);
                    if (most > Tolerance) { differ++; }
                }
            }
            if (differ > 0) { problems.Add($"{differ} pixels of the column drawn differently, by up to {largest} in a channel"); }
            else if (largest > 0) { AppLog.Write($"self-test: resize, the column drawn the same within {Tolerance} a channel (by up to {largest})"); }
        }
        return problems.Count > 0 ? string.Join(", ", problems) : null;
    }

    // What a screen reader is given wrong of the response's items: every block a list item numbered
    // 1 to N in a set of N, and every list item a block's, so the header and the footer are outside
    // the set. WinUI numbers an item only once it has a container, so a block not realised yet is
    // realised first through its peer's VirtualizedItem pattern, as a screen reader moving to it
    // does. Null when it is given all of them right, with how many blocks were realised that way.
    static async Task<(string? Problem, int Realised, double Seconds)> Listed(ListView list)
    {
        var count = list.Items.Count;
        if (count == 0 || FrameworkElementAutomationPeer.CreatePeerForElement(list) is not ItemsControlAutomationPeer peer) { return ("a list to read", 0, 0); }
        var problems = new List<string>();
        var realised = 0;
        var walk = System.Diagnostics.Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var item = peer.CreateItemAutomationPeer(list.Items[i]);
            if (item is null || item.GetAutomationControlType() != AutomationControlType.ListItem) { problems.Add($"block {i + 1} a list item: {item?.GetAutomationControlType().ToString() ?? "no peer"}"); break; }
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
