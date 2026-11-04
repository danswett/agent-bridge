using System.Diagnostics;
using Xunit;

// A speed test on a shared CI runner can't hold a fixed wall-clock bound: under main's whole-ring
// load a linear parse of 60 KB took 1,051 ms against a 1,000 ms bound, on every main run after #494
// (#554 is the same false alarm in NeedsYouTests). What these tests guard against is quadratic work,
// so they judge growth instead, as #482 did for the PWA: the full input against a quarter of it.
// Linear work takes about 4 times as long; the quadratics they target take about 16.
static class Speed
{
    public static void IsLinear(Func<int, string> input, int size, Action<string> work)
    {
        work(input(Math.Max(1, size / 16)));
        var small = Best(input(Math.Max(1, size / 4)), work);
        var large = Best(input(size), work);
        if (large < TimeSpan.FromMilliseconds(200))
        {
            return;
        }

        Assert.True(large < TimeSpan.FromSeconds(2), $"{large.TotalMilliseconds:F0} ms at full size");
        var ratio = large.TotalMilliseconds / Math.Max(1, small.TotalMilliseconds);
        Assert.True(ratio < 9, $"{large.TotalMilliseconds:F0} ms at full size, {ratio:F1} times the quarter size's {small.TotalMilliseconds:F0} ms");
    }

    // The faster of two runs, so one stall on a loaded runner doesn't decide the verdict.
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
    static long sink;

    [Fact]
    public void Quadratic_work_fails_the_growth_check_even_inside_the_hard_bound()
    {
        Assert.ThrowsAny<Xunit.Sdk.XunitException>(() => Speed.IsLinear(n => new string('a', n), 48000, t =>
        {
            long sum = 0;
            for (var i = 0; i < t.Length; i++)
            {
                for (var j = i; j < t.Length; j++)
                {
                    sum += t[j];
                }
            }

            sink = sum;
        }));
    }

    [Fact]
    public void Linear_work_slow_enough_to_be_judged_passes_the_growth_check()
    {
        // 64 steps a character keeps the full size above the 200 ms floor, so the ratio is judged.
        Speed.IsLinear(n => new string('a', n), 8_000_000, t =>
        {
            long sum = 0;
            foreach (var c in t)
            {
                for (var k = 0; k < 64; k++)
                {
                    sum += c ^ k;
                }
            }

            sink = sum;
        });
    }
}
