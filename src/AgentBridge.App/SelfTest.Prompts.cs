using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// A prompt the person didn't type (#489, ui.md §6.4): a schedule's or an agent's prompt is drawn
// as a collapsed line at the conversation column's start in agent colour, with a clock or the
// driven diamond, never as the person's bubble; Show prompt opens it inside a thin agent rule; a
// schedule's repeat folds to "same as last time". The demo sends none on its own, so the check
// sends them, and puts the session back as it was afterwards.
static partial class SelfTest
{
    const string PromptSession = "019a1b2c-codex-example";
    const string Monitor = "Check the deploy pipeline for agent-bridge. If the last run on main failed, find the failing job,\nread its log, and say which test failed and why. If it passed, say nothing new and stop.\nThen look at the open pull requests: for each one whose checks are red, name the check.\nKeep the answer under ten lines. Do not change any file.";

    static async Task CheckPrompts(MainWindow window, MainViewModel main, DemoHost demo, Action<bool, string> check, string outFile)
    {
        if (main.AllCards.FirstOrDefault(c => c.Id == PromptSession) is not { } card) { check(false, "finds a session to send a scheduled prompt to"); return; }
        var original = card.Session;
        var turns = original.Activity.TurnCount ?? 0;
        static string Colour(object? b) => b is SolidColorBrush s ? s.Color.ToString() : "none";
        static bool Shown(FrameworkElement? e)
        {
            for (DependencyObject? at = e; at is not null; at = VisualTreeHelper.GetParent(at))
            {
                if (at is UIElement { Visibility: Visibility.Collapsed }) { return false; }
            }
            return e is { ActualHeight: > 0 };
        }
        Resize(window, 1440, 900);
        try
        {
            var now = DateTimeOffset.UtcNow;
            await demo.Upsert(original with { Status = SessionStatus.Idle, StatusSince = now, UpdatedAt = now, Question = null, Activity = original.Activity with { UserMessage = Monitor, UserMessageFrom = UserMessageFrom.Schedule, UserMessageLabel = "4", TurnCount = turns } });
            var arrived = await Until(() => card.HasPrompt, TimeSpan.FromSeconds(10));
            var view = arrived ? await Choose(window, main, PromptSession, "Prompt") : null;
            var line = view is null ? null : ById<StackPanel>(view, "Prompt");
            var scroll = view is null ? null : ById<ScrollViewer>(view, "Conversation");
            var column = scroll is null ? null : Find<StackPanel>(scroll).FirstOrDefault(s => s.Name == "Conversation");
            if (view is null || line is null || column is null || !await Until(() => Shown(line), TimeSpan.FromSeconds(5))) { check(false, "draws a scheduled prompt's line in the conversation"); return; }
            await Task.Delay(300);

            var label = ById<TextBlock>(view, "PromptLabelText");
            var at = line.TransformToVisual(column).TransformPoint(default).X;
            var inner = column.ActualWidth - column.Padding.Left - column.Padding.Right;
            var problems = new List<string>();
            if (Shown(ById<Border>(view, "YourMessage"))) { problems.Add("your bubble shown"); }
            if (label is null || Text(label) != "Scheduled prompt · #4") { problems.Add($"label \"{(label is null ? "none" : Text(label))}\""); }
            if (label is not null && Colour(label.Foreground) != Colour(Themed(label, "AbAgentText"))) { problems.Add($"label in {Colour(label?.Foreground)}"); }
            if (!Shown(ById<ContentControl>(view, "PromptClock")) || Shown(ById<ContentControl>(view, "PromptDiamond"))) { problems.Add("no clock"); }
            if (Math.Abs(at - column.Padding.Left) > 1 || Math.Abs(line.ActualWidth - inner) > 1) { problems.Add($"line at {at:0}+{line.ActualWidth:0} in a column {column.Padding.Left:0}+{inner:0}"); }
            check(problems.Count == 0, $"draws a scheduled prompt as a line at the column's start, a clock and \"Scheduled prompt · #4\" in agent colour, not your bubble{(problems.Count > 0 ? $": not {string.Join(", ", problems)}" : "")}");

            var preview = ById<TextBlock>(view, "PromptPreview");
            check(preview is { MaxLines: 2 } && Shown(preview) && preview.ActualHeight <= preview.LineHeight * 2 + 2 + (preview.LineHeight == 0 ? 40 : 0),
                $"shows two lines of it under the label ({preview?.ActualHeight:0} px)");

            var toggle = ById<DisclosureButton>(view, "ShowPrompt");
            var peer = toggle is null ? null : FrameworkElementAutomationPeer.CreatePeerForElement(toggle)?.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider;
            check(toggle is not null && peer?.ExpandCollapseState == ExpandCollapseState.Collapsed && AutomationProperties.GetName(toggle) == "Scheduled prompt 4, Show prompt" && toggle is { IsTabStop: true, IsEnabled: true },
                $"offers Show prompt collapsed, a control Tab reaches, named \"{(toggle is null ? "" : AutomationProperties.GetName(toggle))}\"");

            // Compact, as the PWA's (#504's design review): a chevron leading its words and pointing
            // right, the words at the label's size, the control as wide as they are.
            var chevron = ById<ContentControl>(view, "PromptChevron");
            var words = ById<TextBlock>(view, "PromptToggleText");
            static float Angle(UIElement e) => Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(e).RotationAngleInDegrees;
            var compact = new List<string>();
            if (toggle is null || chevron is null || words is null) { compact.Add("no chevron or words"); }
            else
            {
                var chevronAt = chevron.TransformToVisual(toggle).TransformPoint(default).X;
                var wordsAt = words.TransformToVisual(toggle).TransformPoint(default).X;
                if (chevronAt >= wordsAt) { compact.Add($"chevron at {chevronAt:0}, words at {wordsAt:0}"); }
                if (Angle(chevron) != 0) { compact.Add($"chevron turned {Angle(chevron):0}°"); }
                if (label is not null && words.FontSize != label.FontSize) { compact.Add($"words at {words.FontSize} px, the label at {label.FontSize} px"); }
                if (toggle.ActualWidth > chevron.ActualWidth + words.ActualWidth + 4 + toggle.Padding.Left + toggle.Padding.Right + 1) { compact.Add($"{toggle.ActualWidth:0} px wide"); }
                var toggleAt = toggle.TransformToVisual(column).TransformPoint(default).X + toggle.Padding.Left;
                if (Math.Abs(toggleAt - column.Padding.Left) > 1) { compact.Add($"chevron {toggleAt - column.Padding.Left:0} px from the column's start"); }
            }
            check(compact.Count == 0, $"draws Show prompt compact: a chevron pointing right before its words, at the label's size, as wide as they are{(compact.Count > 0 ? $": not {string.Join(", ", compact)}" : "")}");
            await Picture(window.RootElement, Path.ChangeExtension(outFile, ".prompt.collapsed.png"));

            // Opened as a press does it, from the keyboard or the pointer.
            if (toggle is not null) { (FrameworkElementAutomationPeer.CreatePeerForElement(toggle)?.GetPattern(PatternInterface.Invoke) as IInvokeProvider)?.Invoke(); }
            var rule = ById<Border>(view, "PromptRule");
            var opened = await Until(() => card.PromptOpen && Shown(rule), TimeSpan.FromSeconds(5));
            await Task.Delay(400);
            var full = ById<TextBlock>(view, "PromptText");
            var wrongOpen = new List<string>();
            if (!opened) { wrongOpen.Add("not opened"); }
            if (rule is not { BorderThickness: { Left: 2, Top: 0, Right: 0, Bottom: 0 } }) { wrongOpen.Add($"rule {rule?.BorderThickness}"); }
            if (rule?.Background is not null) { wrongOpen.Add("a filled card"); }
            if (rule is not null && Colour(rule.BorderBrush) != Colour(Themed(rule, "AbAgent"))) { wrongOpen.Add($"rule in {Colour(rule.BorderBrush)}, not {Colour(Themed(rule, "AbAgent"))}"); }
            if (Shown(preview)) { wrongOpen.Add("the preview still shown"); }
            if (full is null || full.Text != Monitor) { wrongOpen.Add("not all of it"); }
            if (toggle is null || AutomationProperties.GetName(toggle) != "Scheduled prompt 4, Hide prompt") { wrongOpen.Add($"named \"{(toggle is null ? "" : AutomationProperties.GetName(toggle))}\""); }
            if (peer?.ExpandCollapseState != ExpandCollapseState.Expanded) { wrongOpen.Add("not heard as expanded"); }
            if (chevron is null || Angle(chevron) != 90) { wrongOpen.Add($"chevron turned {(chevron is null ? 0 : Angle(chevron)):0}°, not down"); }
            check(wrongOpen.Count == 0, $"opens Show prompt on all of it inside a thin agent rule on the leading edge, not a filled card, named Hide prompt{(wrongOpen.Count > 0 ? $": not {string.Join(", ", wrongOpen)}" : "")}");
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                window.RootElement.RequestedTheme = theme;
                await Task.Delay(300);
                await Picture(window.RootElement, Path.ChangeExtension(outFile, $".prompt.expanded.{theme.ToString().ToLowerInvariant()}.png"));
            }
            window.RootElement.RequestedTheme = ElementTheme.Default;
            peer?.Collapse();
            await Until(() => !card.PromptOpen, TimeSpan.FromSeconds(5));

            // The schedule fires again with the same words (the host counts one more prompt): it folds,
            // and has no preview. Sent as finished, since the demo ends a working turn on its own and
            // would send it again over what comes next.
            await demo.Upsert(card.Session with { UpdatedAt = DateTimeOffset.UtcNow, Activity = card.Session.Activity with { UserMessage = Monitor, TurnCount = turns + 1 } });
            var folded = await Until(() => label is not null && Text(label) == "Scheduled prompt · #4 · same as last time" && !Shown(preview), TimeSpan.FromSeconds(10));
            check(folded, $"folds a schedule's repeat to \"same as last time\" with no preview ({(label is null ? "" : Text(label))})");
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                window.RootElement.RequestedTheme = theme;
                await Task.Delay(300);
                await Picture(window.RootElement, Path.ChangeExtension(outFile, $".prompt.repeat.{theme.ToString().ToLowerInvariant()}.png"));
            }
            window.RootElement.RequestedTheme = ElementTheme.Default;

            // An agent's prompt: the driven diamond, "Sent by an agent" and who, in secondary text.
            await demo.Upsert(card.Session with { UpdatedAt = DateTimeOffset.UtcNow, Driver = DriverKind.Agent, DriverPrincipal = "homeassistant:0f1e2d3c", Activity = card.Session.Activity with { UserMessage = "Run the nightly check and post the result.", UserMessageFrom = UserMessageFrom.Agent, UserMessageLabel = null, TurnCount = turns + 2 } });
            var sent = await Until(() => label is not null && Text(label) == "Sent by an agent" && Shown(ById<ContentControl>(view, "PromptDiamond")), TimeSpan.FromSeconds(10));
            check(sent && !Shown(ById<ContentControl>(view, "PromptClock")) && ById<TextBlock>(view, "PromptBy") is { Text: "homeassistant:0f1e2d3c" } by && Shown(by) && toggle is not null && AutomationProperties.GetName(toggle) == "Prompt sent by an agent, Show prompt",
                "draws an agent's prompt with the driven diamond and \"Sent by an agent\", the agent in secondary text");
            await Picture(window.RootElement, Path.ChangeExtension(outFile, ".prompt.agent.png"));

            // The person's own again: the bubble, as before.
            await demo.Upsert(original with { UpdatedAt = DateTimeOffset.UtcNow });
            check(await Until(() => !card.HasPrompt && Shown(ById<Border>(view, "YourMessage")) == card.HasUserMessage && !Shown(line), TimeSpan.FromSeconds(10)),
                "draws the person's own prompt as their bubble again, and no prompt line");
        }
        finally
        {
            window.RootElement.RequestedTheme = ElementTheme.Default;
            if (card.HasPrompt) { await demo.Upsert(original with { UpdatedAt = DateTimeOffset.UtcNow }); }
        }
    }

    // A brush as the element's theme has it in the generated design dictionary: ThemeResources
    // follow the theme, so the app-wide lookup would give the default theme's.
    static object? Themed(FrameworkElement e, string key)
    {
        var design = Application.Current.Resources.MergedDictionaries.FirstOrDefault(d => d.Source?.ToString().EndsWith(Design.DesignKeys.Source["ms-appx:///".Length..], StringComparison.OrdinalIgnoreCase) == true);
        var theme = new Windows.UI.ViewManagement.AccessibilitySettings().HighContrast ? "HighContrast" : e.ActualTheme.ToString();
        return design?.ThemeDictionaries.TryGetValue(theme, out var d) == true && d is ResourceDictionary r && r.TryGetValue(key, out var b) ? b : null;
    }
}
