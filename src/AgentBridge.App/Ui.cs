using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// Functions for x:Bind: visibility from a flag or a value, and the palette's brushes by name.
public static class Ui
{
    public static Visibility Show(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
    public static Visibility Hide(bool on) => on ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility Some(object? value) => value is null or "" ? Visibility.Collapsed : Visibility.Visible;
    public static Visibility Count(int n) => n > 0 ? Visibility.Visible : Visibility.Collapsed;
    public static bool Not(bool on) => !on;
    public static double Opaque(bool on) => on ? 1.0 : 0.55;

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    public static Brush ToneBrush(Tone tone) => tone switch
    {
        Tone.Ok => Brush("AbBridge"),
        Tone.Warn => Brush("AbNeedsText"),
        Tone.Bad => Brush("AbDanger"),
        Tone.Progress => Brush("AbBridge"),
        _ => Brush("AbText2"),
    };

    // A card's frame by what it shows: the PWA's colours (pwa/app.css .frame.*), in the design
    // roles: active while working or delegating, needs while waiting, neutral while ending.
    public static Brush StateBrush(string view) => view switch
    {
        "working" or "delegating" => Brush("AbActive"),
        "waiting" => Brush("AbNeeds"),
        "ending" => Brush("AbNeutral"),
        _ => Brush("AbStroke2"),
    };

    // The status dot beside the words (pwa/app.css .dot): a glyph, so neutral when nothing runs,
    // where the frame is only a hairline.
    public static Brush DotBrush(string view) => view switch
    {
        "working" or "delegating" or "waiting" => StateBrush(view),
        _ => Brush("AbNeutral"),
    };

    // The edge and glow: purple in place of the state's colour while an agent drives the session
    // (pwa/app.css .frame.agent).
    public static Brush EdgeBrush(string view, bool agent) => agent ? Brush("AbAgent") : StateBrush(view);

    public static DoubleCollection StateDash(string view) => view switch
    {
        "delegating" => [4, 3],
        "ending" => [1, 2],
        _ => [],
    };

    public static double StateThickness(string view) => view is "working" or "waiting" ? 2 : 1;
}
