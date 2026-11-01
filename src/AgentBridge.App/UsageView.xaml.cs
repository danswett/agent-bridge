using AgentBridge.App.Core;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

public sealed partial class UsageView : UserControl
{
    UsageViewModel viewModel = new();

    public UsageView() => InitializeComponent();

    public UsageViewModel ViewModel
    {
        get => viewModel;
        set
        {
            viewModel = value;
            Bindings.Update();
        }
    }

    public static Brush SeverityBrush(string severity) => severity switch
    {
        "crit" => Ui.Brush("AbDanger"),
        "warn" => Ui.Brush("AbNeeds"),
        _ => Ui.Brush("AbBridge"),
    };

    // The percentage is words, so a warning is in needs' text colour: the needs fill itself is
    // 3.70:1 on the light card, a glyph's contrast, not text's (design/tokens.json color.contrast).
    public static Brush SeverityTextBrush(string severity) => severity == "warn" ? Ui.Brush("AbNeedsText") : SeverityBrush(severity);

    public static Brush PaceBrush(bool ahead) => ahead ? Ui.Brush("AbNeedsText") : Ui.Brush("AbText2");
    public static double AgeOpacity(bool stale) => stale ? 1.0 : 0.8;
    public static string BarName(string label, string percent) => $"{label}: {percent} used";
    public static string Line(string amount, string reset) => string.Join(" · ", new[] { amount, reset }.Where(s => !string.IsNullOrEmpty(s)));
}
