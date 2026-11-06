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
    // A glyph that cross-fades with another in the same place (Send's arrow, ring and check, ui.md §6.5).
    public static double Fade(bool on) => on ? 1.0 : 0.0;

    public static Brush Brush(string key) => (Brush)Application.Current.Resources[key];

    // A reply's or an answer's receipt (ui.md §6.5, decided on #617): its stalls and waits, and
    // You answered, in text2; the exceptions in their own tone. In a contrast theme every word is
    // the window's text (§9), read from the system as it is now: the line is painted again as
    // Windows' colours change (ReceiptMarkView.Tone), maybe before XAML's own resources follow.
    public static Brush ReceiptBrush(Tone tone) =>
        new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? new SolidColorBrush(SystemColours.Of(Windows.UI.ViewManagement.UIElementType.WindowText))
        : tone == Tone.Progress ? Brush("AbText2")
        : ToneBrush(tone);

    public static Brush ToneBrush(Tone tone) => Brush(ToneKey(tone));

    public static string ToneKey(Tone tone) => tone switch
    {
        Tone.Ok => "AbBridge",
        Tone.Warn => "AbNeedsText",
        Tone.Bad => "AbDangerText",
        Tone.Progress => "AbBridge",
        _ => "AbText2",
    };

    // The design's brush for the theme an element is drawn in, which Brush can't give: it reads the
    // app's theme, and an element can ask for another. High contrast takes the system's, as Brush does.
    public static Brush ThemedBrush(string key, ElementTheme theme)
    {
        if (!new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast
            && Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source?.ToString().EndsWith(Design.DesignKeys.Source["ms-appx:///".Length..], StringComparison.OrdinalIgnoreCase) == true) is { } design
            && design.ThemeDictionaries.TryGetValue(theme == ElementTheme.Dark ? "Dark" : "Light", out var d) && d is ResourceDictionary themed
            && themed.TryGetValue(key, out var b) && b is Brush brush)
        {
            return brush;
        }
        return Brush(key);
    }

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

// Restart to update, from the banner or Settings (#516): when a session here is waiting on the
// person, or has a turn running, they are told what a restart now would interrupt and choose;
// Not now is the default, so Enter or Escape restarts nothing.
public static class RestartDialog
{
    // The confirmation Restart to update asks for, just before the host stops (AutoUpdater). A
    // dialog that cannot open (another already is) is Not now: nothing restarts unconfirmed.
    public static Func<string, Task<bool>> For(XamlRoot root) => async warning =>
    {
        try { return await ConfirmAsync(root, warning); }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
            AppLog.Write($"update: Restart to update: {e.Message}");
            return false;
        }
    };

    public static async Task<bool> ConfirmAsync(XamlRoot root, string? warning)
    {
        if (warning is null) { return true; }
        var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
        {
            XamlRoot = root,
            Title = "Restart to update now?",
            Content = warning,
            PrimaryButtonText = "Restart to update",
            CloseButtonText = "Not now",
            DefaultButton = Microsoft.UI.Xaml.Controls.ContentDialogButton.Close,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(dialog, "RestartToUpdateWarning");
        return await dialog.ShowAsync() == Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary;
    }
}
