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

    // watch: hears the delivery's stages as it goes (DeliveryWatch), for a caller that reports them.
    // confirmed: the confirmation an answer carried, as the host's relay path checks it (#803); only
    // there is one made, so Approve on an approval the host escaped anything in goes from nowhere else.
    Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken cancellationToken = default, DeliveryWatch? watch = null, IEscapedConfirmation? confirmed = null);
}

// A person's confirmation of an approval the host escaped anything in (#803), as the host that took
// it through the relay checks it (AgentBridge.Host.EscapedApprovals).
public interface IEscapedConfirmation
{
    // Null when it approves question as the host would send it now; otherwise why not. Asked when
    // the answer is taken and again once its typing slot is held.
    string? Check(Question question);
}

public sealed class AnswerOptions
{
    // Where Copilot's session-state lives; the same root the session source reads.
    public required string CopilotHome { get; init; }

    // Whether this host can deliver at all. Windows only for now; macOS and Linux (tmux)
    // come later.
    public bool PlatformSupported { get; init; } = OperatingSystem.IsWindows();

    // Why a Codex approval cannot be answered yet, or null. On a migrated machine the host's hook
    // intake must first settle markers left from before it took the spool, any of which may
    // already have been answered at the terminal (HookIntake.ApprovalsUnsettled). Given the
    // registration whose marker would be answered.
    public Func<string, string?>? CodexApprovalsUnsettled { get; init; }

    // How long the transcript is watched for the answer once the keys have gone.
    public TimeSpan ConfirmTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    // How often the screen is read while a form's field settles or shows; PollInterval unless
    // set. Every form answer waits on the screen now (#510), so a transcript watched slowly need
    // not make the keys slow too.
    public TimeSpan? ScreenPollInterval { get; init; }

    // How long typed words are given to show on a Copilot session's screen. Not shown by then,
    // their Enter is never pressed, and the person finishes the answer at the machine (#467).
    public TimeSpan WordsWait { get; init; } = TimeSpan.FromSeconds(3);

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

    // As ReplyOptions.Writes: confirmation looks again as soon as the transcript is written to.
    public TranscriptWrites? Writes { get; init; }
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
    // How to read an unconfirmed request's record again, by request (Recheck), and since when:
    // kept as long as the request's outcome is, and no longer.
    readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, (Func<Task<AnswerResult?>> Read, DateTimeOffset At)> rechecks = new();

    // gate: the one the host's ReplyService shares, so an answer and a reply never type into
    // one session at once; a service of its own when none is given (tests).
    // Test-only compatibility overload; production must pass the shared gate explicitly.
    internal AnswerService(ISessionSource source, IProcessInspector processes, IAgentHaBridgeProbe probe, ITargetGuard guard,
        IConsoleInjector injector, AnswerOptions options, Action<string>? log = null, DeliveryGate? gate = null)
        : this(gate ?? new DeliveryGate(), source, processes, probe, guard, injector, options, log) { }

    public AnswerService(DeliveryGate gate, ISessionSource source, IProcessInspector processes, IAgentHaBridgeProbe probe, ITargetGuard guard,
        IConsoleInjector injector, AnswerOptions options, Action<string>? log = null)
    {
        this.source = source;
        this.processes = processes;
        this.probe = probe;
        this.guard = guard;
        this.injector = new GateTrackedInjector(injector, gate);
        this.options = options;
        this.log = log ?? (_ => { });
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        requests = new OutcomeMemory<AnswerResult>(options.Time, options.RememberFor, this.gate, Busy);
        ledger = new AttemptLedger(options.AttemptLedgerPath, options.Time, this.log);
    }

    public bool Available => options.PlatformSupported && probe.Recent() is null;

    public bool ApprovesPermissions => Available && options.CopilotRemoteApproval;

    public Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken cancellationToken = default, DeliveryWatch? watch = null, IEscapedConfirmation? confirmed = null)
    {
        if (!Guid.TryParseExact(request.Request, "D", out var key))
        {
            return Task.FromResult(Refused("request must be a UUID"));
        }
        return requests.Once(key, watch, () => new DeliveryTimeline(options.Time, "answer", key, log), t => DeliverAsync(request, key, t, confirmed), result =>
            log($"answer {key.ToString()[..8]} to {Short(request.Id)}: {result.Outcome.ToString().ToLowerInvariant()}{(result.Reason is null ? "" : $" ({result.Reason})")}"),
            Recheck(key));
    }

    // A resend of a request already settled unconfirmed: what the session has recorded since,
    // read without typing anything, for the same question's own call. So an answer the person
    // submitted at the machine, or the session recorded late, comes back delivered, and one it
    // recorded differently, stage recorded; nothing is ever typed twice, and a newer question is
    // never touched. Null for anything else, which gets its first outcome again.
    Func<AnswerResult, Task<AnswerResult?>> Recheck(Guid key) => async first =>
    {
        if (first.Outcome != AnswerOutcome.Unconfirmed || !rechecks.TryGetValue(key, out var entry)) { return null; }
        AnswerResult? later;
        try { later = await entry.Read(); }
        catch (Exception) { return null; }
        if (later?.Outcome == AnswerOutcome.Delivered) { rechecks.TryRemove(key, out _); }
        return later is null || later == first ? null : later;
    };

    void Recheckable(AnswerRequest request, Func<Task<AnswerResult?>> read)
    {
        var now = options.Time.GetUtcNow();
        foreach (var (old, _) in rechecks.Where(r => now - r.Value.At > options.RememberFor).ToList()) { rechecks.TryRemove(old, out _); }
        rechecks[Guid.Parse(request.Request)] = (read, now);
    }

    async Task<AnswerResult> DeliverAsync(AnswerRequest request, Guid key, DeliveryTimeline timeline, IEscapedConfirmation? confirmed)
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
            if (question.AnswerAtMachine == true) { return Refused(Undrivable(session, question) ?? AtMachine(session.Agent, question)); }
            if (session.Agent is not (AgentKind.Copilot or AgentKind.Claude or AgentKind.Codex))
            {
                return Refused($"answering is not supported for {session.Agent.ToString().ToLowerInvariant()} yet; answer at the machine");
            }
            if (Answers.Check(question, request) is { } invalid) { return Refused(invalid); }
            if (EscapedRefusal(question, request, confirmed) is { } unconfirmed) { return Refused(unconfirmed); }

            if (!gate.TryClaim(session.Id, key)) { return Busy(); }
            try
            {
                return session.Agent switch
                {
                    AgentKind.Claude => await ClaudeAsync(session, question, request, timeline),
                    AgentKind.Codex => await CodexAsync(session, question, request, timeline, confirmed),
                    _ when permission => await CopilotPermissionAsync(session, question, request, timeline, confirmed),
                    _ => await CopilotAsync(session, question, request, timeline),
                };
            }
            finally { gate.Release(session.Id, key); }
        }
        catch (Exception e)
        {
            // Only reached before anything was typed: each agent's path owns its own
            // failures once a key may have gone.
            return Failed(AnswerOutcome.Refused, $"answer {key.ToString()[..8]} to {Short(request.Id)} failed before typing", e);
        }
    }

    // Why a Copilot form held to be answered at the machine is held there, from its own schema:
    // the field, and what about it the keys cannot drive (CopilotFieldSchema), as typing it would
    // have said. Without it the answer was refused with nothing but "can only be answered at the
    // machine", the field's reason never reaching the person (#812 review). Fixed text built from
    // the schema, never from the answer. Null when the question cannot be read again.
    string? Undrivable(Session session, Question question)
    {
        if (session.Agent != AgentKind.Copilot || question.Kind != QuestionKind.Form || !Guid.TryParseExact(session.Id, "D", out _)) { return null; }
        try
        {
            var transcript = Path.Combine(options.CopilotHome, "session-state", session.Id, "events.jsonl");
            if (CopilotTranscript.Find(transcript, question.Id).Arguments is not { } arguments) { return null; }
            var (_, problem) = CopilotForm.Read(arguments);
            return problem;
        }
        catch (Exception) { return null; }
    }

    // Why Approve on an approval the host escaped anything in cannot go as asked (#803), or null: Deny,
    // and an approval with nothing escaped, need no confirmation. Asked of the question as listed
    // when the answer is taken, and again once the typing slot is held, where Answers.Check is not
    // asked again: so a device taken off the list, or a confirmation past its lifetime, meanwhile
    // stops it. A confirmation is good only for the escaped message it was signed for: one carried
    // to a question with nothing escaped refuses Approve too (#836 review). That a question still
    // reads at the slot as it did when the answer was taken is the agent's own check, not this one:
    // a Copilot prompt's id binds its content, and a Codex approval is rebuilt from its fresh marker
    // to exactly the id answered (#958), never read from the cached listing.
    internal static string? EscapedRefusal(Question question, AnswerRequest request, IEscapedConfirmation? confirmed)
    {
        if (question.Kind != QuestionKind.Approval || Decision(request.Fields) != true) { return null; }
        if (question.Escaped is not { } escaped) { return request.Confirm is null && confirmed is null ? null : Changed; }
        if (question.Truncated == true) { return "part of this approval didn't fit in the message from the machine, so it can only be approved at the machine"; }
        if (escaped.Approve == EscapedApproval.TooMany)
        {
            return "this approval holds too many hidden characters to show on another device, so it can only be approved at the machine; deny it from here, or answer at the machine";
        }
        if (escaped.Approve != EscapedApproval.Confirm)
        {
            return "this machine takes Approve for an approval with escaped characters only at the machine (permissions.escapedApproval); deny it from here, or answer at the machine";
        }
        if (confirmed is null)
        {
            return request.Confirm is null
                ? "this approval holds hidden or right-to-left characters, so Approve needs the escaped-command confirmation, which this app does not give: deny it, update the app, or answer at the machine"
                : "an approval with escaped characters is approved only from a person's phone, browser or Windows app through the relay, or at the machine";
        }
        return confirmed.Check(question);
    }

    internal const string Changed = "the approval reads differently now from what was confirmed; read it again";

    static string AtMachine(AgentKind agent, Question question) => question.Kind == QuestionKind.Approval
        ? $"{Name(agent)} permission prompts can only be answered at the machine"
        : $"this {Name(agent)} prompt can only be answered at the machine";

    async Task<AnswerResult> CopilotAsync(Session session, Question question, AnswerRequest request, DeliveryTimeline timeline)
    {
        // The session id names a folder; only the agent's own id shape is accepted.
        if (!Guid.TryParseExact(session.Id, "D", out _)) { return Refused("the session's id is not a Copilot session id"); }
        var transcript = Path.Combine(options.CopilotHome, "session-state", session.Id, "events.jsonl");
        if (Target(session, AgentKind.Copilot) is { } unfit) { return unfit; }
        var pid = session.ProcessId!.Value;

        // One answer typed at a time, across sessions, and the slot is taken before the last
        // checks: an answer that waited behind another must find its question still asked,
        // and agent-ha-bridge still stopped, when its own turn comes, not when it arrived.
        timeline.Checked();
        // Why the question can no longer be typed into, or null: the last checks before the first
        // key, made again before each later write of a plan typed in parts (TypeWordsApartAsync).
        // Between them the host waits on the screen, and the person may answer at the machine,
        // or a permission prompt open over the form, in the meantime.
        string? StillAsked()
        {
            if (probe.Active() is { } active) { return active; }
            if (source.Get(session.Id)?.Question?.Id != question.Id) { return "the session is no longer asking that question"; }
            var now = CopilotTranscript.Find(transcript, question.Id);
            if (now.Arguments is null || now.Answer is not null || now.SessionEnded) { return "the question was answered or closed while the answer was being typed"; }
            var prompts = CopilotPermissionTranscript.Before(transcript, "");
            if (!prompts.Readable) { return "the session's transcript could no longer be read"; }
            if (prompts.PendingPermissions > 0) { return "Copilot opened another prompt while the answer was being typed"; }
            return Target(session, AgentKind.Copilot)?.Reason;
        }

        await gate.WaitToTypeAsync();
        timeline.Turn();
        CopilotPlan plan;
        long from;
        InjectionResult typed;
        string? held;
        var attempt = AttemptLedger.Key("copilot", session.Id, question.Id);
        try
        {
            // Immediately before typing: agent-ha-bridge again, then the question itself, by
            // its tool call id in the transcript, never by how recent anything looks.
            if (probe.Active() is { } active) { return Refused(active); }
            if (source.Get(session.Id)?.Question?.Id != question.Id) { return Stale("the session is no longer asking that question"); }
            var call = CopilotTranscript.Find(transcript, question.Id);
            if (call.Arguments is not { } arguments) { return Stale("the session's transcript no longer shows that question"); }
            if (call.Resumed && call.Answer is null) { return Stale("the session was resumed in a new Copilot CLI since it asked that question; answer at the machine"); }
            if (call.Answer is not null || call.SessionEnded) { return Stale("the question has already been answered"); }
            if (!call.IsMainAgent) { return Refused("the question was asked by a subagent; answer at the machine"); }
            // Keys go to whichever prompt the CLI shows, and a permission dialog (a sub-agent's,
            // say) can be shown over this question, which is the one listed (#173). Read just
            // before typing, since the host's view may be a pass behind.
            var prompts = CopilotPermissionTranscript.Before(transcript, "");
            if (!prompts.Readable) { return Refused("the session's transcript could not be read, so nothing is typed; answer at the machine"); }
            if (prompts.PendingPermissions > 0)
            {
                return Refused("Copilot has more than one prompt waiting, and the screen shows only one of them; answer at the machine");
            }

            var (form, unreadable) = CopilotForm.Read(arguments);
            if (form is null) { return Refused(unreadable!); }
            // An ask_user with no form schema: how Copilot CLI 1.0.105 draws it cannot be told
            // from its code (its elicitations require a schema), so there is nothing to hold the
            // keys to, and they are not typed blind (#510 review).
            if (form.Fields.Count == 0) { return Refused("this question has no form the host can recognise on the session's screen; answer it at the machine"); }
            if (CopilotFormScreen.Ambiguous(form) is { } alike) { return Refused(alike); }
            if (!form.Matches(question)) { return Refused("the question's form reads differently here than the client was shown; answer at the machine"); }
            var (planned, problem) = CopilotPlanner.Plan(form, request);
            if (planned is null) { return Refused(problem!); }
            (plan, from) = (planned, call.ScannedTo);

            if (ledger.Claim(attempt) is { } claimed) { return Refused(claimed); }
            (typed, held) = await TypeWordsApartAsync(session.Id, pid, form, plan.Keys, StillAsked, timeline);
        }
        finally { gate.DoneTyping(); }

        log($"typed into {Short(session.Id)} (pid {pid}): {plan.Keys.Describe()} -> {typed.Status} {typed.Detail}{(held is null ? "" : $"; held before its Enter: {held}")}");
        var seen = new System.Runtime.CompilerServices.StrongBox<DateTimeOffset?>();
        Task<AnswerResult?> Late() => LateCopilotAsync(transcript, question.Id, from, plan, seen, timeline);
        AnswerResult result;
        if (Settle(typed, attempt) is { } settled) { result = settled; }
        else if (held is not null)
        {
            // Held before an Enter, perhaps because the question was answered at the machine
            // meanwhile: what the session has recorded says so, if anything.
            AnswerResult? recorded;
            try { recorded = await Late(); }
            catch (Exception) { recorded = null; }
            result = recorded ?? Unconfirmed($"the answer was typed into the prompt but not submitted: {held}, so its Enter was not pressed; finish it at the machine");
        }
        else
        {
            try { result = await ConfirmAsync(transcript, question.Id, from, plan, timeline); }
            catch (Exception e) { result = Failed(AnswerOutcome.Unconfirmed, $"the answer typed into {Short(session.Id)} could not be checked", e); }
        }
        // Unconfirmed is not the end of it, however it came about, an interrupted injection
        // included: the person may submit it at the machine, or the session record it late. A
        // resend of this request then reads the record again, for this question's own call only,
        // and types nothing (Recheck).
        if (result.Outcome == AnswerOutcome.Unconfirmed) { Recheckable(request, Late); }
        return result;
    }

    // The Copilot plan as typed (#467). Words with an Enter after them are typed on their own,
    // and the Enter goes only once the session's screen shows them, which proves the CLI has
    // taken them in as input of their own. An Enter that reaches it together with them is part
    // of one paste, and Copilot's form took it as a new line in a text field: the answer was
    // typed but never submitted, and nothing said so.
    //
    // Whether they show is counted against how often their end showed before they were typed,
    // on the field they go to: the keys before them (the fields before, or the walk that opens
    // Other) go first, on their own, and the count is taken once the screen has changed from how
    // it stood before them and reads the same twice running. The Enter that commits the field
    // before goes alone, after the screen has settled from that field's keys, so the change is
    // the form moving on and not a list's highlight. Counted earlier, the field switch itself
    // would move the count: a label of the new field reading like the words could pass for
    // them, and the same words given to the field before could hide them.
    //
    // Every one of those writes is held to the screen it was planned on (KeySend.Expect): the
    // worker types nothing of it unless those rows still read exactly so, in the same attach,
    // just before its first key. A permission prompt can open over the form at any moment (#173),
    // and Enter picks whatever it highlights, so a check of the transcript alone, a moment
    // before, is not enough: the semaphore, starting the worker and attaching all come between
    // (sec-510). The question is still checked too, once the injector holds the typing slot
    // (KeyPlan.Proceed). The words' Enter goes alone and at once, held to the rows that show
    // them, and never with the next field's keys.
    //
    // It fails closed. An Enter sent into a screen nobody has read is how #467 happened, so a
    // plan with words is typed only into a screen the host can read (nothing is typed otherwise),
    // and words or a field that do not show on it within WordsWait, a screen that changed, or a
    // question no longer asked, stop it there: nothing more is pressed, and the answer is
    // reported typed (not recorded), for the person to finish at the machine. Held says why;
    // null when every key went.
    async Task<(InjectionResult Result, string? Held)> TypeWordsApartAsync(string sessionId, int pid, CopilotForm form, KeyPlan plan, Func<string?> stillAsked, DeliveryTimeline? timeline = null)
    {
        // Words that leave nothing to look for once white space and line drawing are set aside
        // could never be seen, so their Enter would never go: refused before any key, for the
        // answer to be changed, rather than typed and left.
        if (plan.Sends.Any(s => IsWords(s) && WordsOnScreen.Tail(s.Text).Length == 0))
        {
            return (new InjectionResult(InjectionStatus.NothingTyped,
                "the answer's words are only spaces and line-drawing characters, which the host cannot pick out on the session's screen to see that the CLI has them before it submits them; change the answer, or answer at the machine"), null);
        }
        // Every answer goes field by field, a single list or Yes/No included, words or not: each
        // field's keys held to the form at that field, then its Enter alone, held to the form
        // as it settled. A permission prompt that opens before its transcript entry is written
        // reads as steady as the form, and a lone list's walk and Enter written blind would go
        // into it, the Enter approving what it highlights (sec-510 re-review, #510 review).
        var words = plan.Sends.Any(IsWords);
        if (await ReadRowsAsync(pid) is null)
        {
            return (new InjectionResult(InjectionStatus.NothingTyped, words
                ? "the host could not read the session's screen, which it needs to see typed words before it submits them; answer at the machine"
                : "the host could not read the session's screen, which it needs to type each field of the form only once the form shows it; answer at the machine"), null);
        }
        var pending = new List<KeySend>();
        var typedAny = false;
        // Stopped here: nothing more goes. Held once anything has been typed; nothing typed, and
        // the prompt free to be answered again, otherwise.
        (InjectionResult, string?) Stop(string why) => typedAny
            ? (new InjectionResult(InjectionStatus.Typed, "ok"), why)
            : (new InjectionResult(InjectionStatus.NothingTyped, why.Contains("at the machine", StringComparison.Ordinal) ? why : $"{why}; answer at the machine"), null);
        // One write, still asked for once the injector holds the typing slot. A write that typed
        // nothing (its rows read differently, or the question no longer held) stops it as above;
        // one cut short is interrupted. Null when it went.
        async Task<(InjectionResult, string?)?> WriteAsync(List<KeySend> sends, bool last)
        {
            if (stillAsked() is { } gone) { return Stop(gone); }
            var result = await TypeAsync(pid, new KeyPlan(sends) { Proceed = stillAsked }, timeline, last);
            switch (result.Status)
            {
                case InjectionStatus.Typed:
                    typedAny = true;
                    return null;
                // Nothing typed yet, for a reason of the injector's own (an elevated session, say):
                // said as the injector says it, as any other answer refused there is.
                case InjectionStatus.NothingTyped when !typedAny && result.Detail != Windows.WorkerInjector.ScreenChanged:
                    return (result, null);
                case InjectionStatus.NothingTyped:
                    log($"typing into {Short(sessionId)} (pid {pid}) stopped before {(sends is [{ Text: "", Enter: true }] ? "an Enter" : "its next keys")}: {result.Detail}");
                    return Stop(result.Detail == Windows.WorkerInjector.ScreenChanged ? "the session's screen changed before the next key could go" : result.Detail);
                default:
                    return (AfterPart(result, typedAny), null);
            }
        }

        // The screen the last Enter was pressed on. Whatever goes next waits for the form to move
        // off it, and is held to the field it moved to: read at once, a CLI still showing the
        // field just committed would be taken for the next one.
        IReadOnlyList<string>? leaving = null;
        // The field the next keys are for: one on from each Enter.
        var at = 0;
        // Null when the rows keys are about to be held to show the form at that field. A screen
        // that has settled, or moved on, is not enough: a permission dialog that opens before the
        // transcript says so is just as steady, and keys held to its rows go into it, an Enter
        // picking what it highlights (#510 review).
        string? OffForm(IReadOnlyList<string> rows)
        {
            if (CopilotFormScreen.Showing(rows, form, Math.Min(at, Math.Max(0, form.Fields.Count - 1))) is not { } off) { return null; }
            log($"the screen of {Short(sessionId)} (pid {pid}) is not the form at its next field: {off}; nothing more is typed");
            return off;
        }
        // The field the next keys go to: the one the form moved to, or the screen as it stands
        // before anything has been pressed. Its rows, how often tail shows on it, or why not.
        async Task<(IReadOnlyList<string>? Rows, int Count, string? Why)> NextFieldAsync(string tail)
        {
            if (leaving is null)
            {
                if (await ReadRowsAsync(pid) is not { } rows) { return (null, 0, "the session's screen could no longer be read"); }
                return OffForm(rows) is { } off ? (null, 0, off) : (rows, WordsOnScreen.Count(rows, tail), null);
            }
            var moved = await FieldShownAsync(pid, leaving, tail);
            leaving = null;
            if (moved.Rows is null) { log($"the next field in {Short(sessionId)} (pid {pid}) did not show: {moved.Why}; nothing more is typed"); }
            else if (OffForm(moved.Rows) is { } off) { return (null, 0, off); }
            return moved;
        }
        // One field's keys, which end with the Enter that commits it: held to that field, the keys
        // first, then the Enter alone once the screen has settled from them (an option list's
        // highlight repainting is not the form moving on). Fields are never batched together:
        // each is written only once the form shows it, still asked for, and held to it, so a
        // prompt that opens between two fields gets none of the second's keys.
        async Task<(InjectionResult, string?)?> FieldAsync(List<KeySend> keys, bool last)
        {
            var (shown, _, why) = await NextFieldAsync("");
            if (shown is null) { return Stop(why!); }
            var commit = keys[^1];
            var first = keys.GetRange(0, keys.Count - 1);
            if (commit.Text.Length > 0) { first.Add(commit with { Enter = false, EnterDelayMs = 0, PauseAfterMs = 0 }); }
            if (first.Count > 0)
            {
                first[0] = first[0] with { Expect = Rows(shown) };
                if (await WriteAsync(first, false) is { } stopped) { return stopped; }
                var (settled, unsettled) = await SettledAsync(pid);
                if (settled is null)
                {
                    log($"the screen of {Short(sessionId)} (pid {pid}) did not settle before the field's Enter: {unsettled}; nothing more is typed");
                    return Stop(unsettled!);
                }
                if (OffForm(settled) is { } off) { return Stop(off); }
                shown = settled;
            }
            if (await WriteAsync([new KeySend("", true, 0, commit.PauseAfterMs, Rows(shown))], last) is { } committed) { return committed; }
            leaving = shown;
            at++;
            return null;
        }

        for (var i = 0; i < plan.Sends.Count; i++)
        {
            var send = plan.Sends[i];
            if (!IsWords(send))
            {
                pending.Add(send);
                if (send.Enter)
                {
                    if (await FieldAsync(pending, i == plan.Sends.Count - 1) is { } stopped) { return stopped; }
                    pending = [];
                }
                continue;
            }
            var tail = WordsOnScreen.Tail(send.Text);
            var (field, before, missing) = await NextFieldAsync(tail);
            if (field is null) { return Stop(missing!); }
            if (pending.Count > 0)
            {
                // Other's walk, which has no Enter: held to the list it walks, and the words wait
                // for the change it makes, Other opening.
                var shown = field;
                pending[0] = pending[0] with { Expect = Rows(shown) };
                if (await WriteAsync(pending, false) is { } walked) { return walked; }
                pending = [];
                var (rows, count, why) = await FieldShownAsync(pid, shown, tail);
                if (rows is null)
                {
                    log($"the field the words go to in {Short(sessionId)} (pid {pid}) did not show: {why}; nothing more is typed");
                    return Stop(why!);
                }
                if (OffForm(rows) is { } off) { return Stop(off); }
                (field, before) = (rows, count);
            }
            if (await WriteAsync([send with { Enter = false, EnterDelayMs = 0, PauseAfterMs = 0, Expect = Rows(field) }], false) is { } held) { return held; }
            if (await AwaitOnScreenAsync(pid, tail, before) is not { } showing)
            {
                log($"the words typed into {Short(sessionId)} (pid {pid}) did not show on its screen within {options.WordsWait.TotalSeconds:0.#} s; their Enter is not pressed");
                return (new InjectionResult(InjectionStatus.Typed, "ok"), $"the words did not show on the session's screen within {options.WordsWait.TotalSeconds:0.#} seconds");
            }
            if (OffForm(showing) is { } away) { return Stop(away); }
            // Seen on screen, they reached the CLI apart from anything after them, so their Enter
            // needs no pause of its own; held to the rows that show them, it goes into nothing else.
            if (await WriteAsync([new KeySend("", true, 0, send.PauseAfterMs, Rows(showing))], i == plan.Sends.Count - 1) is { } entered) { return entered; }
            leaving = showing;
            at++;
        }
        if (pending.Count == 0) { return (new InjectionResult(InjectionStatus.Typed, "ok"), null); }
        // Keys with no Enter of their own after the last field (none is planned so): held to the
        // field they go to, like any other.
        var (rest, _, gone) = await NextFieldAsync("");
        if (rest is null) { return Stop(gone!); }
        pending[0] = pending[0] with { Expect = Rows(rest) };
        return await WriteAsync(pending, true) ?? (new InjectionResult(InjectionStatus.Typed, "ok"), null);
    }

    static bool IsWords(KeySend send) => send.Enter && CopilotPlanner.HasWords(send.Text);

    // The rows of a screen as a send holds to them.
    static List<ScreenRow> Rows(IReadOnlyList<string> rows) => [.. rows.Select((text, row) => new ScreenRow(row, text))];

    // Nothing typed by a later part of a plan whose earlier part went is not nothing typed.
    static InjectionResult AfterPart(InjectionResult result, bool typedAny) =>
        typedAny && result.Status == InjectionStatus.NothingTyped
            ? new InjectionResult(InjectionStatus.Interrupted, $"{result.Detail}, after part of the answer was typed")
            : result;

    // The session's screen, or null when it cannot be read.
    async Task<IReadOnlyList<string>?> ReadRowsAsync(int pid)
    {
        try { return (await injector.ReadScreenAsync(pid)).Rows; }
        catch (Exception) { return null; }
    }

    // The screen once two reads running are the same, within WordsWait; null, and why, if not.
    async Task<(IReadOnlyList<string>? Rows, string? Why)> SettledAsync(int pid)
    {
        var started = options.Time.GetTimestamp();
        IReadOnlyList<string>? last = null;
        while (true)
        {
            if (await ReadRowsAsync(pid) is not { } rows) { return (null, "the session's screen could no longer be read"); }
            if (last is not null && rows.SequenceEqual(last)) { return (rows, null); }
            last = rows;
            if (options.Time.GetElapsedTime(started) >= options.WordsWait)
            {
                return (null, $"the session's screen did not settle within {options.WordsWait.TotalSeconds:0.#} seconds");
            }
            await Task.Delay(options.ScreenPollInterval ?? options.PollInterval, options.Time);
        }
    }

    // The field the words go to, and how often their end shows on it: once the screen has
    // changed from how it stood before the keys that open that field, and reads the same twice
    // running. No rows, and why, if that does not happen within WordsWait or the screen cannot
    // be read.
    async Task<(IReadOnlyList<string>? Rows, int Count, string? Why)> FieldShownAsync(int pid, IReadOnlyList<string> shown, string tail)
    {
        var started = options.Time.GetTimestamp();
        IReadOnlyList<string>? last = null;
        while (true)
        {
            if (await ReadRowsAsync(pid) is not { } rows) { return (null, 0, "the session's screen could no longer be read"); }
            // Two reads running, of a screen that has changed: last follows every read, so a
            // repaint that went back to how it stood (B, then A, then B) does not count as B twice.
            if (!rows.SequenceEqual(shown) && last is not null && rows.SequenceEqual(last)) { return (rows, WordsOnScreen.Count(rows, tail), null); }
            last = rows;
            if (options.Time.GetElapsedTime(started) >= options.WordsWait)
            {
                return (null, 0, $"the field the words go to did not show on the session's screen within {options.WordsWait.TotalSeconds:0.#} seconds");
            }
            await Task.Delay(options.ScreenPollInterval ?? options.PollInterval, options.Time);
        }
    }

    // The screen once the words' end shows on it more often than it did before they were typed,
    // within WordsWait; null if it does not.
    async Task<IReadOnlyList<string>?> AwaitOnScreenAsync(int pid, string tail, int before)
    {
        var started = options.Time.GetTimestamp();
        while (true)
        {
            if (await ReadRowsAsync(pid) is { } rows && WordsOnScreen.Count(rows, tail) > before) { return rows; }
            if (options.Time.GetElapsedTime(started) >= options.WordsWait) { return null; }
            await Task.Delay(options.ScreenPollInterval ?? options.PollInterval, options.Time);
        }
    }

    // A Copilot answer reported unconfirmed, read again: delivered once its call records the
    // answer typed; stage recorded if it records another; null while nothing new. A record whose
    // completion has not followed it is given CompletionGrace from when a read first found it,
    // as ConfirmAsync gives it, so a completion saying the call failed can still be seen; that
    // wait is part of the read, so one resend (a client asks once per reconnect) is enough, and
    // after it the record alone decides, as it does there.
    // A match found here is confirmed on the delivery's own timeline, with the record's time, so
    // a repeat asking for progress hears when it was recorded and confirmed, as the first watch
    // would have said (#510 review).
    async Task<AnswerResult?> LateCopilotAsync(string transcript, string toolCallId, long from, CopilotPlan plan, System.Runtime.CompilerServices.StrongBox<DateTimeOffset?> seen, DeliveryTimeline timeline)
    {
        while (true)
        {
            var state = CopilotTranscript.Since(transcript, toolCallId, from);
            if (state.Answer is not { } record) { return null; }
            if (!record.Completed)
            {
                var now = options.Time.GetUtcNow();
                DateTimeOffset first;
                lock (seen) { first = seen.Value ??= now; }
                if (now - first < CompletionGrace)
                {
                    await Task.Delay(options.PollInterval, options.Time);
                    continue;
                }
            }
            timeline.Recorded(record.At);
            var (matched, reason) = CopilotVerifier.Check(plan, record);
            return matched ? Delivered(timeline) : Unconfirmed($"{reason}; check at the machine", AnswerStage.Recorded);
        }
    }

    // How long a recorded answer waits for its call's completion, which says whether it failed.
    static readonly TimeSpan CompletionGrace = TimeSpan.FromSeconds(1);

    // Watches the transcript, from where it stood before the keys went, for the answer to
    // this tool call.
    async Task<AnswerResult> ConfirmAsync(string transcript, string toolCallId, long from, CopilotPlan plan, DeliveryTimeline timeline)
    {
        var started = options.Time.GetTimestamp();
        TimeSpan? firstSeen = null;
        while (true)
        {
            var elapsed = options.Time.GetElapsedTime(started);
            // Taken before the look, so a record written between the look and the wait wakes it.
            using var written = options.Writes?.Next(transcript);
            var state = CopilotTranscript.Since(transcript, toolCallId, from);
            if (state.Answer is { } record)
            {
                // Current builds write the structured answer and the call's completion side by
                // side. A form is confirmed only by its structured answer, so it waits for that
                // up to the timeout; and the completion says whether the call failed, so a
                // moment is allowed for it to follow.
                var grace = elapsed - (firstSeen ??= elapsed) < CompletionGrace;
                var wait = elapsed < options.ConfirmTimeout
                    && ((record.HumanContent is null && plan.Text is null) || (!record.Completed && grace));
                if (!wait)
                {
                    timeline.Recorded(record.At);
                    var (matched, reason) = CopilotVerifier.Check(plan, record);
                    return matched ? Delivered(timeline) : Unconfirmed(reason, AnswerStage.Recorded);
                }
            }
            else if (state.SessionEnded) { return Unconfirmed("the session ended before it recorded an answer"); }
            else if (elapsed >= options.ConfirmTimeout)
            {
                return Unconfirmed(NotRecorded);
            }
            await TranscriptWrites.WaitAsync(written, options.PollInterval, options.Time);
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
    async Task<AnswerResult> CopilotPermissionAsync(Session session, Question question, AnswerRequest request, DeliveryTimeline timeline, IEscapedConfirmation? confirmed)
    {
        if (!Guid.TryParseExact(session.Id, "D", out _)) { return Refused("the session's id is not a Copilot session id"); }
        if (CopilotPermissionPrompt.ParseQuestionId(question.Id) is not { } parsed) { return Refused("this Copilot permission prompt can only be answered at the machine"); }
        var (requestId, digest) = parsed;
        if (Decision(request.Fields) is not { } approve) { return Refused("an approval is answered with decision approve or deny"); }
        var transcript = Path.Combine(options.CopilotHome, "session-state", session.Id, "events.jsonl");
        if (Target(session, AgentKind.Copilot) is { } unfit) { return unfit; }
        var pid = session.ProcessId!.Value;
        var decision = approve ? "approve" : "deny";

        timeline.Checked();
        await gate.WaitToTypeAsync();
        timeline.Turn();
        KeyPlan keys;
        long from;
        InjectionResult typed;
        var attempt = AttemptLedger.Key("copilot", session.Id, question.Id);
        try
        {
            if (probe.Active() is { } active) { return Refused(active); }
            if (source.Get(session.Id)?.Question is not { } listed || listed.Id != question.Id) { return Stale("the session is no longer asking for that permission"); }
            // The confirmation again, as things are now: a device approved no longer, or a policy
            // changed, stops it here, before any key.
            if (EscapedRefusal(listed, request, confirmed) is { } unconfirmed) { return Refused(unconfirmed); }
            var state = CopilotPermissionTranscript.Before(transcript, requestId);
            if (!state.Readable) { return Refused("the session's transcript could not be read, so nothing is typed; answer at the machine"); }
            if (state.Request is not { } pending) { return Stale("Copilot is no longer asking for that permission"); }
            if (!string.Equals(pending.Digest, digest, StringComparison.Ordinal)) { return Stale("Copilot's permission prompt reads differently from what was shown"); }
            if (pending.AtMachine is { } why) { return Refused($"{why} can only be answered at the machine"); }
            if (state.PendingAsks > 0)
            {
                return Refused("Copilot has more than one prompt waiting, and the screen shows only one of them; answer at the machine");
            }

            ConsoleScreen screen;
            try { screen = await injector.ReadScreenAsync(pid); }
            catch (Exception e) { return Failed(AnswerOutcome.Refused, $"could not read the screen of {Short(session.Id)} for permission {Short(requestId)}", e); }
            if (screen.Rows is not { } rows) { return Refused(DeliveryReasons.Of(screen.Problem ?? "the host could not read the session's screen; answer at the machine")); }
            var (choice, problem) = CopilotPermissionScreen.Locate(rows, approve, pending.Probes, Crowd(pending, state));
            if (choice is null) { return Refused($"{problem}; answer at the machine"); }
            // Again after the screen was read: what is on it is this request only if this
            // request was still the one waiting once it had been read, and no request that
            // arrived meanwhile could be the one drawn.
            var after = CopilotPermissionTranscript.Before(transcript, requestId);
            if (!after.Readable) { return Refused("the session's transcript could not be read, so nothing is typed; answer at the machine"); }
            if (after.Request is not { } still || !string.Equals(still.Digest, digest, StringComparison.Ordinal)) { return Stale("Copilot is no longer asking for that permission"); }
            if (after.PendingAsks > 0)
            {
                return Refused("Copilot has more than one prompt waiting, and the screen shows only one of them; answer at the machine");
            }
            (choice, problem) = CopilotPermissionScreen.Locate(rows, approve, pending.Probes, Crowd(pending, after));
            if (choice is null) { return Refused($"{problem}; answer at the machine"); }
            // The screen showing this request is what lets a key go; the order asked is only
            // how the card chose it. One asked after another still waiting is never typed into,
            // whatever the screen shows, since nothing says how Copilot orders them.
            if (!after.First) { return Refused("Copilot is asking for an earlier permission first; answer at the machine"); }
            keys = new KeyPlan([new KeySend(choice.Key, false, 0, 0, choice.Hold)]);
            from = after.ScannedTo;

            if (ledger.Claim(attempt) is { } claimed) { return Refused(claimed); }
            typed = await TypeAsync(pid, keys, timeline);
        }
        finally { gate.DoneTyping(); }

        log($"typed {decision} into {Short(session.Id)} (pid {pid}) for permission {Short(requestId)}: {keys.Describe()} -> {typed.Status} {typed.Detail}");
        if (Settle(typed, attempt) is { } settled) { return settled; }
        try { return await ConfirmPermissionAsync(transcript, requestId, from, approve, timeline); }
        catch (Exception e) { return Failed(AnswerOutcome.Unconfirmed, $"the {decision} typed into {Short(session.Id)} could not be checked", e); }
    }

    // The other requests waiting, each named by its kind and request id, never by what the
    // agent wrote, since a refusal is shown as plain words; one that cannot be read has no
    // text to look for, so it can never be ruled out. Null when none is.
    static CopilotPermissionScreen.Crowd? Crowd(CopilotPermissionPrompt pending, CopilotPermissionState state) =>
        state.Others.Count == 0 ? null
        : new(pending.Dialog, pending.Drawn, [.. state.Others.Select(o => new CopilotPermissionScreen.Other($"{KindName(o.Prompt?.Kind)}, request {Short(o.RequestId)}", o.Prompt?.Drawn ?? [], o.Prompt?.Dialog))]);

    static string KindName(string? kind) => kind switch
    {
        "commands" => "shell",
        "write" => "edit",
        "read" => "read",
        "path" => "path access",
        "url" => "URL",
        "mcp" => "MCP tool",
        "custom-tool" => "tool",
        _ => "permission",
    };

    async Task<AnswerResult> ConfirmPermissionAsync(string transcript, string requestId, long from, bool approve, DeliveryTimeline timeline)
    {
        var started = options.Time.GetTimestamp();
        while (true)
        {
            using var written = options.Writes?.Next(transcript);
            var result = CopilotPermissionTranscript.Since(transcript, requestId, from);
            if (result.Kind is { } kind)
            {
                if (approve ? kind == "approved" : (kind == "denied-interactively-by-user" && !result.Feedback) || (kind == "cancelled" && result.StoppedAfter))
                {
                    return Delivered(timeline);
                }
                // A cancel is a denial only once the person's abort of the turn follows it, which
                // Copilot writes a few milliseconds later; until then, look again.
                if (!approve && kind == "cancelled" && options.Time.GetElapsedTime(started) < options.ConfirmTimeout)
                {
                    await TranscriptWrites.WaitAsync(written, options.PollInterval, options.Time);
                    continue;
                }
                return Unconfirmed($"Copilot recorded the request as {(kind.Length == 0 ? "answered" : kind)}{(result.Feedback ? " with a message" : "")}, not {(approve ? "approved" : "denied")}", AnswerStage.Recorded);
            }
            if (result.SessionEnded) { return Unconfirmed("the session ended before it recorded an answer"); }
            if (options.Time.GetElapsedTime(started) >= options.ConfirmTimeout)
            {
                return Unconfirmed(NotRecorded);
            }
            await TranscriptWrites.WaitAsync(written, options.PollInterval, options.Time);
        }
    }

    static bool? Decision(IReadOnlyDictionary<string, System.Text.Json.JsonElement>? fields) =>
        fields is not null && fields.TryGetValue("decision", out var value) && value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() switch { "approve" => true, "deny" => false, _ => null }
            : null;

    // Claude Code's AskUserQuestion, keyed by its tool_use id in the session's transcript.
    async Task<AnswerResult> ClaudeAsync(Session session, Question question, AnswerRequest request, DeliveryTimeline timeline)
    {
        if (source.Files(session.Id)?.Transcript is not { Length: > 0 } transcript) { return Refused("the session's transcript is not known; answer at the machine"); }
        if (Target(session, AgentKind.Claude) is { } unfit) { return unfit; }
        var pid = session.ProcessId!.Value;

        timeline.Checked();
        await gate.WaitToTypeAsync();
        timeline.Turn();
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
            typed = await TypeAsync(pid, plan.Keys, timeline);
        }
        finally { gate.DoneTyping(); }

        log($"typed into {Short(session.Id)} (pid {pid}): {plan.Keys.Describe()} -> {typed.Status} {typed.Detail}");
        AnswerResult result;
        if (Settle(typed, attempt) is { } settled) { result = settled; }
        else
        {
            try { result = await ConfirmClaudeAsync(session.Id, pid, transcript, question.Id, from, plan, timeline); }
            catch (Exception e) { result = Failed(AnswerOutcome.Unconfirmed, $"the answer typed into {Short(session.Id)} could not be checked", e); }
        }
        if (result.Outcome == AnswerOutcome.Unconfirmed)
        {
            Recheckable(request, () => Task.FromResult(LateClaude(transcript, question.Id, from, plan, timeline)));
        }
        return result;
    }

    // A Claude answer reported unconfirmed, read again, as LateCopilot.
    static AnswerResult? LateClaude(string transcript, string toolUseId, long from, ClaudePlan plan, DeliveryTimeline timeline)
    {
        if (ClaudeTranscript.Since(transcript, toolUseId, from).Answer is not { } record) { return null; }
        var (matched, reason) = ClaudeVerifier.Check(plan, record);
        return matched ? Delivered(timeline) : Unconfirmed(reason!, AnswerStage.Recorded);
    }

    // Watches the transcript for the call's tool_result. Claude can end on a review screen
    // that needs one more Enter, so a question still unanswered after ClaudeReviewAfter gets
    // exactly one, as agent-ha-bridge presses it: never a second, and never once anything
    // has been recorded for the call.
    async Task<AnswerResult> ConfirmClaudeAsync(string sessionId, int pid, string transcript, string toolUseId, long from, ClaudePlan plan, DeliveryTimeline timeline)
    {
        var started = options.Time.GetTimestamp();
        var reviewed = false;
        while (true)
        {
            var elapsed = options.Time.GetElapsedTime(started);
            using var written = options.Writes?.Next(transcript);
            var state = ClaudeTranscript.Since(transcript, toolUseId, from);
            if (state.Answer is { } record)
            {
                var (matched, reason) = ClaudeVerifier.Check(plan, record);
                return matched ? Delivered(timeline) : Unconfirmed(reason!, AnswerStage.Recorded);
            }
            if (state.Interrupted) { return Unconfirmed("the question was interrupted before Claude recorded an answer"); }
            if (elapsed >= options.ConfirmTimeout)
            {
                return Unconfirmed(NotRecorded);
            }
            if (!reviewed && elapsed >= options.ClaudeReviewAfter)
            {
                reviewed = true;
                await SubmitReviewAsync(sessionId, pid, transcript, toolUseId, from, timeline);
            }
            await TranscriptWrites.WaitAsync(written, options.PollInterval, options.Time);
        }
    }

    async Task SubmitReviewAsync(string sessionId, int pid, string transcript, string toolUseId, long from, DeliveryTimeline timeline)
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
            var result = await TypeAsync(pid, enter, timeline);
            log($"pressed Enter on Claude's review screen in {Short(sessionId)} (pid {pid}) -> {result.Status} {result.Detail}");
        }
        finally { gate.DoneTyping(); }
    }

    // A Codex approval, keyed by the generation agent-ha-bridge's hook wrote into its
    // marker (DecisionId) and a digest of what the person was shown (CodexApprovalId, #958).
    // The marker is the gate, read afresh immediately before typing: the same generation,
    // reading exactly as shown, readable, and never already attempted by agent-ha-bridge.
    // The generation alone does not bind the command: Codex can list it again with another,
    // so the question is rebuilt from the fresh marker and its id must still be this one.
    // The session's listing is no help there: it is refreshed by a watcher or a poll, and can
    // still show the approval that was answered after the marker has moved on.
    async Task<AnswerResult> CodexAsync(Session session, Question question, AnswerRequest request, DeliveryTimeline timeline, IEscapedConfirmation? confirmed)
    {
        if (question.Kind != QuestionKind.Approval) { return Refused("this Codex prompt can only be answered at the machine"); }
        if (CodexApprovalId.Parse(question.Id) is not { } parsed) { return Refused("this Codex approval can only be answered at the machine"); }
        if (source.Files(session.Id)?.Registration is not { Length: > 0 } registration) { return Refused("the session's approval record is not known; answer at the machine"); }
        if (Target(session, AgentKind.Codex) is { } unfit) { return unfit; }
        var pid = session.ProcessId!.Value;
        var (keys, decision, problem) = CodexApproval.Plan(request.Fields);
        if (keys is null) { return Refused(problem!); }

        timeline.Checked();
        await gate.WaitToTypeAsync();
        timeline.Turn();
        InjectionResult typed;
        // Keyed by the generation, not the whole id: whatever the generation reads as later,
        // it is typed into once (and ledger entries from before #958 still hold).
        var attempt = AttemptLedger.Key("codex", session.Id, parsed.DecisionId);
        try
        {
            if (probe.Active() is { } active) { return Refused(active); }
            if (options.CodexApprovalsUnsettled?.Invoke(registration) is { } unsettled) { return Refused(unsettled); }
            if (source.Get(session.Id)?.Question?.Id != question.Id) { return Stale("the session is no longer asking for that approval"); }
            var marker = ApprovalMarkerRead.Read(registration, session.Id);
            switch (marker.State)
            {
                case ApprovalMarkerState.Absent: return Stale("Codex is no longer asking for that approval");
                case ApprovalMarkerState.Unreadable: return Refused("Codex's approval record could not be read, so nothing is typed; answer at the machine");
            }
            if (!string.Equals(marker.DecisionId, parsed.DecisionId, StringComparison.Ordinal)) { return Stale("Codex is asking for a different approval now"); }
            if (!string.Equals(CodexApprovalId.Of(marker), question.Id, StringComparison.Ordinal)) { return Stale("Codex's approval reads differently from what was shown"); }
            // The confirmation again, of the question answered: the fresh marker has just rebuilt to
            // exactly its id, so it is what the person confirmed (#958). What may have changed
            // meanwhile is the confirmation's own standing: a device taken off the list, or its lifetime.
            if (EscapedRefusal(question, request, confirmed) is { } unconfirmed) { return Refused(unconfirmed); }
            if (marker.Attempted) { return Refused("agent-ha-bridge has already typed an answer into this approval, and it is never typed again"); }

            if (ledger.Claim(attempt) is { } claimed) { return Refused(claimed); }
            typed = await TypeAsync(pid, keys, timeline);
        }
        finally { gate.DoneTyping(); }

        log($"typed {decision} into {Short(session.Id)} (pid {pid}): {keys.Describe()} -> {typed.Status} {typed.Detail}");
        if (Settle(typed, attempt) is { } settled) { return settled; }
        return Unconfirmed($"typed {decision}, but Codex records nothing that says how an approval was answered, so it cannot be confirmed");
    }

    // The session's process is still the live agent process and may be typed into.
    AnswerResult? Target(Session session, AgentKind agent)
    {
        if (session.ProcessId is not { } pid) { return Refused("the session's process is not known; answer at the machine"); }
        if (!processes.GetAgentProcesses(agent).Any(p => p.Id == pid)) { return Stale("the session's process has ended"); }
        return guard.Refuse(pid) is { } refused ? Refused(refused) : null;
    }

    // timeline: the delivery's, marking the worker's start and end, and typed once every key has
    // gone, before anything else is done (the log line writes a file). A later Enter (Claude's
    // review screen) moves only the end: typed is the first time.
    // One write. An answer typed in parts (a form with words, #467) passes the timeline to every
    // part: its injection runs from the first part's first key to the last part that typed
    // anything, stopped partway or not, and typed is raised by the final part alone.
    async Task<InjectionResult> TypeAsync(int pid, KeyPlan keys, DeliveryTimeline? timeline = null, bool final = true)
    {
        timeline?.InjectStart();
        InjectionResult result;
        try { result = await injector.InjectAsync(pid, keys); }
        catch (Exception e)
        {
            log($"the injector failed while typing into pid {pid}: {e.GetType().Name}: {e.Message}");
            result = new InjectionResult(InjectionStatus.Interrupted, DeliveryReasons.CouldNotDeliver);
        }
        timeline?.InjectEnd(sent: result.Status != InjectionStatus.NothingTyped);
        // A question holds the session, so it was not working.
        if (final && result.Status == InjectionStatus.Typed) { timeline?.Typed(false); }
        return result;
    }

    // What typing came to, when that is already the outcome: nothing typed (refused, and the
    // prompt may be answered again) or keys that may have landed (unconfirmed).
    AnswerResult? Settle(InjectionResult typed, string attempt)
    {
        switch (typed.Status)
        {
            case InjectionStatus.NothingTyped:
                ledger.Release(attempt);
                return Refused(DeliveryReasons.Of(typed.Detail));
            case InjectionStatus.Interrupted:
                return Unconfirmed(typed.Detail == DeliveryReasons.CouldNotDeliver ? typed.Detail : $"{typed.Detail}; part of the answer may have reached the session");
            default:
                return null;
        }
    }

    static AnswerResult Delivered(DeliveryTimeline timeline)
    {
        timeline.Confirmed();
        return new AnswerResult(AnswerOutcome.Delivered);
    }

    static string Name(AgentKind agent) => agent switch
    {
        AgentKind.Claude => "Claude",
        AgentKind.Codex => "Codex",
        _ => "Copilot",
    };

    static string Short(string id) => id.Length > 8 ? id[..8] : id;

    // What went wrong goes to the host's log only: the person is given fixed words, never an
    // exception's text (#802, #995).
    AnswerResult Failed(AnswerOutcome outcome, string what, Exception e)
    {
        log($"{what}: {e.GetType().Name}: {e.Message}");
        return new(outcome, DeliveryReasons.CouldNotDeliver);
    }

    // Typed, and nothing recorded: the keys reached the prompt, but whether they were submitted
    // is not known. The owner's form answer in #467 was exactly that, its last Enter taken as a
    // new line in the words.
    string NotRecorded => $"the answer was typed into the prompt, but the session did not record an answer within {options.ConfirmTimeout.TotalSeconds:0} seconds, so it may not have been submitted";

    static AnswerResult Refused(string reason) => new(AnswerOutcome.Refused, reason);
    static AnswerResult Stale(string reason) => new(AnswerOutcome.Stale, reason);
    static AnswerResult Busy() => new(AnswerOutcome.Busy, "another answer or reply to this session is being delivered");
    // Every unconfirmed answer was typed, at least in part; its stage is recorded only once the session
    // has recorded something for that very question.
    static AnswerResult Unconfirmed(string reason, AnswerStage stage = AnswerStage.Typed) => new(AnswerOutcome.Unconfirmed, reason, stage);
}

// macOS and Linux, until delivery through tmux exists: hello never offers sessions.answer,
// and an answer sent anyway is refused without touching anything.
public sealed class UnsupportedPlatformAnswerer : ISessionAnswerer
{
    public bool Available => false;

    public Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken cancellationToken = default, DeliveryWatch? watch = null, IEscapedConfirmation? confirmed = null) =>
        Task.FromResult(new AnswerResult(AnswerOutcome.Refused, "answering is not supported on this platform yet; answer at the machine"));
}
