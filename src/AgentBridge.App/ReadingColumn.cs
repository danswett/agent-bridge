using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AgentBridge.App;

// A column at the reading measure (ui.md §4.2, §6.4 and §6.5): the width it is offered, up to its MaxWidth,
// from the pane's leading edge, whatever it holds. A StackPanel aligned left asks only for its widest
// child's width, so the conversation was as narrow as its widest block, and your message, a bubble
// at the column's right, sat at the left of a wide pane beside a short reply (#255).
public sealed partial class ReadingColumn : StackPanel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var used = base.MeasureOverride(availableSize);
        return double.IsInfinity(availableSize.Width) ? used : new Size(availableSize.Width, used.Height);
    }
}
