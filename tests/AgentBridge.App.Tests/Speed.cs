using System.Runtime.CompilerServices;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Xunit.Abstractions;

// A speed test on a shared CI runner can't hold a fixed wall-clock bound: under main's whole-ring
// load a linear parse of 60 KB took 1,051 ms against a 1,000 ms bound (#554), and later 2,169 and
// 2,232 ms against a 2,000 ms cap (#688). What these tests guard against is quadratic work, so they
// judge growth only: the input at a quarter and at its full size, timed in turn and the median pair
// taken, as the app's self-test measures (#875), so a slow stretch of the runner lands on both
// sizes of a pair rather than on one size's block. Four times the input takes about four times as
// long when the work is linear, and sixteen times when it is quadratic; MaxRatio (8) is between
// them. The measurer is the tests' own, not the app's (#688): a change to the app's must not
// silently change what every speed check means.
static class Speed
{
    public const int Step = 4;
    // Between linear's 4 and quadratic's 16, geometrically: room for noise above 4, well short of 16.
    public const double MaxRatio = 8;
    // Three pairs, so the median is a true middle: of two, the lower ratio was taken, and one paused
    // quarter-size run (1.6× beside 16×) hid quadratic work (#875).
    public const int Runs = 3;
    // A quarter-size run quicker than this is timed as this long, so a timer's noise on a fast run
    // can't fail it.
    public static readonly TimeSpan QuarterFloor = TimeSpan.FromMilliseconds(10);
    // Work this quick at full size is past judging, and past mattering: a timer's noise would decide.
    public static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(200);

    public readonly record struct Pair(TimeSpan Quarter, TimeSpan Full)
    {
        public double Ratio => Full.TotalMilliseconds / Math.Max(Quarter.TotalMilliseconds, QuarterFloor.TotalMilliseconds);
        public override string ToString() => $"{Quarter.TotalMilliseconds:0} ms, then {Full.TotalMilliseconds:0} ms at {Step}×, {Ratio:0.0}×";
    }

    public static void IsLinear(Func<int, string> input, int size, Action<string> work) => IsLinear(input, size, work, TimeProvider.System);

    // measured hears every pair judged, so a test can show its margin.
    internal static void IsLinear(Func<int, string> input, int size, Action<string> work, TimeProvider time, Action<Pair>? measured = null)
    {
        var quarterSize = Math.Max(1, size / Step);
        // Built before any timing: only the work is measured.
        var quarter = input(quarterSize);
        var full = input(quarterSize * Step);
        string? Judge()
        {
            var pair = Measure(() => work(quarter), () => work(full), time);
            measured?.Invoke(pair);
            return Verdict(pair);
        }

        var verdict = Judge();
        // A quadratic fails again when measured afresh; a pause rarely lands the same way twice.
        if (verdict is not null)
        {
            verdict = Judge();
        }

        Assert.True(verdict is null, verdict);
    }

    // The two sizes are timed in turn, not each in a block of its own: timed as blocks, a slow
    // stretch over one block skewed the ratio either way (#872). Each pair goes in the order opposite
    // to the last, so a machine slowing or speeding up steadily favours neither size, and the median
    // pair outvotes one that a pause splits. One untimed run first: its first call compiles the work,
    // which once inflated the quarter size (#710).
    internal static Pair Measure(Action quarter, Action full, TimeProvider time)
    {
        quarter();
        var pairs = new Pair[Runs];
        for (var i = 0; i < Runs; i++)
        {
            TimeSpan q, f;
            if (i % 2 == 0)
            {
                q = Time(quarter, time);
                f = Time(full, time);
            }
            else
            {
                f = Time(full, time);
                q = Time(quarter, time);
            }

            pairs[i] = new(q, f);
        }

        Array.Sort(pairs, (a, b) => a.Ratio.CompareTo(b.Ratio));
        return pairs[(Runs - 1) / 2];
    }

    static TimeSpan Time(Action work, TimeProvider time)
    {
        var start = time.GetTimestamp();
        work();
        return time.GetElapsedTime(start);
    }

    // Null when the median pair grew linearly, or the full size took under the floor; otherwise why not.
    public static string? Verdict(Pair pair) =>
        pair.Full < Floor || pair.Ratio <= MaxRatio ? null : $"grew faster than linearly: {pair}, over {MaxRatio}×";
}

public sealed class SpeedTests
{
    static Speed.Pair Ms(double quarter, double full) => new(TimeSpan.FromMilliseconds(quarter), TimeSpan.FromMilliseconds(full));

    // #688: a linear parse that took 2,169 ms on windows-11-arm and 2,232 ms on windows-latest at
    // full size failed a 2,000 ms cap. The ratio alone judges it now, however slow the runner.
    [Theory]
    [InlineData(2169)]
    [InlineData(2232)]
    [InlineData(20000)]
    public void Linear_work_passes_however_long_the_full_size_takes(double full) => Assert.Null(Speed.Verdict(Ms(full / 4.4, full)));

    [Theory]
    [InlineData(250)]
    [InlineData(2169)]
    [InlineData(20000)]
    public void Quadratic_work_fails_on_fast_and_slow_runners_alike(double full) => Assert.NotNull(Speed.Verdict(Ms(full / 16, full)));

    [Fact]
    public void Anything_under_the_floor_passes_whatever_its_growth() => Assert.Null(Speed.Verdict(Ms(1, 199)));

    // The measurer on a fake clock: each character of input costs a fixed time, and one run is held
    // up by a pause, as a busy runner's are. The median pair outvotes the pair it lands in, so
    // quadratic work still fails and linear work, however slow, still passes.
    static readonly DateTimeOffset Start = new(2026, 11, 5, 10, 0, 0, TimeSpan.Zero);

    static Action<string> Costing(FakeTimeProvider clock, Func<long, long> microseconds, int pausedCall, TimeSpan pause, List<int>? lengths = null)
    {
        var calls = 0;
        return text =>
        {
            calls++;
            lengths?.Add(text.Length);
            clock.Advance(TimeSpan.FromTicks(microseconds(text.Length) * 10) + (calls == pausedCall ? pause : TimeSpan.Zero));
        };
    }

    [Fact]
    public void The_sizes_are_timed_in_turn_in_alternating_order_after_one_untimed_quarter_size_run()
    {
        var clock = new FakeTimeProvider(Start);
        var lengths = new List<int>();

        Speed.IsLinear(n => new string('[', n), 4000, Costing(clock, n => n * 110, pausedCall: 0, pause: TimeSpan.Zero, lengths: lengths), clock);

        Assert.Equal([1000, 1000, 4000, 4000, 1000, 1000, 4000], lengths);
    }

    // The first call is the measurer's untimed one; the second is the first pair's quarter size.
    [Fact]
    public void Quadratic_work_with_one_paused_quarter_size_run_still_fails_on_a_fake_clock()
    {
        var clock = new FakeTimeProvider(Start);
        // 30,000 characters at full size take 9 s; a quarter of them, 0.56 s.
        var work = Costing(clock, n => n * n / 100, pausedCall: 2, pause: TimeSpan.FromSeconds(30));
        var measured = new List<Speed.Pair>();

        var failed = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Speed.IsLinear(n => new string('[', n), 30000, work, clock, measured.Add));

        Assert.Contains("grew faster than linearly", failed.Message);
        Assert.All(measured, pair => Assert.Equal(16, pair.Ratio, 3));
    }

    [Fact]
    public void Linear_work_slower_than_two_seconds_with_one_paused_run_passes_on_a_fake_clock()
    {
        var clock = new FakeTimeProvider(Start);
        // 20,000 characters at full size take 2.2 s, as #688's did.
        var work = Costing(clock, n => n * 110, pausedCall: 3, pause: TimeSpan.FromSeconds(30));
        var measured = new List<Speed.Pair>();

        Speed.IsLinear(n => new string('[', n), 20000, work, clock, measured.Add);

        Assert.Equal(4, Assert.Single(measured).Ratio, 3);
    }
}

// The proof #688 asks for: the very work of Runs_of_markup_the_parser_is_quadratic_on_are_read_in_linear_time
// with an O(n²) pass added, as a regression in the parser would add one, fails the check, measured
// for real. Each step of the pass waits on the last, so no CPU makes it much cheaper than a few
// nanoseconds. It holds a core for seconds, so it runs alone, in the non-parallel collection of
// the app's own growth controls: beside the other speed tests, it would skew their sizes' times
// and they its.
//
// With the parse linear (L at the quarter size) and the pass quadratic (Q there), the pair's ratio
// is (16Q + 4L) / (Q + L): over 8 only while Q > L/2, and pulled toward 4 as load slows the parse,
// which allocates, more than a chain of square roots. So the pass has to dominate the quarter size,
// on a small input with a heavy step (#911 review):
// - a quarter of 192 `[](` is 48 of them, 144 characters; the full size is 576;
// - L: #688's slow runners parsed about 110 µs a `[](`, so 48 × 110 µs ≈ 5.3 ms;
// - Q: Weight × 144² = 384 × 20,736 ≈ 8.0 million steps; at the 6.7 ns a step windows-11-arm
//   takes (9 million in about 60 ms), about 53 ms, 10L: (16 × 10.1 + 4) / (10.1 + 1) = 14.9×;
// - at 3 ns a step, as quick as any CPU runs one, beside the slowest parse: about 24 ms, 4.5L,
//   (16 × 4.5 + 4) / (4.5 + 1) = 13.8×; so the ratio stays at 13× or more, where 8 fails it;
// - the full size, 16Q + 4L, takes 0.40 s to 0.88 s, over the 200 ms floor; the quarter size,
//   29 ms to 59 ms, over its 10 ms floor;
// - failing, it is measured twice: 2 × (4 quarter + 3 full runs) = 104Q + 32L, about 2.7 s, and
//   5.7 s on windows-11-arm, where the 12,000-character version this replaces took about 10 s.
[Collection(nameof(GrowthTests))]
public sealed class SpeedQuadraticControlTests(ITestOutputHelper output)
{
    const int Weight = 384;

    static double sink;

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    static void Quadratically(string text)
    {
        var x = 1.0;
        var steps = text.Length * Weight;
        for (var i = 0; i < text.Length; i++) { for (var j = 0; j < steps; j++) { x = Math.Sqrt(x + j); } }
        sink = x;
    }

    [Fact]
    public void A_quadratic_pass_added_to_the_markdown_work_fails_the_check()
    {
        static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

        var failed = Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Speed.IsLinear(
            n => Repeat("[](", n),
            192,
            t => { AgentBridge.App.Core.MarkdownModel.Parse(t); AgentBridge.App.Core.MarkdownModel.Outline(t); Quadratically(t); },
            TimeProvider.System,
            pair => output.WriteLine($"measured {pair}, against {Speed.MaxRatio}×")));

        output.WriteLine(failed.Message);
        Assert.Contains("grew faster than linearly", failed.Message);
    }
}