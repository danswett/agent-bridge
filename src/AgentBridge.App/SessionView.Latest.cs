using AgentBridge.App.Core;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

public sealed partial class SessionView
{
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
