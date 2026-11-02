using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace AgentBridge.App;

// Buttons in a row that go on to the next line where the window is too narrow for them all, so
// none is cut off at the 360 px window (#180): a machine's Show sessions, Check and Install.
// WinUI has no wrapping panel of its own.
public sealed partial class WrapRow : Panel
{
    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(WrapRow), new PropertyMetadata(0.0, (d, _) => ((WrapRow)d).InvalidateMeasure()));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    protected override Size MeasureOverride(Size available)
    {
        foreach (var child in Children) { child.Measure(new Size(available.Width, double.PositiveInfinity)); }
        var (width, height) = Lay(available.Width, null);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        Lay(final.Width, (child, at) => child.Arrange(at));
        return final;
    }

    // Each shown child at the end of the line it is on, or the start of the next: the size it all
    // takes, arranging the children where asked.
    (double Width, double Height) Lay(double most, Action<UIElement, Rect>? arrange)
    {
        double x = 0, y = 0, line = 0, widest = 0;
        foreach (var child in Children)
        {
            if (child.Visibility != Visibility.Visible) { continue; }
            var size = child.DesiredSize;
            if (x > 0 && x + size.Width > most)
            {
                y += line + Spacing;
                x = 0;
                line = 0;
            }
            arrange?.Invoke(child, new Rect(x, y, size.Width, size.Height));
            x += size.Width + Spacing;
            line = Math.Max(line, size.Height);
            widest = Math.Max(widest, x - Spacing);
        }
        return (widest, y + line);
    }
}