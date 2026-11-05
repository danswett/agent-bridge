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
        await Task.Delay(700);
        if (SessionView.Shallowest<ScrollViewer>(list) is not { } scroll) { return "a scroller"; }
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
            var differ = 0;
            for (var y = 0; y < height; y++)
            {
                for (var x = Math.Max(0, from); x < Math.Min(width, to); x++)
                {
                    var i = (y * width + x) * 4;
                    if (pixels[i] != before.Pixels[i] || pixels[i + 1] != before.Pixels[i + 1] || pixels[i + 2] != before.Pixels[i + 2] || pixels[i + 3] != before.Pixels[i + 3]) { differ++; }
                }
            }
            if (differ > 0) { problems.Add($"{differ} pixels of the column drawn differently"); }
        }
        return problems.Count > 0 ? string.Join(", ", problems) : null;
    }

    // What a screen reader can't reach of the response: every block an item, numbered in the set,
    // and the last, off screen, found by its text and brought into view. Null when it reaches all.
    static async Task<string?> Reachable(ListView list)
    {
        var count = list.Items.Count;
        if (count == 0 || FrameworkElementAutomationPeer.CreatePeerForElement(list) is not ItemsControlAutomationPeer peer) { return "a list to read"; }
        var problems = new List<string>();
        if (list.ItemsPanelRoot is ItemsStackPanel { FirstVisibleIndex: >= 0 } panel && list.ContainerFromIndex(panel.FirstVisibleIndex) is ListViewItem shown)
        {
            if (Said(shown, panel.FirstVisibleIndex, count) is { } wrong) { problems.Add(wrong); }
        }
        else { problems.Add("a block in view"); }
        var last = (ConversationBlock)list.Items[count - 1];
        if (list.ContainerFromIndex(count - 1) is not null) { problems.Add("the last block off screen to begin with"); }
        if (peer.GetPattern(PatternInterface.ItemContainer) is not IItemContainerProvider items || items.FindItemByProperty(null, AutomationElementIdentifiers.NameProperty, last.Text) is null)
        {
            problems.Add("the last block found by its text");
        }
        if (peer.CreateItemAutomationPeer(last)?.GetPattern(PatternInterface.VirtualizedItem) is IVirtualizedItemProvider virtualised) { virtualised.Realize(); }
        else { problems.Add("the last block realisable"); }
        if (!await Until(() => list.ContainerFromIndex(count - 1) is ListViewItem, TimeSpan.FromSeconds(5))) { problems.Add("the last block realised"); }
        else if (Said(list.ContainerFromIndex(count - 1) as ListViewItem, count - 1, count) is { } wrong) { problems.Add(wrong); }
        return problems.Count > 0 ? string.Join(", ", problems) : null;

        static string? Said(ListViewItem? item, int index, int count)
        {
            var itemPeer = item is null ? null : FrameworkElementAutomationPeer.CreatePeerForElement(item);
            var text = item?.Content is ConversationBlock b ? b.Text : null;
            return itemPeer is null ? $"block {index + 1} as an item"
                : itemPeer.GetPositionInSet() != index + 1 || itemPeer.GetSizeOfSet() != count || itemPeer.GetName() != text
                    ? $"block {index + 1} said as \"{Clip(itemPeer.GetName())}\", {itemPeer.GetPositionInSet()} of {itemPeer.GetSizeOfSet()}"
                    : null;
        }
        static string Clip(string s) => s.Length > 40 ? s[..40] + "…" : s;
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
                if (await Until(() => BlockOf(list, FocusManager.GetFocusedElement(list.XamlRoot)) == next, TimeSpan.FromSeconds(3))) { return null; }
                var focused = FocusManager.GetFocusedElement(list.XamlRoot);
                return focused is ListViewItem or ListView ? $"focus on a block's container: {focused.GetType().Name}"
                    : $"Tab from block {from + 1} to block {next + 1}: it went to {(BlockOf(list, focused) is { } at ? $"block {at + 1}" : focused?.GetType().Name ?? "nothing")}";
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
