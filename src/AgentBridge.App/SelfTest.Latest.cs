using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

static partial class SelfTest
{
    static async Task JumpLatest(SessionView view, Action<bool, string> check)
    {
        var button = ById<Button>(view, "JumpToLatest");
        check(button is { IsTabStop: true, Visibility: Visibility.Visible }
            && AutomationProperties.GetName(button) == "Jump to latest",
            "latest: offers a keyboard-reachable, screen-reader-named Jump to latest");
        if (button is null) { return; }
        ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        check(await Until(() => ConversationScroller(view) is { } scroll
            && scroll.ScrollableHeight - scroll.VerticalOffset <= 1.5, TimeSpan.FromSeconds(3)),
            "latest: activating Jump reaches the conversation end");
    }

    static async Task CheckLatestHatch(MainWindow window, MainViewModel main, Action<bool, string> check)
    {
        var view = await Choose(window, main, "demo-choice", "Response");
        if (view?.Card is not { } original) { check(false, "latest: opens the conversation"); return; }
        var reply = string.Join("\n\n", Enumerable.Repeat("A paragraph in the latest reply.", 40));
        var card = new SessionCardViewModel(original.Session with
        {
            Id = "latest-hatch", Question = null, Status = SessionStatus.Idle,
            Activity = new Activity { Response = reply },
        }, main.Local.Cards);
        try
        {
            view.Card = card;
            if (card.Stored is not { } history || ConversationScroller(view) is not { } scroll)
            { check(false, "latest: has history and a conversation scroller"); return; }
            if (!await Until(() => history.State != HistoryLoad.Loading && scroll.ScrollableHeight > 500, TimeSpan.FromSeconds(5)))
            { check(false, "latest: lays out a reply taller than the viewport"); return; }
            view.ReleasePin();
            scroll.ChangeView(null, 100, null, disableAnimation: true);
            await Still(scroll);
            await JumpLatest(view, check);
            history.AddLive(StoredTurn(9001));
            card.Update(card.Session with { Activity = new Activity { Response = reply + "\n\nA new ending." } });
            await Task.Delay(500);
            await Still(scroll);
            check(scroll.ScrollableHeight - scroll.VerticalOffset <= 1.5,
                "latest: explicit Jump stays pinned as stored turns and the latest reply grow");
            view.ReleasePin();
            scroll.ChangeView(null, scroll.ScrollableHeight - 100, null, disableAnimation: true);
            await Still(scroll);
            var offset = scroll.VerticalOffset;
            history.AddLive(StoredTurn(9002));
            await Task.Delay(500);
            await Still(scroll);
            check(Math.Abs(scroll.VerticalOffset - offset) <= 1.5,
                "latest: after release with no visible stored row, an arrival keeps the numeric offset");
            var anchor = typeof(SessionView).GetField("anchor", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?.GetValue(view);
            var list = ById<ListView>(view, "Conversation");
            check(anchor is ScrollAnchor kept && list?.ContainerFromIndex(kept.Index) is FrameworkElement block
                && Math.Abs(block.TransformToVisual(scroll).TransformPoint(default).Y - kept.Top) <= 1.5,
                "latest: a released history restore recaptures the response block at its restored position");
            scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
            await Still(scroll);
            history.AddLive(StoredTurn(9003));
            await Task.Delay(500);
            await Still(scroll);
            check(scroll.ScrollableHeight - scroll.VerticalOffset <= 1.5,
                "latest: returning to the end after release follows the next stored turn");
            await CheckLatestQuestion(view, card, original.Session.Question, check);
        }
        finally { view.Card = original; }
    }

    static async Task CheckLatestQuestion(SessionView view, SessionCardViewModel card, Question? question, Action<bool, string> check)
    {
        if (question is null || view.FindName("QuestionCard") is not FrameworkElement target
            || target.Parent is not Panel footer || ConversationScroller(view) is not { } scroll)
        { check(false, "latest: has a question and footer for question navigation"); return; }
        var below = new Border { Height = scroll.ViewportHeight * 2 };
        footer.Children.Add(below);
        try
        {
            card.Update(card.Session with { Question = question });
            check(await Until(() => target.ActualHeight > 0, TimeSpan.FromSeconds(3)), "latest: lays out the waiting question");
            await JumpLatest(view, check);
            var bar = ById<Button>(view, "QuestionBar");
            check(bar is { Visibility: Visibility.Visible }, "latest: offers Question waiting after Jump with tall content below it");
            if (bar is null || bar.Visibility != Visibility.Visible) { return; }
            bar.Focus(FocusState.Programmatic);
            ((IInvokeProvider)new ButtonAutomationPeer(bar).GetPattern(PatternInterface.Invoke)).Invoke();
            await Still(scroll);
            below.Height += 10;
            await Task.Delay(500);
            await Still(scroll);
            var top = target.TransformToVisual(scroll).TransformPoint(default).Y;
            check(top < scroll.ViewportHeight && top + target.ActualHeight > 0,
                "latest: Jump then Question waiting keeps the question in view through subsequent layout");
        }
        finally { footer.Children.Remove(below); }
    }

    static void CheckLatestNativeReceipt(SessionView view, LatestInputReceipt? receipt, Action<bool, string> check)
    {
        check(receipt is { } arrived && view.AcceptsLatestNativeReceipt(arrived),
            $"latest: native input receipt belongs to this exact view and its latest issued input ({receipt?.Offset:0.##}/{receipt?.Extent:0.##})");
    }
}
