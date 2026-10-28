using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Discovery;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Delivery.Claude;
using AgentBridge.Host.Delivery.Codex;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

// session.answer for a host: what hello offers, and one answer typed into one session.
public interface ISessionAnswerer
{
    // Whether hello offers sessions.answer now. Asked per hello, so a client sees the
    // current state: agent-ha-bridge starting or stopping changes it.
    bool Available { get; }

    // Whether hello offers sessions.approve: Copilot's permission prompts are approved or
    // denied from other devices here (permissions.remoteApproval on).
    bool ApprovesPermissions => false;

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

    // How long Claude is given to record an answer before its prompt is taken to be on the
    // review screen that needs one more Enter (agent-ha-bridge's Complete-DaemonClaudeAnswer).
    public TimeSpan ClaudeReviewAfter { get; init; } = TimeSpan.FromMilliseconds(1500);

    // The host's own record of prompts already typed into (AttemptLedger), in its profile
    // folder; null keeps it in memory only.
    public string? AttemptLedgerPath { get; init; }

    // The host's permissions.remoteApproval: whether Copilot's permission prompts may be
    // approved or denied from another device. On unless the person turns it off.
    public bool CopilotRemoteApproval { get; init; } = true;

    public TimeProvider Time { get; init; } = TimeProvider.System;
}

// Answers a pending question by typing into the session's own prompt, once, and says what
// happened. The rules, all from the design and agent-ha-bridge:
//   * Never while agent-ha-bridge's daemon is active for this user (G5), checked when
//     the request arrives and again immediately before typing; never into an elevated
//     process.
//   * One answer or reply per session at a time (busy), shared with replies through the
//     DeliveryGate, and one request is typed at most once: its outcome is remembered and
//     returned again for a resend.
//   * One prompt is typed into at most once, whatever the request: the claim is written to
//     the AttemptLedger before the first key, and kept unless nothing was typed.
//   * Immediately before typing, the agent's own record must still show that very prompt
//     unanswered, by its id (stale otherwise, and nothing is typed); a record that cannot
//     be read types nothing.
//   * After typing, delivered only when the transcript records the answer to that call and
//     it is the answer typed; anything else is unconfirmed. Never retried.
// Which prompts each agent can have answered is in docs/host-answers.md.
public sealed class AnswerService : ISessionAnswerer
{
    readonly ISessionSource source;
    readonly IProcessInspector processes;
    readonly IAgentHaBridgeProbe probe;
    readonly ITargetGuard guard;
    readonly IConsoleInjector injector;
    readonly AnswerOptions options;
    readonly Action<string> log;
    readonly DeliveryGate gate;
    readonly OutcomeMemory<AnswerResult> requests;
    readonly AttemptLedger ledger;

    // gate: the one the host's ReplyService shares, so an answer and a reply never type into
    // one session at once; a service of its own when none is given (tests).
    public AnswerService(ISessionSource source, IProcessInspector processes, IAgentHaBridgeProbe probe, ITargetGuard guard,
        IConsoleInjector injector, AnswerOptions options, Action<string>? log = null, DeliveryGate? gate = null)
    {
        this.source = source;
        this.processes = processes;
        this.probe = probe;
        this.guard = guard;
        this.injector = injector;
        this.options = options;
        this.log = log ?? (_ => { });
        this.gate = gate ?? new DeliveryGate();
        requests = new OutcomeMemory<AnswerResult>(options.Time, options.RememberFor);
        ledger = new AttemptLedger(options.AttemptLedgerPath, options.Time);
    }

    public bool Available => options.PlatformSupported && probe.Active() is null;

    public bool ApprovesPermissions => Available && options.CopilotRemoteApproval;

    public Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(request.Request, "D", out var key))
        {
            return Task.FromResult(Refused("request must be a UUID"));
        }
        return requests.Once(key, () => DeliverAsync(request, key), result =>
            log($"answer {key.ToString()[..8]} to {Short(request.Id)}: {result.Outcome.ToString().ToLowerInvariant()}{(result.Reason is null ? "" : $" ({result.Reason})")}"));
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
            var permission = session.Agent == AgentKind.Copilot && question.Kind == QuestionKind.Approval;
            if (permission && !options.CopilotRemoteApproval)
            {
                return Refused("approving Copilot permission prompts from another device is turned off on this machine (permissions.remoteApproval); answer at the machine");
            }
            if (question.AnswerAtMachine == true) { return Refused(AtMachine(session.Agent, question)); }
            if (session.Agent is not (AgentKind.Copilot or AgentKind.Claude or AgentKind.Codex))
            {
                return Refused($"answering is not supported for {session.Agent.ToString().ToLowerInvariant()} yet; answer at the machine");
            }
            if (Answers.Check(question, request) is { } invalid) { return Refused(invalid); }

            if (!gate.TryClaim(session.Id, key)) { return Busy(); }
            try
            {
                return session.Agent switch
                {
                    AgentKind.Claude => await ClaudeAsync(session, question, request),
                    AgentKind.Codex => await CodexAsync(session, question, request),
                    _ when permission => await CopilotPermissionAsync(session, question, request),
                    _ => await CopilotAsync(session, question, request),
                };
            }
            finally { gate.Release(session.Id, key); }
        }
        catch (Exception e)
        {
            // Only reached before anything was typed: each agent's path owns its own
            // failures once a key may have gone.
            return Refused($"the host could not answer: {e.Message}");
        }
    }

    static string AtMachine(AgentKind agent, Question question) => question.Kind == QuestionKind.Approval
        ? $"{Name(agent)} permission prompts can only be answered at the machine"
        : $"this {Name(agent)} prompt can only be answered at the machine";

    async Task<AnswerResult> CopilotAsync(Session session, Question question, AnswerRequest request)
    {
        // The session id names a folder; only the agent's own id shape is accepted.
        if (!Guid.TryParseExact(session.Id, "D", out _)) { return Refused("the session's id is not a Copilot session id"); }
        var transcript = Path.Combine(options.CopilotHome, "session-state", session.Id, "events.jsonl");
        if (Target(session, AgentKind.Copilot) is { } unfit) { return unfit; }
        var pid = session.ProcessId!.Value;

        // One answer typed at a time, across sessions, and the slot is taken before the last
        // checks: an answer that waited behind another must find its question still asked,
        // and agent-ha-bridge still stopped, when its own turn comes, not when it arrived.
        await gate.WaitToTypeAsync();
        CopilotPlan plan;
        long from;
        InjectionResult typed;
        var attempt = AttemptLedger.Key("copilot", session.Id, question.Id);
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

            if (ledger.Claim(attempt) is { } claimed) { return Refused(claimed); }
            typed = await TypeAsync(pid, plan.Keys);
        }
        finally { gate.DoneTyping(); }

        log($"typed into {Short(session.Id)} (pid {pid}): {plan.Keys.Describe()} -> {typed.Status} {typed.Detail}");
        if (Settle(typed, attempt) is { } settled) { return settled; }
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
                // side. A form is confirmed only by its structured answer, so it waits for that
                // up to the timeout; and the completion says whether the call failed, so a
                // moment is allowed for it to follow.
                var grace = elapsed - (firstSeen ??= elapsed) < TimeSpan.FromSeconds(1);
                var wait = elapsed < options.ConfirmTimeout
                    && ((record.HumanContent is null && plan.Text is null) || (!record.Completed && grace));
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

    // A Copilot permission prompt: approve once or deny, never anything that grants more. This
    // goes beyond agent-ha-bridge, which only ever mirrored these prompts. The question id
    // names the request and what the client was shown (CopilotPermissionPrompt), so:
    //   * immediately before typing, the transcript must show that request still waiting,
    //     reading exactly as shown, and nothing else waiting that the screen could be showing
    //     instead (stale or refused otherwise);
    //   * the option is found on the screen by its label, and the key goes only if that row
    //     still reads so when it is typed (the worker checks it in the same attach);
    //   * delivered only when the transcript records that request approved, or denied with no
    //     message; anything else is unconfirmed. Never retried.
    async Task<AnswerResult> CopilotPermissionAsync(Session session, Question question, AnswerRequest request)
    {
        if (!Guid.TryParseExact(session.Id, "D", out _)) { return Refused("the session's id is not a Copilot session id"); }
        if (CopilotPermissionPrompt.ParseQuestionId(question.Id) is not { } parsed) { return Refused("this Copilot permission prompt can only be answered at the machine"); }
        var (requestId, digest) = parsed;
        if (Decision(request.Fields) is not { } approve) { return Refused("an approval is answered with decision approve or deny"); }
        var transcript = Path.Combine(options.CopilotHome, "session-state", session.Id, "events.jsonl");
        if (Target(session, AgentKind.Copilot) is { } unfit) { return unfit; }
        var pid = session.ProcessId!.Value;
        var decision = approve ? "approve" : "deny";

        await gate.WaitToTypeAsync();
        KeyPlan keys;
        long from;
        InjectionResult typed;
        var attempt = AttemptLedger.Key("copilot", session.Id, question.Id);
        try
        {
            if (probe.Active() is { } active) { return Refused(active); }
            if (source.Get(session.Id)?.Question?.Id != question.Id) { return Stale("the session is no longer asking for that permission"); }
            var state = CopilotPermissionTranscript.Before(transcript, requestId);
            if (!state.Readable) { return Refused("the session's transcript could not be read, so nothing is typed; answer at the machine"); }
            if (state.Request is not { } pending) { return Stale("Copilot is no longer asking for that permission"); }
            if (!string.Equals(pending.Digest, digest, StringComparison.Ordinal)) { return Stale("Copilot's permission prompt reads differently from what was shown"); }
            if (pending.AtMachine is { } why) { return Refused($"{why} can only be answered at the machine"); }
            if (state.PendingPermissions + state.PendingAsks > 1)
            {
                return Refused("Copilot has more than one prompt waiting, and the screen shows only one of them; answer at the machine");
            }

            ConsoleScreen screen;
            try { screen = await injector.ReadScreenAsync(pid); }
            catch (Exception e) { return Refused($"the host could not read the session's screen ({e.Message}); answer at the machine"); }
            if (screen.Rows is not { } rows) { return Refused(screen.Problem ?? "the host could not read the session's screen; answer at the machine"); }
            var (choice, problem) = CopilotPermissionScreen.Locate(rows, approve, pending.Probes);
            if (choice is null) { return Refused($"{problem}; answer at the machine"); }
            // Again after the screen was read: what is on it is this request only if this
            // request was still the one waiting once it had been read.
            var after = CopilotPermissionTranscript.Before(transcript, requestId);
            if (!after.Readable) { return Refused("the session's transcript could not be read, so nothing is typed; answer at the machine"); }
            if (after.Request is not { } still || !string.Equals(still.Digest, digest, StringComparison.Ordinal)) { return Stale("Copilot is no longer asking for that permission"); }
            if (after.PendingPermissions + after.PendingAsks > 1)
            {
                return Refused("Copilot has more than one prompt waiting, and the screen shows only one of them; answer at the machine");
            }
            keys = new KeyPlan([new KeySend(choice.Key, false, 0, 0, choice.Hold)]);
            from = after.ScannedTo;

            if (ledger.Claim(attempt) is { } claimed) { return Refused(claimed); }
            typed = await TypeAsync(pid, keys);
        }
        finally { gate.DoneTyping(); }

        log($"typed {decision} into {Short(session.Id)} (pid {pid}) for permission {Short(requestId)}: {keys.Describe()} -> {typed.Status} {typed.Detail}");
        if (Settle(typed, attempt) is { } settled) { return settled; }
        try { return await ConfirmPermissionAsync(transcript, requestId, from, approve); }
        catch (Exception e) { return Unconfirmed($"the {decision} was typed but could not be checked ({e.Message}); check at the machine"); }
    }

    async Task<AnswerResult> ConfirmPermissionAsync(string transcript, string requestId, long from, bool approve)
    {
        var started = options.Time.GetTimestamp();
        while (true)
        {
            var result = CopilotPermissionTranscript.Since(transcript, requestId, from);
            if (result.Kind is { } kind)
            {
                if (approve ? kind == "approved" : kind == "denied-interactively-by-user" && !result.Feedback)
                {
                    return new AnswerResult(AnswerOutcome.Delivered);
                }
                return Unconfirmed($"Copilot recorded the request as {(kind.Length == 0 ? "answered" : kind)}{(result.Feedback ? " with a message" : "")}, not {(approve ? "approved" : "denied")}; check at the machine");
            }
            if (result.SessionEnded) { return Unconfirmed("the session ended before it recorded an answer; check at the machine"); }
            if (options.Time.GetElapsedTime(started) >= options.ConfirmTimeout)
            {
                return Unconfirmed($"the session did not record an answer within {options.ConfirmTimeout.TotalSeconds:0} seconds; check at the machine");
            }
            await Task.Delay(options.PollInterval, options.Time);
        }
    }

    static bool? Decision(IReadOnlyDictionary<string, System.Text.Json.JsonElement>? fields) =>
        fields is not null && fields.TryGetValue("decision", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() switch { "approve" => true, "deny" => false, _ => null }
            : null;

    // Claude Code's AskUserQuestion, keyed by its tool_use id in the session's transcript.
    async Task<AnswerResult> ClaudeAsync(Session session, Question question, AnswerRequest request)
    {
        if (source.Files(session.Id)?.Transcript is not { Length: > 0 } transcript) { return Refused("the session's transcript is not known; answer at the machine"); }
        if (Target(session, AgentKind.Claude) is { } unfit) { return unfit; }
        var pid = session.ProcessId!.Value;

        await gate.WaitToTypeAsync();
        ClaudePlan plan;
        long from;
        InjectionResult typed;
        var attempt = AttemptLedger.Key("claude", session.Id, question.Id);
        try
        {
            if (probe.Active() is { } active) { return Refused(active); }
            if (source.Get(session.Id)?.Question?.Id != question.Id) { return Stale("the session is no longer asking that question"); }
            var call = ClaudeTranscript.Find(transcript, question.Id);
            if (!call.Readable) { return Refused("the session's transcript could not be read, so nothing is typed; answer at the machine"); }
            if (call.Input is not { } input) { return Stale("the session's transcript no longer shows that question"); }
            if (call.Answer is not null || call.Interrupted) { return Stale("the question has already been answered"); }
            if (!call.IsMainAgent) { return Refused("the question was asked by a subagent; answer at the machine"); }

            var (form, unreadable) = ClaudeForm.Read(input);
            if (form is null) { return Refused(unreadable!); }
            if (!form.Matches(question)) { return Refused("the question's form reads differently here than the client was shown; answer at the machine"); }
            var (planned, problem) = ClaudePlanner.Plan(form, request);
            if (planned is null) { return Refused(problem!); }
            (plan, from) = (planned, call.ScannedTo);

            if (ledger.Claim(attempt) is { } claimed) { return Refused(claimed); }
            typed = await TypeAsync(pid, plan.Keys);
        }
        finally { gate.DoneTyping(); }

        log($"typed into {Short(session.Id)} (pid {pid}): {plan.Keys.Describe()} -> {typed.Status} {typed.Detail}");
        if (Settle(typed, attempt) is { } settled) { return settled; }
        try { return await ConfirmClaudeAsync(session.Id, pid, transcript, question.Id, from, plan); }
        catch (Exception e) { return Unconfirmed($"the answer was typed but could not be checked ({e.Message}); check at the machine"); }
    }

    // Watches the transcript for the call's tool_result. Claude can end on a review screen
    // that needs one more Enter, so a question still unanswered after ClaudeReviewAfter gets
    // exactly one, as agent-ha-bridge presses it: never a second, and never once anything
    // has been recorded for the call.
    async Task<AnswerResult> ConfirmClaudeAsync(string sessionId, int pid, string transcript, string toolUseId, long from, ClaudePlan plan)
    {
        var started = options.Time.GetTimestamp();
        var reviewed = false;
        while (true)
        {
            var elapsed = options.Time.GetElapsedTime(started);
            var state = ClaudeTranscript.Since(transcript, toolUseId, from);
            if (state.Answer is { } record)
            {
                var (matched, reason) = ClaudeVerifier.Check(plan, record);
                return matched ? new AnswerResult(AnswerOutcome.Delivered) : Unconfirmed($"{reason}; check at the machine");
            }
            if (state.Interrupted) { return Unconfirmed("the question was interrupted at the machine before Claude recorded an answer; check at the machine"); }
            if (elapsed >= options.ConfirmTimeout)
            {
                return Unconfirmed($"the session did not record an answer within {options.ConfirmTimeout.TotalSeconds:0} seconds; check at the machine");
            }
            if (!reviewed && elapsed >= options.ClaudeReviewAfter)
            {
                reviewed = true;
                await SubmitReviewAsync(sessionId, pid, transcript, toolUseId, from);
            }
            await Task.Delay(options.PollInterval, options.Time);
        }
    }

    async Task SubmitReviewAsync(string sessionId, int pid, string transcript, string toolUseId, long from)
    {
        await gate.WaitToTypeAsync();
        try
        {
            // The same last checks as the answer itself: an Enter typed now goes wherever the
            // session's input is, so not while agent-ha-bridge runs, and not once the call is
            // answered or the turn is over.
            if (probe.Active() is not null) { return; }
            var state = ClaudeTranscript.Since(transcript, toolUseId, from);
            if (!state.Readable || state.Answer is not null || state.Interrupted) { return; }
            if (!processes.GetAgentProcesses(AgentKind.Claude).Any(p => p.Id == pid) || guard.Refuse(pid) is not null) { return; }
            var enter = new KeyPlan([new KeySend("", true, 0, 0)]);
            var result = await TypeAsync(pid, enter);
            log($"pressed Enter on Claude's review screen in {Short(sessionId)} (pid {pid}) -> {result.Status} {result.Detail}");
        }
        finally { gate.DoneTyping(); }
    }

    // A Codex approval, keyed by the generation agent-ha-bridge's hook wrote into its
    // marker (DecisionId). The marker is the gate, read afresh immediately before typing:
    // the same generation, readable, and never already attempted by agent-ha-bridge.
    async Task<AnswerResult> CodexAsync(Session session, Question question, AnswerRequest request)
    {
        if (question.Kind != QuestionKind.Approval) { return Refused("this Codex prompt can only be answered at the machine"); }
        if (source.Files(session.Id)?.Registration is not { Length: > 0 } registration) { return Refused("the session's approval record is not known; answer at the machine"); }
        if (Target(session, AgentKind.Codex) is { } unfit) { return unfit; }
        var pid = session.ProcessId!.Value;
        var (keys, decision, problem) = CodexApproval.Plan(request.Fields);
        if (keys is null) { return Refused(problem!); }

        await gate.WaitToTypeAsync();
        InjectionResult typed;
        var attempt = AttemptLedger.Key("codex", session.Id, question.Id);
        try
        {
            if (probe.Active() is { } active) { return Refused(active); }
            if (source.Get(session.Id)?.Question?.Id != question.Id) { return Stale("the session is no longer asking for that approval"); }
            var marker = ApprovalMarkerRead.Read(registration, session.Id);
            switch (marker.State)
            {
                case ApprovalMarkerState.Absent: return Stale("Codex is no longer asking for that approval");
                case ApprovalMarkerState.Unreadable: return Refused("Codex's approval record could not be read, so nothing is typed; answer at the machine");
            }
            if (!string.Equals(marker.DecisionId, question.Id, StringComparison.Ordinal)) { return Stale("Codex is asking for a different approval now"); }
            if (marker.Attempted) { return Refused("agent-ha-bridge has already typed an answer into this approval, and it is never typed again; check at the machine"); }

            if (ledger.Claim(attempt) is { } claimed) { return Refused(claimed); }
            typed = await TypeAsync(pid, keys);
        }
        finally { gate.DoneTyping(); }

        log($"typed {decision} into {Short(session.Id)} (pid {pid}): {keys.Describe()} -> {typed.Status} {typed.Detail}");
        if (Settle(typed, attempt) is { } settled) { return settled; }
        return Unconfirmed($"typed {decision}, but Codex records nothing that says how an approval was answered, so it cannot be confirmed; check at the machine");
    }

    // The session's process is still the live agent process and may be typed into.
    AnswerResult? Target(Session session, AgentKind agent)
    {
        if (session.ProcessId is not { } pid) { return Refused("the session's process is not known; answer at the machine"); }
        if (!processes.GetAgentProcesses(agent).Any(p => p.Id == pid)) { return Stale("the session's process has ended"); }
        return guard.Refuse(pid) is { } refused ? Refused(refused) : null;
    }

    async Task<InjectionResult> TypeAsync(int pid, KeyPlan keys)
    {
        try { return await injector.InjectAsync(pid, keys); }
        catch (Exception e)
        {
            return new InjectionResult(InjectionStatus.Interrupted, $"the injector failed while typing ({e.Message})");
        }
    }

    // What typing came to, when that is already the outcome: nothing typed (refused, and the
    // prompt may be answered again) or keys that may have landed (unconfirmed).
    AnswerResult? Settle(InjectionResult typed, string attempt)
    {
        switch (typed.Status)
        {
            case InjectionStatus.NothingTyped:
                ledger.Release(attempt);
                return Refused(typed.Detail);
            case InjectionStatus.Interrupted:
                return Unconfirmed($"{typed.Detail}; part of the answer may have reached the session, so check at the machine");
            default:
                return null;
        }
    }

    static string Name(AgentKind agent) => agent switch
    {
        AgentKind.Claude => "Claude",
        AgentKind.Codex => "Codex",
        _ => "Copilot",
    };

    static string Short(string id) => id.Length > 8 ? id[..8] : id;

    static AnswerResult Refused(string reason) => new(AnswerOutcome.Refused, reason);
    static AnswerResult Stale(string reason) => new(AnswerOutcome.Stale, reason);
    static AnswerResult Busy() => new(AnswerOutcome.Busy, "another answer or reply to this session is being delivered");
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
