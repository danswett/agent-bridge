using System.Diagnostics;
using System.Reflection;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Diagnostics;

// An exception as the journal may keep it: its type and the frames it passed through, each as
// declaring type and method name, taken from the runtime's metadata. Never its message, its
// ToString, its stack trace text, its data or its inner exceptions: those carry whatever the
// failing code was holding (a prompt, a path, an account, a token). Frames are read without file
// information, so no source path or line is ever looked up. Names come only from the journal's
// closed sets (JournalIdentifiers), by the type and method themselves: anything else is
// <unrecognized> or <external>, never its own name.
public static class ExceptionShape
{
    static ExceptionShape() => TrustProduct();

    // This assembly and the hook contract it reads: the product code whose frames the host's
    // journal names. Public for a process reading the host's records (the Windows app), which
    // lists the same names by the same assemblies; the host's own executable is in neither.
    public static void TrustProduct()
    {
        JournalIdentifiers.Trust(typeof(ExceptionShape).Assembly);
        JournalIdentifiers.Trust(typeof(AgentBridge.Hooks.SpoolContract).Assembly);
    }

    public static JournalException Project(Exception e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var type = JournalIdentifiers.TypeName(e.GetType()) ?? JournalContract.Unrecognized;
        var frames = new List<JournalFrame>();
        foreach (var frame in new StackTrace(e, fNeedFileInfo: false).GetFrames())
        {
            var kept = JournalIdentifiers.FrameOf(frame.GetMethod()) ?? new JournalFrame(JournalContract.External, JournalContract.External);
            if (kept.Type == JournalContract.External && frames.Count > 0 && frames[^1].Type == JournalContract.External) { continue; }
            frames.Add(kept);
            // One past the bound, so the journal marks the frames truncated.
            if (frames.Count > JournalContract.MaxFrames) { break; }
        }
        return new JournalException(type, frames);
    }
}

// Records for the journal's producers, from typed values only.
public static class JournalEvents
{
    public static JournalEntry Failure(JournalKind kind, Exception e, JournalOutcome outcome = JournalOutcome.Failed, string? version = null) =>
        JournalContract.Draft(kind, outcome, version: version, exception: ExceptionShape.Project(e));

    // An unhandled exception, or an unhandled throw of something that is not an Exception.
    public static JournalEntry Crash(object? thrown, string? version) =>
        thrown is Exception e
            ? Failure(JournalKind.UnhandledException, e, version: version)
            : JournalContract.Draft(JournalKind.UnhandledException, JournalOutcome.Failed, version: version,
                exception: new JournalException(JournalContract.Unrecognized, []));

    // The relay connection (#737 S2): an outcome, a close code and a duration, never the relay's
    // address, a token or an exception's message.
    public static JournalEntry RelayConnected(TimeSpan connectTook, string? version = null) =>
        JournalContract.Draft(JournalKind.RelayConnected, JournalOutcome.Ok, version: version, durationMs: Millis(connectTook));

    // A refusal by the relay is its close code alone. Any other failure is its type and frames
    // (ExceptionShape.Project), or only Cancelled when the attempt was cancelled.
    public static JournalEntry RelayConnectFailed(Exception e, int? refusedCode, TimeSpan took, string? version = null)
    {
        ArgumentNullException.ThrowIfNull(e);
        var duration = Millis(took);
        if (refusedCode is not null) { return JournalContract.Draft(JournalKind.RelayConnectFailed, JournalOutcome.Refused, refusedCode, version, duration); }
        if (e is OperationCanceledException) { return JournalContract.Draft(JournalKind.RelayConnectFailed, JournalOutcome.Cancelled, version: version, durationMs: duration); }
        return JournalContract.Draft(JournalKind.RelayConnectFailed, JournalOutcome.Failed, version: version, durationMs: duration, exception: ExceptionShape.Project(e));
    }

    // An established connection ending: a normal close is Ok, a close code the relay chose
    // (4000s) Refused, and anything else (none, or the transport's) Unavailable.
    public static JournalEntry RelayClosed(int? closeCode, TimeSpan connectedFor, string? version = null) =>
        JournalContract.Draft(JournalKind.RelayClosed,
            closeCode switch { 1000 => JournalOutcome.Ok, >= 4000 and <= 4999 => JournalOutcome.Refused, _ => JournalOutcome.Unavailable },
            closeCode, version, Millis(connectedFor));

    static long Millis(TimeSpan t) => (long)t.TotalMilliseconds;

    // The host is serving. Only the release, never a machine name or path.
    public static JournalEntry HostStarted(string? version) =>
        JournalContract.Draft(JournalKind.HostStarted, JournalOutcome.Ok, version: version);

    // The serve loop ended: normally (no failure), by a cancellation nobody caught, or by an
    // exception, kept as its type and frames only.
    public static JournalEntry HostStopped(Exception? failure, string? version) =>
        failure is null
            ? JournalContract.Draft(JournalKind.HostStopped, JournalOutcome.Ok, version: version)
            : Failure(JournalKind.HostStopped, failure, failure is OperationCanceledException ? JournalOutcome.Cancelled : JournalOutcome.Failed, version);

    // A finished launch or resume (#737 S4): its outcome, and as the code the agent's place in
    // the launcher list (1 is the first), none when the request named no known agent. Never the
    // workspace, prompt, arguments or the outcome's free-text reason.
    public static JournalEntry LaunchRequested(LaunchOutcome outcome, int? agentIndex)
    {
        return JournalContract.Draft(JournalKind.LaunchRequested, outcome switch
        {
            LaunchOutcome.Started => JournalOutcome.Ok,
            LaunchOutcome.Unconfirmed => JournalOutcome.TimedOut,
            LaunchOutcome.Busy => JournalOutcome.Unavailable,
            LaunchOutcome.Refused => JournalOutcome.Refused,
            _ => JournalOutcome.Failed,
        }, agentIndex);
    }

    // An assembly's release, as its informational version without build metadata; null when it
    // has none the journal can keep.
    public static string? Version(Assembly assembly)
    {
        var v = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0];
        return JournalContract.IsVersion(v) ? v : null;
    }
}
