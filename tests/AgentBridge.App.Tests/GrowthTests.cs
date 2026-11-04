using System.Runtime.CompilerServices;
using AgentBridge.App.Core;
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
// The best of five runs at each size absorbs the pauses a busy runner adds to some of them.
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