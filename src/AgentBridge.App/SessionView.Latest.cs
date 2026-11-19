using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

public sealed partial class SessionView
{
    LatestHatch latestHatch = new();

    void HookLatest(ScrollViewer scroll) =>
        scroll.AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            for (var node = e.OriginalSource as DependencyObject; node is not null && node != scroll; node = VisualTreeHelper.GetParent(node))
            {
                if (node is ScrollBar { Orientation: Orientation.Vertical }) { ReleasePin(); return; }
            }
        }), true);

    void OnJumpToLatest(object sender, RoutedEventArgs e)
    {
        ReleasePin();
        latestHatch.Jump();
        pinEnd = true;
        anchor = null;
        keyTarget = null;
        PinLatest();
    }

    void PinLatest()
    {
        if (!latestHatch.Pinned || !IsLoaded || ConversationScroll is not { } scroll) { return; }
        anchor = null;
        if (Math.Abs(scroll.ScrollableHeight - scroll.VerticalOffset) > 0.5) { Anchoring(scroll, scroll.ScrollableHeight); }
    }

    readonly LatestReceiptGate latestNativeInput = new(Guid.NewGuid());

    internal LatestInputStamp BeginLatestNativeInput() => latestNativeInput.Issue();

    internal LatestInputReceipt StampLatestNativeInput(LatestInputStamp input, ScrollViewer target)
    {
        if (input.ViewId != latestNativeInput.ViewId || !ReferenceEquals(target, ConversationScroll))
        {
            throw new InvalidOperationException("latest: native input receipt target is not the issued conversation view");
        }
        // A delayed arrival keeps its issued stamp, never the counter's newer sequence.
        return input.Arrived(target.VerticalOffset, target.ScrollableHeight);
    }

    internal bool AcceptsLatestNativeReceipt(LatestInputReceipt receipt) => latestNativeInput.Accepts(receipt);
}
