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

    public static AppPreferences Failed(AppPreferences p, DateTimeOffset now, out DateTimeOffset retryAt)
    {
        var failures = (p.UpdateFailures ?? 0) + 1;
        retryAt = now + After(failures);
        return p with { UpdateFailures = failures, UpdateRetryAt = retryAt };
    }

    // An update took: what failed before is over.
    public static AppPreferences Cleared(AppPreferences p) => p with { UpdateFailures = null, UpdateRetryAt = null };
}

// An install that could not go ahead because something outside the app holds files in the
// installation, found before the host was stopped (HostManager.StopAndApplyAsync's preflight).
public sealed class UpdateBlockedException(string blockers)
    : InvalidOperationException($"{Prefix}{blockers}; the update is tried again later")
{
    public const string Prefix = "files in the installation are held by ";
}
