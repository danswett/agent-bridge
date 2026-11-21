using AgentBridge.Local;

namespace AgentBridge.App.Core;

public static class VersionSkewHeal
{
    public const int FailureBound = 3;

    public abstract record Decision;
    public sealed record Converged : Decision;
    public sealed record Heal(string Target) : Decision;
    public sealed record Hold(DateTimeOffset? Until, string Reason) : Decision;

    // The caller supplies only a checksum-verified cached package and records lastAttempt
    // before applying. This pure decision neither verifies bytes nor owns an in-flight apply.
    public static Decision Decide(string? appVersion, string? hostVersion, string? verifiedCachedVersion,
        int failures, DateTimeOffset? lastAttempt, DateTimeOffset now)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(failures);
        var app = UpdateVersions.Identity(appVersion);
        var host = UpdateVersions.Identity(hostVersion);
        if (app is null || host is null) { return new Hold(null, "version-unknown"); }
        if (string.Equals(app, host, StringComparison.OrdinalIgnoreCase)) { return new Converged(); }

        // Release discovery selects numeric release tags; unsupported ordering fails closed.
        if (!Version.TryParse(app, out var appRelease) || !Version.TryParse(host, out var hostRelease))
        {
            return new Hold(null, "version-unknown");
        }
        if (verifiedCachedVersion is null
            || !Version.TryParse(UpdateVersions.Identity(verifiedCachedVersion), out var target)
            || target < appRelease || target < hostRelease)
        {
            return new Hold(null, "no-verified-package");
        }

        if (lastAttempt is { } attemptedAt)
        {
            // Before three failures even an unfinished attempt gets a cooldown; at the bound
            // the existing exponential policy applies, capped by UpdateBackoff.
            var delay = UpdateBackoff.After(failures >= FailureBound ? failures : 1);
            var start = attemptedAt.ToUniversalTime();
            var until = start > DateTimeOffset.MaxValue - delay ? DateTimeOffset.MaxValue : start + delay;
            if (now < until)
            {
                return new Hold(until, failures >= FailureBound ? "failure-backoff" : "attempt-backoff");
            }
        }
        else if (failures > 0) { return new Hold(null, "attempt-time-unknown"); }

        return new Heal(verifiedCachedVersion);
    }
}
