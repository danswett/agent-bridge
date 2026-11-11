using System.Globalization;

namespace AgentBridge.App.Core;

public sealed record MachineLoadValues(string? State, double? AgeMs, double? StaleAfterMs, double? CpuPercent, double? TotalBytes, double? AvailableBytes);
public sealed record MachineLoadBar(string Label, double? Percent, string Text);
public sealed record MachineLoadBars(string State, string Word, string Icon, double? AgeMs, MachineLoadBar Cpu, MachineLoadBar Memory)
{
    // Same presentation rules as pwa/js/load-bars.js; freshness comes from the record, not a timer.
    public static MachineLoadBars From(MachineLoadValues? load, bool online = true)
    {
        var state = !online ? "offline" : load?.State is "fresh" or "partial" or "stale" or "unavailable" ? load.State : "unavailable";
        var age = Number(load?.AgeMs);
        if (state is "fresh" or "partial" && age is { } ms && Number(load?.StaleAfterMs) is { } limit && ms > limit)
            state = "stale";
        var live = state is "fresh" or "partial";
        var cpu = live && Number(load?.CpuPercent) is { } p && p <= 100 ? p : (double?)null;
        var total = Number(load?.TotalBytes);
        var available = Number(load?.AvailableBytes);
        var memory = live && total is > 0 && available is { } a && a <= total.Value
            ? (total.Value - a) / total.Value * 100 : (double?)null;
        if (state == "fresh" && (cpu is null || memory is null)) { state = "partial"; }
        var (word, icon) = state switch
        {
            "fresh" => ("Fresh", "\u25cf"),
            "partial" => ("Partial", "\u25d0"),
            "stale" => ("Stale", "\u25cc"),
            "offline" => ("Offline", "\u2298"),
            _ => ("Unavailable", "\u25cb"),
        };
        return new(state, word, icon, age, Bar("CPU (whole machine)", cpu), Bar("Memory used", memory, rounded: true));
    }

    static double? Number(double? value) => value is { } n && double.IsFinite(n) && n >= 0 ? n : null;
    static MachineLoadBar Bar(string label, double? value, bool rounded = false) =>
        new(label, value, value is { } n ? (rounded ? Math.Floor(n * 10 + 0.5) / 10 : n).ToString(CultureInfo.InvariantCulture) + "%" : "no figure");
}
