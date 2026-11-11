using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// The sign-in captures owed before sessions.signin is turned on (Creative, #800 6655285995, and
// #895 6660406957, 6660693656): every state of the banner and the sheet, in Light and Dark, and in
// high contrast on CI (CheckHighContrast). They are the banner as it first comes (its sessions links
// in its line, #963), a Needs sign-in
// row, a signIn this app doesn't know (no claim, nothing to press), a sign-in under way from another
// device (Show code), the sheet with its code (Copy its one primary) and the banner behind it, Failed, an expired code,
// and signed in waiting for the sessions to confirm. The behaviour is CheckSignIn's to prove; these
// are the pictures, each with a check that it is what it says. One scenario for all of them, as the
// self-test's time is counted (build/app-self-test.ps1). The scenario's sessions go again after.
static partial class SelfTest
{
    static async Task CaptureSignIn(MainWindow window, MainViewModel main, DemoHost remote, Action<bool, string> check, string outFile, bool contrast = false)
    {
        window.ShowPage("sessions");
        var page = window.Sessions;
        var waiting = main.Waiting;
        var root = window.RootElement.XamlRoot;
        var where = contrast ? " in high contrast" : "";
        static void Press(ButtonBase button) => ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();
        UIElement? Sheet() => VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Select(p => p.Child).OfType<UIElement>().FirstOrDefault(c => Find<ContentDialog>(c).Any(d => AutomationProperties.GetAutomationId(d) == "SignInSheet"));
        Border? Banner() => Find<Border>(page).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "SignInBanner" && b.IsLoaded);
        Button? Verb() => Banner() is { } b ? Find<Button>(b).FirstOrDefault(x => AutomationProperties.GetAutomationId(x) == "SignInVerb" && x.Visibility == Visibility.Visible) : null;
        string Line() => main.SignIns.FirstOrDefault()?.Line ?? "";
        string File(string what, string look) => Path.ChangeExtension(outFile, $".signin-{what}{look}.png");
        // What an element sits on: the first opaque fill under it, else the theme's shell from the
        // generated brushes (Dark's window is see-through), never a colour of the self-test's own.
        Windows.UI.Color Under(DependencyObject at)
        {
            for (var up = VisualTreeHelper.GetParent(at); up is not null; up = VisualTreeHelper.GetParent(up))
            {
                var fill = up switch { Panel p => p.Background, Border b => b.Background, Control c => c.Background, _ => null };
                if (fill is SolidColorBrush { Color.A: 255 } solid) { return solid.Color; }
            }
            var theme = window.RootElement.ActualTheme == ElementTheme.Dark ? "Dark" : "Light";
            return Application.Current.Resources.MergedDictionaries
                .Select(d => d.ThemeDictionaries.TryGetValue(theme, out var td) && td is ResourceDictionary rd && rd.TryGetValue("AbShell", out var b) && b is SolidColorBrush s ? s.Color : (Windows.UI.Color?)null)
                .FirstOrDefault(c => c is not null) ?? default;
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
        // Each picture in Light and Dark; in the high-contrast pass, as Windows draws it.
        async Task Looks(Func<string, Task> take)
        {
            if (contrast) { await Task.Delay(300); await take(".hc"); return; }
            await Theme(ElementTheme.Light);
            await take("");
            await Theme(ElementTheme.Dark);
            await take(".dark");
            await Theme(ElementTheme.Light);
        }
        Task Banners(string what) => Looks(look => Banner() is { } b ? Shot(b, File(what, look)) : Task.CompletedTask);
        async Task CloseSheet()
        {
            if (Sheet() is { } open && Find<Button>(open).FirstOrDefault(b => b.Name == "CloseButton") is { } close) { Press(close); }
            await Until(() => Sheet() is null, TimeSpan.FromSeconds(10));
        }
        try
        {
            AppLog.Write($"self-test: sign-in captures{where}: the banner");
            await remote.NeedSignInAsync();
            var came = await Until(() => Verb() is { Content: "Sign in" }, TimeSpan.FromSeconds(20));
            check(came, $"draws the sign-in banner for the captures{where}");
            if (!came) { return; }
            if (contrast && Verb() is { } signIn)
            {
                var signInPlate = await AccentPlate(signIn);
                check(signInPlate is null, $"draws the banner's Sign in in Highlight and HighlightText only{where} ({signInPlate})");
            }
            // The banner as it first comes: its line names its sessions, each a link (ui.md §6.13).
            var linked = await Until(() => Banner() is { } fresh && Find<TextBlock>(fresh).Any(t => AutomationProperties.GetAutomationId(t) == "SignInAffected" && t.ActualHeight > 0 && t.Inlines.OfType<Hyperlink>().Count() == 3), TimeSpan.FromSeconds(5));
            check(linked, $"captures the banner with its sessions as links in its line{where} ({Line()})");
            await Banners("banner");

            // A row of a session that needs sign-in, as the list draws it.
            var card = main.AllCards.FirstOrDefault(c => c.Id == DemoHost.SignInSessions[0]);
            if (card is not null) { page.Rows.ScrollIntoView(card); }
            SessionRow? Row() => Find<SessionRow>(page.Rows).FirstOrDefault(r => r.Card?.Id == DemoHost.SignInSessions[0] && r.ActualHeight > 0);
            var rowShown = await Until(() => Row() is not null, TimeSpan.FromSeconds(5));
            var rowWords = Row() is { } sr && ById<TextBlock>(sr, "RowFacts") is { } facts ? Text(facts) : "";
            check(rowShown && rowWords.StartsWith("Needs sign-in", StringComparison.Ordinal), $"captures a row that needs sign-in{where} ({rowWords})");
            if (rowShown)
            {
                await Looks(async look =>
                {
                    if (Row() is not { } r) { check(false, $"keeps the sign-in row drawn for its typography check{look}"); return; }
                    await CheckSignInRowTypography(window, main, r, check, look);
                    await Shot(r, File("row", look));
                });
            }
            if (page.Rows.Items.Count > 0) { page.Rows.ScrollIntoView(page.Rows.Items[0]); }

            // A way to sign in this app doesn't know: no claim, so nothing to press, and never "at the
            // machine" (6648000166).
            AppLog.Write($"self-test: sign-in captures{where}: no claim");
            await remote.SignInWayAsync("rdp");
            var noClaim = await Until(() => Banner() is not null && Verb() is null && Line().StartsWith("3 sessions affected: ", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
            check(noClaim, $"captures a banner whose way to sign in makes no claim{where}: nothing to press, never at the machine ({Line()})");
            if (noClaim) { await Banners("no-claim"); }
            await remote.SignInWayAsync("phone");
            await Until(() => Verb() is { Content: "Sign in" }, TimeSpan.FromSeconds(10));

            // A sign-in another device runs: Show code, the sheet closed here.
            AppLog.Write($"self-test: sign-in captures{where}: show code");
            await remote.SignInUnderWayAsync(true);
            var elsewhere = await Until(() => Sheet() is null && Verb() is { Content: "Show code" } && Line() == "Waiting for sign-in", TimeSpan.FromSeconds(10));
            check(elsewhere, $"captures a sign-in under way from another device{where}: Show code, the sheet closed ({Line()})");
            if (elsewhere) { await Banners("show-code"); }
            await remote.SignInUnderWayAsync(false);
            await Until(() => Verb() is { Content: "Sign in" }, TimeSpan.FromSeconds(10));

            AppLog.Write($"self-test: sign-in captures{where}: the sheet");
            Press(Verb()!);
            var opened = await Until(() => main.SignInSheet is { HasCode: true } && Sheet() is not null && Line().StartsWith("Waiting for sign-in", StringComparison.Ordinal), TimeSpan.FromSeconds(10));
            check(opened, $"captures the sheet with the code, and the banner while it is shown{where} ({Line()})");
            if (opened)
            {
                // Copy the one primary (accent), Cancel secondary (Creative 6675007095).
                var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
                var copy = Sheet() is { } shown ? Find<Button>(shown).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "SignInCopy") : null;
                var cancel = Sheet() is { } open ? Find<Button>(open).FirstOrDefault(b => b.Name == "CloseButton") : null;
                check(ReferenceEquals(copy?.Style, accent) && cancel is not null && !ReferenceEquals(cancel.Style, accent), $"captures the sheet with Copy its one primary button{where}");
                await Looks(async look =>
                {
                    if (Sheet() is { } sheet) { await Picture(sheet, File("sheet", look)); }
                    if (Banner() is { } shown) { await Shot(shown, File("code-shown", look)); }
                });
            }

            // The demo's done carries no reason, as the protocol's signin.done has none (#834, #841).
            AppLog.Write($"self-test: sign-in captures{where}: failed");
            await remote.FinishSignInAsync("failed");
            var failed = await Until(() => Sheet() is null && Line() == "Sign-in failed" && Verb() is { Content: "Try again" }, TimeSpan.FromSeconds(10));
            check(failed, $"captures a failed sign-in's banner, with Try again{where} ({Line()})");
            if (failed) { await Banners("failed"); }

            AppLog.Write($"self-test: sign-in captures{where}: expired");
            remote.NextCodeLife = TimeSpan.FromSeconds(2);
            if (Verb() is { } retry) { Press(retry); }
            // The app's one-second clock (App.xaml.cs) doesn't run under the self-test: ticked here, as it
            // would be, so the code runs out on the sheet.
            var expired = await Until(() => { main.Tick(); return main.SignInSheet is { Expired: true } && Sheet() is not null; }, TimeSpan.FromSeconds(15));
            remote.NextCodeLife = TimeSpan.FromMinutes(15);
            var words = Sheet() is { } es ? Find<TextBlock>(es).Where(t => t.Visibility == Visibility.Visible).Select(Text).ToList() : [];
            check(expired && words.Contains("Code expired") && !words.Any(w => w.Contains("WDJB", StringComparison.Ordinal)), $"captures the sheet of a code that ran out, its code gone{where} ({string.Join(" | ", words)})");
            if (expired) { await Looks(look => Sheet() is { } sheet ? Picture(sheet, File("expired", look)) : Task.CompletedTask); }
            await CloseSheet();

            AppLog.Write($"self-test: sign-in captures{where}: confirming");
            if (await Until(() => Verb() is not null, TimeSpan.FromSeconds(5))) { Press(Verb()!); }
            await Until(() => main.SignInSheet is { HasCode: true }, TimeSpan.FromSeconds(10));
            await remote.FinishSignInAsync("signedIn");
            var confirming = await Until(() => Sheet() is null && Line() == "Signed in \u00b7 waiting for 3 sessions to confirm", TimeSpan.FromSeconds(15));
            check(confirming, $"captures the banner signed in and waiting for the sessions to confirm{where} ({Line()})");
            if (confirming) { await Banners("confirming"); }
        }
        finally
        {
            remote.NextCodeLife = TimeSpan.FromMinutes(15);
            await CloseSheet();
            await remote.EndSignInScenarioAsync();
            await Until(() => main.AllCards.All(c => !c.Id.StartsWith("demo-signin", StringComparison.Ordinal) && c.Id is not ("demo-error-quota" or "demo-stalled")) && main.Waiting == waiting, TimeSpan.FromSeconds(15));
            if (!contrast) { window.RootElement.RequestedTheme = ElementTheme.Default; }
            AppLog.Write($"self-test: sign-in captures{where}: gone");
        }
    }

    static async Task CheckSignInRowTypography(MainWindow window, MainViewModel main, SessionRow row, Action<bool, string> check, string look)
    {
        var facts = ById<TextBlock>(row, "RowFacts");
        var words = row.FindName("StatusWords") as Run;
        if (facts is null || words is null || words.Text != "Needs sign-in")
        {
            check(false, $"finds the drawn Needs sign-in status for its typography check{look}");
            return;
        }

        // Inter's tabular figures substitute a wider hyphen too (#903). Compare the actual
        // row's character advances with the same face's proportional text, not just its string.
        var referenceWords = new Run { Text = words.Text };
        var reference = new TextBlock
        {
            Style = (Style)Application.Current.Resources["AbTypeMeta"],
            FontFamily = facts.FontFamily,
            FontSize = facts.FontSize,
            FontWeight = facts.FontWeight,
            FontStyle = facts.FontStyle,
            CharacterSpacing = facts.CharacterSpacing,
            IsTextScaleFactorEnabled = facts.IsTextScaleFactorEnabled,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Typography.SetNumeralAlignment(referenceWords, Microsoft.UI.Xaml.FontNumeralAlignment.Proportional);
        reference.Inlines.Add(referenceWords);
        var tabularWords = new Run { Text = words.Text };
        var tabular = new TextBlock
        {
            Style = reference.Style,
            FontFamily = reference.FontFamily,
            FontSize = reference.FontSize,
            FontWeight = reference.FontWeight,
            FontStyle = reference.FontStyle,
            CharacterSpacing = reference.CharacterSpacing,
            IsTextScaleFactorEnabled = reference.IsTextScaleFactorEnabled,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        Typography.SetNumeralAlignment(tabularWords, Microsoft.UI.Xaml.FontNumeralAlignment.Tabular);
        tabular.Inlines.Add(tabularWords);
        var host = new StackPanel { Opacity = 0, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left };
        host.Children.Add(reference);
        host.Children.Add(tabular);
        var context = main.AllMachines[0].Cards;
        var counted = DemoHost.Delegating(context.Time.GetUtcNow()) with { Id = "demo-row-typography", Subagents = [] };
        counted = counted with { Activity = counted.Activity with { Background = Background.Of(1, 1) } };
        var countingCard = new SessionCardViewModel(counted, context);
        var counting = new SessionRow { Card = countingCard };
        host.Children.Add(counting);
        var root = (Panel)window.RootElement;
        root.Children.Add(host);
        try
        {
            host.UpdateLayout();
            await Task.Delay(50);
            static double Advance(Run run, int at)
            {
                if (at < 0) { return double.NaN; }
                var start = run.ContentStart.GetPositionAtOffset(at, LogicalDirection.Forward);
                var next = run.ContentStart.GetPositionAtOffset(at + 1, LogicalDirection.Forward);
                return start is null || next is null ? double.NaN
                    : next.GetCharacterRect(LogicalDirection.Forward).X - start.GetCharacterRect(LogicalDirection.Forward).X;
            }
            var hyphen = words.Text.IndexOf('-');
            var drawn = Advance(words, hyphen);
            var normal = Advance(referenceWords, hyphen);
            var wide = Advance(tabularWords, hyphen);
            check(normal > 0 && wide > normal + 1, $"distinguishes the font's proportional and tabular hyphens{look} ({normal:F2} / {wide:F2} px)");
            check(normal > 0 && Math.Abs(drawn - normal) <= 0.25,
                $"draws sign-in with proportional hyphen spacing in the row status{look} ({drawn:F2} px, {normal:F2} expected)");

            var age = ById<TextBlock>(row, "Age");
            check(Typography.GetNumeralAlignment(facts) == Microsoft.UI.Xaml.FontNumeralAlignment.Tabular
                && age is not null && Typography.GetNumeralAlignment(age) == Microsoft.UI.Xaml.FontNumeralAlignment.Tabular,
                $"keeps the row's other metadata and changing age tabular{look}");
            check(words.FontFamily.Source == facts.FontFamily.Source && words.FontSize == facts.FontSize
                && words.CharacterSpacing == facts.CharacterSpacing,
                $"keeps the row status in its meta face, size and tracking{look}");

            var countWords = counting.FindName("StatusWords") as Run;
            check(countWords is not null && countWords.Text == "Delegating to 1 agent"
                && Typography.GetNumeralAlignment(countWords) == Microsoft.UI.Xaml.FontNumeralAlignment.Tabular,
                $"keeps mixed words and a delegate count in the status run tabular{look} ({countWords?.Text})");
            if (countWords is not null)
            {
                var one = Advance(countWords, countWords.Text.IndexOf('1'));
                countingCard.Update(counted with { Activity = counted.Activity with { Background = Background.Of(8, 1) } });
                host.UpdateLayout();
                await Task.Delay(50);
                var eight = Advance(countWords, countWords.Text.IndexOf('8'));
                check(countWords.Text == "Delegating to 8 agents" && one > 0 && Math.Abs(one - eight) <= 0.25,
                    $"keeps the count column width when a delegate count changes from 1 to 8{look} ({one:F2} / {eight:F2} px)");
            }
            counting.Card = row.Card;
            host.UpdateLayout();
            await Task.Delay(50);
            check(countWords is not null && countWords.Text == words.Text
                && Typography.GetNumeralAlignment(countWords) == Microsoft.UI.Xaml.FontNumeralAlignment.Proportional,
                $"uses proportional status words when a counted row is reused for sign-in{look}");
        }
        finally { root.Children.Remove(host); countingCard.Detach(); }
    }
}
