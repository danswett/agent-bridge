using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// The conversation's delegate lines folded into one row (#1063, ui.md §6.4 Runs fold), on the demo's
// session with eight delegates: closed by default as "8 delegates · 5 reported back, 1 failed ›" with
// "2 still working" a target of its own after it; opened, exactly its lines; a delegate's question
// still in the stream outside it; a narrow pane; focus when its link goes; and open or closed as it
// was left when the session is chosen again. Left open, for the line check that follows.
static partial class SelfTest
{
    const string FoldSummary = "8 delegates · 5 reported back, 1 failed";
    const string FoldName = "8 delegates · 5 reported back, 1 failed, 2 still working";

    static async Task CheckEventsFold(MainWindow window, MainViewModel main, SessionView view, SessionCardViewModel card, PaneLayoutViewModel layout, DemoHost demo, Action<bool, string> check, string outFile)
    {
        var original = card.Session;
        var toggle = ById<DisclosureButton>(view, "DelegateFold");
        var working = ById<HyperlinkButton>(view, "DelegateFoldWorking");
        var words = ById<TextBlock>(view, "DelegateFoldText");
        var rule = ById<Border>(view, "EventsRule");
        var chevron = ById<ContentControl>(view, "EventsChevron");
        var list = ById<ItemsControl>(view, "DelegateEvents");
        if (toggle is null || working is null || words is null || rule is null || chevron is null || list is null) { check(false, "draws the delegate lines' fold: its button, text, chevron, link and lines"); return; }
        var expand = FrameworkElementAutomationPeer.CreatePeerForElement(toggle)?.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider;
        static float Angle(UIElement e) => Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(e).RotationAngleInDegrees;
        static bool Drawn(FrameworkElement? e)
        {
            for (DependencyObject? at = e; at is not null; at = VisualTreeHelper.GetParent(at))
            {
                if (at is UIElement { Visibility: Visibility.Collapsed }) { return false; }
            }
            return e is { ActualHeight: > 0, ActualWidth: > 0 };
        }
        double Left(FrameworkElement e) => e.TransformToVisual(view).TransformPoint(default).X;
        static bool Under(DependencyObject? e, DependencyObject ancestor)
        {
            for (; e is not null; e = VisualTreeHelper.GetParent(e))
            {
                if (e == ancestor) { return true; }
            }
            return false;
        }
        // The session pane's own XAML, on the shell colour a Mica window shows under it in each theme
        // (the composed window can't be read back on a machine whose desktop is not being drawn).
        async Task Themes(string name)
        {
            foreach (var theme in new[] { ElementTheme.Light, ElementTheme.Dark })
            {
                window.RootElement.RequestedTheme = theme;
                await Task.Delay(300);
                var key = theme == ElementTheme.Dark ? "Dark" : "Light";
                var shell = Application.Current.Resources.MergedDictionaries
                    .Select(d => d.ThemeDictionaries.TryGetValue(key, out var td) && td is ResourceDictionary rd && rd.TryGetValue("AbShell", out var b) && b is SolidColorBrush s ? s.Color : (Windows.UI.Color?)null)
                    .FirstOrDefault(c => c is not null) ?? default;
                await Picture(view, Path.ChangeExtension(outFile, $".delegates.fold.{name}.{theme.ToString().ToLowerInvariant()}.png"), shell);
            }
            window.RootElement.RequestedTheme = ElementTheme.Default;
        }
        // Closed, however it was left: the demo's session starts so, and is closed again for each look.
        async Task Close()
        {
            if (card.Delegates.EventsOpen) { expand?.Collapse(); }
            await Until(() => !card.Delegates.EventsOpen && !Drawn(rule), TimeSpan.FromSeconds(5));
            await Task.Delay(300);
        }
        async Task Open()
        {
            if (!card.Delegates.EventsOpen) { expand?.Expand(); }
            await Until(() => card.Delegates.EventsOpen && Drawn(rule), TimeSpan.FromSeconds(5));
            await Task.Delay(400);
        }
        try
        {
            Resize(window, 1440, 1000);
            await Until(() => layout.Mode == PaneMode.Three, TimeSpan.FromSeconds(10));
            var ready = await Until(() => Drawn(toggle) && Drawn(working), TimeSpan.FromSeconds(10));
            var problems = new List<string>();
            if (!ready) { problems.Add("not drawn"); }
            else
            {
                if (words.Text != FoldSummary) { problems.Add($"words \"{words.Text}\""); }
                if (AutomationProperties.GetName(toggle) != FoldName) { problems.Add($"named \"{AutomationProperties.GetName(toggle)}\""); }
                if (AutomationProperties.GetName(working) != "2 still working, opens the first") { problems.Add($"link named \"{AutomationProperties.GetName(working)}\""); }
                if (expand?.ExpandCollapseState != ExpandCollapseState.Collapsed || card.Delegates.EventsOpen) { problems.Add("not closed by default"); }
                if (Drawn(rule) || Find<FlowPanel>(list).Any(Drawn)) { problems.Add("its lines drawn"); }
                if (Angle(chevron) != 0) { problems.Add($"chevron turned {Angle(chevron):0}°"); }
                var chevronRight = chevron.TransformToVisual(toggle).TransformPoint(default).X + chevron.ActualWidth;
                if (chevron.TransformToVisual(toggle).TransformPoint(default).X <= words.TransformToVisual(toggle).TransformPoint(default).X || chevronRight > toggle.ActualWidth + 1) { problems.Add("the chevron not ending the button's text inside it"); }
                if (Find<HyperlinkButton>(toggle).Count > 0) { problems.Add("the link inside the button"); }
                var gap = Left(working) - (Left(toggle) + toggle.ActualWidth);
                if (Math.Abs(gap - 12) > 1.5) { problems.Add($"{gap:0.#} px between the button and the link, not 12"); }
                if (!toggle.IsTabStop || !working.IsTabStop || toggle.ActualHeight < 32 || working.ActualHeight < 32) { problems.Add($"targets {toggle.ActualHeight:0} and {working.ActualHeight:0} high, or no tab stop"); }
                if (card.Delegates.FoldWorkingId != "demo-agent-witness") { problems.Add($"the link opens {card.Delegates.FoldWorkingId}, not the earliest started"); }
            }
            check(problems.Count == 0, $"folds the delegate lines into one closed row, \"{FoldSummary}\" with the chevron ending its button, and \"2 still working\" a target of its own after it, named for a screen reader with the counts and the state said apart{(problems.Count > 0 ? $": not {string.Join(", ", problems)}" : "")}");
            await Themes("closed");

            // The link opens the earliest started of those still working, in the inspector.
            ((IInvokeProvider)new HyperlinkButtonAutomationPeer(working).GetPattern(PatternInterface.Invoke)).Invoke();
            check(await Until(() => card.Delegates.SelectedId == "demo-agent-witness" && layout.ShowDelegateInspector, TimeSpan.FromSeconds(5)), "opens the earliest started delegate still working from the fold's link, leaving the fold closed");
            layout.CloseDelegate();

            // Pressed, as Enter or Space press a button: exactly the lines, in place.
            ((IInvokeProvider)new ButtonAutomationPeer(toggle).GetPattern(PatternInterface.Invoke)).Invoke();
            var shown = await Until(() => Drawn(rule) && Find<FlowPanel>(list).Count(Drawn) == card.Delegates.Events.Count, TimeSpan.FromSeconds(5));
            await Task.Delay(400);
            var open = new List<string>();
            if (!shown) { open.Add($"{Find<FlowPanel>(list).Count(Drawn)} of {card.Delegates.Events.Count} lines drawn"); }
            if (expand?.ExpandCollapseState != ExpandCollapseState.Expanded) { open.Add("not heard as expanded"); }
            if (AutomationProperties.GetName(toggle) != FoldName) { open.Add($"name changed to \"{AutomationProperties.GetName(toggle)}\""); }
            if (Angle(chevron) != 90) { open.Add($"chevron turned {Angle(chevron):0}°, not down"); }
            if (rule is not { BorderThickness: { Left: 1, Top: 0, Right: 0, Bottom: 0 } }) { open.Add($"rule {rule.BorderThickness}"); }
            check(open.Count == 0, $"opens the fold in place on every line it holds, unchanged, under a rule, with the state heard as expanded and not in the name{(open.Count > 0 ? $": not {string.Join(", ", open)}" : "")}");
            await Themes("open");

            // A delegate's question is a card in the stream, never a line, so the fold can't hide it.
            var question = new Question
            {
                Id = "toolu_demo_delegate_question",
                Kind = QuestionKind.Form,
                Agent = "demo-agent-witness",
                Message = "Which timer should the witness follow?",
                AskedAt = DateTimeOffset.UtcNow.AddSeconds(-20),
                Fields = [new Field { Name = "answer", Type = FieldType.Choice, Options = [new Option("first", "The first reply of a turn"), new Option("latest", "The latest reply (Recommended)")] }],
            };
            await demo.Upsert(original with
            {
                Status = SessionStatus.Waiting,
                Question = question,
                WaitingOn = "demo-agent-witness",
                UpdatedAt = DateTimeOffset.UtcNow,
                Subagents = [.. original.Subagents!.Select(a => a.Id == "demo-agent-witness" ? a with { Status = SubagentStatus.Waiting } : a)],
            });
            var asked = await Until(() => ById<FrameworkElement>(view, "Question") is { } q && Drawn(q), TimeSpan.FromSeconds(10));
            var card2 = ById<FrameworkElement>(view, "Question");
            var outside = asked && card2 is not null && !Under(card2, rule) && !Under(card2, toggle);
            await Close();
            check(asked && outside && Drawn(toggle) && AutomationProperties.GetName(toggle) == FoldName && card.AsksText == "Delegate Read witness shell logic asks",
                $"keeps a delegate's pending question in the stream outside the fold, which adds no attention state of its own ({card.AsksText})");
            await Themes("question");
            await demo.Upsert(original with { UpdatedAt = DateTimeOffset.UtcNow });
            await Until(() => ById<FrameworkElement>(view, "Question") is not { } gone || !Drawn(gone), TimeSpan.FromSeconds(10));

            // A narrow pane: the words wrap, the link follows or wraps under them, every target in the column.
            Resize(window, 520, 900);
            await Until(() => layout.Mode == PaneMode.One && Drawn(toggle), TimeSpan.FromSeconds(10));
            await Task.Delay(400);
            await Close();
            var narrow = new List<string>();
            if (Left(toggle) + toggle.ActualWidth > view.ActualWidth + 1 || Left(working) + working.ActualWidth > view.ActualWidth + 1) { narrow.Add("a target outside the pane"); }
            if (toggle.ActualHeight < 32 || working.ActualHeight < 32) { narrow.Add("a target under 32 high"); }
            check(narrow.Count == 0 && Drawn(toggle) && Drawn(working), $"draws the closed fold in a narrow pane with both targets inside it{(narrow.Count > 0 ? $": not {string.Join(", ", narrow)}" : "")}");
            await Themes("narrow.closed");
            await Open();
            await Themes("narrow.open");
            Resize(window, 1440, 1000);
            await Until(() => layout.Mode == PaneMode.Three && Drawn(toggle), TimeSpan.FromSeconds(10));

            // Open or closed as it was left, for as long as the app runs: another session chosen and this one again.
            await Open();
            await Choose(window, main, "demo-approval", "Option:approve");
            var back = await Choose(window, main, DelegatingId, "DelegateStrip");
            var againToggle = back is null ? null : ById<DisclosureButton>(back, "DelegateFold");
            check(card.Delegates.EventsOpen && againToggle is not null && (FrameworkElementAutomationPeer.CreatePeerForElement(againToggle)?.GetPattern(PatternInterface.ExpandCollapse) as IExpandCollapseProvider)?.ExpandCollapseState == ExpandCollapseState.Expanded,
                "keeps the fold open or closed as it was left when the session is chosen again");

            // Focus on the link as it goes (§9.1 A4): to the fold's button while the fold stays, else to the first delegate's name.
            await Close();
            working.Focus(FocusState.Keyboard);
            await Task.Delay(200);
            var focusedLink = ReferenceEquals(FocusManager.GetFocusedElement(window.RootElement.XamlRoot), working);
            await demo.Upsert(original with { UpdatedAt = DateTimeOffset.UtcNow, Subagents = [.. original.Subagents!.Select(a => a.Status == SubagentStatus.Working ? a with { Status = SubagentStatus.Ended, Outcome = SubagentOutcome.Done, EndedAt = DateTimeOffset.UtcNow } : a)] });
            var toButton = await Until(() => !card.Delegates.HasFoldLink && ReferenceEquals(FocusManager.GetFocusedElement(window.RootElement.XamlRoot), toggle), TimeSpan.FromSeconds(5));
            check(focusedLink && toButton, $"moves focus from the \"still working\" link to the fold's button when that link goes, the fold staying ({FocusManager.GetFocusedElement(window.RootElement.XamlRoot)?.GetType().Name})");
            toggle.Focus(FocusState.Keyboard);
            await demo.Upsert(original with { UpdatedAt = DateTimeOffset.UtcNow, Subagents = [original.Subagents!.First(a => a.Id == "demo-agent-witness")] });
            var toName = await Until(() => !card.Delegates.HasFold && FocusManager.GetFocusedElement(window.RootElement.XamlRoot) is HyperlinkButton { Tag: "demo-agent-witness" }, TimeSpan.FromSeconds(5));
            check(toName, $"moves focus to the first delegate's name when the fold goes with it ({FocusManager.GetFocusedElement(window.RootElement.XamlRoot)?.GetType().Name}; fold {card.Delegates.HasFold}, {Find<HyperlinkButton>(list).Count} links, {card.Delegates.Events.Count} lines, rule drawn {Drawn(rule)})");
            await demo.Upsert(original with { UpdatedAt = DateTimeOffset.UtcNow });
            await Until(() => card.Delegates.HasFold && Drawn(toggle), TimeSpan.FromSeconds(10));
        }
        finally
        {
            window.RootElement.RequestedTheme = ElementTheme.Default;
            Resize(window, 1440, 1000);
        }
        // Left open: its lines are the next check's.
        await Open();
    }
}
