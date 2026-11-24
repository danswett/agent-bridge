using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// #4243: the owner wheels with the pointer over the transcript while older history is still drawing.
// Identical notches must move the same distance, and spinning down to the end must never move back up.
// Control-level notches (the wheel handler, then an animated ChangeView), not physical input: a measurement
// of what the app does between and under notches, recorded per frame to <out>.wheel-notch.json.
static partial class SelfTest
{
    static HistoryTurn MixedTurn(int n) => new()
    {
        Id = $"notch{n}",
        At = StoredFrom.AddMinutes(n),
        Prompt = new HistoryPrompt { Text = $"Notch prompt {n}" },
        Parts = new[] { new HistoryPart { Id = $"notchp{n}", Kind = HistoryPartKind.Response, At = StoredFrom.AddMinutes(n),
            Text = n % 3 == 0 ? $"Ran tool {n}:\n\n```\n{string.Join("\n", Enumerable.Range(1, 18).Select(l => $"line {l} of tool output {n}"))}\n```" : $"Reply {n}, short." } },
    };

    // Only this stage, with the frame trace left beside <out> as <out>.wheel-notch.json: for reproducing a flake with the whole trace (the CI artifact carries none). Control-level notches, so
    // no OS input is sent. Run it with --self-test-wheel-notch <out>.
    public static async Task RunWheelNotchAsync(MainWindow window, MainViewModel main, DemoHost demo, string outFile, Action<int> exit)
    {
        var (passed, failed) = (new List<string>(), new List<string>());
        void Check(bool ok, string what)
        {
            (ok ? passed : failed).Add(what);
            if (!ok) { AppLog.Write($"self-test: failed: {what}"); }
        }
        // The whole self-test has loaded the window by the time it gets here; on its own, wait until the window has a XamlRoot.
        await Until(() => window.RootElement.XamlRoot is not null, TimeSpan.FromSeconds(15));
        try { await CheckWheelNotches(window, main, demo, Check, outFile); }
        catch (Exception e) { Check(false, $"wheel notch crashed: {e}"); }
        await Write(outFile, new Dictionary<string, object?>(), passed, failed, null);
        exit(failed.Count == 0 ? 0 : 1);
    }

    static async Task CheckWheelNotches(MainWindow window, MainViewModel main, DemoHost demo, Action<bool, string> check, string outFile)
    {
        window.ShowPage("sessions");
        Resize(window, 1000, 700);
        const string id = "demo-wheel-notch";
        var fixture = DemoHost.Heavy(DateTimeOffset.UtcNow).Single(s => s.Id == DemoHost.HeavyId) with { Id = id, Title = "Wheel notch distance", Subagents = null };
        var trace = new List<object>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await demo.PutAsync(fixture);
        SessionView? view = null;
        try
        {
            if (!await Until(() => main.AllCards.Any(c => c.Id == id), TimeSpan.FromSeconds(10))
                || main.AllCards.Single(c => c.Id == id).Stored is not { } stored)
            { check(false, "wheel notch setup: receives an isolated demo history"); return; }
            await stored.LoadNewestAsync();
            for (var n = 1; n <= 300; n++) { stored.AddLive(MixedTurn(n)); }
            view = await Choose(window, main, id, "Response");
            if (view is null || ConversationScroller(view) is not { } scroll)
            { check(false, "wheel notch setup: has the conversation scroller"); return; }
            // The owner's pointer rests over the transcript as they wheel.
            view.HoldConversationPointer(4243);
            var moving = false;
            var phase = "setup";
            scroll.ViewChanged += (_, e) =>
            {
                moving = e.IsIntermediate;
                trace.Add(new { ms = clock.ElapsedMilliseconds, phase, kind = "view", offset = scroll.VerticalOffset, extent = scroll.ScrollableHeight, e.IsIntermediate });
            };
            async Task<double> Frame()
            {
                var drawn = new TaskCompletionSource<bool>();
                EventHandler<object> rendered = (_, _) => drawn.TrySetResult(true);
                CompositionTarget.Rendering += rendered;
                try { await drawn.Task.WaitAsync(TimeSpan.FromSeconds(3)); }
                finally { CompositionTarget.Rendering -= rendered; }
                trace.Add(new { ms = clock.ElapsedMilliseconds, phase, kind = "frame", offset = scroll.VerticalOffset, extent = scroll.ScrollableHeight, moving });
                return scroll.VerticalOffset;
            }
            const double notch = 3 * ConversationKeyboard.Line;
            // One notch: the wheel handler's custody, an animated move, then frames until it is still. Returns where it
            // started, where it was sent, and the lowest offset any frame showed.
            async Task<(double From, double To, double Low)> Notch(double direction)
            {
                var from = scroll.VerticalOffset;
                var to = Math.Clamp(from + direction * notch, 0, scroll.ScrollableHeight);
                view.WheelForSelfTest(direction < 0 ? 120 : -120);
                scroll.ChangeView(null, to, null, disableAnimation: false);
                trace.Add(new { ms = clock.ElapsedMilliseconds, phase, kind = "notch", from, to });
                var low = from;
                for (var (n, still) = (0, 0); n < 40 && still < 3; n++)
                {
                    low = Math.Min(low, await Frame());
                    still = moving ? 0 : still + 1;
                }
                return (from, to, low);
            }
            for (var n = 0; n < 30; n++) { await Frame(); }
            phase = "up";
            // Measured from the end, as the person sees it: older turns drawn above during a notch raise the offset by
            // their height while what is read stays put (CI 45303880104 read -660, -306, … as raw offset). Eight notches
            // stay within the first viewport above the end, where every row below is already laid out.
            var (moved, grew) = (new List<double>(), new List<double>());     // grew: the extent's change per notch, so a short notch can be told from a lost prepend
            for (var n = 0; n < 8; n++)
            {
                var (fromEnd, extentBefore) = (scroll.ScrollableHeight - scroll.VerticalOffset, scroll.ScrollableHeight);
                var (_, to, _) = await Notch(-1);
                if (to > 0.5) { moved.Add(Math.Round(scroll.ScrollableHeight - scroll.VerticalOffset - fromEnd, 1)); grew.Add(Math.Round(scroll.ScrollableHeight - extentBefore)); }
            }
            var notchFailure = WheelNotchRule.Failure(moved, notch);   // each notch within 4 px, the notches together within 1 px each on average (the owner's call, #4500)
            check(notchFailure is null,
                $"wheel notch distance: identical notches up move {notch} each from the end while older history draws ({notchFailure ?? "within the rule"}; moved {string.Join(", ", moved)}; extent grew {string.Join(", ", grew)})");
            phase = "down";
            var (offsets, back) = (new List<double>(), 0.0);
            var high = scroll.VerticalOffset;
            for (var n = 0; n < 14; n++)
            {
                var (_, _, low) = await Notch(1);
                back = Math.Max(back, high - Math.Min(low, scroll.VerticalOffset));
                high = Math.Max(high, scroll.VerticalOffset);
                offsets.Add(Math.Round(scroll.VerticalOffset, 1));
            }
            // Judged by STATE, not by how long the view takes: the worst distance from the end over a fixed 60 frames failed about one main run in three on a loaded hosted runner (quiet gap 771,
            // 1833) while the worst back stayed 0. Settled = a run of rendered frames at the end, not moving, offset and extent unchanged; never moving back up stays strict, across the
            // notches above and every frame here (ScrollSettle, tested in App.Tests). The wall clock is only a backstop so a view that never arrives fails instead of hanging the run.
            phase = "settle";
            var settle = new ScrollSettle(high, back);
            var (settleFrom, giveUp) = (clock.Elapsed, clock.Elapsed + TimeSpan.FromSeconds(90));
            while (!settle.Done && clock.Elapsed < giveUp) { await Frame(); settle.Observe(scroll.VerticalOffset, scroll.ScrollableHeight, moving); }
            check(settle.Passed,
                $"wheel down to the end: never moves back up and stays at the end ({settle.Describe()}, {(clock.Elapsed - settleFrom).TotalSeconds:0.#} s, pin {view.PinState()}; offsets {string.Join(", ", offsets)}; end {scroll.ScrollableHeight:0.#})");
        }
        finally
        {
            view?.ReleaseConversationPointer(4243);
            await File.WriteAllTextAsync(Path.ChangeExtension(outFile, ".wheel-notch.json"), JsonSerializer.Serialize(trace));
            Away(window, main);
            await demo.RemoveSessionAsync(id);
        }
    }
}
