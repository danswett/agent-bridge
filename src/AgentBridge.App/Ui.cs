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
        Tone.Ok => Brush("AbOk"),
        Tone.Warn => Brush("AbWarningText"),
        Tone.Bad => Brush("AbDanger"),
        Tone.Progress => Brush("AbPrimary"),
        _ => Brush("AbMuted"),
    };

    // A card's frame by what it shows: the PWA's colours (pwa/app.css .frame.*).
    public static Brush StateBrush(string view) => view switch
    {
        "working" or "delegating" => Brush("AbPrimary"),
        "waiting" => Brush("AbWarning"),
        _ => Brush("AbBorder"),
    };

    public static DoubleCollection StateDash(string view) => view switch
    {
        "delegating" => [4, 3],
        "ending" => [1, 2],
        _ => [],
    };

    public static double StateThickness(string view) => view is "working" or "waiting" ? 2 : 1;
}
