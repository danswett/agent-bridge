using System.Collections.ObjectModel;
using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// The conversation's response as a list (#765): its top-level blocks are the list's items, so only
// those near the viewport are realised, and a live resize re-wraps those alone; it once re-wrapped
// every block of a long response on every step of a drag. The block at the top of the viewport
// stays there through a change of width: the header above the blocks re-wraps too, and the list
// alone would keep the offset, not the block.
public sealed partial class SessionView
{
    ObservableCollection<ConversationBlock> blocks = [];
    // What the list was last made from, so a card's every change doesn't parse its response again.
    SessionCardViewModel? blocksOf;
    string? blocksFrom;

    ScrollViewer? ConversationScroll { get; set; }
    ScrollAnchor? anchor;
    // The offset a correction of ours asked for: its ViewChanged is not the person scrolling.
    double? anchoring;

    void ShowResponse(bool reset)
    {
        var card = Card;
        var from = card is { HasVoice: true } ? card.Response : null;
        if (!reset && card == blocksOf && from == blocksFrom) { return; }
        blocksOf = card;
        blocksFrom = from;
        IReadOnlyList<MdBlock> next = from is null ? [] : MarkdownModel.Parse(from);
        if (reset || !ConversationBlocks.Update(blocks, next))
        {
            blocks = [.. next.Select(b => new ConversationBlock(b))];
            ConversationList.ItemsSource = blocks;
        }
        // Indexes have moved: the anchor taken again once the list has laid them out.
        anchor = null;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, TakeAnchor);
        // The response's empty view held 8 under its label, as its first block does.
        FooterLead.Height = card is { HasVoice: true } && blocks.Count == 0 ? 8 : 0;
    }

    void HookConversation()
    {
        if (ConversationScroll is not null || Shallowest<ScrollViewer>(ConversationList) is not { } scroll) { return; }
        ConversationScroll = scroll;
        scroll.ViewChanged += (_, e) =>
        {
            follow.Scrolled(scroll.VerticalOffset, scroll.ScrollableHeight);
            // A question still moving into view is held from the pointer until it has stopped.
            guard.Scrolled(DateTimeOffset.UtcNow);
            if (anchoring is not { } asked || Math.Abs(scroll.VerticalOffset - asked) >= 0.5) { TakeAnchor(); }
            if (!e.IsIntermediate) { anchoring = null; }
        };
        scroll.DirectManipulationStarted += (_, _) => follow.PanStarted();
        scroll.DirectManipulationCompleted += (_, _) => follow.PanEnded();
    }

    // The block across the viewport's top edge, where it starts against that edge.
    void TakeAnchor()
    {
        anchor = null;
        if (ConversationScroll is not { } scroll || ConversationList.ItemsPanelRoot is not ItemsStackPanel panel || panel.FirstVisibleIndex < 0) { return; }
        var realised = new List<(int, double, double)>();
        for (var i = panel.FirstVisibleIndex; i <= Math.Min(panel.FirstVisibleIndex + 1, blocks.Count - 1); i++)
        {
            if (ConversationList.ContainerFromIndex(i) is FrameworkElement c) { realised.Add((i, Top(c, scroll), c.ActualHeight)); }
        }
        anchor = ScrollAnchor.At(realised);
    }

    // Laid out at a new width: the anchor block back where it was, in the same pass, before it is drawn.
    void KeepAnchor()
    {
        if (anchor is not { } a || ConversationScroll is not { } scroll || ConversationList.ContainerFromIndex(a.Index) is not FrameworkElement c) { return; }
        var to = a.OffsetFor(scroll.VerticalOffset, Top(c, scroll), scroll.ScrollableHeight);
        if (Math.Abs(to - scroll.VerticalOffset) < 0.5) { return; }
        anchoring = to;
        scroll.ChangeView(null, to, null, disableAnimation: true);
    }

    static double Top(UIElement element, UIElement viewport) => element.TransformToVisual(viewport).TransformPoint(default).Y;

    // Each block named by its text, as a screen reader finds it unrealised (ConversationBlock).
    void OnBlockShown(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (!args.InRecycleQueue && args.Item is ConversationBlock block) { AutomationProperties.SetName(args.ItemContainer, block.Text); }
    }

    // The list's own scroller: the first found going down, above anything its items hold.
    internal static T? Shallowest<T>(DependencyObject root) where T : DependencyObject
    {
        var queue = new Queue<DependencyObject>([root]);
        while (queue.TryDequeue(out var node))
        {
            if (node is T t && node != root) { return t; }
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++) { queue.Enqueue(VisualTreeHelper.GetChild(node, i)); }
        }
        return null;
    }
}
