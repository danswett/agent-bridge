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

    // Three by default, so the median is a true middle: of two, the lower ratio was taken, and one
    // paused smaller run (1.6× beside 16×) hid quadratic work (#875, Copilot).
    public const int DefaultRuns = 3;

    // runs pairs, each the smaller size and the larger timed one after the other; the result is the
    // pair whose ratio is the median (with an even count, the lower of the middle two).
    public static Result Measure(Action<int> work, int size, int runs = DefaultRuns) => Measure(work, size, runs, TimeProvider.System);

    // The two sizes are timed in turn, not each in a block of its own. Timed as blocks (every
    // smaller run, then every larger one, the quickest of each), a slow stretch of a shared runner
    // that covered one block moved only that size's best, and skewed the ratio either way: a linear
    // control measured 9.9× and a quadratic one 6.6× against a limit of 8× (#872, #681). In turn, a
    // stretch falls on both sizes of every pair it covers, which keep their ratio; each pair is taken
    // in the order opposite to the last, so a machine slowing or speeding up steadily favours
    // neither size; and the median pair outvotes the few a pause or a stretch's edge splits. One
    // untimed run first: its first call compiles the work, which once inflated the smaller size.
    internal static Result Measure(Action<int> work, int size, int runs, TimeProvider time)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(runs, 1);
        work(size);
        var pairs = new List<Result>(runs);
        for (var i = 0; i < runs; i++)
        {
            TimeSpan small, large;
            if (i % 2 == 0)
            {
                small = Time(() => work(size), time);
                large = Time(() => work(size * Step), time);
            }
            else
            {
                large = Time(() => work(size * Step), time);
                small = Time(() => work(size), time);
            }
            pairs.Add(new(small, large, large.TotalMilliseconds / Math.Max(small.TotalMilliseconds, Floor.TotalMilliseconds)));
        }
        pairs.Sort((a, b) => a.Ratio.CompareTo(b.Ratio));
        return pairs[(pairs.Count - 1) / 2];
    }

    static TimeSpan Time(Action work, TimeProvider time)
    {
        var start = time.GetTimestamp();
        work();
        return time.GetElapsedTime(start);
    }
}
