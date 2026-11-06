using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AgentBridge.App;

// Children side by side, starting a new line when the next does not fit: the status line's
// flex-wrap in the PWA (.status-line). A horizontal StackPanel measured its words at full width,
// so a long delegate's name, or a count with "answer at the machine", ran off the card. Each child
// is offered the whole line once it is wider than the line, so a Grid with a star column (the glyph
// and its words) wraps its words rather than overflowing.
public sealed partial class FlowPanel : Panel
{
    public double Spacing { get; set; } = 8;
    public double LineSpacing { get; set; } = 2;

    // For the self-test's live resize (#765): how often a line was laid out at a new width, and how
    // often a child of one had to be measured again because of it, across every FlowPanel.
    internal static long NewWidths;
    internal static long Remeasured;

    // What each child was offered last, and its width unconstrained when that was wider than the line.
    readonly Dictionary<UIElement, (double Offered, double Natural)> offered = [];
    double laidAt = double.NaN;

    protected override Size MeasureOverride(Size availableSize)
    {
        if (!availableSize.Width.Equals(laidAt))
        {
            if (!double.IsNaN(laidAt)) { NewWidths++; }
            laidAt = availableSize.Width;
        }
        var size = Lay(availableSize.Width, (_, _) => { });
        // A child that left (a footer's delegate line ending) is forgotten, not kept for the panel's life.
        if (offered.Count > Children.Count)
        {
            foreach (var gone in offered.Keys.Where(k => !Children.Contains(k)).ToList()) { offered.Remove(gone); }
        }
        return size;
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
            var size = Fit(child, width);
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

    // A child that fits the line is measured unconstrained, which comes out as it would at the line's
    // width, and stays measured as the line narrows or widens (a live resize, #765): only one wider
    // than the line is offered it, and measured again at each new width. Which of the two a child was
    // is kept, so a layout pass never measures it both ways.
    Size Fit(UIElement child, double width)
    {
        var known = offered.TryGetValue(child, out var was);
        var natural = was.Natural;
        double offer;
        if (known && !double.IsPositiveInfinity(was.Offered) && natural > width)
        {
            offer = width;
            Offer(child, offer, was.Offered);
        }
        else
        {
            offer = double.PositiveInfinity;
            Offer(child, offer, known ? was.Offered : offer);
            natural = child.DesiredSize.Width;
            if (natural > width)
            {
                offer = width;
                Offer(child, offer, double.PositiveInfinity);
            }
        }
        offered[child] = (offer, natural);
        return child.DesiredSize;
    }

    static void Offer(UIElement child, double width, double before)
    {
        if (!width.Equals(before)) { Remeasured++; }
        child.Measure(new Size(width, double.PositiveInfinity));
    }
}
