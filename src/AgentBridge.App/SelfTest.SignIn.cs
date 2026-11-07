using AgentBridge.App.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AgentBridge.App;

// Sign-in (#800, ui.md §6.13), on the demo's other machine: sessions whose agent needs sign-in say so
// on their rows, never in Needs you, under one banner for the machine, agent and account whose line
// names them as links; an error and
// a session not responding say so on their rows. Only Sign in asks the machine for a code; its sheet
// starts on Cancel, with Copy its one primary button, which Enter presses but on another button, whose Enter is its own; it shows the code selectable and named a character at a time, and its buttons are
// named for what they act on. A spoofed link is refused whole. Signed in, the sessions wait to confirm, never recovered until the
// host clears them; then the banner says so. The
// scenario's sessions go again after, so the passes after this one see the demo as it was.
static partial class SelfTest
{
    // A line's words as drawn, a link's included.
    static string Flat(IEnumerable<Inline> inlines) => string.Concat(inlines.Select(i => i switch { Run r => r.Text, Span s => Flat(s.Inlines), _ => "" }));

    static async Task CheckSignIn(MainWindow window, MainViewModel main, DemoHost remote, Action<bool, string> check, string outFile)
    {
        window.ShowPage("sessions");
        var page = window.Sessions;
        var waiting = main.Waiting;
        var root = window.RootElement.XamlRoot;
        static void Press(ButtonBase button) => ((IInvokeProvider)FrameworkElementAutomationPeer.CreatePeerForElement(button).GetPattern(PatternInterface.Invoke)).Invoke();
        UIElement? Sheet() => VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Select(p => p.Child).OfType<UIElement>().FirstOrDefault(c => Find<ContentDialog>(c).Any(d => AutomationProperties.GetAutomationId(d) == "SignInSheet"));
        Border? Banner() => Find<Border>(page).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "SignInBanner" && b.IsLoaded);
        Button? Verb() => Banner() is { } b ? Find<Button>(b).FirstOrDefault(x => AutomationProperties.GetAutomationId(x) == "SignInVerb" && x.Visibility == Visibility.Visible) : null;
        // As the row shows it: the error and the one not responding are driven by an agent.
        string Row(string id) => main.AllCards.FirstOrDefault(c => c.Id == id)?.RowStatus ?? "";
        try
        {
            await remote.NeedSignInAsync();
            var came = await Until(() => Verb() is not null && main.AllCards.Any(c => c.Id == "demo-stalled"), TimeSpan.FromSeconds(20));
            check(came, "draws one sign-in banner for the other machine's Copilot sessions that need sign-in");
            if (!came) { return; }
            // In the list's header, with Needs you: however many banners there are, they scroll with the
            // list and never push it, or a lower Sign in, off the window.
            var rows = Find<ListView>(page).FirstOrDefault(l => AutomationProperties.GetAutomationId(l) == "Rows");
            DependencyObject? up = Banner();
            while (up is not null && !ReferenceEquals(up, rows)) { up = VisualTreeHelper.GetParent(up); }
            check(rows is not null && up is not null && rows.ActualHeight >= 200, $"scrolls the sign-in banners with the session list, which keeps its height ({rows?.ActualHeight:F0} px)");
            var head = Find<TextBlock>(Banner()!).FirstOrDefault(t => AutomationProperties.GetAutomationId(t) == "SignInHead");
            // Its line names its sessions, each a link to it, with no line of titles of its own (ui.md §6.13).
            var line = Find<TextBlock>(Banner()!).FirstOrDefault(t => AutomationProperties.GetAutomationId(t) == "SignInAffected" && t.IsLoaded && t.ActualHeight > 0);
            var said = line is null ? "" : Flat(line.Inlines);
            var links = line?.Inlines.OfType<Hyperlink>().Select(l => Flat(l.Inlines)).ToList() ?? [];
            check(head?.Text == "Copilot on Build server needs sign-in · @demo-user" && said == "3 sessions affected: Coordinate the release, Fix the relay reconnect, Review the usage panel"
                && links.SequenceEqual(["Coordinate the release", "Fix the relay reconnect", "Review the usage panel"]) && AutomationProperties.GetName(Banner()!) == head.Text,
                $"heads the banner with the machine, agent and account, names the sessions it holds up in its line as links, and is named as shown ({head?.Text} / {said} / {links.Count} links)");
            var verb = Verb()!;
            check(verb.Content as string == "Sign in" && AutomationProperties.GetName(verb) == "Sign in to Copilot on Build server", $"names Sign in for what it signs in, its words first ({AutomationProperties.GetName(verb)})");
            check(DemoHost.SignInSessions.All(id => Row(id) == "Needs sign-in") && Row("demo-error-quota") == "Agent error" && Row("demo-stalled") == "Not responding",
                $"says Needs sign-in, Agent error and Not responding on their rows, never Idle or Working ({string.Join(", ", DemoHost.SignInSessions.Append("demo-error-quota").Append("demo-stalled").Select(Row))})");
            check(await Until(() => main.Waiting == waiting, TimeSpan.FromSeconds(5)), $"counts none of them in Needs you or the badge: the banner owns sign-in (owner decision 6675817525; {main.Waiting - waiting} more)");
            check(remote.SignInLog.Count == 0, "asks the machine for nothing until a person presses Sign in");
            await Picture(window.RootElement, Path.ChangeExtension(outFile, ".signin.png"));

            Press(verb);
            var opened = await Until(() => Sheet() is { } s && Find<TextBlock>(s).Any(t => AutomationProperties.GetAutomationId(t) == "SignInCode" && t.Text == "WDJB-MJHT"), TimeSpan.FromSeconds(10));
            check(opened, "opens the sheet with GitHub's code from the machine's signin.code");
            if (opened)
            {
                var sheet = Sheet()!;
                var code = Find<TextBlock>(sheet).First(t => AutomationProperties.GetAutomationId(t) == "SignInCode");
                check(code.IsTextSelectionEnabled && AutomationProperties.GetName(code) == "Code: W D J B, M J H T", "shows the code selectable, named a character at a time");
                var words = Find<TextBlock>(sheet).Select(Text).ToList();
                check(words.Contains("Open this page and enter the code.") && words.Contains("github.com/login/device") && words.Any(w => w.StartsWith("Expires ", StringComparison.Ordinal)), "says where to enter it, the page as plain text, and when it expires");
                var copy = Find<Button>(sheet).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "SignInCopy");
                var open = Find<Button>(sheet).FirstOrDefault(b => AutomationProperties.GetAutomationId(b) == "SignInOpen");
                check(AutomationProperties.GetName(copy!) == "Copy code for Copilot on Build server" && AutomationProperties.GetName(open!) == "Open github.com to sign in to Copilot on Build server",
                    "names Copy and Open for what they act on, their words first");
                var cancel = Find<Button>(sheet).FirstOrDefault(b => b.Name == "CloseButton");
                var startsOnCancel = await Until(() => ReferenceEquals(FocusManager.GetFocusedElement(root), cancel), TimeSpan.FromSeconds(5));
                check(startsOnCancel && AutomationProperties.GetName(cancel!) == "Cancel sign-in for Copilot on Build server", "starts the sheet on Cancel, named for the sign-in it cancels");
                // Copy is the one primary and the default; Cancel is secondary (Creative 6675007095).
                var accent = (Style)Application.Current.Resources["AccentButtonStyle"];
                var dialog = Find<ContentDialog>(sheet).FirstOrDefault();
                check(ReferenceEquals(copy!.Style, accent) && !ReferenceEquals(cancel!.Style, accent) && dialog?.DefaultButton == ContentDialogButton.None,
                    $"draws Copy as the one primary button and Cancel as secondary (Copy accent {ReferenceEquals(copy.Style, accent)}, Cancel accent {ReferenceEquals(cancel?.Style, accent)}, default {dialog?.DefaultButton})");
                // Enter on Copy copies the code, the sheet still open and nothing cancelled.
                var shown = main.SignInSheet!;
                var copies = 0;
                Action<string> counted = _ => copies++;
                shown.CopiedSaid += counted;
                async Task<string> Enter(Func<bool> done)
                {
                    var by = SelfTest.Press(window.Handle, VirtualKey.Enter, foreground: true, extended: false);
                    if (!await Until(done, TimeSpan.FromSeconds(2)) && by == "SendInput")
                    {
                        by = SelfTest.Press(window.Handle, VirtualKey.Enter, foreground: false, extended: false);
                        await Until(done, TimeSpan.FromSeconds(2));
                    }
                    return by;
                }
                copy!.Focus(FocusState.Keyboard);
                var onCopy = await Until(() => ReferenceEquals(FocusManager.GetFocusedElement(root), copy), TimeSpan.FromSeconds(2));
                var keyed = await Enter(() => copies > 0);
                check(onCopy && copies == 1 && main.SignInSheet is { Copied.Length: > 0 } && Sheet() is not null && remote.SignInLog.SequenceEqual([AuthState.Start]),
                    $"copies the code on Enter at Copy, the sheet still open and nothing cancelled (by {keyed}: '{main.SignInSheet?.Copied}', {copies} copies, {remote.SignInLog.Count} ops)");
                check(remote.SignInLog.SequenceEqual([AuthState.Start]), "sends signin.start once, on the press");
                await Picture(sheet, Path.ChangeExtension(outFile, ".signin-sheet.png"));
                // A focused button's Enter is its own (Creative 6691577488): on Cancel it cancels the
                // sign-in, and copies nothing.
                cancel!.Focus(FocusState.Keyboard);
                var onCancel = await Until(() => ReferenceEquals(FocusManager.GetFocusedElement(root), cancel), TimeSpan.FromSeconds(2));
                keyed = await Enter(() => Sheet() is null);
                var cancelled = await Until(() => Sheet() is null && remote.SignInLog.Count == 2, TimeSpan.FromSeconds(10));
                shown.CopiedSaid -= counted;
                check(onCancel && cancelled && remote.SignInLog[1] == AuthState.Cancel && copies == 1,
                    $"closes the sheet on Enter at Cancel, cancels the flow at the machine and copies nothing (by {keyed}: {copies} copies, {remote.SignInLog.Count} ops)");
                check(await Until(() => Verb() is { } v && ReferenceEquals(FocusManager.GetFocusedElement(root), v), TimeSpan.FromSeconds(5)), "puts focus back on Sign in when the sheet closes");
            }

            // A link that isn't GitHub's: nothing of it is drawn, and there is nothing to open.
            remote.NextVerificationUri = "https://github.com.evil.example/login/device";
            if (Verb() is { } again) { Press(again); }
            var refused = await Until(() => Sheet() is { } s && Find<TextBlock>(s).Any(t => t.Visibility == Visibility.Visible && t.Text.StartsWith("This sign-in code couldn", StringComparison.Ordinal)), TimeSpan.FromSeconds(10));
            var spoofShown = Sheet() is { } spoofed && Find<TextBlock>(spoofed).Any(t => t.Text.Contains("evil", StringComparison.Ordinal));
            var openShown = Sheet() is { } withOpen && Find<Button>(withOpen).Any(b => AutomationProperties.GetAutomationId(b) == "SignInOpen" && b.IsLoaded && b.ActualWidth > 0);
            check(refused && !spoofShown && !openShown, "refuses a sign-in link that isn't GitHub's: no link, no code, nothing to open");
            var retry = Sheet() is { } refusedRetry ? Find<Button>(refusedRetry).FirstOrDefault(b => b.Name == "PrimaryButton" && b.Visibility == Visibility.Visible) : null;
            check(retry is not null && retry.Content as string == "Try again" && AutomationProperties.GetName(retry) == "Try again to sign in to Copilot on Build server",
                $"offers Try again on the refused sheet, named for what it retries ({(retry is null ? "none" : AutomationProperties.GetName(retry))})");
            if (Sheet() is { } refusedSheet && Find<Button>(refusedSheet).FirstOrDefault(b => b.Name == "CloseButton") is { } close) { Press(close); }
            await Until(() => Sheet() is null, TimeSpan.FromSeconds(10));
            remote.NextVerificationUri = "https://github.com/login/device";

            // Signed in at github.com: the sessions wait to confirm, never recovered on signin.done alone.
            if (await Until(() => Verb() is not null, TimeSpan.FromSeconds(5))) { Press(Verb()!); }
            await Until(() => Sheet() is not null, TimeSpan.FromSeconds(10));
            // signin.done before the host's pending (#878): focus goes to the status line, never back to
            // Sign in, and stays on it as the banner is drawn anew for confirming.
            await remote.FinishSignInAsync("signedIn", pendingFirst: false);
            string Focused() => FocusManager.GetFocusedElement(root) is FrameworkElement fe ? $"{fe.GetType().Name} {AutomationProperties.GetAutomationId(fe)} '{AutomationProperties.GetName(fe)}'" : "nothing";
            bool OnStatus(string? line = null) => FocusManager.GetFocusedElement(root) is ContentControl c && c.IsLoaded && AutomationProperties.GetAutomationId(c) == "SignInStatus"
                && (line is null || AutomationProperties.GetName(c) == line);
            var beforePending = await Until(() => Sheet() is null && OnStatus(), TimeSpan.FromSeconds(10));
            check(beforePending, $"puts focus on the banner's status line when the sheet closes on success before the host's pending, not on Sign in (#878; focus on {Focused()})");
            await remote.SignedInPendingAsync();
            string Text0(string id) => main.AllCards.FirstOrDefault(c => c.Id == id)?.Line.Text ?? "";
            var confirming = await Until(() => Sheet() is null && main.SignIns.Any(b => b.Line == "Signed in \u00b7 waiting for 3 sessions to confirm")
                && DemoHost.SignInSessions.All(id => Text0(id) == "Signed in \u00b7 waiting for the session to confirm"), TimeSpan.FromSeconds(15));
            var onStatus = await Until(() => OnStatus("Signed in \u00b7 waiting for 3 sessions to confirm"), TimeSpan.FromSeconds(5));
            check(onStatus, $"puts focus on the banner's status line when the sheet closes on success, its button gone, and keeps it there as the banner is drawn anew (ui.md §6.13; focus on {Focused()})");
            check(confirming && !main.SignIns.Any(b => b.Line.Contains("recovered", StringComparison.Ordinal)) && Verb() is null && main.Waiting == waiting,
                $"says signed in and waiting for the sessions to confirm, never recovered, with nothing to press and nothing in Needs you ({string.Join(" | ", main.SignIns.Select(b => b.Line))})");
            // Signed in, then failed again while confirming: a new attempt cancelled puts focus back on
            // Sign in, not on the status line as the earlier success did (r4483045694). Signing in once
            // more still counts as this device's when the sessions recover.
            await remote.FailSignInAgainAsync();
            if (await Until(() => Verb() is not null, TimeSpan.FromSeconds(10))) { Press(Verb()!); }
            await Until(() => Sheet() is not null, TimeSpan.FromSeconds(10));
            if (Sheet() is { } againSheet && Find<Button>(againSheet).FirstOrDefault(b => b.Name == "CloseButton") is { } againCancel) { Press(againCancel); }
            var backOnVerb = await Until(() => Sheet() is null && Verb() is { } v && ReferenceEquals(FocusManager.GetFocusedElement(root), v), TimeSpan.FromSeconds(10));
            check(backOnVerb, $"puts focus back on Sign in when a later attempt is cancelled, after an earlier one signed in (r4483045694; focus on {Focused()})");
            if (Verb() is { } once) { Press(once); }
            await Until(() => Sheet() is not null, TimeSpan.FromSeconds(10));
            await remote.FinishSignInAsync("signedIn");
            await Until(() => Sheet() is null && main.SignIns.Any(b => b.Line == "Signed in \u00b7 waiting for 3 sessions to confirm"), TimeSpan.FromSeconds(15));
            await remote.ConfirmSignInAsync();
            var signedIn = await Until(() => main.SignIns.Any(b => b.Line == "Signed in \u00b7 3 sessions recovered") && DemoHost.SignInSessions.All(id => Row(id) == "Idle"), TimeSpan.FromSeconds(15));
            check(signedIn, "says the sessions recovered, and they are idle again, only once the host clears them");
            // Said from the window's root, so it is heard with another page open too.
            window.ShowPage("settings");
            await Task.Delay(100);
            static bool Shown(UIElement e)
            {
                for (DependencyObject? at = e; at is not null; at = VisualTreeHelper.GetParent(at)) { if (at is UIElement u && u.Visibility != Visibility.Visible) { return false; } }
                return true;
            }
            check(Shown(page.SignInSpeaker) && !Shown(page), "says sign-in changes from where a screen reader hears them while another page is open");
            window.ShowPage("sessions");
        }
        finally
        {
            remote.NextVerificationUri = "https://github.com/login/device";
            if (Sheet() is { } left && Find<Button>(left).FirstOrDefault(b => b.Name == "CloseButton") is { } close) { Press(close); }
            await remote.EndSignInScenarioAsync();
            await Until(() => main.AllCards.All(c => !c.Id.StartsWith("demo-signin", StringComparison.Ordinal) && c.Id is not ("demo-error-quota" or "demo-stalled")) && main.Waiting == waiting, TimeSpan.FromSeconds(15));
        }
    }
}
