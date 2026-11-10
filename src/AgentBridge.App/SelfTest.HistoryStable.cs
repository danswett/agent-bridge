using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

// #1739: events arriving must not move a transcript or redraw rows already drawn. Every step logs the
// scroll offset, the visible row ids and the row elements created so far, so a failure names its step.
static partial class SelfTest
{
    static string Visible(ItemsRepeater rows)
    {
        var ids = new List<string>();
        var count = rows.ItemsSourceView?.Count ?? 0;
        for (var i = 0; i < count; i++)
        {
            if (rows.TryGetElement(i) is not null && rows.ItemsSourceView!.GetAt(i) is HistoryRow row) { ids.Add(row.Id); }
        }
        return ids.Count == 0 ? "none" : $"{ids[0]}..{ids[^1]} ({ids.Count})";
    }

    static async Task CheckHistoryStable(MainWindow window, MainViewModel main, Action<bool, string> check)
    {
        window.ShowPage("sessions");
        Resize(window, 1000, 700);
        var view = await Choose(window, main, "demo-approval", "Response");
        var stored = view?.Card?.Stored;
        var card = view?.Card;
        if (view is null || stored is null || card is null) { check(false, "has a session whose stored turns can be drawn to check that events do not move it"); return; }
        var rows = ById<ItemsRepeater>(view, "History");
        var scroll = ConversationScroller(view);
        if (rows is null || scroll is null) { check(false, "finds the History rows and the conversation scroller"); return; }

        var created = 0;
        rows.ElementPrepared += (_, _) => created++;
        var next = 1000;
        for (var i = 0; i < 300; i++) { stored.AddLive(StoredTurn(next++)); }
        if (!await Until(() => (rows.ItemsSourceView?.Count ?? 0) >= 300 && scroll.ScrollableHeight > 2 * scroll.ViewportHeight, TimeSpan.FromSeconds(15)))
        {
            check(false, $"draws 300 stored turns in a tall conversation ({rows.ItemsSourceView?.Count} drawn, {scroll.ScrollableHeight:0} px)");
            return;
        }

        async Task Middle()
        {
            view.ReleasePin();
            scroll.ChangeView(null, scroll.ScrollableHeight / 2, null, disableAnimation: true);
            await Task.Delay(700);
        }

        string Step(string name) { var s = $"{name}: offset {scroll.VerticalOffset:0.##} of {scroll.ScrollableHeight:0}, visible {Visible(rows)}, row elements created {created}"; AppLog.Write($"self-test: {s}"); return s; }

        // 7 and 10: a burst of 50 stored turns at the middle: the offset is never 0 and never changes, the selection stays.
        await Middle();
        var kept = scroll.VerticalOffset;
        var seen = Visible(rows);
        var selected = window.Sessions.Rows.SelectedItem;
        var baseline = created;
        var zero = new List<string>();
        var drift = new List<string>();
        for (var i = 1; i <= 50; i++)
        {
            stored.AddLive(StoredTurn(next++));
            await Task.Delay(20);
            Step($"burst {i}");
            if (scroll.VerticalOffset == 0) { zero.Add($"{i}"); }
            if (Math.Abs(scroll.VerticalOffset - kept) > 0.5) { drift.Add($"{i}: {scroll.VerticalOffset:0.##}"); }
        }
        await Task.Delay(700);
        var cue = ById<Button>(view, "NewMessages");
        check(zero.Count == 0, $"never jumps to offset 0 during a burst of 50 events (at steps {string.Join(", ", zero)})");
        check(drift.Count == 0 && Math.Abs(scroll.VerticalOffset - kept) < 0.5, $"keeps the offset exactly through a burst of 50 events ({Step("burst end")}; kept {kept:0.##}; drift {string.Join("; ", drift)})");
        check(created - baseline <= 50, $"creates at most 50 row elements for 50 events ({created - baseline})");
        check(cue is { Visibility: Visibility.Visible, Content: "50 new messages" }, $"shows the cue with count 50 after the burst ({cue?.Visibility}, {cue?.Content})");
        check(ReferenceEquals(selected, window.Sessions.Rows.SelectedItem), "keeps the selected session through a burst of 50 events");
        _ = seen;

        // 9: at the bottom with a question showing, a reply follows to the new row; then scrolled back with the cue and the question together.
        var session = card.Session;
        card.Update(session with
        {
            Question = new Question
            {
                Id = "self-test-stable",
                Kind = QuestionKind.Form,
                Message = "Keep going?",
                AskedAt = DateTimeOffset.UtcNow,
                Fields = [new Field { Name = "answer", Type = FieldType.Choice, Options = [new Option("y", "Yes"), new Option("n", "No")] }],
            },
        });
        await Until(() => view.QuestionShown is { IsHitTestVisible: true }, TimeSpan.FromSeconds(5));
        scroll.ChangeView(null, scroll.ScrollableHeight, null, disableAnimation: true);
        await Task.Delay(700);
        stored.AddLive(StoredTurn(next++));
        await Task.Delay(1200);
        var atEnd = scroll.VerticalOffset >= scroll.ScrollableHeight - 1.5 && scroll.VerticalOffset > 0;
        check(atEnd, $"follows a reply to the new row at the bottom while a question bar shows, not to the top ({Step("reply with question")})");

        await Middle();
        var middle = scroll.VerticalOffset;
        stored.AddLive(StoredTurn(next++));
        await Task.Delay(1200);
        cue = ById<Button>(view, "NewMessages");
        var question = view.QuestionShown as FrameworkElement;
        var overlap = false;
        if (cue is { Visibility: Visibility.Visible } && question is { Visibility: Visibility.Visible })
        {
            var a = cue.TransformToVisual(view).TransformBounds(new Windows.Foundation.Rect(0, 0, cue.ActualWidth, cue.ActualHeight));
            var b = question.TransformToVisual(view).TransformBounds(new Windows.Foundation.Rect(0, 0, question.ActualWidth, question.ActualHeight));
            a.Intersect(b);
            overlap = !a.IsEmpty && a.Width > 1 && a.Height > 1;
        }
        check(cue is { Visibility: Visibility.Visible } && question is { Visibility: Visibility.Visible } && !overlap && Math.Abs(scroll.VerticalOffset - middle) < 0.5,
            $"shows the cue and the question bar together, neither covered, offset kept ({cue?.Visibility}, {question?.Visibility}, overlap {overlap}; {Step("cue with question")})");
        card.Update(session);

        // 6 and 10: a reply growing by 20 text updates is the same row element throughout; scrolled back nothing moves, at the bottom it follows.
        var list = ById<ListView>(view, "Conversation");
        string Reply(int n) => string.Join("\n\n", Enumerable.Range(1, n).Select(i => $"Streamed paragraph {i} of a reply that keeps growing as the agent writes."));
        async Task<(bool same, bool offsetKept, bool followed)> Grow(bool atBottom)
        {
            card.Update(card.Session with { Activity = card.Session.Activity with { Response = Reply(3) } });
            await Task.Delay(700);
            scroll.ChangeView(null, atBottom ? scroll.ScrollableHeight : scroll.ScrollableHeight / 2, null, disableAnimation: true);
            if (!atBottom) { view.ReleasePin(); }
            await Task.Delay(700);
            var first = list?.ContainerFromIndex(0);
            var offset = scroll.VerticalOffset;
            var sameRow = first is not null;
            var followed = true;
            var kept = true;
            for (var i = 1; i <= 20; i++)
            {
                card.Update(card.Session with { Activity = card.Session.Activity with { Response = Reply(3 + i) } });
                await Task.Delay(120);
                Step($"growth {(atBottom ? "bottom" : "middle")} {i}");
                sameRow &= ReferenceEquals(first, list?.ContainerFromIndex(0));
                if (atBottom) { followed &= scroll.VerticalOffset >= scroll.ScrollableHeight - 1.5 && scroll.VerticalOffset > 0; }
                else { kept &= Math.Abs(scroll.VerticalOffset - offset) < 0.5; }
            }
            return (sameRow, kept, followed);
        }

        var selectedBefore = window.Sessions.Rows.SelectedItem;
        var mid = await Grow(atBottom: false);
        check(mid.same && mid.offsetKept, $"a growing reply keeps its row element and the offset when scrolled back (same row {mid.same}, offset kept {mid.offsetKept})");
        var low = await Grow(atBottom: true);
        check(low.same && low.followed, $"a growing reply keeps its row element and the view follows it at the bottom (same row {low.same}, followed {low.followed})");
        check(ReferenceEquals(selectedBefore, window.Sessions.Rows.SelectedItem), "keeps the selected session while a reply grows");
        // The demo's card is shared with the checks after this one.
        card.Update(session);
    }
}
