using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// Send's check (ui.md §6.5, decided on #617): Send, or an answer's button, shows it for about a
// second once what it sent is delivered (ReceiptTiming.SendCheckHold), in place of its ring. The
// check icon's own geometry (design/icons/check.svg), stroked like Send's arrow and drawn at its
// size in the button's current ink, so it holds in a contrast theme as the arrow does (§9). A
// Path, never Segoe's CheckMark (the owner's decision). Drawn only: the line's live region says
// "Reply delivered".
public sealed partial class CheckGlyph : UserControl
{
    // On the icon's 20 px box, as AbIconSend draws its arrow.
    const string Check = "M4.5,10.5 L8,14 L15.5,6";

    public CheckGlyph()
    {
        IsTabStop = false;
        IsHitTestVisible = false;
        AutomationProperties.SetAccessibilityView(this, AccessibilityView.Raw);
        var path = new Microsoft.UI.Xaml.Shapes.Path
        {
            Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), Check),
            StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
        };
        // The button's current ink: Send's Bridge fill takes its on-accent colour, a contrast theme its own.
        path.SetBinding(Microsoft.UI.Xaml.Shapes.Shape.StrokeProperty, new Binding { Source = this, Path = new PropertyPath(nameof(Foreground)) });
        var box = new Grid { Width = 20, Height = 20 };
        box.Children.Add(path);
        Content = new Viewbox { Width = 16, Height = 16, Child = box };
    }
}