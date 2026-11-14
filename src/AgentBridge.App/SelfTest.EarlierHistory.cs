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
    static async Task CheckEarlierHistory(MainWindow window, MainViewModel main, Action<bool, string> check, bool latest = false)
    {
        var view = await Choose(window, main, "demo-approval", "Response");
        if (view is null) { check(false, "opens a conversation to check earlier messages"); return; }
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "ab-earlier-" + Guid.NewGuid().ToString("N"))).FullName;
        var requests = new List<string?>();
        TaskCompletionSource? pageHold = null;
        await using var server = LocalServer.Start(LocalEndpoint.For(folder), async (_, op, data) =>
        {
            object? result = null;
            if (op == Ops.Hello) { result = new HelloResult(1, new MachineInfo("earlier", "Test", "windows", "test"), new[] { HistoryProtocol.Capability }); }
            if (op == HistoryProtocol.Op)
            {
                var before = data.TryGetProperty("before", out var b) ? b.GetString() : null;
                lock (requests) { requests.Add(before); }
                if (before is not null && Volatile.Read(ref pageHold) is { } hold) { await hold.Task.WaitAsync(TimeSpan.FromSeconds(10)); }
                var end = before is null ? 80 : int.Parse(before);
                var from = Math.Max(0, end - 20);
                result = new HistoryResult { Turns = Enumerable.Range(from + 1, end - from).Select(StoredTurn).ToArray(), Before = from > 0 ? $"{from}" : null };
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
            if (latest)
            {
                var tabStop = scroll.IsTabStop;
                try
                {
                    scroll.IsTabStop = true;
                    await LatestJump(window, view, scroll, check);
                    var end = scroll.VerticalOffset;
                    check(await LatestInput(window, scroll, "wheel", up: true), "latest: native wheel releases the hatch before earlier paging");
                    check(await Until(() => scroll.VerticalOffset < end - 1.5, TimeSpan.FromSeconds(3)), "latest: wheel moves away from the end before earlier paging");
                    await Still(scroll);
                }
                finally { scroll.IsTabStop = tabStop; }
            }
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
        }
        finally { view.Card = previous; Directory.Delete(folder); }
    }
}
