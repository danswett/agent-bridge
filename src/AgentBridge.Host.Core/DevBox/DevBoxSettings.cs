using System.Text.Json;

namespace AgentBridge.Host.Core.DevBox;

// config.json's devBox section (docs/host-config.md), agent-ha-bridge's devBox.* kept to its
// meaning (hooks/bridge-devbox.ps1, Get-BridgeDevBoxIntervalMinutes):
//
//   keepAwake        clear this Dev Box's pending stop-on-disconnect while the host runs (off)
//   intervalMinutes  how often a pass runs (15), never more than 30
//
// The cap is the whole point of the interval. A stop-on-disconnect occurrence exists only from
// the last session going until one grace period later, and Dev Box offers no grace under 60
// minutes. agent-ha-bridge 1.25.0 polled every 4 hours against a 60-minute grace, and its Dev Box
// hibernated three times in a day while every pass that ran reported success: it was never
// looking during the hour that mattered. 30 still leaves two passes inside the narrowest window.
//
// What is wrong is said, and read in the direction that does least: keepAwake that is not true or
// false, or a section that is not an object, is off; an interval that is not a whole number from 1
// is the default; one above the cap is the cap.
public sealed record DevBoxSettings
{
    public const int DefaultIntervalMinutes = 15;
    public const int MaxIntervalMinutes = 30;

    public static readonly DevBoxSettings Default = new();

    public bool KeepAwake { get; init; }
    public int IntervalMinutes { get; init; } = DefaultIntervalMinutes;

    public static (DevBoxSettings Settings, IReadOnlyList<string> Problems) Read(JsonElement? section)
    {
        var problems = new List<string>();
        if (section is not { } s || s.ValueKind == JsonValueKind.Null) { return (Default, problems); }
        if (s.ValueKind != JsonValueKind.Object)
        {
            problems.Add("devBox is not an object, so keep-awake is off");
            return (Default, problems);
        }
        var settings = Default;
        if (s.TryGetProperty("keepAwake", out var keepAwake) && keepAwake.ValueKind != JsonValueKind.Null)
        {
            if (keepAwake.ValueKind is JsonValueKind.True or JsonValueKind.False) { settings = settings with { KeepAwake = keepAwake.ValueKind == JsonValueKind.True }; }
            else { problems.Add("devBox.keepAwake is not true or false, so keep-awake is off"); }
        }
        if (s.TryGetProperty("intervalMinutes", out var interval) && interval.ValueKind != JsonValueKind.Null)
        {
            if (interval.ValueKind != JsonValueKind.Number || !interval.TryGetInt32(out var minutes) || minutes < 1)
            {
                problems.Add($"devBox.intervalMinutes is not a whole number of minutes from 1 to {MaxIntervalMinutes}, so {DefaultIntervalMinutes} is used");
            }
            else if (minutes > MaxIntervalMinutes)
            {
                problems.Add($"devBox.intervalMinutes is more than {MaxIntervalMinutes}, which could miss a pending stop entirely, so {MaxIntervalMinutes} is used");
                settings = settings with { IntervalMinutes = MaxIntervalMinutes };
            }
            else { settings = settings with { IntervalMinutes = minutes }; }
        }
        return (settings, problems);
    }
}
