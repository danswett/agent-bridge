using System.Runtime.CompilerServices;
using AgentBridge.App.Core;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The self-test's check that agent text is drawn in linear time judges growth between two sizes,
// not milliseconds (#681): these are its controls, a quadratic cost it must fail and a linear one,
// however slow, it must pass. Each step is a chain of square roots, each waiting on the last, so
// no CPU runs it much faster than a few nanoseconds and the smaller size stays above Growth.Floor:
// a ratio taken against the floor would be a wall-clock limit again. Run alone: other tests on the
// same cores during one size and not the other would skew the ratio.
//
// The loops are compiled fully optimized from their first call. Tiered compilation otherwise runs
// the smaller size, which comes first, on unoptimized code that calls Step instead of inlining it,
// until tier 1 arrives about 100 ms later. That inflated the smaller time and pulled a quadratic
// control down to 6.1× on a Linux runner (#710), when about 16× is expected, against a limit of 8×.
// Five pairs, the sizes timed in turn and the median pair taken (Growth.Measure), absorb the pauses
// and the slow stretches a busy runner adds to some of them (#872).
[Collection(nameof(GrowthTests))]
public sealed class GrowthTests
{
    static double sink;

    const int Runs = 5;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static double Step(double x, int i) => Math.Sqrt(x + i);

    // A cost that is the square of the input, as Markdig's was on runs of `[](`.
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void Quadratic(int n)
    {
        var x = 1.0;
        for (var i = 0; i < n; i++) { for (var j = 0; j < n; j++) { x = Step(x, j); } }
        sink = x;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void Linear(int n, int slower)
    {
        var x = 1.0;
        for (var i = 0; i < n * slower; i++) { x = Step(x, i); }
        sink = x;
    }

    [Fact]
    public void Quadratic_work_is_not_linear()
    {
        var result = Growth.Measure(Quadratic, 2000, Runs);

        Assert.True(result.Small >= Growth.Floor, $"too quick to judge: {result}");
        Assert.False(result.Linear, result.ToString());
    }

    [Fact]
    public void Linear_work_is_linear_however_slow_the_machine()
    {
        // Twice as slow at both sizes as the quadratic's smaller one, as a busy Arm64 runner is slower
        // than a laptop: the same growth.
        var result = Growth.Measure(n => Linear(n, slower: 2 * 2000), 2000, Runs);

        Assert.True(result.Small >= Growth.Floor, $"too quick to judge: {result}");
        Assert.True(result.Linear, result.ToString());
    }
}

[CollectionDefinition(nameof(GrowthTests), DisableParallelization = true)]
public sealed class GrowthCollection;

// The measurer itself, on a fake clock (#872): work that costs a fixed time for each unit of it,
// three times that through a slow stretch of the machine in one phase of the measurement, as a
// shared runner has. Blocks times it as Measure did before: every smaller run, then every larger
// one, the quickest of each. A stretch over one size's block skewed that ratio past the limit, a
// linear control to 12× and a quadratic one to 5.3×; timed in turn, the sizes keep their ratio.
public sealed class GrowthMeasureTests
{
    const int Size = 1000;
    const int Runs = 5;
    static readonly DateTimeOffset Start = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);
    // 20 µs a unit: the linear work's smaller size takes 20 ms, past Growth.Floor.
    static readonly TimeSpan Unit = TimeSpan.FromTicks(200);

    static Action<int> Costing(FakeTimeProvider clock, Func<int, long> units, TimeSpan slowFrom, TimeSpan slowUntil) => n =>
    {
        var at = clock.GetUtcNow() - Start;
        var slower = at >= slowFrom && at < slowUntil ? 3 : 1;
        clock.Advance(Unit * (units(n) * slower));
    };

    static Growth.Result Blocks(FakeTimeProvider clock, Action<int> work)
    {
        TimeSpan Quickest(int n)
        {
            var best = TimeSpan.MaxValue;
            for (var i = 0; i < Runs; i++)
            {
                var start = clock.GetTimestamp();
                work(n);
                var took = clock.GetElapsedTime(start);
                if (took < best) { best = took; }
            }
            return best;
        }
        var small = Quickest(Size);
        var large = Quickest(Size * Growth.Step);
        return new(small, large, large.TotalMilliseconds / Math.Max(small.TotalMilliseconds, Growth.Floor.TotalMilliseconds));
    }

    // Main at b1da948b: 61 ms, then 603 ms at 4×, 9.9×. The stretch begins as the smaller block
    // ends (5 runs of 20 ms) and lasts to the end.
    [Fact]
    public void A_slow_stretch_over_the_larger_runs_no_longer_makes_linear_work_look_steeper()
    {
        Func<int, long> linear = n => n;
        var from = Unit * (Runs * Size);
        var before = new FakeTimeProvider(Start);
        var now = new FakeTimeProvider(Start);

        var blocks = Blocks(before, Costing(before, linear, from, TimeSpan.MaxValue));
        var measured = Growth.Measure(Costing(now, linear, from, TimeSpan.MaxValue), Size, Runs, now);

        Assert.False(blocks.Linear, $"timed as blocks: {blocks}");
        Assert.True(measured.Linear, measured.ToString());
        Assert.Equal(Growth.Step, measured.Ratio, 3);
    }

    // #872 on #837: 91 ms, then 606 ms at 4×, 6.6×. The stretch covers the smaller block, each of
    // its 5 runs three times as slow, and ends there.
    [Fact]
    public void A_slow_stretch_over_the_smaller_runs_no_longer_makes_quadratic_work_look_linear()
    {
        Func<int, long> quadratic = n => (long)n * n / 50;
        var until = Unit * (3 * Runs * quadratic(Size));
        var before = new FakeTimeProvider(Start);
        var now = new FakeTimeProvider(Start);

        var blocks = Blocks(before, Costing(before, quadratic, TimeSpan.Zero, until));
        var measured = Growth.Measure(Costing(now, quadratic, TimeSpan.Zero, until), Size, Runs, now);

        Assert.True(blocks.Linear, $"timed as blocks: {blocks}");
        Assert.False(measured.Linear, measured.ToString());
        Assert.Equal(Growth.Step * Growth.Step, measured.Ratio, 3);
    }

    // The default, as the app's self-test measures: one paused smaller run (1.6× beside 16×) is
    // outvoted, so quadratic work still reads as quadratic (#875, Copilot).
    [Fact]
    public void A_paused_smaller_run_cannot_hide_quadratic_work_at_the_default_number_of_pairs()
    {
        var clock = new FakeTimeProvider(Start);
        var run = 0;
        var measured = Growth.Measure(n =>
        {
            // Run 1 is the untimed first run; run 2 is the first pair's smaller one.
            clock.Advance(Unit * ((long)n * n / 50) * (++run == 2 ? 10 : 1));
        }, Size, Growth.DefaultRuns, clock);

        Assert.False(measured.Linear, measured.ToString());
        Assert.Equal(Growth.Step * Growth.Step, measured.Ratio, 3);
    }

    // A pause in one run of one pair is outvoted, whichever size it falls on.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void A_pause_in_one_run_is_outvoted_by_the_other_pairs(int pausedRun)
    {
        var clock = new FakeTimeProvider(Start);
        var run = 0;
        var measured = Growth.Measure(n =>
        {
            // The untimed first run is run 0; the pause lands on the first pair's smaller or larger.
            clock.Advance(Unit * n * (++run == 2 + pausedRun ? 10 : 1));
        }, Size, Runs, clock);

        Assert.Equal(Growth.Step, measured.Ratio, 3);
    }
}