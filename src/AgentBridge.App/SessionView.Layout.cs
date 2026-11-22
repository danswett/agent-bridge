using Windows.Foundation;

namespace AgentBridge.App;

public sealed partial class SessionView
{
    protected override Size MeasureOverride(Size availableSize) =>
        DuringConversationLayout(() => base.MeasureOverride(availableSize));

    protected override Size ArrangeOverride(Size finalSize) =>
        DuringConversationLayout(() => base.ArrangeOverride(finalSize));
}
