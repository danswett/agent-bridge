using System.Diagnostics;
using Xunit;

// A speed test on a shared CI runner can't hold a fixed wall-clock bound: under main's whole-ring
// load a linear parse of 60 KB took 1,051 ms against a 1,000 ms bound, on every main run after #494
// (#554 is the same false alarm in NeedsYouTests). What these tests guard against is quadratic work,
// so they judge growth instead, as #482 did for the PWA (pwa/test/unit/markdown.test.mjs): the input
// at a sixteenth, a quarter and its full size. Linear work grows about 4 and 16 times across those;
// the quadratics they target grow about 16 and 256 times.
static class Speed
{
    public static readonly TimeSpan Floor = TimeSpan.FromMilliseconds(200);

    public static void IsLinear(Func<int, string> input, int size, Action<string> work)
    {
        var texts = new[] { input(Math.Max(1, size / 16)), input(Math.Max(1, size / 4)), input(size) };
        work(texts[0]);
        var verdict = Verdict(Measure(texts, work));
        // One stall can land in a single size's runs, as the PWA's 22.9 → 250.8 ms did; a quadratic
        // fails again when measured afresh, a stall rarely lands the same way twice.
        if (verdict is not null)
        {
            verdict = Verdict(Measure(texts, work));
        }

        Assert.True(verdict is null, verdict?.Reason);
    }

    public sealed record Failure(string Reason);

    // Past the floor, both growth bounds must fail so one slow size doesn't decide.
    public static Failure? Verdict(TimeSpan[] times)
    {
        var (smallest, middle, largest) = (times[0], times[1], times[2]);
        if (largest < Floor)
        {
            return null;
        }

        var overMiddle = largest.TotalMilliseconds / Math.Max(0.1, middle.TotalMilliseconds);
        var overSmallest = largest.TotalMilliseconds / Math.Max(0.1, smallest.TotalMilliseconds);
        return overMiddle >= 9 && overSmallest >= 100
            ? new($"{largest.TotalMilliseconds:F0} ms at full size: {overMiddle:F1} times the quarter size and {overSmallest:F0} times the sixteenth")
            : null;
    }

    static TimeSpan[] Measure(string[] texts, Action<string> work) => texts.Select(t => Best(t, work)).ToArray();

    // The faster of two runs, so one stall on a loaded runner doesn't decide a size's time.
    static TimeSpan Best(string text, Action<string> work)
    {
        var best = TimeSpan.MaxValue;
        for (var i = 0; i < 2; i++)
        {
            var clock = Stopwatch.StartNew();
            work(text);
            if (clock.Elapsed < best)
            {
                best = clock.Elapsed;
            }
        }

        return best;
    }
}

public sealed class SpeedTests
{
    static TimeSpan[] Ms(double a, double b, double c) => [TimeSpan.FromMilliseconds(a), TimeSpan.FromMilliseconds(b), TimeSpan.FromMilliseconds(c)];

    [Fact]
    public void Linear_growth_past_the_floor_passes() => Assert.Null(Speed.Verdict(Ms(60, 240, 960)));

    [Theory]
    [InlineData(1000)]
    [InlineData(2169)]
    [InlineData(20000)]
    public void Quadratic_growth_fails_on_fast_and_slow_runners(double largest)
    {
        var verdict = Speed.Verdict(Ms(largest / 256, largest / 16, largest));

        Assert.NotNull(verdict);
    }

    [Fact]
    public void One_slow_size_on_a_loaded_runner_does_not_fail_on_its_own()
    {
        // The PWA's false alarm (#482): 22.9 ms at the quarter size, then 250.8 ms after a collection.
        Assert.Null(Speed.Verdict(Ms(6, 22.9, 250.8)));
    }

    [Fact]
    public void Anything_under_the_floor_passes_whatever_its_growth() => Assert.Null(Speed.Verdict(Ms(0.1, 3, 199)));

    [Theory]
    [InlineData(2000)]
    [InlineData(2169)]
    [InlineData(20000)]
    public void Linear_growth_still_passes_when_a_slow_runner_takes_more_than_two_seconds(double largest) =>
        Assert.Null(Speed.Verdict(Ms(largest / 16, largest / 4, largest)));
}
