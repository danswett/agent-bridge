using System.Collections.Concurrent;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

// session.answer for a host: what hello offers, and one answer typed into one session.
public interface ISessionAnswerer
{
    // Whether hello offers sessions.answer now. Asked per hello, so a client sees the
    // current state: agent-ha-bridge starting or stopping changes it.
    bool Available { get; }

    Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken cancellationToken = default);
}

public sealed class AnswerOptions
{
    // Where Copilot's session-state lives; the same root the session source reads.
    public required string CopilotHome { get; init; }

    // Whether this host can deliver at all. Windows only for now; macOS and Linux (tmux)
    // come later.
    public bool PlatformSupported { get; init; } = OperatingSystem.IsWindows();

    // How long the transcript is watched for the answer once the keys have gone.
    public TimeSpan ConfirmTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    // How long a request's outcome is returned again for a resend. The contract says at
    // least 10 minutes.
    public TimeSpan RememberFor { get; init; } = TimeSpan.FromMinutes(15);

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

// Answers a pending question by typing into the session's own prompt, once, and says what
// happened. The rules, all from the design:
//   * Never while agent-ha-bridge's daemon is active for this user (G5), checked when
//     the request arrives and again immediately before typing.
//   * One answer per session at a time (busy), and one request is typed at most once: its
//     outcome is remembered and returned again for a resend.
//   * Immediately before typing, the transcript must still show that very question
//     unanswered (stale otherwise, and nothing is typed).
//   * After typing, delivered only when the transcript records the answer to that tool
//     call and it is the answer typed; anything else is unconfirmed. Never retried.
public sealed class AnswerService : ISessionAnswerer
{
    readonly ISessionSource source;
    readonly IProcessInspector processes;
    readonly IAgentHaBridgeProbe probe;
    readonly ITargetGuard guard;
    readonly IConsoleInjector injector;
    readonly AnswerOptions options;
    readonly Action<string> log;
    readonly Lock gate = new();
    readonly Dictionary<Guid, Remembered> requests = [];
    readonly ConcurrentDictionary<string, Guid> delivering = new(StringComparer.Ordinal);
    readonly SemaphoreSlim typing = new(1, 1);

    sealed class Remembered(Task<AnswerResult> outcome)
    {
        public Task<AnswerResult> Outcome { get; } = outcome;
        public DateTimeOffset? Settled;
    }

    public AnswerService(ISessionSource source, IProcessInspector processes, IAgentHaBridgeProbe probe, ITargetGuard guard,
        IConsoleInjector injector, AnswerOptions options, Action<string>? log = null)
    {
        this.source = source;
        this.processes = processes;
        this.probe = probe;
        this.guard = guard;
        this.injector = injector;
        this.options = options;
        this.log = log ?? (_ => { });
    }

    public bool Available => options.PlatformSupported && probe.Active() is null;

    public Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(request.Request, "D", out var key))
        {
            return Task.FromResult(Refused("request must be a UUID"));
        }
        TaskCompletionSource<AnswerResult> outcome;
        Remembered entry;
        lock (gate)
        {
            Forget();
            if (requests.TryGetValue(key, out var known)) { return known.Outcome; }
            outcome = new TaskCompletionSource<AnswerResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            entry = new Remembered(outcome.Task);
            requests[key] = entry;
        }
        // Not tied to the request's channel: a channel that closes mid-delivery must not cut
        // the delivery short, and a resend on the next channel gets the outcome.
        _ = Task.Run(async () =>
        {
            var result = await DeliverAsync(request, key);
            lock (gate) { entry.Settled = options.Time.GetUtcNow(); }
            log($"answer {key.ToString()[..8]} to {Short(request.Id)}: {result.Outcome.ToString().ToLowerInvariant()}{(result.Reason is null ? "" : $" ({result.Reason})")}");
            outcome.SetResult(result);
        }, CancellationToken.None);
        return outcome.Task;
    }

    void Forget()
    {
        var before = options.Time.GetUtcNow() - options.RememberFor;
        foreach (var (key, entry) in requests.Where(r => r.Value.Settled < before).ToList()) { requests.Remove(key); }
    }

    async Task<AnswerResult> DeliverAsync(AnswerRequest request, Guid key)
    {
        try
        {
            if (!options.PlatformSupported) { return Refused("answering is not supported on this platform yet; answer at the machine"); }
            if (probe.Active() is { } active) { return Refused(active); }
            var session = source.Get(request.Id);
            if (session is null) { return Stale("the session has ended"); }
            if (session.Question is not { } question || question.Id != request.Question) { return Stale("the session is no longer asking that question"); }
            if (session.Agent != AgentKind.Copilot)
            {
                return Refused($"answering is not supported for {session.Agent.ToString().ToLowerInvariant()} yet; answer at the machine");
            }
            if (question.Kind == QuestionKind.Approval) { return Refused("answering Copilot permission prompts is not supported yet; answer at the machine"); }
            if (Answers.Check(question, request) is { } invalid) { return Refused(invalid); }

            if (!delivering.TryAdd(session.Id, key)) { return Busy(); }
            try { return await CopilotAsync(session, question, request); }
            finally { delivering.TryRemove(session.Id, out _); }
        }
        catch (Exception e)
        {
            // Only reached before anything was typed: CopilotAsync owns its own failures
            // once a key may have gone.
            return Refused($"the host could not answer: {e.Message}");
        }
    }

    async Task<AnswerResult> CopilotAsync(Session session, Question question, AnswerRequest request)
    {
        // The session id names a folder; only the agent's own id shape is accepted.
        if (!Guid.TryParseExact(session.Id, "D", out _)) { return Refused("the session's id is not a Copilot session id"); }
        var transcript = Path.Combine(options.CopilotHome, "session-state", session.Id, "events.jsonl");
        if (session.ProcessId is not { } pid) { return Refused("the session's process is not known; answer at the machine"); }
        if (!processes.GetAgentProcesses(AgentKind.Copilot).Any(p => p.Id == pid)) { return Stale("the session's process has ended"); }
        if (guard.Refuse(pid) is { } refused) { return Refused(refused); }

        // One answer typed at a time, across sessions, and the slot is taken before the last
        // checks: an answer that waited behind another must find its question still asked,
        // and agent-ha-bridge still stopped, when its own turn comes, not when it arrived.
        await typing.WaitAsync();
        CopilotPlan plan;
        long from;
        InjectionResult typed;
        try
        {
            // Immediately before typing: agent-ha-bridge again, then the question itself, by
            // its tool call id in the transcript, never by how recent anything looks.
            if (probe.Active() is { } active) { return Refused(active); }
            if (source.Get(session.Id)?.Question?.Id != question.Id) { return Stale("the session is no longer asking that question"); }
            var call = CopilotTranscript.Find(transcript, question.Id);
            if (call.Arguments is not { } arguments) { return Stale("the session's transcript no longer shows that question"); }
            if (call.Answer is not null || call.SessionEnded) { return Stale("the question has already been answered"); }
            if (!call.IsMainAgent) { return Refused("the question was asked by a subagent; answer at the machine"); }

            var (form, unreadable) = CopilotForm.Read(arguments);
            if (form is null) { return Refused(unreadable!); }
            if (!form.Matches(question)) { return Refused("the question's form reads differently here than the client was shown; answer at the machine"); }
            var (planned, problem) = CopilotPlanner.Plan(form, request);
            if (planned is null) { return Refused(problem!); }
            (plan, from) = (planned, call.ScannedTo);

            try { typed = await injector.InjectAsync(pid, plan.Keys); }
            catch (Exception e)
            {
                return Unconfirmed($"the injector failed while typing ({e.Message}); check at the machine");
            }
        }
        finally { typing.Release(); }

        log($"typed into {Short(session.Id)} (pid {pid}): {plan.Keys.Describe()} -> {typed.Status} {typed.Detail}");
        switch (typed.Status)
        {
            case InjectionStatus.NothingTyped: return Refused(typed.Detail);
            case InjectionStatus.Interrupted: return Unconfirmed($"{typed.Detail}; part of the answer may have reached the session, so check at the machine");
        }
        try { return await ConfirmAsync(transcript, question.Id, from, plan); }
        catch (Exception e) { return Unconfirmed($"the answer was typed but could not be checked ({e.Message}); check at the machine"); }
    }

    // Watches the transcript, from where it stood before the keys went, for the answer to
    // this tool call.
    async Task<AnswerResult> ConfirmAsync(string transcript, string toolCallId, long from, CopilotPlan plan)
    {
        var started = options.Time.GetTimestamp();
        TimeSpan? firstSeen = null;
        while (true)
        {
            var elapsed = options.Time.GetElapsedTime(started);
            var state = CopilotTranscript.Since(transcript, toolCallId, from);
            if (state.Answer is { } record)
            {
                // Current builds write the structured answer and the call's completion side by
                // side. The structured answer is the one compared exactly, and the completion
                // says whether the call failed, so a moment is allowed for whichever is still
                // missing; a form of several fields, whose rendering cannot confirm it, waits
                // for its structured answer as long as it takes.
                var grace = elapsed - (firstSeen ??= elapsed) < TimeSpan.FromSeconds(1);
                var wait = elapsed < options.ConfirmTimeout
                    && ((record.HumanContent is null && plan.Text is null && (plan.Fields.Count > 1 || grace))
                        || (!record.Completed && grace));
                if (!wait)
                {
                    var (matched, reason) = CopilotVerifier.Check(plan, record);
                    return matched ? new AnswerResult(AnswerOutcome.Delivered) : Unconfirmed($"{reason}; check at the machine");
                }
            }
            else if (state.SessionEnded) { return Unconfirmed("the session ended before it recorded an answer; check at the machine"); }
            else if (elapsed >= options.ConfirmTimeout)
            {
                return Unconfirmed($"the session did not record an answer within {options.ConfirmTimeout.TotalSeconds:0} seconds; check at the machine");
            }
            await Task.Delay(options.PollInterval, options.Time);
        }
    }

    static string Short(string id) => id.Length > 8 ? id[..8] : id;

    static AnswerResult Refused(string reason) => new(AnswerOutcome.Refused, reason);
    static AnswerResult Stale(string reason) => new(AnswerOutcome.Stale, reason);
    static AnswerResult Busy() => new(AnswerOutcome.Busy, "another answer to this session is being delivered");
    static AnswerResult Unconfirmed(string reason) => new(AnswerOutcome.Unconfirmed, reason);
}

// macOS and Linux, until delivery through tmux exists: hello never offers sessions.answer,
// and an answer sent anyway is refused without touching anything.
public sealed class UnsupportedPlatformAnswerer : ISessionAnswerer
{
    public bool Available => false;

    public Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AnswerResult(AnswerOutcome.Refused, "answering is not supported on this platform yet; answer at the machine"));
}
