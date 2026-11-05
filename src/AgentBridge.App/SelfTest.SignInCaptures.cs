using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// The sign-in captures owed before sessions.signin is turned on (Creative, #800 6655285995): the
// banner and the sheet in Light and Dark (high contrast in CheckHighContrast, on CI), and every state
// they can be in: a way to sign in this app doesn't know (no claim, nothing to press), the code
// shown, Failed, an expired code, and signed in waiting for the sessions to confirm. The behaviour
// is CheckSignIn's to prove; these are the pictures, each with a check that it is what it says.
// One scenario for all of them, as the self-test's time is counted (build/app-self-test.ps1). The
// scenario's sessions go again after, as they came.
static partial class SelfTest
{
    static async Task CaptureSignIn(MainWindow window, MainViewModel main, DemoHost remote, Action<bool, string> check, string outFile, bool contrast = false)
    {
        window.ShowPage("sessions");
        var page = window.Sessions;
        var waiting = main.Waiting;
        var root = window.RootElement.XamlRoot;
        static void Press(ButtonBase button) => ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();
        UIElement? Sheet() => VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Select(p => p.Child).OfType<UIElement>().FirstOrDefault(c => Find<ContentDialog>(c).Any(d => AutomationProperties.GetAutomationId(d) == "SignInSheet"));
        Border? Banner() => Find<Border>(page).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "SignInBanner" && b.IsLoaded);
        Button? Verb() => Banner() is { } b ? Find<Button>(b).FirstOrDefault(x => AutomationProperties.GetAutomationId(x) == "SignInVerb" && x.Visibility == Visibility.Visible) : null;
        string Line() => main.SignIns.FirstOrDefault()?.Line ?? "";
        string File(string what, string look = "") => Path.ChangeExtension(outFile, $".signin-{what}{look}.png");
        // What the banner sits on: the first opaque fill under it.
        Windows.UI.Color Under(DependencyObject at)
        {
            for (var up = VisualTreeHelper.GetParent(at); up is not null; up = VisualTreeHelper.GetParent(up))
            {
                var fill = up switch { Panel p => p.Background, Border b => b.Background, Control c => c.Background, _ => null };
                if (fill is SolidColorBrush { Color.A: 255 } solid) { return solid.Color; }
            }
            return window.RootElement.ActualTheme == ElementTheme.Dark ? Windows.UI.Color.FromArgb(255, 0x20, 0x23, 0x26) : Windows.UI.Color.FromArgb(255, 255, 255, 255);
        }
        Task Shot(UIElement element, string file) => Picture(element, file, Under(element));
        // The sheet is a popup, outside the root whose theme is set here; with Windows itself in that
        // theme it would follow, so it is given the same.
        async Task Theme(ElementTheme theme)
        {
            window.RootElement.RequestedTheme = theme;
            if (Sheet() is { } open) { foreach (var d in Find<ContentDialog>(open)) { d.RequestedTheme = theme; } }
            await Task.Delay(300);
        }
        async Task Both(Func<string, Task> take)
        {
            if (contrast) { await Task.Delay(300); await take(".hc"); return; }
            await Theme(ElementTheme.Light);
            await take("");
            await Theme(ElementTheme.Dark);
            await take(".dark");
            await Theme(ElementTheme.Light);
        }
        async Task CloseSheet()
        {
            if (Sheet() is { } open && Find<Button>(open).FirstOrDefault(b => b.Name == "CloseButton") is { } close) { Press(close); }
            await Until(() => Sheet() is null, TimeSpan.FromSeconds(10));
        }
        try
        {
            AppLog.Write("self-test: sign-in captures: the banner");
            await remote.NeedSignInAsync();
            var came = await Until(() => Verb() is { Content: "Sign in" }, TimeSpan.FromSeconds(20));
            check(came, $"draws the sign-in banner for the captures{(contrast ? " in high contrast" : "")}");
            if (!came) { return; }
            // The banner itself: CheckSignIn's result.signin.png has it in the whole window.
            await Both(look => Banner() is { } b ? Shot(b, File("banner", look)) : Task.CompletedTask);

            AppLog.Write("self-test: sign-in captures: the sheet");
            Press(Verb()!);
            var opened = await Until(() => main.SignInSheet is { HasCode: true } && Sheet() is not null && Line().StartsWith("Waiting for sign-in", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
            check(opened, $"captures the sheet with the code, and the banner while it is shown{(contrast ? " in high contrast" : "")} ({Line()})");
            if (opened)
            {
                await Both(async look =>
                {
                    if (Sheet() is { } sheet) { await Picture(sheet, File("sheet", look)); }
                    if (Banner() is { } shown) { await Shot(shown, File("code-shown", look)); }
                });
            }
            if (contrast) { return; }

            AppLog.Write("self-test: sign-in captures: failed");
            await remote.FinishSignInAsync("failed");
            var failed = await Until(() => Sheet() is null && Line() == "Sign-in failed" && Verb() is { Content: "Try again" }, TimeSpan.FromSeconds(10));
            check(failed, $"captures a failed sign-in's banner, with Try again ({Line()})");
            if (failed) { await Task.Delay(300); await Shot(Banner()!, File("failed")); }

            AppLog.Write("self-test: sign-in captures: expired");
            remote.NextCodeLife = TimeSpan.FromSeconds(2);
            if (Verb() is { } retry) { Press(retry); }
            // The app's one-second clock (App.xaml.cs) doesn't run under the self-test: ticked here, as it
            // would be, so the code runs out on the sheet.
            var expired = await Until(() => { main.Tick(); return main.SignInSheet is { Expired: true } && Sheet() is not null; }, TimeSpan.FromSeconds(15));
            remote.NextCodeLife = TimeSpan.FromMinutes(15);
            var words = Sheet() is { } es ? Find<TextBlock>(es).Where(t => t.Visibility == Visibility.Visible).Select(Text).ToList() : [];
            check(expired && words.Contains("Code expired") && !words.Any(w => w.Contains("WDJB", StringComparison.Ordinal)), $"captures the sheet of a code that ran out, its code gone ({string.Join(" | ", words)})");
            if (expired) { await Task.Delay(300); await Picture(Sheet()!, File("expired")); }
            await CloseSheet();

            AppLog.Write("self-test: sign-in captures: confirming");
            if (await Until(() => Verb() is not null, TimeSpan.FromSeconds(5))) { Press(Verb()!); }
            await Until(() => main.SignInSheet is { HasCode: true }, TimeSpan.FromSeconds(10));
            await remote.FinishSignInAsync("signedIn");
            var confirming = await Until(() => Sheet() is null && Line() == "Signed in \u00b7 waiting for 3 sessions to confirm", TimeSpan.FromSeconds(15));
            check(confirming, $"captures the banner signed in and waiting for the sessions to confirm ({Line()})");
            if (confirming) { await Task.Delay(300); await Shot(Banner()!, File("confirming")); }

            // A way to sign in this app doesn't know: no claim, so nothing to press, and never "at the
            // machine" (6648000166). Needed again first, as a new failure would make it.
            AppLog.Write("self-test: sign-in captures: no claim");
            await remote.FailSignInAgainAsync();
            await remote.SignInWayAsync("rdp");
            var noClaim = await Until(() => main.SignIns.FirstOrDefault()?.Head.Contains("needs sign-in", StringComparison.Ordinal) == true && Banner() is not null && Verb() is null, TimeSpan.FromSeconds(10));
            var said = Banner() is { } nb ? string.Join(" ", Find<TextBlock>(nb).Where(t => t.Visibility == Visibility.Visible && t.ActualWidth > 0).Select(Text)) : "";
            check(noClaim && !said.Contains("at the machine", StringComparison.Ordinal), $"captures a banner whose way to sign in makes no claim: nothing to press, never at the machine ({Line()})");
            if (noClaim) { await Task.Delay(300); await Shot(Banner()!, File("no-claim")); }
        }
        finally
        {
            AppLog.Write("self-test: sign-in captures: done");
            remote.NextCodeLife = TimeSpan.FromMinutes(15);
            await CloseSheet();
            await remote.EndSignInScenarioAsync();
            await Until(() => main.AllCards.All(c => !c.Id.StartsWith("demo-signin", StringComparison.Ordinal) && c.Id is not ("demo-error-quota" or "demo-stalled")) && main.Waiting == waiting, TimeSpan.FromSeconds(15));
            window.RootElement.RequestedTheme = ElementTheme.Default;
            AppLog.Write("self-test: sign-in captures: gone");
        }
    }
}
