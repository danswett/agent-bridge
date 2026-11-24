using System.Globalization;

namespace AgentBridge.App.Core;

// The published-app self-test's "wheel notch distance" verdict (#4500, #4243), the owner's call: half a pixel is overly precise and prone to flakiness. Rows above the view are estimated until measured,
// so one notch can read a few pixels off while nothing the reader sees is wrong. Each notch is judged within PerNotch of the notch distance (a jump such as 402 still fails), and the notches together
// within PerNotchOnAverage each of the total (a steady drift, last night's 40 to 44 px, sums 32 px short and fails). "Never moves back up" is ScrollSettle's and is not loosened.
public static class WheelNotchRule
{
    public const double PerNotch = 4;
    public const double PerNotchOnAverage = 1;
    public const int MinimumNotches = 4;

    /// <summary>Null when the notches pass; otherwise which rule failed and the numbers.</summary>
    public static string? Failure(IReadOnlyList<double> moved, double notch)
    {
        static string N(double v) => v.ToString("0.#", CultureInfo.InvariantCulture);
        if (moved.Count < MinimumNotches) { return $"too few notches measured ({moved.Count}, need {MinimumNotches})"; }
        for (var i = 0; i < moved.Count; i++)
        {
            if (Math.Abs(moved[i] - notch) > PerNotch) { return $"per-notch rule: notch {i + 1} moved {N(moved[i])}, more than {N(PerNotch)} from {N(notch)}"; }
        }
        var (sum, expected, allowed) = (moved.Sum(), moved.Count * notch, moved.Count * PerNotchOnAverage);
        return Math.Abs(sum - expected) > allowed ? $"sum rule: the {moved.Count} notches moved {N(sum)} together, more than {N(allowed)} from {N(expected)}" : null;
    }
}
