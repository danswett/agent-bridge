using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AgentBridge.App;

public sealed partial class ConversationListView : ListView
{
    internal Func<Func<Size>, Size>? LayoutOwner { get; set; }

    protected override Size MeasureOverride(Size availableSize) =>
        LayoutOwner is { } owner ? owner(() => base.MeasureOverride(availableSize)) : base.MeasureOverride(availableSize);

    protected override Size ArrangeOverride(Size finalSize) =>
        LayoutOwner is { } owner ? owner(() => base.ArrangeOverride(finalSize)) : base.ArrangeOverride(finalSize);
}
