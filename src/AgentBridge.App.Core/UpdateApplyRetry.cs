using System.Globalization;

namespace AgentBridge.App.Core;

public enum ApplyRetryKind { Done, Retry, GiveUp }

// A process that held a file in the installation when the apply failed (Restart Manager's answer, as InstallHolder reads it).
public sealed record ApplyHolder(int Pid, string? Path);

// Retry: try the same verified package again After this long (At, on the caller's clock). GiveUp: stop, and say why: Reason, the Step that
// ended it, who held the files and how long it had been trying (#2869: "failure messages name the step, the holder PID/path and the timing").
public sealed record ApplyRetryDecision(ApplyRetryKind Kind, TimeSpan? After = null, DateTimeOffset? At = null, string? Reason = null,
    string? Step = null, ApplyHolder? Holder = null, TimeSpan Elapsed = default)
{
    public string Message
    {
        get
        {
            if (Kind != ApplyRetryKind.GiveUp) { return ""; }
            var time = Elapsed >= TimeSpan.FromMinutes(1) ? $"{(int)Elapsed.TotalMinutes} min" : $"{(int)Elapsed.TotalSeconds} s";
            var held = Holder is null ? "" : string.Create(CultureInfo.InvariantCulture, $"; files held by pid {Holder.Pid}{(string.IsNullOrEmpty(Holder.Path) ? "" : $" ({Holder.Path})")}");
            return $"The update was not applied: {Reason} at {Step ?? "apply"} after {time}{held}";
        }
    }
}

// #2869 E1 (problem 4, the automatic apply path; docs/app-windows.md, Updates, "Completion evidence"): whether the checksum-verified cached-package
// apply is tried again after it ended, and when. Pure: the end (UpdateApplyResult, the observer seam), whether the package still matches its
// checksum, who held the files, the failures so far and the clock are all arguments, so nothing here runs an apply or reads a file. Wiring it into
// the coordinator or the in-place helper is a later slice (E2).
//   * Completed: done.
//   * A package that no longer matches its checksum is never applied again, whatever the end.
//   * Unknown: never retried. The apply may still be running; a second one would be a second authority over the installation.
//   * Failed with a holder seen: retried after UpdateBackoff.After(n), to MaxAttempts; then given up with the step, holder and elapsed time.
//   * Failed with no holder seen: not retried. Nothing says a second try would end differently, and the backoff exists to stop 394-a-day loops (#1092).
public static class UpdateApplyRetry
{
    // The attempts (the first and its retries) before giving up. A held file is usually a Windows service or a scan that lets go in minutes; with
    // UpdateBackoff's 10, 20 and 40 minutes this tries for about 70.
    public const int MaxAttempts = 4;

    public static ApplyRetryDecision Decide(UpdateApplyResult result, bool checksumVerified, ApplyHolder? holder, int failedBefore, DateTimeOffset firstAttemptAt, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.End == UpdateApplyEnd.Completed) { return new(ApplyRetryKind.Done); }
        var elapsed = now > firstAttemptAt ? now - firstAttemptAt : TimeSpan.Zero;
        ApplyRetryDecision Stop(string reason) => new(ApplyRetryKind.GiveUp, Reason: reason, Step: result.Step, Holder: holder, Elapsed: elapsed);

        if (!checksumVerified) { return Stop("checksum"); }
        if (result.End != UpdateApplyEnd.Failed) { return Stop($"unknown-end:{result.Code ?? "unknown"}"); }
        if (holder is null) { return Stop("no-holder-seen"); }
        var failures = Math.Max(0, failedBefore) + 1;
        if (failures >= MaxAttempts) { return Stop("retries-exhausted"); }
        var after = UpdateBackoff.After(failures);
        return new(ApplyRetryKind.Retry, after, now + after, Step: result.Step, Holder: holder, Elapsed: elapsed);
    }
}
