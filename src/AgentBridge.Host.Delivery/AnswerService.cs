using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Discovery;
using AgentBridge.Host.Core.Parsing;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Delivery.Claude;
using AgentBridge.Host.Delivery.Codex;
using AgentBridge.Host.Delivery.Copilot;
using System.Security.Cryptography;
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
    // sender: who asked (the channel's peer, or the connector). Only the one who sent a prompt's
    // first answer is given its outcome again for the same answer under a new request (#1093).
    Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken cancellationToken = default, DeliveryWatch? watch = null, IEscapedConfirmation? confirmed = null, string? sender = null);
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

    // How long the screen is read again, when a read does not show the question's form at the
    // field the next keys go to, or the worker refused a write because the screen changed,
    // before the answer stops there (#1123). One read mid-redraw (streaming output, a spinner,
    // the conversation scrolling) was enough to refuse an answer or leave a form part answered.
    public TimeSpan FormRecheck { get; init; } = TimeSpan.FromSeconds(2);

    // How long a form left part answered waits, holding no keys, for the screen to show it again
    // at the field it stopped at, to finish it from there (#1123); then it is reset (ResetWait).
    // Zero leaves a part-answered form as it was left, for the person to finish at the machine.
    public TimeSpan ResumeWait { get; init; } = TimeSpan.FromSeconds(60);

    // How often the screen is read while a part-answered form waits to be resumed.
    public TimeSpan ResumePoll { get; init; } = TimeSpan.FromSeconds(1);

    // How long, after a part-answered form was cancelled with Esc, the session is given to ask
    // the same question again for the answer to be typed into it afresh. Not asked again by then,
    // the answer's card is retired as out of date.
    public TimeSpan ResetWait { get; init; } = TimeSpan.FromSeconds(60);

    // How long a request's outcome is returned again for a resend. The contract says at
    // least 10 minutes.
    public TimeSpan RememberFor { get; init; } = TimeSpan.FromMinutes(15);

    // How long Claude is given to record an answer before its prompt is taken to be on the
    // review screen that needs one more Enter (agent-ha-bridge's Complete-DaemonClaudeAnswer).
    public TimeSpan ClaudeReviewAfter { get; init; } = TimeSpan.FromMilliseconds(1500);

    // The host's own record of prompts already typed into (AttemptLedger), in its profile
    // folder; null keeps it in memory only.
    public string? AttemptLedgerPath { get; init; }
    public AttemptLedger? AttemptLedger { get; init; }

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
//     the AttemptLedger before the first key, and kept unless nothing was typed. The same
//     answer sent again under another request UUID is that request resent: it gets the first
//     outcome (read again if unconfirmed) and types nothing; a different answer is refused (#1093).
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
        ledger = options.AttemptLedger ?? new AttemptLedger(options.AttemptLedgerPath, options.Time, this.log, expire: false);
        requests = new OutcomeMemory<AnswerResult>(options.Time, options.RememberFor, this.gate, Busy,
            key => ledger.Outcome(key) is { } result ? (true, result) : (false, default!),
            (key, result, owner) => ledger.RecordOutcome(key, result, owner as AttemptLedger.Owner) is null ? result
                : ledger.HasClaim(key) ? Unconfirmed(DeliveryReasons.CouldNotDeliver) : Refused(DeliveryReasons.CouldNotDeliver));
        // The words of every answer whose session or question has gone go (after one last read of its
        // record): at start, as a session changes, and as an answer settles (its question may have
        // gone while it was typed, with no change to come).
        _ = SweepAsync();
        // A service built only to show it does no delivery work has no source to hear.
        if (source is not null) { source.Changed += (_, change) => _ = SweepAsync(change.Id); }
    }

    public bool Available => options.PlatformSupported && probe.Recent() is null;

    public bool ApprovesPermissions => Available && options.CopilotRemoteApproval;

    // Test-only: run once a call has registered as its prompt's first answer, and before it is
    // admitted to the request cache, to hold it there while another identical call arrives (#1100).
    internal Action? AfterRegistered { get; set; }

    public Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken cancellationToken = default, DeliveryWatch? watch = null, IEscapedConfirmation? confirmed = null, string? sender = null)
    {
        if (!Guid.TryParseExact(request.Request, "D", out var key))
        {
            return Task.FromResult(Refused("request must be a UUID"));
        }
        // The same answer to the same prompt under another request UUID (the phone reloaded, the
        // first one's record gone), from the same sender, is that first request sent again: it is
        // given the first outcome and nothing is typed (#1093). Its own UUID stays unknown. From
        // another sender, or saying anything else, it goes the usual way and is refused the same.
        // A request UUID belongs to the sender, session and question it was admitted for: any
        // other asking for it, whether the first's record is still held or not, is a request of
        // its own under a new UUID, so that no outcome, stage or progress is given to it.
        var prompt = (request.Id, request.Question);
        var by = new AttemptLedger.Owner(sender, request.Id, request.Question);
        Task<AnswerResult> AsAnother() => AnswerAsync(request with { Request = Guid.NewGuid().ToString("D") }, cancellationToken, watch, confirmed, sender);
        // Finding the prompt's first answer, registering this one as it, and admitting it to the
        // request cache are one step: a second identical call cannot land between them and start
        // a delivery of its own beside the registered one, so there is one delivery per prompt, a
        // later identical call joins it, and its outcome is saved against the right record.
        lock (firstGate)
        {
            var first = SameAnswerBefore(prompt, key, AnswerDigest.Of(request), sender, out var foreign);
            if (foreign) { return AsAnother(); }
            if (first is null) { AfterRegistered?.Invoke(); }
            if (first is not null)
            {
                log($"answer {key.ToString()[..8]} to {Short(request.Id)}: the outcome of {first.Request.ToString()[..8]}, the same answer, is returned and nothing is typed");
                if (!requests.Knows(first.Request)) { return ReadAgain(first, true); }
            }
            var flow = first?.Request ?? key;
            var outcome = requests.Once(flow, watch, () => new DeliveryTimeline(options.Time, "answer", flow, log),
                t => first is null ? Typing(key, () => DeliverAsync(request, key, t, confirmed, by)) : Task.FromResult(AsRetried(first.Last ?? Unconfirmed(DeliveryReasons.AnswerNotConfirmed), true)),
                result =>
                {
                    log($"answer {flow.ToString()[..8]} to {Short(request.Id)}: {result.Outcome.ToString().ToLowerInvariant()}{(result.Reason is null ? "" : $" ({result.Reason})")}");
                    Settled(prompt, flow, result);
                    if (first is null && result.Outcome == AnswerOutcome.Unconfirmed) { _ = Task.Run(() => SweepAsync(request.Id)); }
                },
                Recheck(flow), by, AsAnother, result => AsRetried(result, true));
            // Refused to start (the host is departing): nothing ran, so nothing is the first.
            if (first is null && !requests.Knows(key) && firstAnswers.TryGetValue(prompt, out var mine) && mine.Request == key && !mine.Restored) { firstAnswers.Remove(prompt); }
            return outcome;
        }
    }

    // The first answer's outcome once its request has been forgotten: an unconfirmed one is read
    // again from the session, as a resend within the request's memory is, so it turns delivered
    // when the session has recorded it since, however long after. Nothing is typed.
    async Task<AnswerResult> ReadAgain(FirstAnswer first, bool retry)
    {
        var last = first.Last ?? Unconfirmed(DeliveryReasons.AnswerNotConfirmed);
        Func<Task<AnswerResult?>>? read;
        lock (firstGate) { read = first.Read; }
        // The words it would be read from are the ledger's to keep: once they have gone (past an
        // exit, or lost to a delete that failed), nothing is read from memory either, so a retry is
        // told the same before a restart as after it; and what a last read made of it then stands.
        if (read is not null && !ledger.HoldsWords(first.Request))
        {
            lock (firstGate) { first.Read = read = null; }
            if (Further(ledger.ResultOf(first.Request), last) is { } stored)
            {
                lock (firstGate) { first.Last = stored; }
                last = stored;
            }
        }
        if (last.Outcome == AnswerOutcome.Unconfirmed && read is not null)
        {
            AnswerResult? later = null;
            try { later = await read(); }
            catch (Exception) { }
            if (later is not null)
            {
                lock (firstGate)
                {
                    first.Last = later;
                    if (later.Outcome == AnswerOutcome.Delivered) { first.Read = null; }
                }
                ledger.RecordReread(first.Request, later);
                last = later;
            }
        }
        return AsRetried(last, retry);
    }

    // What the ledger has since an unconfirmed outcome, when it says more: settled otherwise, or
    // recorded where it was only typed. Null when it says nothing more.
    static AnswerResult? Further(AnswerResult? stored, AnswerResult known) =>
        stored is null || known.Outcome != AnswerOutcome.Unconfirmed ? null
        : stored.Outcome != AnswerOutcome.Unconfirmed || (stored.Stage == AnswerStage.Recorded && known.Stage != AnswerStage.Recorded) ? stored
        : null;

    // Typed, and nothing recorded for it since: the first attempt's own words (what was typed
    // and what was held back, or what the session recorded instead) are not the retry's to say;
    // it only knows nothing was typed again. Whatever the stage, which still says how far the
    // first got.
    static AnswerResult AsRetried(AnswerResult result, bool retry) =>
        retry && result.Outcome == AnswerOutcome.Unconfirmed ? result with { Reason = DeliveryReasons.AnswerNotConfirmed } : result;

    // What each prompt's first answer was, by session and question: kept as long as the ledger
    // keeps the prompt's claim. A host that restarts finds it again in the ledger, which since
    // #1159 slice 0 keeps its sender, digest and outcome, and while it is unconfirmed what reading
    // it again needs (Restore).
    readonly Dictionary<(string Session, string Question), FirstAnswer> firstAnswers = [];
    readonly Lock firstGate = new();

    sealed class FirstAnswer(Guid request, byte[] digest, string? sender, DateTimeOffset at)
    {
        public Guid Request { get; } = request;
        public byte[] Digest { get; } = digest;
        // Who sent it (the channel's peer, or the connector): only that sender is given its
        // outcome for the same answer, so no other can learn what a first answer said by
        // guessing it under a new request.
        public string? Sender { get; } = sender;
        public DateTimeOffset At { get; } = at;
        // What it came to once typed: delivered or unconfirmed, and as read again since.
        public AnswerResult? Last;
        // How to read the session's record of that same call again, without typing, while it
        // is unconfirmed; kept as long as the answer is.
        public Func<Task<AnswerResult?>>? Read;
        // Found in the ledger after a restart rather than admitted by this host.
        public bool Restored;
    }

    // The prompt's first answer as the ledger keeps it, for a host that has none in memory (it
    // restarted): its outcome (unconfirmed, if the host stopped before recording one), and while
    // its words are held, a read of its record again with the plan rebuilt from them.
    void Restore((string Session, string Question) prompt)
    {
        if (firstAnswers.ContainsKey(prompt) || ledger.FirstFor(prompt.Session, prompt.Question) is not { } kept) { return; }
        var first = new FirstAnswer(kept.Request, kept.Digest, kept.By.Sender, kept.At)
        {
            Last = kept.Result ?? Unconfirmed(AttemptLedger.NotRecorded),
            Restored = true,
        };
        if (kept is { Reread: { } at, Words: { } words } && Reader(kept.By, at, words, kept.Request) is { } read)
        {
            first.Read = read;
            rechecks[kept.Request] = (read, options.Time.GetUtcNow());
        }
        firstAnswers[prompt] = first;
    }

    // Reads the session's record of a held answer again from what the ledger keeps: the agent,
    // transcript, call and offset, and the plan rebuilt from the answer's words, as the first
    // delivery built it. Copilot forms and Claude only; nothing else is read again.
    Func<Task<AnswerResult?>>? Reader(AttemptLedger.Owner by, AttemptLedger.Reread at, AnswerWords.Words words, Guid request)
    {
        var asked = new AnswerRequest { Id = by.Session, Question = by.Question, Request = request.ToString("D"), Text = words.Text, Fields = words.Fields };
        var timeline = new DeliveryTimeline(options.Time, "answer", request, log);
        switch (at.Agent)
        {
            case "copilot":
                var seen = new System.Runtime.CompilerServices.StrongBox<DateTimeOffset?>();
                return async () =>
                {
                    if (CopilotTranscript.Find(at.Transcript, at.Call).Arguments is not { } arguments
                        || CopilotForm.Read(arguments).Form is not { } form
                        || CopilotPlanner.Plan(form, asked).Plan is not { } plan) { return null; }
                    return await LateCopilotAsync(at.Transcript, at.Call, at.Offset, plan, seen, timeline);
                };
            case "claude":
                return () =>
                {
                    if (ClaudeTranscript.Find(at.Transcript, at.Call).Input is not { } input
                        || ClaudeForm.Read(input).Form is not { } form
                        || ClaudePlanner.Plan(form, asked).Plan is not { } plan) { return Task.FromResult<AnswerResult?>(null); }
                    return Task.FromResult(LateClaude(at.Transcript, at.Call, at.Offset, plan, timeline));
                };
            default:
                return null;
        }
    }

    // The words of a held answer whose session, or the question they were typed into, has gone:
    // its record is read one last time, so an answer the session recorded still settles (delivered,
    // or recorded), and then the words go (Trust's condition 3). Not while this host is still
    // typing or watching it: its question may be out of the listing for a moment then. A claim
    // left from before a restart is never in flight here.
    async Task SweepAsync(string? only = null)
    {
        try
        {
            foreach (var (key, request, by, at) in ledger.Holding())
            {
                if (typing.ContainsKey(request) || (only is not null && by.Session != only)) { continue; }
                if (source.GetForDelivery(by.Session)?.Question?.Id is { } asked && (asked == at.Call || asked == by.Question)) { continue; }
                if (ledger.WordsOf(key) is { } words && Reader(by, at, words, request) is { } read)
                {
                    AnswerResult? later = null;
                    try { later = await read(); }
                    catch (Exception e) { log($"the last read of answer {request.ToString()[..8]} to {Short(by.Session)} failed: {e.GetType().Name}"); }
                    if (later is not null)
                    {
                        ledger.RecordReread(request, later);
                        lock (firstGate)
                        {
                            foreach (var first in firstAnswers.Values.Where(f => f.Request == request)) { first.Last = later; }
                        }
                    }
                }
                ledger.Cancel(key);
            }
        }
        catch (Exception e) { log($"the words of unconfirmed answers could not be swept: {e.GetType().Name}"); }
    }

    // The requests this host is delivering: typed, or being watched for their record.
    readonly System.Collections.Concurrent.ConcurrentDictionary<Guid, byte> typing = new();

    async Task<AnswerResult> Typing(Guid request, Func<Task<AnswerResult>> deliver)
    {
        typing[request] = 0;
        try { return await deliver(); }
        finally { typing.TryRemove(request, out _); }
    }

    // The prompt's first answer when this request, from the same sender, says the same, to be
    // given its outcome (this request's own UUID included, once the request's memory has
    // forgotten it); null when this is the prompt's first request, a resend the request's own
    // memory answers, or another sender or a different answer, which goes the usual way and is
    // refused by the ledger once anything was typed: the same refusal for both, so a guess
    // cannot be told from a miss. A request seen first is recorded as the prompt's first.
    FirstAnswer? SameAnswerBefore((string Session, string Question) prompt, Guid key, byte[] digest, string? sender, out bool foreign)
    {
        foreign = false;
        lock (firstGate)
        {
            var now = options.Time.GetUtcNow();
            foreach (var old in firstAnswers.Where(a => now - a.Value.At > AttemptLedger.Keep).Select(a => a.Key).ToList()) { firstAnswers.Remove(old); }
            Restore(prompt);
            // A UUID is the sender's, for the prompt it was first admitted for, whatever question
            // or sender comes with it now and however long ago the request's own record went.
            foreach (var (admitted, owner) in firstAnswers)
            {
                if (owner.Request == key && (admitted != prompt || !string.Equals(owner.Sender, sender, StringComparison.Ordinal))) { foreign = true; return null; }
            }
            // And after a restart, as the ledger has it: its receipt or claim is the owner's alone,
            // and one from before #1159, whose owner is not known, is nobody's (#1159 slice 0).
            if (!requests.Knows(key) && ledger.Known(key, out var by)
                && (by is null || by.Session != prompt.Session || by.Question != prompt.Question || !string.Equals(by.Sender, sender, StringComparison.Ordinal)))
            {
                foreign = true;
                return null;
            }
            if (firstAnswers.TryGetValue(prompt, out var known))
            {
                // Both compared, whatever the first says, and the digests in constant time.
                var sameSender = string.Equals(known.Sender, sender, StringComparison.Ordinal);
                var same = CryptographicOperations.FixedTimeEquals(known.Digest, digest) & sameSender;
                // The request's own receipt, still remembered after a restart, answers its resend
                // itself, as it did before #1159 (OutcomeMemory reads it again if unconfirmed).
                if (known.Request == key) { return known.Last is not null && !requests.Knows(key) && !(known.Restored && ledger.Outcome(key) is not null) ? known : null; }
                if (!same) { return null; }
                // Still known, or settled and forgotten with its outcome kept.
                if (requests.Knows(known.Request) || known.Last is not null) { return known; }
                // Never run, or forgotten before its outcome was kept: no longer the first.
                firstAnswers.Remove(prompt);
            }
            if (!requests.Knows(key)) { firstAnswers[prompt] = new FirstAnswer(key, digest, sender, now); }
            return null;
        }
    }

    // A first answer that was typed keeps its outcome, and as it is read again; one that was not
    // (refused, stale, busy) leaves the prompt open to be answered afresh.
    void Settled((string Session, string Question) prompt, Guid flow, AnswerResult result)
    {
        lock (firstGate)
        {
            if (!firstAnswers.TryGetValue(prompt, out var known) || known.Request != flow) { return; }
            if (result.Outcome is AnswerOutcome.Delivered or AnswerOutcome.Unconfirmed) { known.Last = result; } else { firstAnswers.Remove(prompt); }
        }
    }

    // A resend of a request already settled unconfirmed: what the session has recorded since,
    // read without typing anything, for the same question's own call. So an answer the person
    // submitted at the machine, or the session recorded late, comes back delivered, and one it
    // recorded differently, stage recorded; nothing is ever typed twice, and a newer question is
    // never touched. Null for anything else, which gets its first outcome again.
    Func<AnswerResult, Task<AnswerResult?>> Recheck(Guid key) => async first =>
    {
        if (first.Outcome != AnswerOutcome.Unconfirmed) { return null; }
        // Its words gone from the ledger (past an exit, or lost to a delete that failed), nothing
        // is read from memory either; what a last read made of it then stands (ReadAgain).
        if (!ledger.HoldsWords(key))
        {
            rechecks.TryRemove(key, out _);
            return Further(ledger.ResultOf(key), first);
        }
        if (!rechecks.TryGetValue(key, out var entry)) { return null; }
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
        var key = Guid.Parse(request.Request);
        rechecks[key] = (read, now);
        // The prompt's first answer keeps it for as long as it keeps the outcome, past the
        // request's own memory, so a retry long after still reads the record again.
        lock (firstGate)
        {
            if (firstAnswers.TryGetValue((request.Id, request.Question), out var known) && known.Request == key) { known.Read = read; }
        }
    }

    async Task<AnswerResult> DeliverAsync(AnswerRequest request, Guid key, DeliveryTimeline timeline, IEscapedConfirmation? confirmed, AttemptLedger.Owner by)
    {
        try
        {
            if (!options.PlatformSupported) { return Refused("answering is not supported on this platform yet; answer at the machine"); }
            if (probe.Active() is { } active) { return Refused(active); }
            var session = source.GetForDelivery(request.Id);
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
                    AgentKind.Claude => await ClaudeAsync(session, question, request, timeline, by),
                    AgentKind.Codex => await CodexAsync(session, question, request, timeline, confirmed, by),
                    _ when permission => await CopilotPermissionAsync(session, question, request, timeline, confirmed, by),
                    _ => await CopilotAsync(session, question, request, timeline, by),
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

    // Every answer to a Copilot form leaves one line in the log saying how it went
    // (CopilotAnswerTrace), whatever it ended in, the screen reads it made counted against it.
    async Task<AnswerResult> CopilotAsync(Session session, Question question, AnswerRequest request, DeliveryTimeline timeline, AttemptLedger.Owner by)
    {
        var trace = new CopilotAnswerTrace(options.Time, request.Request, session.Id);
        CopilotAnswerTrace.Current.Value = trace;
        AnswerResult? result = null;
        try { return result = await CopilotFormAsync(session, question, request, timeline, trace, by); }
        finally
        {
            CopilotAnswerTrace.Current.Value = null;
            trace.Stage("confirmed");
            log(trace.Line(result));
        }
    }

    async Task<AnswerResult> CopilotFormAsync(Session session, Question question, AnswerRequest request, DeliveryTimeline timeline, CopilotAnswerTrace trace, AttemptLedger.Owner by, bool redo = false)
    {
        // The session id names a folder; only the agent's own id shape is accepted.
        if (!Guid.TryParseExact(session.Id, "D", out _)) { return Refused("the session's id is not a Copilot session id"); }
        var transcript = Path.Combine(options.CopilotHome, "session-state", session.Id, "events.jsonl");
        trace.Cli(CopilotAnswerTrace.CliOf(transcript));
        if (Target(session, AgentKind.Copilot) is { } unfit) { return unfit; }
        var pid = session.ProcessId!.Value;

        // One answer typed at a time, across sessions, and the slot is taken before the last
        // checks: an answer that waited behind another must find its question still asked,
        // and agent-ha-bridge still stopped, when its own turn comes, not when it arrived.
        timeline.Checked();
        // Why the question can no longer be typed into, or null: the last checks before the first
        // key, made again before each later write of a plan typed in parts (TypeWordsApartAsync).
        // Between them the host waits on the screen, and the person may answer at the machine,
        // or a permission prompt open over the form, in the meantime. The prompts waiting are kept
        // up to date from what the transcript gains, not read again from its start each time
        // (Copilot 4478363669): these checks run with the typing slot every session shares held.
        var waiting = new CopilotPermissionWatch(transcript, "");
        // The question's call, likewise: found and validated once, before the first key, then
        // read only from where that read stopped, for an answer or the session's end (Copilot
        // 4479319394; searched from the end before every write, it read up to the whole
        // transcript, twice a write, with the slot held). A transcript that cannot be read, or
        // is not the file the call was found in (shorter, another file, or other bytes where it
        // was read: JsonlMark, Copilot 4484702591), stops it.
        CopilotCallWatch? asked = null;
        string? StillAsked()
        {
            // A check that fails once keys have gone stops the answer there, as any other reason
            // does: reported typed and unconfirmed, and read back later. Thrown on, it reached
            // DeliverAsync's catch, which says nothing was typed (Copilot 4479319354).
            try { return Asked(); }
            catch (Exception e)
            {
                log($"the check of {Short(session.Id)} between a form's writes failed: {e.GetType().Name}: {e.Message}");
                return "the host could not check the session again";
            }
        }
        string? Asked()
        {
            if (probe.Active() is { } active) { return active; }
            if (source.GetForDelivery(session.Id)?.Question is not { } listed || listed.Id != question.Id) { return "the session is no longer asking that question"; }
            if (listed.AnswerAtMachine == true) { return AtMachine(session.Agent, listed); }
            if (asked is null) { return "the question was not found in the session's transcript"; }
            var now = asked.Read();
            if (!now.Readable) { return "the session's transcript could no longer be read, or was replaced or cut short"; }
            if (now.Answer is not null || now.SessionEnded) { return "the question was answered or closed while the answer was being typed"; }
            var prompts = waiting.Read();
            if (!prompts.Readable) { return "the session's transcript could no longer be read"; }
            // Another file in its place, read again from its start: what it says is not what
            // the answer was checked against (Copilot 4484702591).
            if (prompts.Replaced) { return "the session's transcript was replaced while the answer was being typed"; }
            if (prompts.PendingPermissions > 0) { return AnotherPrompt; }
            // Any other ask, by its call id (a delegate's, opened since the first key): its form
            // can read the same as this one, and the session's listing keeps this one's id, so
            // the screen checks would pass and the rest of the keys go to that call. Counted, one
            // ask closing as another opened passed (Copilot 4479319296, Trust's re-review).
            if (prompts.Asks!.Any(id => id != question.Id)) { return "Copilot asked another question while the answer was being typed"; }
            // And this one still waiting: a turn ended on an error closes it with no answer or
            // end of the session on its call, before the listing or the screen says so (Copilot
            // 4480250639).
            if (!prompts.Asks!.Contains(question.Id)) { return "the question was closed while the answer was being typed"; }
            return Target(session, AgentKind.Copilot)?.Reason;
        }

        await gate.WaitToTypeAsync();
        trace.Stage("slot");
        timeline.Turn();
        CopilotPlan plan;
        CopilotForm form;
        System.Text.Json.JsonElement original;
        long from;
        InjectionResult typed;
        string? held;
        string? partial;
        var progress = new FormProgress();
        var attempt = AttemptLedger.Key("copilot", session.Id, question.Id);
        try
        {
            // Immediately before typing: agent-ha-bridge again, then the question itself, by
            // its tool call id in the transcript, never by how recent anything looks.
            if (probe.Active() is { } active) { return Refused(active); }
            if (source.Get(session.Id)?.Question is not { } listed || listed.Id != question.Id) { return Stale("the session is no longer asking that question"); }
            // Marked for the machine while this waited for the slot (a delegate's question opened
            // beside it, say): as it would have been refused on arrival (Trust's #510 re-review).
            if (listed.AnswerAtMachine == true) { return Refused(AtMachine(session.Agent, listed)); }
            var call = CopilotTranscript.Find(transcript, question.Id);
            if (call.Arguments is not { } arguments) { return Stale("the session's transcript no longer shows that question"); }
            if (call.Resumed && call.Answer is null) { return Stale("the session was resumed in a new Copilot CLI since it asked that question; answer at the machine"); }
            if (call.Answer is not null || call.SessionEnded) { return Stale("the question has already been answered"); }
            if (!call.IsMainAgent) { return Refused("the question was asked by a subagent; answer at the machine"); }
            asked = new CopilotCallWatch(transcript, question.Id, call.ScannedTo);
            // Keys go to whichever prompt the CLI shows, and a permission dialog (a sub-agent's,
            // say) can be shown over this question, which is the one listed (#173). Read just
            // before typing, since the host's view may be a pass behind.
            var prompts = waiting.Read();
            if (!prompts.Readable) { return Refused("the session's transcript could not be read, so nothing is typed; answer at the machine"); }
            if (prompts.PendingPermissions > 0 || prompts.Asks!.Any(id => id != question.Id))
            {
                return Refused("Copilot has more than one prompt waiting, and the screen shows only one of them; answer at the machine");
            }
            // Closed with no record on its call, as a turn that ended on an error closes it (Copilot
            // 4480250639): the listing may not have caught up.
            if (!prompts.Asks!.Contains(question.Id)) { return Stale("the session is no longer asking that question"); }

            var (read, unreadable) = CopilotForm.Read(arguments);
            if (read is null) { return Refused(unreadable!); }
            (form, original) = (read, arguments);
            trace.Form(form);
            // An ask_user with no form schema: how Copilot CLI 1.0.105 draws it cannot be told
            // from its code (its elicitations require a schema), so there is nothing to hold the
            // keys to, and they are not typed blind (#510 review).
            if (form.Fields.Count == 0) { return Refused("this question has no form the host can recognise on the session's screen; answer it at the machine"); }
            if (CopilotFormScreen.Ambiguous(form) is { } alike) { return Refused(alike); }
            if (!form.Matches(question)) { return Refused("the question's form reads differently here than the client was shown; answer at the machine"); }
            var (planned, problem) = CopilotPlanner.Plan(form, request);
            if (planned is null) { return Refused(problem!); }
            // Other's words go into a box the session draws apart from the form, which no key here
            // is held to: refused before any key, as the listing (Other unavailable) already says;
            // Answers.Check refuses it first for a field listed so, and this for one listed before.
            if (CopilotPlanner.ThroughOther(form, planned))
            {
                return Refused(AnswerReasons.Text(AnswerReasons.OtherNotSendable));
            }
            (plan, from) = (planned, call.ScannedTo);

            if (Claim(attempt, request, by, new("copilot", transcript, question.Id, from)) is { } claimed) { return Refused(claimed); }
            (typed, held, partial) = await TypeWordsApartAsync(session.Id, pid, form, plan.Keys, StillAsked, timeline, progress);
            trace.Stage("typed");
        }
        finally { gate.DoneTyping(); }

        log($"typed into {Short(session.Id)} (pid {pid}): {plan.Keys.Describe()} -> {typed.Status} {typed.Detail}{(held is null ? "" : $"; held before its Enter: {held}")}");
        var seen = new System.Runtime.CompilerServices.StrongBox<DateTimeOffset?>();
        Task<AnswerResult?> Late() => LateCopilotAsync(transcript, question.Id, from, plan, seen, timeline);
        // Stopped part answered, the form is not left so if the host can help it (#1123): it is
        // finished from the field it stopped at once the screen shows it there again, or else
        // cancelled and answered afresh when the session asks the same question again, or the
        // answer's card is retired as out of date (RecoverAsync).
        if (options.ResumeWait > TimeSpan.Zero && (typed.Status == InjectionStatus.Interrupted || (typed.Status == InjectionStatus.Typed && held is not null)))
        {
            Recovered recovered;
            try { recovered = await RecoverAsync(typed, held); }
            catch (Exception e)
            {
                log($"recovering the part-answered form in {Short(session.Id)} failed: {e.GetType().Name}: {e.Message}");
                trace.Resume(CopilotAnswerTrace.ResumeHeld);
                recovered = new(typed, held, null, false);
            }
            if (recovered.Result is { } final)
            {
                if (!recovered.Redone && final.Outcome == AnswerOutcome.Unconfirmed) { Recheckable(request, Late); }
                return final;
            }
            (typed, held) = (recovered.Typed, recovered.Held);
            partial = held is not null && progress.Taken > 0 ? progress.Said(form) : null;
        }
        AnswerResult result;
        if (Settle(typed, attempt) is { } settled) { result = settled; }
        else if (held is not null)
        {
            // Held before an Enter, perhaps because the question was answered at the machine
            // meanwhile: what the session has recorded says so, if anything.
            AnswerResult? recorded;
            try { recorded = await Late(); }
            catch (Exception) { recorded = null; }
            result = recorded ?? Unconfirmed(partial is not null
                ? $"the answer was typed into the prompt but not submitted whole: {partial}, and nothing more was pressed ({held}); finish it at the machine"
                : $"the answer was typed into the prompt but not submitted: {held}, so its Enter was not pressed; finish it at the machine");
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

        // A form left part answered (#1123; docs/host-answers.md, "A form left part answered").
        // First it is resumed: holding no keys and not the typing slot, the screen is read every
        // ResumePoll for up to ResumeWait, and only when it shows this very form at the field the
        // keys stopped at, the question still asked and no other prompt open, does anything more
        // go, from that field on (TypeFieldsAsync): no field is typed twice, and one whose keys
        // went gets only what follows them, once it shows exactly what they left in it. Then, if
        // it cannot be, the form is reset: one Esc, held to rows that show this very form, its
        // "esc cancel" hint among them, and nothing else (not even that) when the screen shows
        // anything else. Cancelled, the old call positively closed, the session may ask the same
        // question again: if it does within ResetWait, its arguments identical to the ones
        // answered in order and in every detail, the answer is typed into it afresh, once, every
        // check made again from its first field, for this same card; a different question, or
        // none, retires the card as out of date, with nothing typed. A form not seen to close is
        // left unconfirmed, for the person at the machine.
        async Task<Recovered> RecoverAsync(InjectionResult typedFirst, string? heldFirst)
        {
            var (went, stop) = (typedFirst, heldFirst);
            var started = options.Time.GetTimestamp();
            var resumes = 0;
            // The whole wait is held whatever the first pass left: a form cut short (Interrupted),
            // or a field with nothing to verify it by, is not resumed, but is still given the wait
            // before the Esc, for the person to finish it at the machine or the session to close it.
            while (resumes < MaxResumes && options.Time.GetElapsedTime(started) < options.ResumeWait)
            {
                await Task.Delay(options.ResumePoll, options.Time);
                await gate.WaitToTypeAsync();
                try
                {
                    if (Waits() is { } why)
                    {
                        // A permission prompt over the form may be answered meanwhile; anything else
                        // means the question is no longer there to finish.
                        if (why == AnotherPrompt) { continue; }
                        trace.Resume(CopilotAnswerTrace.ResumeGone);
                        return new(went, why, null, false);
                    }
                    if (went.Status != InjectionStatus.Typed || !progress.Resumable || progress.Taken >= form.Fields.Count) { continue; }
                    // Only a screen showing this form at that field is resumed into; any other
                    // waits for the next read.
                    if (await ReadRowsAsync(pid) is not { } rows || CopilotFormScreen.Showing(rows, form, Math.Min(progress.Taken, form.Fields.Count - 1)) is not null) { continue; }
                    var field = progress.Taken + 1;
                    var (again, stopped, _) = await TypeWordsApartAsync(session.Id, pid, form, plan.Keys, StillAsked, timeline, progress);
                    // Nothing typed (the field not yet drawn as it was left, say): read again later.
                    if (again.Status == InjectionStatus.NothingTyped) { continue; }
                    trace.Resumed();
                    log($"resumed the part-answered form in {Short(session.Id)} (pid {pid}) at field {field} of {form.Fields.Count} -> {again.Status}{(stopped is null ? "" : $"; held: {stopped}")}");
                    if (again.Status == InjectionStatus.Typed && stopped is null)
                    {
                        trace.Resume(CopilotAnswerTrace.ResumeResumed);
                        return new(again, null, null, false);
                    }
                    if (again.Status == InjectionStatus.Interrupted) { went = again; }
                    else if (again.Status == InjectionStatus.Typed) { (stop, resumes) = (stopped, resumes + 1); }
                }
                finally { gate.DoneTyping(); }
            }
            // Resetting once: a form already answered afresh is never reset again.
            if (redo)
            {
                trace.Resume(CopilotAnswerTrace.ResumeHeld);
                return new(went, stop, null, false);
            }
            await gate.WaitToTypeAsync();
            InjectionResult escaped;
            try
            {
                if (Waits() is { } why)
                {
                    trace.Resume(why == AnotherPrompt ? CopilotAnswerTrace.ResumeHeld : CopilotAnswerTrace.ResumeGone);
                    return new(went, why, null, false);
                }
                var rows = await ReadRowsAsync(pid);
                // Esc only where the hints say it cancels: on Copilot CLI 1.0.106 a text or number
                // field holding words says "esc clear", and Esc there empties the field instead.
                if (rows is null || !Enumerable.Range(0, form.Fields.Count).Any(i => CopilotFormScreen.Cancels(rows, form, i)))
                {
                    log($"the part-answered form in {Short(session.Id)} (pid {pid}) is not on its screen to be reset by esc (gone, or esc would only clear a field); nothing is pressed");
                    trace.Resume(CopilotAnswerTrace.ResumeHeld);
                    return new(went, stop ?? "the screen did not show the form again", null, false);
                }
                escaped = await TypeAsync(pid, new KeyPlan([new KeySend(KeyPlan.Escape, false, 0, EscapePauseMs, Rows(rows), Below(rows))]) { Proceed = StillAsked }, timeline, false);
            }
            finally { gate.DoneTyping(); }
            log($"reset the part-answered form in {Short(session.Id)} (pid {pid}): esc -> {escaped.Status} {escaped.Detail}");
            if (escaped.Status != InjectionStatus.Typed)
            {
                trace.Resume(CopilotAnswerTrace.ResumeHeld);
                return new(went, stop ?? "the screen changed before it could be reset", null, false);
            }
            // The session asking again, once the cancelled call is positively closed in its
            // transcript (Closed): the same question, its arguments identical to the ones answered
            // in order and in every detail (Identical), takes the answer afresh; anything else
            // retires this card.
            var since = options.Time.GetTimestamp();
            while (options.Time.GetElapsedTime(since) < options.ResetWait)
            {
                await Task.Delay(options.PollInterval, options.Time);
                if (source.Get(session.Id)?.Question is not { } listed || listed.Id == question.Id || !Closed()) { continue; }
                if (listed.Kind == question.Kind && CopilotTranscript.Find(transcript, listed.Id).Arguments is { } reasked
                    && Identical(reasked, original))
                {
                    log($"the session {Short(session.Id)} asked the reset question again; the answer is typed into it afresh");
                    // Said before the redo, so what became of the redo is kept with it.
                    trace.Resume(CopilotAnswerTrace.ResumeRedone);
                    var redone = await CopilotFormAsync(session, listed, request, timeline, trace, by, redo: true);
                    return new(went, null, redone, true);
                }
                trace.Resume(CopilotAnswerTrace.ResumeReplaced);
                return new(went, null, Stale(Retired("the session has asked something else since")), false);
            }
            // Out of date only once the form is positively gone; a form still asked, or one a
            // permission prompt is drawn over, or one the host cannot tell closed, was not
            // cancelled, and is left for the person.
            if (!Closed())
            {
                trace.Resume(CopilotAnswerTrace.ResumeUncancelled);
                return new(went, null, Unconfirmed($"the answer was typed into the prompt but not submitted whole: {progress.Said(form)}; the host pressed Esc to cancel it and it was not seen to close, so nothing more is pressed; finish it at the machine"), false);
            }
            trace.Resume(CopilotAnswerTrace.ResumeStale);
            return new(went, null, Stale(Retired("the session did not ask it again")), false);
        }

        // What keeps a part-answered form from being resumed or reset now: AnotherPrompt while
        // the transcript records a permission prompt pending over this very call, still open and
        // the only question asked (Codex review on a174e20b). Refreshed, the session's listing
        // then shows that approval as its question, or this form as answered at the machine,
        // both of which StillAsked reads as the question gone, abandoning the recovery for good;
        // once the prompt is answered the listing shows the form again. Otherwise StillAsked.
        string? Waits()
        {
            try
            {
                if (probe.Active() is null && asked is not null && asked.Read() is { Readable: true, Answer: null, SessionEnded: false }
                    && waiting.Read() is { Readable: true, Replaced: false, PendingPermissions: > 0 } prompts
                    && prompts.Asks!.Contains(question.Id) && prompts.Asks!.All(id => id == question.Id))
                {
                    return AnotherPrompt;
                }
            }
            catch (Exception e)
            {
                log($"the check of {Short(session.Id)} for a prompt over its form failed: {e.GetType().Name}: {e.Message}");
            }
            return StillAsked();
        }

        // Whether the transcript positively says this question's call is over: answered, the
        // session ended, or the call closed. False whenever it cannot be read, or was replaced.
        bool Closed()
        {
            try
            {
                if (asked is null) { return false; }
                var call = asked.Read();
                if (!call.Readable) { return false; }
                if (call.Answer is not null || call.SessionEnded) { return true; }
                var prompts = waiting.Read();
                return prompts.Readable && !prompts.Replaced && !prompts.Asks!.Contains(question.Id);
            }
            catch (Exception e)
            {
                log($"the check of {Short(session.Id)} for its reset form failed: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }
    }

    // A form left part answered, after RecoverAsync: as typing left it (Typed, Held), or settled
    // (Result), Redone when that is the outcome of the answer typed afresh into the question asked again.
    readonly record struct Recovered(InjectionResult Typed, string? Held, AnswerResult? Result, bool Redone);

    // A pending permission prompt opened over the form (Asked): a resume waits for it to go.
    const string AnotherPrompt = "Copilot opened another prompt while the answer was being typed";

    // How many times a part-answered form is resumed, each typing more of it, before it is reset.
    const int MaxResumes = 3;

    // After the Esc that cancels a part-answered form, before anything else is read.
    const int EscapePauseMs = 200;

    static string Retired(string why) =>
        $"this question is out of date: the answer was left part answered at the machine, so the host cancelled the form there, and {why}";

    // Whether two question arguments are the same in every detail and in order (#1123 review):
    // an object's properties are its form's fields, in the order the form asks them, so a re-ask
    // with the same fields reordered is a different form (JsonElement.DeepEquals ignores order).
    // Strings compare ordinally, numbers by their exact text.
    internal static bool Identical(System.Text.Json.JsonElement a, System.Text.Json.JsonElement b)
    {
        if (a.ValueKind != b.ValueKind) { return false; }
        switch (a.ValueKind)
        {
            case System.Text.Json.JsonValueKind.Object:
                var left = a.EnumerateObject().ToList();
                var right = b.EnumerateObject().ToList();
                return left.Count == right.Count
                    && left.Zip(right).All(p => string.Equals(p.First.Name, p.Second.Name, StringComparison.Ordinal) && Identical(p.First.Value, p.Second.Value));
            case System.Text.Json.JsonValueKind.Array:
                var these = a.EnumerateArray().ToList();
                var those = b.EnumerateArray().ToList();
                return these.Count == those.Count && these.Zip(those).All(p => Identical(p.First, p.Second));
            case System.Text.Json.JsonValueKind.String:
                return string.Equals(a.GetString(), b.GetString(), StringComparison.Ordinal);
            case System.Text.Json.JsonValueKind.Number:
                return string.Equals(a.GetRawText(), b.GetRawText(), StringComparison.Ordinal);
            default:
                return true;
        }
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
    //
    // Stopped after one or more fields took their Enter, the form is left part answered, and
    // Partial says how far it got, for the person finishing it (#510 review, Copilot
    // 5780329899). Every later field's title is vouched for before the first key, by the tab
    // bar that names them all (CopilotFormScreen), but a later field can still fail to show: a
    // permission prompt opening over it, or its heading scrolled off a short window by a long
    // field.
    async Task<(InjectionResult Result, string? Held, string? Partial)> TypeWordsApartAsync(string sessionId, int pid, CopilotForm form, KeyPlan plan, Func<string?> stillAsked, DeliveryTimeline? timeline = null, FormProgress? progress = null)
    {
        progress ??= new FormProgress();
        var (result, held) = await TypeFieldsAsync(sessionId, pid, form, plan, stillAsked, progress, timeline);
        CopilotAnswerTrace.Current.Value?.Taken(progress.Taken);
        return (result, held, held is not null && progress.Taken > 0 ? progress.Said(form) : null);
    }

    // How far a form's keys got: the fields that took their Enter, and whether any key has gone
    // into the one after them. Kept in memory only, for the delivery that typed them, and never
    // the answer's words: a resume reads the words again from the request it is part of.
    sealed class FormProgress
    {
        public int Taken;
        public bool Partway;
        // What the field after Taken was left showing, for a resume to find on screen before its
        // Enter goes (#1123): its input as drawn once a walk through its options had settled
        // (Walked), or that words were typed into it (Words), which then must show there whole,
        // exactly as typed (CopilotFormScreen.Holds, read from the plan, never kept apart from it).
        // Unknown once a write may have gone in part, or a walk went and the screen did not settle
        // from it: that field can then only be reset, never resumed.
        public IReadOnlyList<string>? Walked;
        public bool Words;
        public bool Unknown;

        // Partway, and nothing on record to verify it by.
        public bool Resumable => !Unknown && (!Partway || Walked is not null || Words);

        public void Committed(int taken) => (Taken, Partway, Walked, Words) = (taken, false, null, false);

        public string Said(CopilotForm form)
        {
            string Name(CopilotField f) => $"\"{f.Label ?? f.Name}\"";
            var rest = Taken >= form.Fields.Count ? ""
                : Partway ? $"; {Name(form.Fields[Taken])} was typed into but not submitted, and no field after it was reached"
                : "; the rest were not typed";
            return $"only the form's first {Taken} of {form.Fields.Count} fields were answered ({string.Join(", ", form.Fields.Take(Taken).Select(Name))}){rest}";
        }
    }

    async Task<(InjectionResult Result, string? Held)> TypeFieldsAsync(string sessionId, int pid, CopilotForm form, KeyPlan plan, Func<string?> stillAsked, FormProgress progress, DeliveryTimeline? timeline)
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
        // The rows the last write that went was held to: the screen it went into.
        IReadOnlyList<string> wrote = [];
        // Stopped here: nothing more goes. Held once anything has been typed; nothing typed, and
        // the prompt free to be answered again, otherwise.
        (InjectionResult, string?) Stop(string why) => typedAny
            ? (new InjectionResult(InjectionStatus.Typed, "ok"), why)
            : (new InjectionResult(InjectionStatus.NothingTyped, why.Contains("at the machine", StringComparison.Ordinal) ? why : $"{why}; answer at the machine"), null);
        // One write, still asked for once the injector holds the typing slot, held to rows: the
        // worker types nothing of it unless the screen still reads exactly so. A write it refused
        // that way, nothing of it typed, is tried again on the screen read anew, but only once
        // that shows the form at the same field with the field itself drawn as it was (SameField):
        // what changed was elsewhere on the screen (output scrolling above the form, a spinner),
        // and the keys do the same there. Refused for that once, a form was left part answered
        // (#1123). A write that typed nothing otherwise (its question no longer held) stops it as
        // above; one cut short is interrupted. Null when it went.
        async Task<(InjectionResult, string?)?> WriteAsync(List<KeySend> sends, bool last, IReadOnlyList<string> rows)
        {
            var started = options.Time.GetTimestamp();
            while (true)
            {
                if (stillAsked() is { } gone) { return Stop(gone); }
                sends[0] = sends[0] with { Expect = Rows(rows), Checks = Below(rows) };
                var result = await TypeAsync(pid, new KeyPlan(sends) { Proceed = stillAsked }, timeline, last);
                if (result.Status == InjectionStatus.Typed)
                {
                    typedAny = true;
                    progress.Partway = true;
                    wrote = rows;
                    return null;
                }
                if (result.Status != InjectionStatus.NothingTyped)
                {
                    progress.Unknown = true;
                    return (AfterPart(result, typedAny), null);
                }
                var changed = result.Detail == Windows.WorkerInjector.ScreenChanged;
                // Nothing typed yet, for a reason of the injector's own (an elevated session, say):
                // said as the injector says it, as any other answer refused there is.
                if (!typedAny && !changed) { return (result, null); }
                if (changed && await SameFieldAsync(rows, started) is { } again)
                {
                    log($"the screen of {Short(sessionId)} (pid {pid}) changed before {(sends is [{ Text: "", Enter: true }] ? "an Enter" : "its next keys")}, and read again shows the form at the same field as it was; they are held to it and typed again");
                    CopilotAnswerTrace.Current.Value?.Retried();
                    rows = again;
                    continue;
                }
                log($"typing into {Short(sessionId)} (pid {pid}) stopped before {(sends is [{ Text: "", Enter: true }] ? "an Enter" : "its next keys")}: {result.Detail}");
                return Stop(changed ? "the session's screen changed before the next key could go" : result.Detail);
            }
        }

        // The screen read again, within FormRecheck of started, once it shows the form at the
        // field the next keys go to, that field drawn exactly as in rows (its options and where
        // the pointer is, or what is typed into it); null if it does not by then.
        async Task<IReadOnlyList<string>?> SameFieldAsync(IReadOnlyList<string> rows, long started)
        {
            if (CopilotFormScreen.Input(rows, form, Field()) is not { } field) { return null; }
            while (options.Time.GetElapsedTime(started) < options.FormRecheck)
            {
                await Task.Delay(options.ScreenPollInterval ?? options.PollInterval, options.Time);
                if (await ReadRowsAsync(pid) is not { } now) { return null; }
                if (CopilotFormScreen.Input(now, form, Field()) is { } drawn && drawn.SequenceEqual(field)) { return now; }
            }
            return null;
        }

        // The screen the last Enter was pressed on. Whatever goes next waits for the form to move
        // off it, and is held to the field it moved to: read at once, a CLI still showing the
        // field just committed would be taken for the next one.
        IReadOnlyList<string>? leaving = null;
        // The field the next keys are for: one on from each Enter, and on a resume the field the
        // form was left at.
        var at = progress.Taken;
        // Resumed with keys already in the field at: they are never typed again, and only what
        // follows them goes, once that field shows exactly what they left in it.
        var resuming = progress.Partway;
        // The rows keys are about to be held to, when they show the form at that field, or rows
        // read again within FormRecheck that do; null, and why, when none does. A screen that has
        // settled, or moved on, is not enough: a permission dialog that opens before the
        // transcript says so is just as steady, and keys held to its rows go into it, an Enter
        // picking what it highlights (#510 review). Nor is one read that does not show it the
        // end: read mid-redraw, a form on screen did not show, and the answer was refused or the
        // form left part answered (#1123). Each refusal logs which check failed and the screen's
        // outline (CopilotFormScreen.Diagnose), never what it says.
        async Task<(IReadOnlyList<string>? Rows, string? Why)> OnFormAsync(IReadOnlyList<string> rows)
        {
            var (why, check, row, outline) = CopilotFormScreen.Diagnose(rows, form, Field());
            if (why is null) { return (rows, null); }
            var started = options.Time.GetTimestamp();
            var again = 0;
            while (options.Time.GetElapsedTime(started) < options.FormRecheck)
            {
                await Task.Delay(options.ScreenPollInterval ?? options.PollInterval, options.Time);
                CopilotAnswerTrace.Current.Value?.Reread();
                again++;
                if (await ReadRowsAsync(pid) is not { } now) { break; }
                var read = CopilotFormScreen.Diagnose(now, form, Field());
                if (read.Why is null)
                {
                    log($"the screen of {Short(sessionId)} (pid {pid}) showed the form at its next field when read again ({again} more read{(again == 1 ? "" : "s")}; the first failed check {CopilotFormScreen.Name(check)}: {outline})");
                    return (now, null);
                }
                (why, check, row, outline) = read;
            }
            CopilotAnswerTrace.Current.Value?.Failed(check, row);
            log($"the screen of {Short(sessionId)} (pid {pid}) is not the form at its next field: {why}; read {again} more time{(again == 1 ? "" : "s")}; {outline}; nothing more is typed");
            return (null, why);
        }
        int Field() => Math.Min(at, Math.Max(0, form.Fields.Count - 1));
        // Why the field the next keys go to is not as it opens (CopilotFormScreen.Untouched), on a
        // first pass and a resume alike, before its first key: anything typed into it at the
        // machine would be added to, and submitted with, the answer's keys (#1123 review). Null
        // when it is untouched.
        string? Touched(IReadOnlyList<string> rows)
        {
            if (CopilotFormScreen.Untouched(rows, form, Field())) { return null; }
            log($"the field the next keys go to in {Short(sessionId)} (pid {pid}) is not as it opens: something is typed into it; nothing is typed into it");
            return "the field the next keys go to already shows something typed into it at the machine, which they would be added to";
        }
        // How often tail shows in the input of the field the next keys go to; none on rows that
        // do not show the form at that field, so words are never taken to have shown there.
        Func<IReadOnlyList<string>, int> Shows(string tail) => rows =>
            CopilotFormScreen.Input(rows, form, Field()) is { } input ? WordsOnScreen.Count(input, tail) : 0;
        // The field the next keys go to: the one the form moved to, or the screen as it stands
        // before anything has been pressed. Its rows, how often tail shows on it, or why not.
        async Task<(IReadOnlyList<string>? Rows, int Count, string? Why)> NextFieldAsync(string tail)
        {
            if (leaving is null)
            {
                if (await ReadRowsAsync(pid) is not { } rows) { return (null, 0, "the session's screen could no longer be read"); }
                var (on, off) = await OnFormAsync(rows);
                return on is null ? (null, 0, off) : (on, Shows(tail)(on), null);
            }
            var moved = await FieldShownAsync(pid, leaving, Shows(tail));
            leaving = null;
            if (moved.Rows is null) { log($"the next field in {Short(sessionId)} (pid {pid}) did not show: {moved.Why}; nothing more is typed"); }
            else
            {
                var (on, off) = await OnFormAsync(moved.Rows);
                return on is null ? (null, 0, off) : (on, Shows(tail)(on), null);
            }
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
            if (!resuming && Touched(shown) is { } touched) { return Stop(touched); }
            var commit = keys[^1];
            var first = keys.GetRange(0, keys.Count - 1);
            if (commit.Text.Length > 0) { first.Add(commit with { Enter = false, EnterDelayMs = 0, PauseAfterMs = 0 }); }
            if (resuming)
            {
                // Its walk went before the form was left: its Enter goes only to the options drawn
                // exactly as the walk left them, the same row highlighted.
                resuming = false;
                if (progress.Walked is not { } left || CopilotFormScreen.Input(shown, form, Field()) is not { } now || !now.SequenceEqual(left))
                {
                    return Stop("the field the form was left at does not show the choice its keys made");
                }
            }
            else if (first.Count > 0)
            {
                if (await WriteAsync(first, false, shown) is { } stopped) { return stopped; }
                var (settled, unsettled) = await SettledAsync(pid);
                if (settled is null)
                {
                    log($"the screen of {Short(sessionId)} (pid {pid}) did not settle before the field's Enter: {unsettled}; nothing more is typed");
                    return Stop(unsettled!);
                }
                var (on, off) = await OnFormAsync(settled);
                if (on is null) { return Stop(off!); }
                shown = on;
                progress.Walked = CopilotFormScreen.Input(on, form, Field());
            }
            if (await WriteAsync([new KeySend("", true, 0, commit.PauseAfterMs)], last, shown) is { } committed) { return committed; }
            leaving = wrote;
            at++;
            progress.Committed(at);
            return null;
        }

        for (var i = Start(plan, at); i < plan.Sends.Count; i++)
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
            if (!resuming && Touched(field) is { } typedThere) { return Stop(typedThere); }
            IReadOnlyList<string>? showing = null;
            if (resuming)
            {
                // Resumed in this field: its walk to Other, or its words, went before the form was
                // left, and none of it is typed again. Words already typed get their Enter only once
                // they show in it; a walk, only once the field is drawn exactly as it left it.
                resuming = false;
                if (progress.Words)
                {
                    if (!CopilotFormScreen.Holds(field, form, Field(), send.Text)) { return Stop("the field the form was left at does not show exactly the words typed into it"); }
                    showing = field;
                }
                else if (progress.Walked is not { } left || CopilotFormScreen.Input(field, form, Field()) is not { } now || !now.SequenceEqual(left))
                {
                    return Stop("the field the form was left at does not show the choice its keys made");
                }
                pending = [];
            }
            else if (pending.Count > 0)
            {
                // Other's walk, which has no Enter: held to the list it walks, and the words wait
                // for the change it makes, Other opening.
                var shown = field;
                if (await WriteAsync(pending, false, shown) is { } walked) { return walked; }
                pending = [];
                var (rows, _, why) = await FieldShownAsync(pid, wrote, Shows(tail));
                if (rows is null)
                {
                    log($"the field the words go to in {Short(sessionId)} (pid {pid}) did not show: {why}; nothing more is typed");
                    return Stop(why!);
                }
                var (on, off) = await OnFormAsync(rows);
                if (on is null) { return Stop(off!); }
                (field, before) = (on, Shows(tail)(on));
                progress.Walked = CopilotFormScreen.Input(on, form, Field());
            }
            if (showing is null)
            {
                if (await WriteAsync([send with { Enter = false, EnterDelayMs = 0, PauseAfterMs = 0 }], false, field) is { } held) { return held; }
                (progress.Walked, progress.Words) = (null, true);
                if (await AwaitOnScreenAsync(pid, Shows(tail), before) is not { } shown)
                {
                    log($"the words typed into {Short(sessionId)} (pid {pid}) did not show on its screen within {options.WordsWait.TotalSeconds:0.#} s; their Enter is not pressed");
                    return (new InjectionResult(InjectionStatus.Typed, "ok"), $"the words did not show on the session's screen within {options.WordsWait.TotalSeconds:0.#} seconds");
                }
                showing = shown;
                // Spaces typed at the machine into an empty box show only once the words follow
                // them: the words must start the input, or their Enter is not pressed (Begins).
                if (form.Fields[Field()].Kind is (CopilotFieldKind.Text or CopilotFieldKind.Number) && !CopilotFormScreen.Begins(shown, form, Field(), send.Text))
                {
                    log($"the words typed into {Short(sessionId)} (pid {pid}) do not start its field's input: something typed at the machine is before them; their Enter is not pressed");
                    return (new InjectionResult(InjectionStatus.Typed, "ok"), "the field the words went to shows something typed at the machine before them, so their Enter was not pressed");
                }
            }
            // Shown in the field's input (Shows reads only the form at that field), the words
            // were typed into this form at this field.
            // Seen on screen, they reached the CLI apart from anything after them, so their Enter
            // needs no pause of its own; held to the rows that show them, it goes into nothing else.
            if (await WriteAsync([new KeySend("", true, 0, send.PauseAfterMs)], i == plan.Sends.Count - 1, showing) is { } entered) { return entered; }
            leaving = wrote;
            at++;
            progress.Committed(at);
        }
        if (pending.Count == 0) { return (new InjectionResult(InjectionStatus.Typed, "ok"), null); }
        // Keys with no Enter of their own after the last field (none is planned so): held to the
        // field they go to, like any other.
        var (rest, _, gone) = await NextFieldAsync("");
        if (rest is null) { return Stop(gone!); }
        return await WriteAsync(pending, true, rest) ?? (new InjectionResult(InjectionStatus.Typed, "ok"), null);
    }

    static bool IsWords(KeySend send) => send.Enter && CopilotPlanner.HasWords(send.Text);

    // Where in a plan the keys of the field after the first `fields` begin: each field's keys
    // end with its Enter (CopilotPlanner).
    static int Start(KeyPlan plan, int fields)
    {
        var (i, n) = (0, 0);
        while (n < fields && i < plan.Sends.Count) { if (plan.Sends[i++].Enter) { n++; } }
        return i;
    }

    // The rows of a screen as a send holds to them.
    static List<ScreenRow> Rows(IReadOnlyList<string> rows) => [.. rows.Select((text, row) => new ScreenRow(row, text))];

    // And nothing under them: every row below the last one read is blank when the key goes, as
    // the worker checks in the same read (ScreenCheck.BlankFrom). Held to the rows read alone, a
    // console that grew after the read, a permission prompt drawn in the rows it gained, passed:
    // OffForm had already looked, the transcript may not yet say so, and the Enter that commits
    // the field would pick what the prompt highlights (#510 review, Copilot 4478363609; #843's
    // bottom anchor).
    static List<ScreenRowCheck> Below(IReadOnlyList<string> rows) => [new(rows.Count, ScreenCheck.BlankFrom, [])];

    // Nothing typed by a later part of a plan whose earlier part went is not nothing typed.
    static InjectionResult AfterPart(InjectionResult result, bool typedAny) =>
        typedAny && result.Status == InjectionStatus.NothingTyped
            ? new InjectionResult(InjectionStatus.Interrupted, $"{result.Detail}, after part of the answer was typed")
            : result;

    // The session's screen, or null when it cannot be read.
    async Task<IReadOnlyList<string>?> ReadRowsAsync(int pid)
    {
        CopilotAnswerTrace.Current.Value?.Read();
        try { return (await injector.ReadScreenAsync(pid)).Rows; }
        catch (Exception e)
        {
            log($"could not read the screen of pid {pid}: {e.GetType().Name}: {e.Message}");
            return null;
        }
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

    // The field the words go to, and how often their end shows in its input (shows): once the screen has
    // changed from how it stood before the keys that open that field, and reads the same twice
    // running. No rows, and why, if that does not happen within WordsWait or the screen cannot
    // be read.
    async Task<(IReadOnlyList<string>? Rows, int Count, string? Why)> FieldShownAsync(int pid, IReadOnlyList<string> shown, Func<IReadOnlyList<string>, int> shows)
    {
        var started = options.Time.GetTimestamp();
        IReadOnlyList<string>? last = null;
        while (true)
        {
            if (await ReadRowsAsync(pid) is not { } rows) { return (null, 0, "the session's screen could no longer be read"); }
            // Two reads running, of a screen that has changed: last follows every read, so a
            // repaint that went back to how it stood (B, then A, then B) does not count as B twice.
            if (!rows.SequenceEqual(shown) && last is not null && rows.SequenceEqual(last)) { return (rows, shows(rows), null); }
            last = rows;
            if (options.Time.GetElapsedTime(started) >= options.WordsWait)
            {
                return (null, 0, $"the field the words go to did not show on the session's screen within {options.WordsWait.TotalSeconds:0.#} seconds");
            }
            await Task.Delay(options.ScreenPollInterval ?? options.PollInterval, options.Time);
        }
    }

    // The screen once the words' end shows in the field's input (shows) more often than it did before they were typed,
    // within WordsWait; null if it does not.
    async Task<IReadOnlyList<string>?> AwaitOnScreenAsync(int pid, Func<IReadOnlyList<string>, int> shows, int before)
    {
        var started = options.Time.GetTimestamp();
        while (true)
        {
            if (await ReadRowsAsync(pid) is { } rows && shows(rows) > before) { return rows; }
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
            // A form is confirmed only by its structured answer, so a completion found first (its
            // rendering alone) waits for it, as ConfirmAsync does, up to ConfirmTimeout from when
            // a read first found the record. Taken at once, it settled as recorded, and the
            // structured answer written just after never reached the client (Copilot 4479319436).
            var structured = record.HumanContent is null && plan.Text is null;
            if (!record.Completed || structured)
            {
                var now = options.Time.GetUtcNow();
                DateTimeOffset first;
                lock (seen) { first = seen.Value ??= now; }
                if ((!record.Completed && now - first < CompletionGrace) || (structured && now - first < options.ConfirmTimeout))
                {
                    await Task.Delay(options.PollInterval, options.Time);
                    continue;
                }
            }
            timeline.Recorded(record.At);
            var (matched, reason) = CopilotVerifier.Check(plan, record);
            return matched ? Delivered(timeline) : Unconfirmed(reason, AnswerStage.Recorded);
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
    async Task<AnswerResult> CopilotPermissionAsync(Session session, Question question, AnswerRequest request, DeliveryTimeline timeline, IEscapedConfirmation? confirmed, AttemptLedger.Owner by)
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

            if (Claim(attempt, request, by, null) is { } claimed) { return Refused(claimed); }
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
        : new(pending.Dialog, pending.Drawn, [.. state.Others.Select(o => new CopilotPermissionScreen.Other($"{KindName(o.Described.Value?.Kind)}, request {Short(o.RequestId)}", o.Described.Value?.Drawn ?? [], o.Described.Value?.Dialog))]);

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
    async Task<AnswerResult> ClaudeAsync(Session session, Question question, AnswerRequest request, DeliveryTimeline timeline, AttemptLedger.Owner by)
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

            if (Claim(attempt, request, by, new("claude", transcript, question.Id, from)) is { } claimed) { return Refused(claimed); }
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
    async Task<AnswerResult> CodexAsync(Session session, Question question, AnswerRequest request, DeliveryTimeline timeline, IEscapedConfirmation? confirmed, AttemptLedger.Owner by)
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

            if (Claim(attempt, request, by, null) is { } claimed) { return Refused(claimed); }
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

    // The prompt claimed before the first key, for the sender, session and question the request
    // was admitted for, with a hash of the answer; and, where its record can be read again
    // (reread), the answer's words, kept apart (AnswerWords), or it is refused before any key.
    string? Claim(string attempt, AnswerRequest request, AttemptLedger.Owner by, AttemptLedger.Reread? reread) =>
        ledger.Claim(attempt, Guid.Parse(request.Request), new AttemptLedger.Admission(by, AnswerDigest.Of(request), reread,
            reread is null ? null : new AnswerWords.Words(request.Text, request.Fields)));

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

    // With the code a client says in its own words, for a reason that has one (AnswerReasons).
    static AnswerResult Refused(string reason) => new(AnswerOutcome.Refused, reason) { Code = AnswerReasons.Code(reason) };
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

    public Task<AnswerResult> AnswerAsync(AnswerRequest request, CancellationToken cancellationToken = default, DeliveryWatch? watch = null, IEscapedConfirmation? confirmed = null, string? sender = null) =>
        Task.FromResult(new AnswerResult(AnswerOutcome.Refused, "answering is not supported on this platform yet; answer at the machine"));
}
