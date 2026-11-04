using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AgentBridge.App;

// Answers moved where they are (#491, the app's half of #559): a pending approval's Approve once
// and Deny moved in place under a pointer that stays still, and that pointer pressing where it
// rests. Each move must hold them, so the press reaches nothing or is dropped: a press in the same
// task as the change, before any layout pass; a banner above growing; the delegate strip appearing
// and going; and Needs you slid down as the list is pulled back from its end. Each case starts
// with the answers taking clicks, so each hold is its own. At whatever size the screen allows: the
// strip is drawn only at three panes, and below that the list and the session take turns.
static partial class SelfTest
{
    static async Task CheckAnswerMoves(MainWindow window, MainViewModel main, Action<bool, string> check)
    {
        var page = window.Sessions;
        var layout = page.Layout!;
        var root = window.RootElement.XamlRoot;
        Resize(window, 1440, 900);
        await Until(() => layout.Mode == PaneMode.Three, TimeSpan.FromSeconds(10));
        var view = await Choose(window, main, "demo-approval", "Option:approve");
        var banner = ById<InfoBar>(window.RootElement, "Banner");
        if (view?.Card is not { Question: not null } card || banner is null || page.FindName("LaneItems") is not ItemsControl lane)
        {
            check(false, "finds the demo's permission prompt, the banner and Needs you to move answers under a pointer");
            return;
        }
        layout.Tab = SessionTab.Conversation;
        var original = card.Session;
        var size = $"{root.Size.Width:0}×{root.Size.Height:0}, {layout.Mode}";
        List<FrameworkElement> Choices() => [.. new[] { "Option:approve", "Option:deny" }.Select(id => ById<Button>(view, id)).OfType<FrameworkElement>().Where(b => b.ActualWidth > 0)];
        List<FrameworkElement> LaneAnswers() => [.. Find<Button>(lane).Where(b => b.IsEnabled && AutomationId(b) is "NeedsYouApprove" or "NeedsYouDeny" && b.ActualWidth > 0)];
        string Pressed(List<Point> before) => Press(root, Resting(before, Choices()), Choices(), view.QuestionShown, view.PointerLets);
        string LanePressed(List<Point> before) => Press(root, Resting(before, LaneAnswers()), LaneAnswers(), lane, page.LanePointerLets);
        async Task<bool> Takes() => await Until(() => view.QuestionShown is { IsHitTestVisible: true } && Choices().Count == 2, TimeSpan.FromSeconds(5));
        async Task<bool> LaneTakes() => await Until(() => lane.IsHitTestVisible && LaneAnswers().Count > 0, TimeSpan.FromSeconds(5));
        async Task<bool> Moved(Func<List<FrameworkElement>> answers, List<Point> before) =>
            await Until(() => answers() is [var a, ..] && before.Count > 0 && Math.Abs(Centre(a).Y - before[0].Y) > 1, TimeSpan.FromSeconds(5));

        try
        {
            // A press in the same task as a banner opening above the question, before any layout
            // pass: it is on Approve once where the last layout put it, and the read it brings finds
            // the move.
            var ready = await Takes();
            if (Choices() is [var approve, ..])
            {
                var at = Centre(approve);
                banner.Message = "Lost the connection to the demo machine's host.";
                banner.IsOpen = true;
                var let = view.PointerLets(approve);
                var moved = await Moved(Choices, [at]);
                check(ready && moved && !let, $"drops a pointer's press on Approve once in the same task as a banner opening above it, before any layout pass ({size}; moved: {moved}; let through: {let})");
            }
            else { check(false, $"finds Approve once on the permission prompt ({size})"); }

            // The banner's words growing to more lines over the question, Approve once brought near
            // the top of the conversation first, so it is still in sight in a short window.
            if (Choices() is [var first, ..]) { first.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false, VerticalAlignmentRatio = 0.2 }); }
            await Task.Delay(200);
            ready = await Takes();
            var before = Choices().Select(Centre).ToList();
            banner.Message = Longer(root);
            var grew = await Moved(Choices, before);
            var pressed = Pressed(before);
            check(ready && grew && Stopped(pressed), $"holds the question's answers from a pointer resting where they moved as a banner above them grows ({size}; moved: {grew}; the press: {pressed})");
            banner.IsOpen = false;
            banner.Message = "";

            // The delegate strip appearing over the conversation, then going, the question still.
            if (layout.Mode == PaneMode.Three)
            {
                ready = await Takes();
                before = Choices().Select(Centre).ToList();
                var listing = DemoHost.Delegating(DateTimeOffset.UtcNow);
                card.Update(original with { Subagents = listing.Subagents, Activity = (original.Activity ?? new Activity()) with { Background = listing.Activity!.Background } });
                var shown = await Until(() => layout.ShowStrip && ById<FrameworkElement>(view, "DelegateStrip") is { ActualHeight: > 0 }, TimeSpan.FromSeconds(5)) && await Moved(Choices, before);
                pressed = Pressed(before);
                check(ready && shown && Stopped(pressed), $"holds the question's answers from a pointer resting where they moved as the delegate strip appears above them (shown: {shown}; the press: {pressed})");

                ready = await Takes();
                before = Choices().Select(Centre).ToList();
                card.Update(original);
                var gone = await Until(() => !layout.ShowStrip, TimeSpan.FromSeconds(5)) && await Moved(Choices, before);
                pressed = Pressed(before);
                check(ready && gone && Stopped(pressed), $"holds the question's answers from a pointer resting where they moved as the delegate strip goes (gone: {gone}; the press: {pressed})");
            }
            else
            {
                // Below three panes there is no strip: the Delegates tab is in the bar already there.
                AppLog.Write($"self-test: the delegate strip's moves are checked at three panes only, not at {size}");
            }

            // Needs you: on screen beside the session, or, in one column, with the session closed.
            if (layout.Mode == PaneMode.One) { main.Selected = null; }
            var listed = await Until(() => layout.ShowList, TimeSpan.FromSeconds(5));
            ready = listed && await LaneTakes();
            before = LaneAnswers().Select(Centre).ToList();
            banner.Message = Longer(root);
            banner.IsOpen = true;
            grew = await Moved(LaneAnswers, before);
            pressed = LanePressed(before);
            check(ready && grew && Stopped(pressed), $"holds Needs you's answers from a pointer resting where they moved as a banner opens above the list ({size}; moved: {grew}; the press: {pressed})");
            banner.IsOpen = false;
            banner.Message = "";

            await LaneTakes();
            await CheckLaneClamp(main, page, LaneAnswers, LaneTakes, LanePressed, size, check);
        }
        finally
        {
            banner.IsOpen = false;
            banner.Message = "";
            if (!ReferenceEquals(card.Session, original)) { card.Update(original); }
            main.Selected = card;
        }
    }

    // A banner's words a line or two longer than one line at this width: a move small enough to
    // leave the answers in sight in a short window.
    static string Longer(XamlRoot root)
    {
        var words = "The demo machine's host stopped answering, and the app is trying it again.";
        var text = words;
        while (text.Length < root.Size.Width / 4) { text += " " + words; }
        return text;
    }

    // Needs you is the list's header: with the list scrolled to its end and what is below the lane
    // getting shorter, the list is pulled back and the lane slides down under a still pointer. The
    // list is made to end just past the lane's answers, whatever its length and the window's
    // height (a short list, or a small screen that shows little of it): its bottom space grows to
    // make it longer, and rows are taken out for a moment to make it shorter.
    static async Task CheckLaneClamp(MainViewModel main, SessionsPage page, Func<List<FrameworkElement>> answers, Func<Task<bool>> takes,
        Func<List<Point>, string> pressed, string size, Action<bool, string> check)
    {
        var list = page.Rows;
        var padding = list.Padding;
        var taken = new List<(int At, SessionCardViewModel Card)>();
        try
        {
            if (Find<ScrollViewer>(list).FirstOrDefault() is not { Content: UIElement content } scroller || answers() is not [var approve, ..])
            {
                check(false, $"finds the list's scroller and Needs you's Approve once ({size})");
                return;
            }
            var view = scroller.ViewportHeight;
            // Scrolled to its end with Approve once just inside the top; pulled back by `slide`, it
            // is still in sight.
            var over = Math.Max(0, approve.TransformToVisual(content).TransformPoint(default).Y - 8);
            var slide = Math.Min(60, Math.Min(over, view - approve.ActualHeight - 16));
            var end = over - slide + view;
            // Shorter: rows taken out from the end, those with no question first, until it fits.
            foreach (var asks in new[] { false, true })
            {
                for (var i = main.Sessions.Count - 1; i >= 0 && scroller.ExtentHeight > end; i--)
                {
                    var row = main.Sessions[i];
                    if ((row.Question is not null) != asks || row == main.Selected) { continue; }
                    main.Sessions.RemoveAt(i);
                    taken.Add((i, row));
                    list.UpdateLayout();
                }
            }
            var natural = scroller.ExtentHeight;
            var fits = natural <= end + 0.5 && slide > 2;
            // Longer: the list's bottom space, so it ends with Approve once near the top of the view.
            list.Padding = padding with { Bottom = padding.Bottom + Math.Max(0, over + view - natural) };
            list.UpdateLayout();
            scroller.ChangeView(null, scroller.ScrollableHeight, null, true);
            var atEnd = await Until(() => scroller.ScrollableHeight > 2 && Math.Abs(scroller.VerticalOffset - scroller.ScrollableHeight) < 1, TimeSpan.FromSeconds(5));
            var ready = await takes();
            var before = answers().Select(Centre).ToList();
            var offset = scroller.VerticalOffset;
            // What is below the lane gets shorter by `slide`: the list is pulled back by as much.
            list.Padding = padding with { Bottom = padding.Bottom + Math.Max(0, end - natural) };
            var slid = await Until(() => scroller.VerticalOffset < offset - 1 && answers() is [var a, ..] && before.Count > 0 && Centre(a).Y - before[0].Y > 1, TimeSpan.FromSeconds(5));
            var press = pressed(before);
            check(fits && atEnd && ready && slid && Stopped(press),
                $"holds Needs you's answers from a pointer resting where they moved as the list, at its end, is pulled back by what is below getting shorter ({size}; {offset:0}→{scroller.VerticalOffset:0}, {taken.Count} rows taken out; fits: {fits}, at the end: {atEnd}, slid: {slid}, the press: {press})");
        }
        finally
        {
            list.Padding = padding;
            foreach (var (at, card) in Enumerable.Reverse(taken)) { if (!main.Sessions.Contains(card)) { main.Sessions.Insert(Math.Min(at, main.Sessions.Count), card); } }
        }
    }

    static Point Centre(FrameworkElement e) => e.TransformToVisual(null).TransformPoint(new Point(e.ActualWidth / 2, e.ActualHeight / 2));
    static Rect Bounds(FrameworkElement e) => e.TransformToVisual(null).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight));
    static string AutomationId(DependencyObject o) => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(o);

    // Where a pointer resting still through a move now has an answer under it: the first of the
    // answers' places before the move that one of them covers now, else where the first now is.
    static Point Resting(IReadOnlyList<Point> before, IReadOnlyList<FrameworkElement> answers)
    {
        foreach (var p in before) { if (answers.Any(a => Bounds(a).Contains(p))) { return p; } }
        return answers.Count > 0 ? Centre(answers[0]) : default;
    }

    // A pointer pressing at `at` now, as Windows would route it: "answered" if it reaches an answer
    // in holder and the answer lets it through; "held" if nothing there takes it (held answers are
    // out of hit testing); "dropped" if the answer drops it. "On no answer" and "out of sight" (not
    // in what scrolls the holder's view) prove nothing, and fail the case.
    static string Press(XamlRoot root, Point at, IReadOnlyList<FrameworkElement> answers, UIElement? holder, Func<DependencyObject, bool> lets)
    {
        if (holder is null || !answers.Any(a => Bounds(a).Contains(at))) { return "on no answer"; }
        if (Above<ScrollViewer>(holder, _ => true) is { } scroller && !Bounds(scroller).Contains(at)) { return "out of sight"; }
        var top = VisualTreeHelper.FindElementsInHostCoordinates(at, root.Content).FirstOrDefault();
        if (top is null || !Motion.Within(top, holder)) { return "held"; }
        for (DependencyObject? up = top; up is not null && up != holder; up = VisualTreeHelper.GetParent(up))
        {
            if (up is ButtonBase) { return lets(top) ? "answered" : "dropped"; }
        }
        return "held";
    }

    static bool Stopped(string press) => press is "held" or "dropped";
}
