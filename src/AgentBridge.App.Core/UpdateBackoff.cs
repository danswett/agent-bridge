namespace AgentBridge.App.Core;

// How long an install that failed is left alone before the app tries it again by itself (#1092).
// On 2026-11-06 a Windows service held a file in the installation, Update.exe failed to move it
// and started the old app again, whose in-memory "failed lately" was gone: it installed again
// two idle minutes later, stopping the host each time, 394 times in a day. So the count and the
// next attempt are kept in the preferences, across the restart that a failed update causes.
public static class UpdateBackoff
{
    public static readonly TimeSpan First = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Cap = TimeSpan.FromHours(1);

    // After the nth failure in a row: First, doubling each time, to Cap.
    public static TimeSpan After(int failures) =>
        failures <= 1 ? First : TimeSpan.FromTicks(Math.Min(Cap.Ticks, First.Ticks << Math.Min(failures - 1, 8)));

    // Failures count only against the same release: a different one failing starts again at First.
    public static AppPreferences Failed(AppPreferences p, DateTimeOffset now, string? release, out DateTimeOffset retryAt)
    {
        var failures = (p.UpdateFailedRelease == release ? p.UpdateFailures ?? 0 : 0) + 1;
        retryAt = now + After(failures);
        return p with { UpdateFailures = failures, UpdateRetryAt = retryAt, UpdateFailedRelease = release };
    }

    // An update took: what failed before is over.
    public static AppPreferences Cleared(AppPreferences p) => p with { UpdateFailures = null, UpdateRetryAt = null, UpdateFailedRelease = null };

    // An install of this release is starting: failures of another release no longer count.
    public static AppPreferences Attempting(AppPreferences p, string? release) =>
        p.UpdateFailedRelease == release || p.UpdateFailedRelease is null && p.UpdateFailures is null
            ? p with { UpdateFailedRelease = release }
            : p with { UpdateFailures = null, UpdateRetryAt = null, UpdateFailedRelease = release };

    // When the pending release may be tried again by itself, or null if nothing holds it: only the
    // release that failed waits, and a time further off than Cap (the clock moved back) is Cap on.
    public static DateTimeOffset? RetryAt(AppPreferences p, DateTimeOffset now, string? pending)
    {
        if (p.UpdateRetryAt is not { } retry || now >= retry) { return null; }
        if (p.UpdateFailedRelease is not null && p.UpdateFailedRelease != pending) { return null; }
        return retry > now + Cap ? now + Cap : retry;
    }
}

// Whether a process the Restart Manager lists as holding a file in the installation is something
// outside it that blocks an update. One whose path can't be read (an elevated or other user's
// process, whose image name MainModule can't see) is not counted: our own host or helpers could be
// among them, and a false blocker fails every preflight until the backoff wedges updates for good.
public static class InstallHolder
{
    public static bool Blocks(int pid, int ownPid, string? path, string installRoot) =>
        pid != ownPid && path is { Length: > 0 } && !path.StartsWith(installRoot, StringComparison.OrdinalIgnoreCase);
}

// An install that could not go ahead because something outside the app holds files in the
// installation, found before the host was stopped (HostManager.StopAndApplyAsync's preflight).
public sealed class UpdateBlockedException(string blockers)
    : InvalidOperationException($"{Prefix}{blockers}; the update is tried again later")
{
    public const string Prefix = "files in the installation are held by ";
}
