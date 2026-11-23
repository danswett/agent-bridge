using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

public enum SkewHealKind { Converged, Heal, Hold }

// Heal: start the checksum-verified apply of Target. Hold: not yet, until Until (when there is a time) and why (Reason).
public sealed record SkewHealDecision(SkewHealKind Kind, string? Target = null, DateTimeOffset? Until = null, string? Reason = null);

// #2869 G1 (problem 3, version skew): what to do when the app and the host report different releases (#2833; detection is
// SettingsViewModel.VersionSkew). Pure: no I/O, the clock and every fact are arguments. It never discovers a package: the newest
// VERIFIED cached one (checksum checked, marked ready) is an input, and an absent or older one is a Hold, never a Heal. Wiring this
// into the auto-updater (G2) is a later slice.
//
// Releases are ordered by the comparer the updater already trusts, ReleaseVersions.TryRun (0.1.N, N a canonical run number: what
// ReleaseEligibility.IsNewer and the halt policy use), on UpdateVersions.Identity's normalised text. Anything else, a prerelease
// included, is not a release it can order: unknown, never a heal target, so no ordering of prerelease text can ever pick a downgrade.
public static class VersionSkewHeal
{
    // After this many failed heals in a row the Hold says so ("failed-repeatedly"); it still lets one more through once the backoff has
    // passed, since UpdateBackoff.After doubles to its one-hour cap and so never loops tightly.
    public const int FailureBound = 3;

    // G2 WIRING CONTRACT (#2869): `verifiedCached` is the caller's say-so. G2 passes ONLY the version of a cached package it has VERIFIED itself,
    // at the call, by the same checksum check the install uses (marked ready, digest matching the chosen feed's); never a version read from a file
    // name, a locator, the relay, the host or the other side of the skew, and null when it has none. A cached version at least as new as both sides
    // is a Heal on that word alone: this function cannot tell a verified package from a named one.
    public static SkewHealDecision Decide(string? app, string? host, string? verifiedCached, int failures, DateTimeOffset? lastAttempt, DateTimeOffset now)
    {
        // A missing, 0.0.0 or unreadable one is unknown, not skew.
        if (Run(app) is not { } appRun || Run(host) is not { } hostRun) { return Hold("version-unknown"); }
        if (appRun == hostRun) { return new(SkewHealKind.Converged); }

        // Both sides must end on a package at least as new as the newer of them.
        if (Run(verifiedCached) is not { } cachedRun || cachedRun < Math.Max(appRun, hostRun)) { return Hold("no-verified-package"); }

        if (failures > 0 && lastAttempt is { } last)
        {
            // A last attempt from a clock that has since moved back waits at most the cap from now, not for ever.
            var until = last + UpdateBackoff.After(failures);
            if (until > now + UpdateBackoff.Cap) { until = now + UpdateBackoff.Cap; }
            if (now < until) { return Hold(failures >= FailureBound ? "failed-repeatedly" : "backoff", until); }
        }
        return new(SkewHealKind.Heal, UpdateVersions.Identity(verifiedCached));
    }

    static long? Run(string? version) =>
        UpdateVersions.Identity(version) is { } identity && ReleaseVersions.TryRun(identity, out var run) ? run : null;

    static SkewHealDecision Hold(string reason, DateTimeOffset? until = null) => new(SkewHealKind.Hold, null, until, reason);
}
