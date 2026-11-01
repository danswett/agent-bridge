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

    public static Brush PaceBrush(bool ahead) => ahead ? Ui.Brush("AbNeedsText") : Ui.Brush("AbText2");
    public static double AgeOpacity(bool stale) => stale ? 1.0 : 0.8;
    public static string BarName(string label, string percent) => $"{label}: {percent} used";
    public static string Line(string amount, string reset) => string.Join(" · ", new[] { amount, reset }.Where(s => !string.IsNullOrEmpty(s)));
}
