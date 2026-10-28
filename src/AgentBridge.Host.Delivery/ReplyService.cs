using System.Collections.Concurrent;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

// session.reply for a host: what hello offers, and one reply typed into one session.
public interface ISessionReplier
{
    // Whether hello offers sessions.reply now; asked per hello, like answers.
    bool Available { get; }

    Task<ReplyResult> ReplyAsync(ReplyRequest request, CancellationToken cancellationToken = default);
}

public sealed class ReplyOptions
{
    public required string CopilotHome { get; init; }

    public bool PlatformSupported { get; init; } = OperatingSystem.IsWindows();

    // How long the transcript is watched for the reply once its keys have gone. Generous,
    // because Copilot records a prompt typed while it works only when it takes it: at the
    // start of its next model round, after the tools running now have finished. One typed
    // while it is idle is recorded at once, so this is only ever waited out in full when
    // the reply did not land.
    public TimeSpan ConfirmTimeout { get; init; } = TimeSpan.FromSeconds(60);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan RememberFor { get; init; } = TimeSpan.FromMinutes(15);

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

// Types a reply into a session's own input, once, and says what happened. The rules are
// the answer's (AnswerService), with the DeliveryGate shared between them:
//   * Never while agent-ha-bridge's daemon is active for this user (G5), checked when the
//     request arrives and again immediately before typing; never into an elevated process.
//   * One delivery per session at a time (busy), answers and replies alike, held until the
//     keys have gone; one request is typed at most once, its outcome remembered.
//   * Never while the session is asking anything: typed words would answer its question,
//     and Enter accept a permission prompt. Checked against the session and, immediately
//     before typing, its transcript.
//   * Delivered only when the transcript records a prompt from the person, after the point
//     read just before typing, that is the reply typed. Anything else is unconfirmed.
//     Never retried.
public sealed class ReplyService : ISessionReplier
{
    readonly ISessionSource source;
    readonly IProcessInspector processes;
    readonly IAgentHaBridgeProbe probe;
    readonly ITargetGuard guard;
    readonly IConsoleInjector injector;
    readonly ReplyOptions options;
    readonly Action<string> log;
    readonly DeliveryGate gate;
    readonly OutcomeMemory<ReplyResult> requests;
    // Prompts already taken as the record of a reply, by transcript and line offset: two
    // replies of the same words are two prompts, and each confirms one.
    readonly ConcurrentDictionary<(string Transcript, long Offset), DateTimeOffset> claimed = new();

    public ReplyService(ISessionSource source, IProcessInspector processes, IAgentHaBridgeProbe probe, ITargetGuard guard,
        IConsoleInjector injector, ReplyOptions options, Action<string>? log = null, DeliveryGate? gate = null)
    {
        this.source = source;
        this.processes = processes;
        this.probe = probe;
        this.guard = guard;
        this.injector = injector;
        this.options = options;
        this.log = log ?? (_ => { });
        this.gate = gate ?? new DeliveryGate();
        requests = new OutcomeMemory<ReplyResult>(options.Time, options.RememberFor);
    }

    public bool Available => options.PlatformSupported && probe.Active() is null;

    public Task<ReplyResult> ReplyAsync(ReplyRequest request, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(request.Request, "D", out var key))
        {
            return Task.FromResult(Refused("request must be a UUID"));
        }
        return requests.Once(key, () => DeliverAsync(request, key), result =>
            log($"reply {key.ToString()[..8]} to {Short(request.Id)}: {result.Outcome.ToString().ToLowerInvariant()}{(result.Reason is null ? "" : $" ({result.Reason})")}"));
    }

    async Task<ReplyResult> DeliverAsync(ReplyRequest request, Guid key)
    {
        Typed typed;
        try
        {
            if (!options.PlatformSupported) { return Refused("replying is not supported on this platform yet; reply at the machine"); }
            if (probe.Active() is { } active) { return Refused(active); }
            if (Replies.Check(request) is { } invalid) { return Refused(invalid); }
            var session = source.Get(request.Id);
            if (session is null) { return Stale("the session has ended"); }
            if (session.Agent != AgentKind.Copilot)
            {
                return Refused($"replying is not supported for {session.Agent.ToString().ToLowerInvariant()} yet; reply at the machine");
            }
            if (!gate.TryClaim(session.Id, key)) { return Busy(); }
            try
            {
                if (session.Question is not null) { return Asking(); }
                var outcome = await CopilotAsync(session, request.Text!);
                if (outcome.Result is { } result) { return result; }
                typed = outcome.Typed!;
            }
            finally { gate.Release(session.Id, key); }
        }
        catch (Exception e)
        {
            // Only reached before anything was typed: CopilotAsync owns its own failures
            // once a key may have gone.
            return Refused($"the host could not send the reply: {e.Message}");
        }

        // The session is free again once the keys have gone: a question it asks next can be
        // answered while this waits for the prompt to be taken, which for a busy session can
        // be a while.
        try { return await ConfirmAsync(typed); }
        catch (Exception e) { return Unconfirmed($"the reply was typed but could not be checked ({e.Message}); check at the machine"); }
    }

    sealed record Typed(string Transcript, long From, string Words);

    sealed record Attempt(ReplyResult? Result, Typed? Typed);

    async Task<Attempt> CopilotAsync(Session session, string text)
    {
        // The session id names a folder; only the agent's own id shape is accepted.
        if (!Guid.TryParseExact(session.Id, "D", out _)) { return Done(Refused("the session's id is not a Copilot session id")); }
        var transcript = Path.Combine(options.CopilotHome, "session-state", session.Id, "events.jsonl");
        if (session.ProcessId is not { } pid) { return Done(Refused("the session's process is not known; reply at the machine")); }
        if (!processes.GetAgentProcesses(AgentKind.Copilot).Any(p => p.Id == pid)) { return Done(Stale("the session's process has ended")); }
        if (guard.Refuse(pid) is { } refused) { return Done(Refused(refused)); }
        var (keys, words) = CopilotReply.Plan(text);

        // One delivery typed at a time across sessions, and the last checks are made once its
        // turn has come, not when it arrived.
        await gate.WaitToTypeAsync();
        long from;
        InjectionResult result;
        try
        {
            if (probe.Active() is { } active) { return Done(Refused(active)); }
            var now = source.Get(session.Id);
            if (now is null) { return Done(Stale("the session has ended")); }
            if (now.Question is not null) { return Done(Asking()); }
            // The transcript, read now, may be ahead of the session source: a question asked a
            // moment ago is there first.
            var before = CopilotPrompts.Before(transcript);
            if (before.Asking) { return Done(Asking()); }
            from = before.ScannedTo;

            try { result = await injector.InjectAsync(pid, keys); }
            catch (Exception e)
            {
                return Done(Unconfirmed($"the injector failed while typing ({e.Message}); check at the machine"));
            }
        }
        finally { gate.DoneTyping(); }

        log($"typed a reply into {Short(session.Id)} (pid {pid}): {keys.Describe()} -> {result.Status} {result.Detail}");
        return result.Status switch
        {
            InjectionStatus.NothingTyped => Done(Refused(result.Detail)),
            InjectionStatus.Interrupted => Done(Unconfirmed($"{result.Detail}; part of the reply may have reached the session, so check at the machine")),
            _ => new Attempt(null, new Typed(transcript, from, words)),
        };

        static Attempt Done(ReplyResult r) => new(r, null);
    }

    // Watches the transcript, from where it stood before the keys went, for the person's
    // prompt that is this reply.
    async Task<ReplyResult> ConfirmAsync(Typed typed)
    {
        var started = options.Time.GetTimestamp();
        var other = false;
        while (true)
        {
            var since = CopilotPrompts.Since(typed.Transcript, typed.From);
            foreach (var prompt in since.Prompts)
            {
                if (!CopilotReply.Matches(typed.Words, prompt.Content)) { other = true; continue; }
                if (Claim(typed.Transcript, prompt.Offset)) { return new ReplyResult(AnswerOutcome.Delivered); }
            }
            if (since.SessionEnded) { return Unconfirmed("the session ended before it recorded the reply; check at the machine"); }
            if (options.Time.GetElapsedTime(started) >= options.ConfirmTimeout)
            {
                return Unconfirmed(other
                    ? "the session recorded a different message, not this reply; check at the machine"
                    : $"the session did not record the reply within {options.ConfirmTimeout.TotalSeconds:0} seconds; check at the machine");
            }
            await Task.Delay(options.PollInterval, options.Time);
        }
    }

    bool Claim(string transcript, long offset)
    {
        var now = options.Time.GetUtcNow();
        foreach (var old in claimed.Where(c => now - c.Value > options.RememberFor).Select(c => c.Key).ToList()) { claimed.TryRemove(old, out _); }
        return claimed.TryAdd((transcript, offset), now);
    }

    static string Short(string id) => id.Length > 8 ? id[..8] : id;

    static ReplyResult Refused(string reason) => new(AnswerOutcome.Refused, reason);
    static ReplyResult Stale(string reason) => new(AnswerOutcome.Stale, reason);
    static ReplyResult Busy() => new(AnswerOutcome.Busy, "another answer or reply to this session is being delivered");
    static ReplyResult Unconfirmed(string reason) => new(AnswerOutcome.Unconfirmed, reason);
    static ReplyResult Asking() => new(AnswerOutcome.Refused, "the session is waiting for an answer to its question; answer that first");
}

// macOS and Linux, until delivery through tmux exists: hello never offers sessions.reply,
// and a reply sent anyway is refused without touching anything.
public sealed class UnsupportedPlatformReplier : ISessionReplier
{
    public bool Available => false;

    public Task<ReplyResult> ReplyAsync(ReplyRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ReplyResult(AnswerOutcome.Refused, "replying is not supported on this platform yet; reply at the machine"));
}
