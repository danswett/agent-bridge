using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AgentBridge.App;

// Children side by side, starting a new line when the next does not fit: the status line's
// flex-wrap in the PWA (.status-line). A horizontal StackPanel measured its words at full width,
// so a long delegate's name, or a count with "answer at the machine", ran off the card. Each child
// is offered the whole line, so a Grid with a star column (the glyph and its words) wraps its
// words rather than overflowing.
public sealed partial class FlowPanel : Panel
{
    public double Spacing { get; set; } = 8;
    public double LineSpacing { get; set; } = 2;

    protected override Size MeasureOverride(Size availableSize)
    {
        return Lay(availableSize.Width, (_, _) => { });
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Lay(finalSize.Width, (child, rect) => child.Arrange(rect));
        return finalSize;
    }

    // Measures every shown child against the line's width and places it, returning the size used.
    Size Lay(double width, Action<UIElement, Rect> place)
    {
        double x = 0, y = 0, line = 0, widest = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed) { continue; }
            child.Measure(new Size(width, double.PositiveInfinity));
            var size = child.DesiredSize;
            // An empty piece of text takes no room and no spacing.
            if (size.Width <= 0) { place(child, new Rect(x, y, 0, 0)); continue; }
            if (x > 0 && x + Spacing + size.Width > width)
            {
                y += line + LineSpacing;
                x = 0;
                line = 0;
            }
            if (x > 0) { x += Spacing; }
            place(child, new Rect(x, y, Math.Min(size.Width, width), size.Height));
            x += size.Width;
            line = Math.Max(line, size.Height);
            widest = Math.Max(widest, Math.Min(x, width));
        }
        return new Size(widest, y + line);
    }
}
