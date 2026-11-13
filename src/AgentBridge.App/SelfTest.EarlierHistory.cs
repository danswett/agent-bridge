using AgentBridge.App.Core;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;

namespace AgentBridge.App;

static partial class SelfTest
{
    static async Task CheckEarlierHistory(MainWindow window, MainViewModel main, Action<bool, string> check)
    {
        var view = await Choose(window, main, "demo-approval", "Response");
        if (view is null) { check(false, "opens a conversation to check earlier messages"); return; }
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ab-earlier-" + Guid.NewGuid().ToString("N"))).FullName;
        var requests = new List<string?>();
        TaskCompletionSource? pageHold = null;
        var failPage = 0;
        var limited = 0;
        await using var server = LocalServer.Start(LocalEndpoint.For(folder), async (_, op, data) =>
        {
            object? result = null;
            if (op == Ops.Hello) { result = new HelloResult(1, new MachineInfo("earlier", "Test", "windows", "test"), [HistoryProtocol.Capability]); }
            if (op == HistoryProtocol.Op)
            {
                var before = data.TryGetProperty("before", out var b) ? b.GetString() : null;
                lock (requests) { requests.Add(before); }
                if (before is not null && Volatile.Read(ref pageHold) is { } hold) { await hold.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                if (Volatile.Read(ref failPage) != 0) { throw new IOException("The earlier page failed."); }
                var end = before is null ? 80 : int.Parse(before);
                if (Volatile.Read(ref limited) != 0) { end = 20; }
                var from = Math.Max(0, end - 20);
                result = new HistoryResult
                {
                    Turns = [.. Enumerable.Range(from + 1, end - from).Select(StoredTurn)],
                    Before = from > 0 ? $"{from}" : null,
                    Start = from > 0 ? null : Volatile.Read(ref limited) != 0 ? new HistoryStart { Limited = true } : new HistoryStart { At = StoredFrom },
                };
            }
            return result;
        });
        var ui = new QueueDispatcher(view.DispatcherQueue);
        await using var host = new HostConnection(ct => LocalClient.ConnectAsync(LocalEndpoint.For(folder), TimeSpan.FromSeconds(2), ct), ui) { Subscribes = false };
        host.Start();
        await host.WhenConnectedAsync().WaitAsync(TimeSpan.FromSeconds(10));
        var previous = view.Card;
        var card = new SessionCardViewModel(previous!.Session with { Id = "earlier", Question = null, Status = SessionStatus.Idle, Activity = new Activity { Response = "Latest reply." } },
            new CardContext { Host = host, Deliveries = new Deliveries(host, ui), Folds = new FoldStore(null), Capabilities = () => host.Capabilities });
        try
        {
            view.Card = card;
            var history = card.Stored!;
            var rows = ById<ItemsRepeater>(view, "History")!;
            await Until(() => history.Turns.Count == 20 && rows.ItemsSourceView.Count == 20, TimeSpan.FromSeconds(10));
            await Task.Delay(700);
            var scroll = ConversationScroller(view)!;
            async Task Page(bool button, int count)
            {
                var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Volatile.Write(ref pageHold, hold);
                scroll.ChangeView(null, button ? scroll.ScrollableHeight / 2 : scroll.ViewportHeight / 2, null, disableAnimation: true);
                view.ReleasePin();
                await Task.Delay(700);
                var first = Enumerable.Range(0, rows.ItemsSourceView.Count).Select(i => (Index: i, Row: rows.TryGetElement(i) as FrameworkElement))
                    .FirstOrDefault(r => r.Row is not null && r.Row.TransformToVisual(scroll).TransformPoint(default).Y + r.Row.ActualHeight > 0);
                var id = first.Row is null ? null : ((HistoryRow)rows.ItemsSourceView.GetAt(first.Index)).Id;
                var top = first.Row?.TransformToVisual(scroll).TransformPoint(default).Y;
                if (button && ById<Button>(view, "LoadEarlierMessages") is { } load)
                {
                    ((IInvokeProvider)new ButtonAutomationPeer(load).GetPattern(PatternInterface.Invoke)).Invoke();
                }
                hold.TrySetResult();
                await Until(() => history.Turns.Count >= count, TimeSpan.FromSeconds(5));
                await Task.Delay(700);
                var index = history.Turns.ToList().FindIndex(t => t.Id == id);
                var after = index < 0 ? null : rows.TryGetElement(index) as FrameworkElement;
                check(history.Turns.Count == count && after is not null && top is { } y && Math.Abs(after.TransformToVisual(scroll).TransformPoint(default).Y - y) <= 1,
                    $"{(button ? "Load earlier messages" : "scrolling near the top")} adds twenty earlier messages and keeps the first visible turn in place");
            }
            await Page(true, 40);
            await Page(false, 60);
            check(history.Turns.Select(t => t.Id).Distinct().Count() == history.Turns.Count, "earlier pages never duplicate a turn");
            lock (requests) { check(requests.SequenceEqual(new string?[] { null, "60", "40" }), "the button and scrolling use consecutive pages of the same history"); }
            var line = ById<TextBlock>(view, "HistoryLine")!;
            var retry = Find<Button>(view).Single(b => b.Content is "Try again");
            check(line.Visibility == Visibility.Collapsed && retry.Visibility == Visibility.Collapsed,
                "shows neither a start row nor Try again while the host still has earlier messages");

            (HistoryRow Row, double Top) Reading()
            {
                for (var i = 0; i < rows.ItemsSourceView.Count; i++)
                {
                    if (rows.TryGetElement(i) is FrameworkElement row && row.TransformToVisual(scroll).TransformPoint(default).Y is var y && y + row.ActualHeight > 0)
                    {
                        return ((HistoryRow)rows.ItemsSourceView.GetAt(i), y);
                    }
                }
                throw new InvalidOperationException("No stored turn is visible.");
            }
            void Kept((HistoryRow Row, double Top) before, string when)
            {
                var now = Reading();
                check(ReferenceEquals(before.Row, now.Row) && Math.Abs(before.Top - now.Top) <= 1,
                    $"keeps the first visible turn in place {when} ({before.Row.Id} at {before.Top:0.#}, now {now.Row.Id} at {now.Top:0.#})");
            }
            scroll.ChangeView(null, scroll.ScrollableHeight / 2, null, disableAnimation: true);
            view.ReleasePin();
            await Task.Delay(700);
            var reading = Reading();
            var held = history.Turns.ToArray();
            var drawn = Enumerable.Range(0, rows.ItemsSourceView.Count).Select(rows.ItemsSourceView.GetAt).ToArray();
            Volatile.Write(ref failPage, 1);
            await history.LoadOlderAsync();
            await Until(() => retry.Visibility == Visibility.Visible, TimeSpan.FromSeconds(5));
            await Task.Delay(700);
            check(line.Text == "Couldn't load earlier messages" && line.Visibility == Visibility.Visible && retry.Visibility == Visibility.Visible,
                "a failed page shows one Couldn't load earlier messages row with Try again");
            check(held.SequenceEqual(history.Turns) && drawn.SequenceEqual(Enumerable.Range(0, rows.ItemsSourceView.Count).Select(rows.ItemsSourceView.GetAt)),
                "a failed page leaves the held turns and drawn rows unchanged");
            Kept(reading, "when only the failed status row appears");

            reading = Reading();
            var retryHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Volatile.Write(ref pageHold, retryHold);
            Volatile.Write(ref failPage, 0);
            ((IInvokeProvider)new ButtonAutomationPeer(retry).GetPattern(PatternInterface.Invoke)).Invoke();
            await Until(() => history.State == HistoryLoad.Loading && retry.Visibility == Visibility.Collapsed, TimeSpan.FromSeconds(5));
            await Task.Delay(700);
            Kept(reading, "while Try again retries the page without replacing the turns");
            retryHold.TrySetResult();
            await Until(() => history.State == HistoryLoad.StartReached && rows.ItemsSourceView.Count == 80, TimeSpan.FromSeconds(10));
            await Task.Delay(700);
            check(line.Visibility == Visibility.Visible && line.Text == HistoryDraw.Line(HistoryLoad.StartReached, 80, false, new HistoryStart { At = StoredFrom })
                && Find<TextBlock>(view).Count(b => b.Visibility == Visibility.Visible && b.Text.StartsWith("Start of session · ", StringComparison.Ordinal)) == 1
                && retry.Visibility == Visibility.Collapsed && ById<Button>(view, "LoadEarlierMessages")!.Visibility == Visibility.Collapsed,
                "the last page shows exactly one dated Start of session row and no earlier or retry action");
            Kept(reading, "when the retry reaches the start of the session");
            lock (requests) { check(requests.TakeLast(2).SequenceEqual(new[] { "20", "20" }), "Try again asks the real loader for the failed cursor again"); }

            Volatile.Write(ref limited, 1);
            view.Card = new SessionCardViewModel(card.Session with { Id = "limited" },
                new CardContext { Host = host, Deliveries = new Deliveries(host, ui), Folds = new FoldStore(null), Capabilities = () => host.Capabilities });
            await Until(() => view.Card.Stored?.State == HistoryLoad.StartReached && line.Text == "Start of available history", TimeSpan.FromSeconds(10));
            check(line.Visibility == Visibility.Visible && line.Text == "Start of available history"
                && Find<TextBlock>(view).Count(b => b.Visibility == Visibility.Visible && b.Text == "Start of available history") == 1,
                "a limited last page shows exactly one Start of available history row");
        }
        finally { view.Card = previous; Directory.Delete(folder); }
    }
}
