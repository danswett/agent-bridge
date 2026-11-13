using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Foundation;
using Windows.System;

namespace AgentBridge.App;

static partial class SelfTest
{
    static async Task CheckLatestFocus(MainWindow window, SessionView view, ScrollViewer scroll, Action<bool, string> check)
    {
        if (view.Card is not { } card || ById<ListView>(view, "Conversation") is not { ItemsPanelRoot: ItemsStackPanel panel } list)
        { check(false, "latest focus: has a conversation list"); return; }
        var cache = panel.CacheLength;
        var response = string.Join("\n\n", Enumerable.Range(0, 3).Select(n => $"```text\n{string.Join("\n", Enumerable.Repeat($"Focus block {n}", 40))}\n```"));
        try
        {
            panel.CacheLength = 20;
            foreach (var forward in new[] { true, false })
            {
                var input = forward ? "Tab" : "Shift+Tab";
                card.Update(card.Session with { Activity = (card.Session.Activity ?? new Activity()) with { Response = response } });
                await LatestJump(window, view, scroll, check);
                await LatestForeground(window);
                Button? Copy(int index) => list.ContainerFromIndex(index) is { } block ? ById<Button>(block, "CopyCode") : null;
                check(await LatestUntil(() => Copy(0) is not null && Copy(1) is not null && Copy(2) is not null, TimeSpan.FromSeconds(3)),
                    $"latest focus: realises the three Copy buttons for {input}");
                var source = Copy(forward ? 0 : 2);
                var target = Copy(1);
                if (source is null || target is null) { return; }
                bool Visible() => ConversationKeyboard.WhollyInView(target.TransformToVisual(scroll).TransformPoint(new Point()).Y, target.ActualHeight, scroll.ViewportHeight);
                check(!Visible(), $"latest focus: {input} target starts off screen after Jump");
                // Set up the starting stop without scrolling; only the real Tab may take the view.
                var held = new TypedEventHandler<UIElement, BringIntoViewRequestedEventArgs>((_, e) => e.Handled = true);
                list.BringIntoViewRequested += held;
                try { check((await FocusManager.TryFocusAsync(source, FocusState.Keyboard)).Succeeded, $"latest focus: focuses the starting Copy for {input}"); await Still(scroll); }
                finally { list.BringIntoViewRequested -= held; }
                check(ReferenceEquals(FocusManager.GetFocusedElement(list.XamlRoot), source) && scroll.ScrollableHeight - scroll.VerticalOffset <= 1.5,
                    $"latest focus: starting focus leaves Jump pinned before {input}");
                var seen = false;
                var released = false;
                var heard = new KeyEventHandler((_, e) => { if (e.Key == VirtualKey.Tab) { seen = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down) == !forward; } });
                var up = new KeyEventHandler((_, e) => { if (e.Key == VirtualKey.Tab) { released = true; } });
                scroll.AddHandler(UIElement.PreviewKeyDownEvent, heard, true);
                scroll.AddHandler(UIElement.KeyUpEvent, up, true);
                try
                {
                    Press(window.Handle, VirtualKey.Tab, foreground: true, extended: false, shift: !forward, nativeOnly: true);
                    check(await LatestUntil(() => seen && released, TimeSpan.FromSeconds(3)), $"latest focus: native {input} arrives and releases");
                }
                finally { scroll.RemoveHandler(UIElement.PreviewKeyDownEvent, heard); scroll.RemoveHandler(UIElement.KeyUpEvent, up); }
                check(await LatestUntil(() => ReferenceEquals(FocusManager.GetFocusedElement(list.XamlRoot), target) && Visible(), TimeSpan.FromSeconds(3)),
                    $"latest focus: {input} brings the focused off-screen Copy into view after Jump");
                await Still(scroll);
                var offset = scroll.VerticalOffset;
                var extent = scroll.ScrollableHeight;
                var stable = true;
                for (var n = 1; n <= 20; n++)
                {
                    card.Update(card.Session with { Activity = (card.Session.Activity ?? new Activity()) with { Response = response + string.Concat(Enumerable.Repeat("\n\nA later paragraph grows below the focused Copy.", n)) } });
                    await Task.Delay(100);
                    stable &= ReferenceEquals(FocusManager.GetFocusedElement(list.XamlRoot), target) && Visible() && Math.Abs(scroll.VerticalOffset - offset) <= 1.5;
                }
                check(scroll.ScrollableHeight > extent + 1.5 && stable, $"latest focus: twenty growth updates keep the {input} target focused and visible without repinning");
            }
        }
        finally { panel.CacheLength = cache; }
    }

    static async Task CheckLatestQuestion(MainWindow window, SessionView view, ScrollViewer scroll, Action<bool, string> check)
    {
        if (view.Card is not { Question: not null } card || ById<Button>(view, "JumpToLatest") is not { } jump || ById<Button>(view, "QuestionBar") is not { } waiting
            || ById<ItemsControl>(view, "DelegateEvents") is not { } events)
        { check(false, "latest question: has an open question and both navigation buttons"); return; }
        var original = card.Session;
        var open = card.Delegates.EventsOpen;
        var now = DateTimeOffset.UtcNow;
        var agents = Enumerable.Range(0, 60).Select(n => new Subagent { Id = $"latest-question-{n}", Name = $"Question reveal delegate {n}",
            Kind = SubagentKind.Task, Status = SubagentStatus.Ended, Outcome = SubagentOutcome.Done, EndedAt = now.AddMinutes(-n - 1) }).ToArray();
        try
        {
            card.Update(original with { Subagents = agents.Take(40).ToArray() });
            card.Delegates.EventsOpen = true;
            if (view.QuestionShown is not FrameworkElement question) { check(false, "latest question: draws the question"); return; }
            bool Visible() => ConversationKeyboard.WhollyInView(question.TransformToVisual(scroll).TransformPoint(new Point()).Y, question.ActualHeight, scroll.ViewportHeight);
            await LatestForeground(window);
            foreach (var button in new[] { jump, waiting })
            {
                var activated = false;
                var released = false;
                RoutedEventHandler clicked = (_, _) => activated = true;
                var up = new KeyEventHandler((_, e) => { if (e.Key == VirtualKey.Enter) { released = true; } });
                button.Click += clicked;
                view.AddHandler(UIElement.KeyUpEvent, up, true);
                try
                {
                    check(button.Focus(FocusState.Keyboard), $"latest question: focuses {button.Content}");
                    Press(window.Handle, VirtualKey.Enter, foreground: true, extended: false, nativeOnly: true);
                    check(await LatestUntil(() => activated && released, TimeSpan.FromSeconds(3)), $"latest question: native Enter activates and releases {button.Content}");
                }
                finally { button.Click -= clicked; view.RemoveHandler(UIElement.KeyUpEvent, up); }
                if (button == jump)
                {
                    check(await LatestUntil(() => events.ActualHeight > scroll.ViewportHeight, TimeSpan.FromSeconds(3)), "latest question: expanded delegate events exceed a viewport below the question");
                    check(await LatestUntil(() => scroll.ScrollableHeight - scroll.VerticalOffset <= 1.5 && waiting.Visibility == Visibility.Visible
                        && question.TransformToVisual(scroll).TransformPoint(new Point()).Y + question.ActualHeight < 0, TimeSpan.FromSeconds(3)),
                        "latest question: Jump pins below the off-screen question and offers Question waiting");
                }
            }
            check(await LatestUntil(Visible, TimeSpan.FromSeconds(3)), "latest question: Question waiting reveals the question after Jump");
            view.UpdateLayout();
            await Still(scroll);
            check(Visible(), "latest question: the explicit reveal survives settled layout");
            var offset = scroll.VerticalOffset;
            var extent = scroll.ScrollableHeight;
            var stable = true;
            for (var n = 1; n <= 20; n++)
            {
                card.Update(original with { Subagents = agents.Take(40 + n).ToArray() });
                await Task.Delay(100);
                stable &= Visible() && Math.Abs(scroll.VerticalOffset - offset) <= 1.5;
            }
            check(scroll.ScrollableHeight > extent + 1.5 && stable, "latest question: twenty delegate-growth updates retain the revealed question without repinning");
        }
        finally { card.Update(original); card.Delegates.EventsOpen = open; }
    }
}
