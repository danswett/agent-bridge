using AgentBridge.Local;

namespace AgentBridge.App.Core;

public enum SkewHealKind { Converged, Heal, Hold }

// Heal: start the checksum-verified apply of Target. Hold: not yet, until Until (when there is a time) and why (Reason).
public sealed record SkewHealDecision(SkewHealKind Kind, string? Target = null, DateTimeOffset? Until = null, string? Reason = null);

// #2869 G1 (problem 3, version skew): what to do when the app and the host report different releases (#2833; detection is
// SettingsViewModel.VersionSkew). Pure: no I/O, the clock and every fact are arguments. It never discovers a package: the newest
// VERIFIED cached one (checksum checked, marked ready) is an input, and an absent or older one is a Hold, never a Heal. Wiring this
// into the auto-updater (G2) is a later slice.
public static class VersionSkewHeal
{
    // After this many failed heals in a row the Hold says so ("failed-repeatedly"); it still lets one more through once the backoff has
    // passed, since UpdateBackoff.After doubles to its one-hour cap and so never loops tightly.
    public const int FailureBound = 3;

    public static SkewHealDecision Decide(string? app, string? host, string? verifiedCached, int failures, DateTimeOffset? lastAttempt, DateTimeOffset now)
    {
        // Releases compare by UpdateVersions.Identity, the one normaliser: a missing, 0.0.0 or unreadable one is unknown, not skew.
        if (UpdateVersions.Identity(app) is not { } appId || UpdateVersions.Identity(host) is not { } hostId
            || Parse(appId) is not { } appRelease || Parse(hostId) is not { } hostRelease) { return Hold("version-unknown"); }
        if (string.Equals(appId, hostId, StringComparison.OrdinalIgnoreCase)) { return new(SkewHealKind.Converged); }

        // Both sides must end on a package at least as new as the newer of them.
        var newer = appRelease.CompareTo(hostRelease) >= 0 ? appRelease : hostRelease;
        if (UpdateVersions.Identity(verifiedCached) is not { } cachedId || Parse(cachedId) is not { } cached || cached.CompareTo(newer) < 0) { return Hold("no-verified-package"); }

        if (failures > 0 && lastAttempt is { } last)
        {
            // A last attempt from a clock that has since moved back waits at most the cap from now, not for ever.
            var until = last + UpdateBackoff.After(failures);
            if (until > now + UpdateBackoff.Cap) { until = now + UpdateBackoff.Cap; }
            if (now < until) { return Hold(failures >= FailureBound ? "failed-repeatedly" : "backoff", until); }
        }
        return new(SkewHealKind.Heal, cachedId);
    }

    static SkewHealDecision Hold(string reason, DateTimeOffset? until = null) => new(SkewHealKind.Hold, null, until, reason);

    // major.minor.build with an optional -prerelease: a prerelease is older than its release; two compare by ordinal text.
    sealed record Release(Version Core, string? Pre) : IComparable<Release>
    {
        public int CompareTo(Release? other) =>
            other is null ? 1
            : Core.CompareTo(other.Core) is var c and not 0 ? c
            : Pre is null ? (other.Pre is null ? 0 : 1)
            : other.Pre is null ? -1 : string.CompareOrdinal(Pre, other.Pre);
    }

    static Release? Parse(string identity)
    {
        var dash = identity.IndexOf('-');
        var core = dash < 0 ? identity : identity[..dash];
        return core.Count(c => c == '.') >= 1 && Version.TryParse(core, out var v) ? new(v, dash < 0 ? null : identity[(dash + 1)..]) : null;
    }
}
