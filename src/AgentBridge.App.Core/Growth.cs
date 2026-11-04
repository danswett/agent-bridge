using System.Diagnostics;

namespace AgentBridge.App.Core;

// Whether work grows linearly with its input, judged by timing it at two sizes in the same run
// rather than against a wall-clock limit (#681). A slow machine is slower at both sizes, so a
// limit in milliseconds failed on CI's Arm64 runner (20,000 `[](` drawn in 1.6 s, linearly) while
// a quadratic cost still shows: four times the input takes about four times as long when the work
// is linear, and sixteen times when it is quadratic.
public static class Growth
{
    public const int Step = 4;
    // Between the two, geometrically: room for noise above 4, well short of 16.
    public const double MaxRatio = 8;
    // A smaller run quicker than this is timed as this long, so a timer's noise on a fast run can't
    // fail it; work that is really quadratic is long past it at the sizes measured.
    public static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(10);

    public readonly record struct Result(TimeSpan Small, TimeSpan Large, double Ratio)
    {
        public bool Linear => Ratio <= MaxRatio;
        public override string ToString() => $"{Small.TotalMilliseconds:0} ms, then {Large.TotalMilliseconds:0} ms at {Step}×, {Ratio:0.0}×";
    }

    // The quickest of a few runs at each size: what the work costs, without the pauses a busy
    // machine adds to some of them.
    public static Result Measure(Action<int> work, int size, int runs = 2)
    {
        var small = Quickest(() => work(size), runs);
        var large = Quickest(() => work(size * Step), runs);
        return new(small, large, large.TotalMilliseconds / Math.Max(small.TotalMilliseconds, Floor.TotalMilliseconds));
    }

    static TimeSpan Quickest(Action work, int runs)
    {
        var best = TimeSpan.MaxValue;
        for (var i = 0; i < runs; i++)
        {
            var clock = Stopwatch.StartNew();
            work();
            best = TimeSpan.FromTicks(Math.Min(best.Ticks, clock.Elapsed.Ticks));
        }
        return best;
    }
}
