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
        Tone.Bad => Brush("AbDangerText"),
        Tone.Progress => Brush("AbBridge"),
        _ => Brush("AbText2"),
    };

    // A status's words (docs/design/ui.md §2.2, §3): needs' text for a question, agent's text while
    // an agent drives, else secondary, as the meta line they lead is (§6.1, §6.2). Never active or
    // neutral, which are glyph colours; the glyph itself is StatusGlyphView. SessionRow and
    // SessionView set these through their Words states, as ThemeResources that follow a theme
    // change; this is what the self-test expects.
    public static string WordsKey(StatusTone tone) => tone switch
    {
        StatusTone.Needs => "AbNeedsText",
        StatusTone.Agent => "AbAgentText",
        _ => "AbText2",
    };

    public static Brush WordsBrush(StatusTone tone) => Brush(WordsKey(tone));
}
