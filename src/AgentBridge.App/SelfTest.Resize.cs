using System.Diagnostics;
using System.Text.Json;
using AgentBridge.App.Core;
using AgentBridge.Protocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AgentBridge.App;

// A live resize keeps the display's refresh rate (#765). The window is resized as an edge dragged
// steadily resizes it, a step a frame, from three panes to one column and back, and every interval
// between the UI thread's frames is kept (CompositionTarget.Rendering): first with the demo's own
// sessions, then with a window as heavy as a real one (DemoHost.Heavy). What it found goes in the
// result file, and beside it in .resize.json. A report, not a bar: CI's runners draw in software,
// so the numbers are for comparing one run with another, and the check fails only when the pass
// could not measure.
static partial class SelfTest
{
    const double SweepWide = 1560, SweepNarrow = 400, SweepStep = 8, SweepHeight = 900;
    // A hang guard, not a bar: the two sweeps share 30 s, the light one at most half, which keeps the
    // whole self-test well inside its 180 s (build/app-self-test.ps1) on CI's arm64 runner. A sweep
    // that runs out is cut short and reported so (lightFinished, heavyFinished); it fails the check
    // only when it never crossed all three width classes, since how fast a runner draws is reported
    // and never judged (#681).
    static readonly TimeSpan SweepBudget = TimeSpan.FromSeconds(30);

    static async Task MeasureResize(MainWindow window, MainViewModel main, DemoHost demo, Action<bool, string> check, string outFile, Dictionary<string, object?> result)
    {
        var layout = window.Sessions.Layout!;
        window.ShowPage("sessions");
        Resize(window, SweepWide, SweepHeight);
        layout.InspectorWanted = true;
        await Choose(window, main, "demo-approval", "Response");
        await Task.Delay(700);
        var budget = Stopwatch.StartNew();
        var (light, lightDone) = await Sweep(window, SweepBudget / 2);
        var lightTook = budget.Elapsed;

        await demo.AddHeavyAsync();
        await Until(() => main.AllCards.Any(c => c.Id == DemoHost.HeavyId), TimeSpan.FromSeconds(10));
        Resize(window, SweepWide, SweepHeight);
        var view = await Choose(window, main, DemoHost.HeavyId, "Response");
        layout.CloseDelegate();
        var card = view?.Card;
        // The strip with every delegate in it, the finished ones unfolded, not six and "14 finished".
        if (card is not null) { card.Delegates.FinishedOpen = true; }
        int Delegates() => card?.Delegates.Strip.Count(i => i.IsDelegate) ?? 0;
        var response = view is null ? null : ById<FrameworkElement>(view, "Response") as MarkdownView;
        var drawn = card is not null && await Until(() => card.History.Count >= Limits.History && response is { Children.Count: > 0 } && Delegates() >= DemoHost.HeavyDelegates && layout.ShowStrip && layout.ShowInspector, TimeSpan.FromSeconds(10));
        var data = new Dictionary<string, int>
        {
            ["sessions"] = main.AllCards.Count(),
            ["responseBlocks"] = response?.Children.Count ?? 0,
            ["delegateEvents"] = card?.Delegates.Events.Count ?? 0,
            ["toolRows"] = card?.History.Count ?? 0,
            ["delegates"] = Delegates(),
        };
        check(drawn && data["sessions"] > 15 && data["responseBlocks"] + data["delegateEvents"] > 200 && data["delegates"] == DemoHost.HeavyDelegates,
            $"draws a window as heavy as a real one for the resize pass, every delegate in its strip ({string.Join(", ", data.Select(d => $"{d.Value} {d.Key}"))})");
        await Task.Delay(700);
        var heavyLimit = SweepBudget - lightTook;
        var (heavy, heavyDone) = await Sweep(window, heavyLimit);
        if (card is not null) { card.Delegates.FinishedOpen = false; }

        // Back where the drag began, the session still chosen and every pane drawn.
        var settled = await Until(() => layout.Mode == PaneMode.Three && main.Selected == card && layout.ShowInspector, TimeSpan.FromSeconds(5));
        check(settled, "keeps the session chosen, with its inspector, through a live resize across every width class");
        await Picture(window.RootElement, Path.ChangeExtension(outFile, ".resize.png"));

        var report = new Dictionary<string, object?>
        {
            ["method"] = $"AppWindow.Resize a step a frame, {SweepWide}→{SweepNarrow}→{SweepWide} effective px by {SweepStep}, height {SweepHeight}; the Stopwatch interval between CompositionTarget.Rendering callbacks, by the window's width class when drawn",
            ["budgetMs"] = FrameTimes.Budget,
            ["longMs"] = FrameTimes.Long,
            ["scale"] = window.RootElement.XamlRoot.RasterizationScale,
            ["data"] = data,
            ["light"] = light.Report(),
            ["lightFinished"] = lightDone,
            ["heavy"] = heavy.Report(),
            ["heavyFinished"] = heavyDone,
        };
        result["resize"] = report;
        await File.WriteAllTextAsync(Path.ChangeExtension(outFile, ".resize.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
        string Said(FrameSummary s) => $"p50 {s.P50:0.#} ms, p95 {s.P95:0.#} ms, max {s.Max:0.#} ms, {s.OverLong} over {FrameTimes.Long} ms of {s.Frames}";
        AppLog.Write($"self-test: resize, light: {Said(light.All)}; heavy: {Said(heavy.All)}");
        // The frame times are reported only, a sweep cut short by the hang guard included; the pass
        // fails only when a sweep never reached every width class, so nothing was measured there.
        var crossed = new[] { PaneMode.Three, PaneMode.Two, PaneMode.One }.All(m => light.In(m).Frames > 0 && heavy.In(m).Frames > 0);
        string Cut(string name, bool finished, TimeSpan limit) => finished ? "" : $"; the {name} sweep cut short at {limit.TotalSeconds:0.#} s";
        if (!lightDone || !heavyDone) { AppLog.Write($"self-test: resize, reported, not failed:{Cut("light", lightDone, SweepBudget / 2)}{Cut("heavy", heavyDone, heavyLimit)}"); }
        check(crossed, $"measures the UI thread's frames through a live resize across the three width classes (heavy: {Said(heavy.All)}; light: {Said(light.All)}{Cut("light", lightDone, SweepBudget / 2)}{Cut("heavy", heavyDone, heavyLimit)})");
    }

    // Narrowed a step a frame to the narrowest, then widened to where it began, or as far as it got
    // in the time it was given. Each step is made just after a frame, so the interval to the next
    // holds its layout and drawing.
    static async Task<(FrameTimes Times, bool Finished)> Sweep(MainWindow window, TimeSpan limit)
    {
        var root = window.RootElement;
        var scale = root.XamlRoot.RasterizationScale;
        var widths = new Queue<double>();
        for (var w = SweepWide - SweepStep; w >= SweepNarrow; w -= SweepStep) { widths.Enqueue(w); }
        for (var w = SweepNarrow + SweepStep; w <= SweepWide; w += SweepStep) { widths.Enqueue(w); }
        var times = new FrameTimes();
        var done = new TaskCompletionSource();
        var clock = Stopwatch.StartNew();
        double? last = null;
        var stepping = false;
        void OnFrame(object? sender, object e)
        {
            if (done.Task.IsCompleted) { return; }
            var now = clock.Elapsed.TotalMilliseconds;
            if (last is { } before) { times.Add(now - before, root.XamlRoot.Size.Width); }
            last = now;
            if (stepping) { return; }
            if (!widths.TryDequeue(out var next)) { done.TrySetResult(); return; }
            stepping = window.DispatcherQueue.TryEnqueue(() =>
            {
                stepping = false;
                // A step queued as the limit ran out is dropped: the window goes back where it began.
                if (done.Task.IsCompleted) { return; }
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Ceiling(next * scale), (int)Math.Ceiling(SweepHeight * scale)));
            });
        }
        CompositionTarget.Rendering += OnFrame;
        try
        {
            var finished = await Task.WhenAny(done.Task, Task.Delay(limit > TimeSpan.Zero ? limit : TimeSpan.Zero)) == done.Task;
            return (times, finished);
        }
        finally
        {
            CompositionTarget.Rendering -= OnFrame;
            done.TrySetResult();
            // Where it began, however far a sweep cut short got.
            Resize(window, SweepWide, SweepHeight);
        }
    }
}
