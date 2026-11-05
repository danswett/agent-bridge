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
    static readonly TimeSpan SweepLongest = TimeSpan.FromSeconds(20);

    static async Task MeasureResize(MainWindow window, MainViewModel main, DemoHost demo, Action<bool, string> check, string outFile, Dictionary<string, object?> result)
    {
        var layout = window.Sessions.Layout!;
        window.ShowPage("sessions");
        Resize(window, SweepWide, SweepHeight);
        layout.InspectorWanted = true;
        await Choose(window, main, "demo-approval", "Response");
        await Task.Delay(700);
        var (light, lightDone) = await Sweep(window);

        await demo.AddHeavyAsync();
        await Until(() => main.AllCards.Any(c => c.Id == DemoHost.HeavyId), TimeSpan.FromSeconds(10));
        Resize(window, SweepWide, SweepHeight);
        var view = await Choose(window, main, DemoHost.HeavyId, "Response");
        layout.CloseDelegate();
        var card = view?.Card;
        var response = view is null ? null : ById<FrameworkElement>(view, "Response") as MarkdownView;
        var drawn = card is not null && await Until(() => card.History.Count >= Limits.History && response is { Children.Count: > 0 } && layout.ShowStrip && layout.ShowInspector, TimeSpan.FromSeconds(10));
        var data = new Dictionary<string, int>
        {
            ["sessions"] = main.AllCards.Count(),
            ["responseBlocks"] = response?.Children.Count ?? 0,
            ["delegateEvents"] = card?.Delegates.Events.Count ?? 0,
            ["toolRows"] = card?.History.Count ?? 0,
            ["delegates"] = card?.Delegates.Strip.Count ?? 0,
        };
        check(drawn && data["sessions"] > 15 && data["responseBlocks"] + data["delegateEvents"] > 200,
            $"draws a window as heavy as a real one for the resize pass ({string.Join(", ", data.Select(d => $"{d.Value} {d.Key}"))})");
        await Task.Delay(700);
        var (heavy, heavyDone) = await Sweep(window);

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
        var crossed = new[] { PaneMode.Three, PaneMode.Two, PaneMode.One }.All(m => light.In(m).Frames > 0 && heavy.In(m).Frames > 0);
        check(crossed, $"measures the UI thread's frames through a live resize across the three width classes (heavy: {Said(heavy.All)}; light: {Said(light.All)})");
    }

    // Narrowed a step a frame to the narrowest, then widened to where it began. Each step is made
    // just after a frame, so the interval to the next holds its layout and drawing.
    static async Task<(FrameTimes Times, bool Finished)> Sweep(MainWindow window)
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
                window.AppWindow.Resize(new Windows.Graphics.SizeInt32((int)Math.Ceiling(next * scale), (int)Math.Ceiling(SweepHeight * scale)));
            });
        }
        CompositionTarget.Rendering += OnFrame;
        try
        {
            var finished = await Task.WhenAny(done.Task, Task.Delay(SweepLongest)) == done.Task;
            return (times, finished);
        }
        finally
        {
            CompositionTarget.Rendering -= OnFrame;
            // Where it began, however far a sweep cut short got.
            Resize(window, SweepWide, SweepHeight);
        }
    }
}
