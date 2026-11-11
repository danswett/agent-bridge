using Stopwatch = System.Diagnostics.Stopwatch;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AgentBridge.App;

static partial class SelfTest
{
    public static async Task RunHistoryScrollBudgetAsync(MainWindow window, MainViewModel main, DemoHost demo, string outFile, Action<int> exit)
    {
        var result = new Dictionary<string, object?>();
        var passed = new List<string>();
        var failed = new List<string>();
        void Check(bool ok, string what)
        {
            (ok ? passed : failed).Add(what);
            AppLog.Write($"history budget: {(ok ? "passed" : "failed")}: {what}");
        }
        try { await CheckHistoryScrollBudget(window, main, demo, Check, result); }
        catch (Exception e) { Check(false, $"history budget crashed: {e}"); }
        await Write(outFile, result, passed, failed, null);
        exit(failed.Count == 0 ? 0 : 1);
    }

    static async Task CheckHistoryScrollBudget(MainWindow window, MainViewModel main, DemoHost demo,
        Action<bool, string> check, Dictionary<string, object?> result)
    {
        const string id = "demo-scroll-budget";
        const int turns = 500;
        const double budgetMs = 200;
        var at = StoredFrom;
        await demo.PutAsync(new Session
        {
            Id = id, Agent = AgentKind.Copilot, Title = "Long conversation scroll budget",
            Status = SessionStatus.Idle, StatusSince = at, UpdatedAt = at,
            Activity = new Activity { UserMessage = "The latest exchange", Response = "The latest reply.", TurnCount = turns },
        });
        try
        {
            if (!await Until(() => main.AllCards.Any(c => c.Id == id), TimeSpan.FromSeconds(10)))
            {
                check(false, "receives the long conversation fixture");
                return;
            }
            window.ShowPage("sessions");
            Resize(window, 1000, 700);
            // Initialise the shared pane before timing another session's open.
            var away = await Choose(window, main, "demo-choice", "Response");
            var card = main.AllCards.Single(c => c.Id == id);
            if (away is null || card.Stored is not { } stored || ConversationScroller(away) is not { } scroll
                || ById<ListView>(away, "Conversation") is not { } list)
            {
                check(false, "has the shared conversation scroller and the fixture's history");
                return;
            }
            for (var n = 1; n <= turns; n++)
            {
                var items = string.Join("\n", Enumerable.Range(0, 5).Select(i =>
                    $"- **Step {i + 1}** of reply {n}: change `file{i}.js`, see [the notes](https://example.com/{n}/{i})."));
                var code = string.Join("\n", Enumerable.Range(0, 8).Select(i => $"const value{i} = compute({n}, {i});"));
                stored.AddLive(new HistoryTurn
                {
                    Id = $"budget-{n}", At = at.AddMinutes(n),
                    Prompt = new HistoryPrompt { Text = $"Demo prompt {n}" },
                    Parts = [new HistoryPart
                    {
                        Id = $"budget-{n}-reply", Kind = HistoryPartKind.Response, At = at.AddMinutes(n).AddSeconds(3),
                        Text = $"Reply {n} starts here, with a few words of *explanation* before the list.\n\n{items}\n\n```js\n{code}\n```\n\nAnd a closing paragraph for reply {n}, with `inline code` and **bold**.",
                    }],
                });
            }
            check(stored.Turns.Count == turns, $"holds all {turns} realistic Markdown turns before opening");
            var watch = new Stopwatch();
            double? receivedMs = null, movedMs = null, from = null;
            var method = "none";
            var openedAtBottom = false;
            var done = new TaskCompletionSource<bool>();
            var heard = new KeyEventHandler((_, e) =>
            {
                if (e.Key == VirtualKey.PageUp && from is not null) { receivedMs ??= watch.Elapsed.TotalMilliseconds; }
            });
            void Changed(object? sender, ScrollViewerViewChangedEventArgs e)
            {
                if (receivedMs is not null && from is { } start && scroll.VerticalOffset < start - 0.5)
                {
                    movedMs ??= watch.Elapsed.TotalMilliseconds;
                    done.TrySetResult(true);
                }
            }
            var timer = window.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromMilliseconds(16);
            timer.Tick += (_, _) =>
            {
                if (from is not null || main.Selected != card || scroll.ScrollableHeight <= scroll.ViewportHeight
                    || scroll.VerticalOffset < scroll.ScrollableHeight - 1.5) { return; }
                openedAtBottom = true;
                if (!list.Focus(FocusState.Programmatic)) { return; }
                from = scroll.VerticalOffset;
                // Native queued input, not ChangeView or a ready-looking control. The receipt and
                // resulting ViewChanged are timed; this is not physical foreground-input evidence.
                method = Press(window.Handle, VirtualKey.PageUp, foreground: false);
            };
            scroll.AddHandler(UIElement.PreviewKeyDownEvent, heard, true);
            scroll.ViewChanged += Changed;
            try
            {
                watch.Start();
                timer.Start();
                window.Sessions.Rows.SelectedItem = card;
                await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10)));
            }
            finally
            {
                timer.Stop();
                scroll.RemoveHandler(UIElement.PreviewKeyDownEvent, heard);
                scroll.ViewChanged -= Changed;
            }
            result["historyScrollBudget"] = new
            {
                turns, budgetMs, method, receivedMs, movedMs, openedAtBottom,
                from, offset = scroll.VerticalOffset, extent = scroll.ScrollableHeight,
            };
            check(openedAtBottom, "opens the 500-turn conversation at the latest before input");
            check(receivedMs is < budgetMs && movedMs is < budgetMs,
                $"handles the first scroll within {budgetMs} ms on {turns} turns: {method}, input {receivedMs?.ToString("0.0") ?? "absent"} ms, movement {movedMs?.ToString("0.0") ?? "absent"} ms");
        }
        finally
        {
            Away(window, main);
            await demo.RemoveSessionAsync(id);
            await Until(() => main.AllCards.All(c => c.Id != id), TimeSpan.FromSeconds(10));
        }
    }
}
