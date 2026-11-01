using System.Buffers.Text;
using System.Security.Cryptography;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

// session.stop for a host: what hello offers, and one session ended.
public interface ISessionStopper
{
    // Whether hello offers sessions.stop now; asked per hello, like answers.
    bool Available { get; }

    Task<StopResult> StopAsync(StopRequest request, CancellationToken cancellationToken = default);
}

// What opening a session's process came to: held, gone, or refused with the reason.
public sealed record HeldOpen(IHeldProcess? Process, bool Gone, string? Refusal)
{
    public static HeldOpen Of(IHeldProcess process) => new(process, false, null);
    public static HeldOpen Exited() => new(null, true, null);
    public static HeldOpen Refused(string reason) => new(null, false, reason);
}

// A session's process, held so that its id cannot pass to another process while it is.
public interface IHeldProcess : IDisposable
{
    int Id { get; }

    // True once the process has exited; false if it is still running after timeout.
    Task<bool> WaitForExitAsync(TimeSpan timeout);

    // Null once termination is asked for; otherwise why it could not be.
    string? Terminate();
}

// Opens the process with this id only if it is still the one that started at startedAt, runs
// as this user and is not elevated. The real one is WindowsProcessControl; tests use a fake
// that only ever touches processes they started.
public interface IProcessControl
{
    HeldOpen Open(int processId, DateTimeOffset startedAt);
}

public sealed class StopOptions
{
    // Where Copilot's session-state lives, to check its transcript for a question before
    // typing /exit.
    public required string CopilotHome { get; init; }

    public bool PlatformSupported { get; init; } = OperatingSystem.IsWindows();

    // How long a first press waits for the second. agent-ha-bridge: StopConfirmSeconds.
    public TimeSpan ConfirmWindow { get; init; } = TimeSpan.FromSeconds(Stops.ConfirmSeconds);

    // How long the agent is given to exit on /exit before it is terminated.
    // agent-ha-bridge: Stop-BridgeCopilotSession's GraceSeconds.
    public TimeSpan Grace { get; init; } = TimeSpan.FromSeconds(12);

    // How long a terminated process is given to be gone.
    public TimeSpan TerminateWait { get; init; } = TimeSpan.FromSeconds(5);

    public TimeSpan RememberFor { get; init; } = TimeSpan.FromMinutes(15);

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

// Ends a session the way agent-ha-bridge's End session does (hooks/daemon-launch.ps1,
// Invoke-PendingStops; hooks/session-launch.ps1, Stop-BridgeCopilotSession):
//   * A session that is not idle (working, waiting on you, or idle with background work it
//     started) takes two presses. The first only arms, and says so; the second, within
//     ConfirmWindow and against the same process, ends it. Enforced here, not by a client,
//     because every client and every device can send session.stop. An idle session ends on
//     one press.
//   * Graceful first: /exit typed into the agent's console, as a reply would be, so the CLI
//     writes its transcript and releases its lock, and the session stays resumable. If it is
//     still running after Grace, the process alone is terminated, never its tree.
//   * Never the wrong process: the process discovery bound the session to, by id and start
//     time, must be the one running under that id now, both when arming and when
//     confirming, and is held open by handle from then until it has gone, so its id cannot
//     pass to another process.
//   * Never while agent-ha-bridge is active for this user (G5), checked when the request
//     arrives, before typing and before terminating; never an elevated or another user's
//     process. One delivery per session at a time: ending holds the session's DeliveryGate
//     claim throughout, so no answer or reply is typed into a session being ended.
public sealed class StopService : ISessionStopper
{
    static readonly KeyPlan ExitKeys = new([new KeySend("/exit", true, CopilotPlanner.ReplyEnterDelayMs, 0)]);

    readonly ISessionSource source;
    readonly IProcessInspector processes;
    readonly IAgentHaBridgeProbe probe;
    readonly IConsoleInjector injector;
    readonly IProcessControl control;
    readonly StopOptions options;
    readonly Action<string> log;
    readonly DeliveryGate gate;
    readonly OutcomeMemory<StopResult> requests;
    readonly Lock armsGate = new();
    // First presses waiting for their second, by session. Several can be waiting at once (two
    // devices pressed), and any one of them confirms.
    readonly Dictionary<string, List<Arm>> arms = new(StringComparer.Ordinal);

    sealed record Arm(string Token, int ProcessId, DateTimeOffset StartedAt, DateTimeOffset At);

    public StopService(ISessionSource source, IProcessInspector processes, IAgentHaBridgeProbe probe, IConsoleInjector injector,
        IProcessControl control, StopOptions options, Action<string>? log = null, DeliveryGate? gate = null)
    {
        this.source = source;
        this.processes = processes;
        this.probe = probe;
        this.injector = injector;
        this.control = control;
        this.options = options;
        this.log = log ?? (_ => { });
        this.gate = gate ?? new DeliveryGate();
        requests = new OutcomeMemory<StopResult>(options.Time, options.RememberFor);
    }

    public bool Available => options.PlatformSupported && probe.Active() is null;

    public Task<StopResult> StopAsync(StopRequest request, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(request.Request, "D", out var key)) { return Task.FromResult(Refused("request must be a UUID")); }
        return requests.Once(key, () => EndAsync(request, key), result =>
            log($"end {key.ToString()[..8]} for {Short(request.Id)}: {result.Outcome.ToString().ToLowerInvariant()}{(result.Forced == true ? " (terminated)" : "")}{(result.Reason is null ? "" : $" ({result.Reason})")}"));
    }

    async Task<StopResult> EndAsync(StopRequest request, Guid key)
    {
        Session session;
        int processId;
        DateTimeOffset startedAt;
        var confirmed = false;
        try
        {
            if (!options.PlatformSupported) { return Refused("ending sessions is not supported on this platform yet; end it at the machine"); }
            if (probe.Active() is { } active) { return Refused(active); }
            if (Stops.Check(request) is { } invalid) { return Refused(invalid); }
            if (source.Get(request.Id) is not { } found) { return Stale("the session has ended"); }
            session = found;
            // The process discovery bound the session to, by id and start time, and the one
            // running under that id now must be it. A session whose process has exited stays
            // listed until the next discovery pass, and its id can pass to another agent
            // process meanwhile, which must never be ended in its place.
            if (source.ProcessOf(session.Id) is not { } bound) { return Refused("the session's process is not known; end it at the machine"); }
            processId = bound.Id;
            // The start time is the process's identity: without it, a process that later takes
            // the id could not be told from this one.
            if (bound.StartedAt is not { } started) { return Refused("the host cannot read when the session's process started, so it cannot tell it from another; end it at the machine"); }
            startedAt = started;
            if (processes.GetAgentProcesses(session.Agent).FirstOrDefault(p => p.Id == processId) is not { StartedAt: { } now } || !Same(now, startedAt))
            {
                return Stale("the session's process has ended");
            }

            // Refused on the first press, not after the person has confirmed.
            var check = control.Open(processId, startedAt);
            check.Process?.Dispose();
            if (check.Refusal is { } refusal) { return Refused(refusal); }
            if (check.Gone) { return Stale("the session's process has ended"); }
            // Another press is ending it already.
            if (session.Status == SessionStatus.Ending) { return Busy(); }

            if (Doing(session) is { } doing)
            {
                if (!Confirms(session.Id, request.Confirm, processId, startedAt, out var why)) { return AskAgain(session.Id, processId, startedAt, doing, why); }
                confirmed = true;
            }
            Disarm(session.Id);
        }
        catch (Exception e)
        {
            return Refused($"the host could not end the session: {e.Message}");
        }

        if (!gate.TryClaim(session.Id, key)) { return Busy(); }
        StopResult result;
        try
        {
            try { result = await EndProcessAsync(session, processId, startedAt, confirmed); }
            catch (Exception e) { result = Failed($"the host could not finish ending the session ({e.Message}); check at the machine"); }
        }
        finally { gate.Release(session.Id, key); }
        // Removed as soon as it has gone, rather than at the next discovery pass; shown as it
        // really is again if it is still there.
        if (result.Outcome == StopOutcome.Ended)
        {
            try { await source.RefreshAsync(); }
            catch (Exception) { }
        }
        await SetEndingAsync(session.Id, false);
        return result;
    }

    // A first press, or one that does not confirm: armed, and answered with the token a second
    // press sends.
    StopResult AskAgain(string sessionId, int pid, DateTimeOffset startedAt, string doing, string? why)
    {
        var token = ArmFor(sessionId, pid, startedAt);
        var seconds = (int)Math.Round(options.ConfirmWindow.TotalSeconds);
        log($"end requested for {Short(sessionId)} (pid {pid}) while {doing}; {why ?? "waiting for a second press"}");
        return new StopResult(StopOutcome.Confirm, $"{(why is null ? "" : why + "; ")}{doing}; the confirmation lapses in {seconds} s")
        {
            Confirm = token,
            ConfirmSeconds = seconds,
        };
    }

    // confirmed: whether this press was the second press of a session that was not idle. One
    // that was idle when it arrived is checked again once the typing turn is taken, which can
    // be a while if another session is being typed into: a session that has started working
    // or asking meanwhile needs a second press like any other.
    async Task<StopResult> EndProcessAsync(Session session, int pid, DateTimeOffset startedAt, bool confirmed)
    {
        var opened = control.Open(pid, startedAt);
        if (opened.Refusal is { } refusal) { return Refused(refusal); }
        // Gone between the press and now: what the press asked for has happened.
        if (opened.Process is not { } held) { return Ended(false); }
        using (held)
        {
            var typed = false;
            await gate.WaitToTypeAsync();
            try
            {
                if (probe.Active() is { } active) { return Refused(active); }
                // Read before the session is shown as ending, which would hide its own status.
                if (source.Get(session.Id) is not { } now) { return Stale("the session has ended"); }
                if (!confirmed && Doing(now) is { } doing) { return AskAgain(session.Id, pid, startedAt, doing, "the session is no longer idle"); }
                await SetEndingAsync(session.Id, true);
                // Typed words would answer a question, and Enter accept it: a session that is
                // asking anything is terminated without them.
                var asking = now.Question is not null || CopilotAsking(session);
                if (!asking)
                {
                    InjectionResult typing;
                    try { typing = await injector.InjectAsync(pid, ExitKeys); }
                    catch (Exception e) { typing = new InjectionResult(InjectionStatus.Interrupted, e.Message); }
                    typed = typing.Status != InjectionStatus.NothingTyped;
                    log($"typed /exit into {Short(session.Id)} (pid {pid}) -> {typing.Status} {typing.Detail}");
                }
                else
                {
                    log($"{Short(session.Id)} (pid {pid}) is asking a question, so /exit is not typed");
                }
            }
            finally { gate.DoneTyping(); }

            if (typed && await held.WaitForExitAsync(options.Grace))
            {
                log($"ended {Short(session.Id)}: exited cleanly (pid {pid})");
                return Ended(false);
            }
            if (probe.Active() is { } started)
            {
                return Failed($"{started}, so the host stopped short of terminating the session; check at the machine");
            }
            var refused = held.Terminate();
            if (await held.WaitForExitAsync(options.TerminateWait))
            {
                log($"ended {Short(session.Id)}: {(typed ? $"did not exit within {options.Grace.TotalSeconds:0} s; " : "")}terminated pid {pid}");
                return Ended(true);
            }
            return Failed(refused ?? "the session's process was terminated but has not exited; check at the machine");
        }
    }

    // A Copilot question, a subagent's included, written since the session source last read
    // the transcript. One that cannot be read cannot say nothing is asking.
    bool CopilotAsking(Session session)
    {
        if (session.Agent != AgentKind.Copilot || !Guid.TryParseExact(session.Id, "D", out _)) { return false; }
        var state = CopilotPrompts.Before(Path.Combine(options.CopilotHome, "session-state", session.Id, "events.jsonl"));
        return !state.Readable || state.Asking;
    }

    async Task SetEndingAsync(string id, bool ending)
    {
        try { await source.SetEndingAsync(id, ending); }
        catch (Exception) { }
    }

    // What a second press would interrupt, as agent-ha-bridge's Get-DaemonStopConfirmHint
    // says it; null for an idle session, which ends on one press.
    static string? Doing(Session s) => s.Status switch
    {
        SessionStatus.Working => "this session is still working",
        SessionStatus.Waiting => "this session is waiting on you",
        _ when s.Activity.Background is { Agents: > 0 } => "this session is waiting for background agents it started",
        _ when s.Activity.Background is not null => "this session is waiting for background commands it started",
        _ => null,
    };

    // Whether this press confirms a first press of the same session against the same process,
    // within the window. Only positive proof confirms; why not, when a token was sent.
    bool Confirms(string sessionId, string? token, int pid, DateTimeOffset startedAt, out string? why)
    {
        why = null;
        if (token is null) { return false; }
        lock (armsGate)
        {
            var arm = arms.TryGetValue(sessionId, out var list) ? list.FirstOrDefault(a => string.Equals(a.Token, token, StringComparison.Ordinal)) : null;
            if (arm is null || options.Time.GetUtcNow() - arm.At > options.ConfirmWindow)
            {
                why = "the first press has lapsed";
                return false;
            }
            if (arm.ProcessId != pid || !Same(arm.StartedAt, startedAt))
            {
                why = "the session's process has changed since the first press";
                return false;
            }
            arms.Remove(sessionId);
            return true;
        }
    }

    string ArmFor(string sessionId, int pid, DateTimeOffset startedAt)
    {
        var token = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        var now = options.Time.GetUtcNow();
        lock (armsGate)
        {
            foreach (var id in arms.Keys.ToList())
            {
                arms[id].RemoveAll(a => now - a.At > options.ConfirmWindow);
                if (arms[id].Count == 0) { arms.Remove(id); }
            }
            if (!arms.TryGetValue(sessionId, out var list)) { arms[sessionId] = list = []; }
            list.Add(new Arm(token, pid, startedAt, now));
        }
        return token;
    }

    void Disarm(string sessionId)
    {
        lock (armsGate) { arms.Remove(sessionId); }
    }

    // Two readings of one creation time, which may differ by rounding but never by more.
    static bool Same(DateTimeOffset a, DateTimeOffset b) => Math.Abs((a - b).Ticks) <= TimeSpan.TicksPerMillisecond;

    static string Short(string id) => id.Length > 8 ? id[..8] : id;

    static StopResult Ended(bool forced) => new(StopOutcome.Ended) { Forced = forced ? true : null };
    static StopResult Refused(string reason) => new(StopOutcome.Refused, reason);
    static StopResult Stale(string reason) => new(StopOutcome.Stale, reason);
    static StopResult Failed(string reason) => new(StopOutcome.Failed, reason);
    static StopResult Busy() => new(StopOutcome.Busy, "an answer or reply to this session is being delivered, or it is already being ended");
}

// macOS and Linux, until delivery through tmux exists: hello never offers sessions.stop, and a
// stop sent anyway is refused without touching anything.
public sealed class UnsupportedPlatformStopper : ISessionStopper
{
    public bool Available => false;

    public Task<StopResult> StopAsync(StopRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new StopResult(StopOutcome.Refused, "ending sessions is not supported on this platform yet; end it at the machine"));
}
