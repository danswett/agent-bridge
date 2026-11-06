using System.Collections.Concurrent;
using System.Threading.Channels;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Host.Delivery.AgentReplies;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Host.Delivery.Windows;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

// session.reply for a host: what hello offers, and one reply typed into one session.
public interface ISessionReplier
{
    // Whether hello offers sessions.reply now; asked per hello, like answers.
    bool Available { get; }

    // Whether hello offers sessions.attach now: replies that carry files.
    bool TakesAttachments => false;

    // watch: hears the delivery's stages as it goes (DeliveryWatch), for a caller that reports them.
    Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default, DeliveryWatch? watch = null);
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

    // How much longer a reply that timed out unrecorded is watched for, without changing its
    // outcome, so its confirmation line can be followed by when it was recorded after all
    // (#1025): how late "late" is. The host sets five minutes (Program.cs); zero watches no
    // further. Its looks are LateLook apart: the agent's own time on the record is what is
    // measured, so they need not be quick.
    public TimeSpan LateWatch { get; init; } = TimeSpan.Zero;

    public TimeSpan LateLook { get; init; } = TimeSpan.FromSeconds(5);

    // How long Claude or Codex is given to record a reply before its Enter is taken to have
    // been swallowed as part of the paste, and pressed again (agent-ha-bridge's
    // Confirm-DaemonClaudeSubmit waited 1.2 s), and how many times at most.
    public TimeSpan SubmitAfter { get; init; } = TimeSpan.FromMilliseconds(1500);

    public int ExtraEnters { get; init; } = 2;

    // How long, at most, an input box still taking the reply in is waited for before Enter is
    // given up on. Codex, streaming output, was seen live taking several seconds over it.
    public TimeSpan SubmitPatience { get; init; } = TimeSpan.FromSeconds(15);

    // How long Codex is given to show a pasted image path as "[Image #n]" before the next keys.
    public TimeSpan ImageWait { get; init; } = TimeSpan.FromSeconds(10);

    public TimeSpan RememberFor { get; init; } = TimeSpan.FromMinutes(15);

    public TimeProvider Time { get; init; } = TimeProvider.System;

    // Writes to transcripts as the session source's watcher hears them: confirmation looks again
    // at once rather than at its next poll. Null: polling alone, as before.
    public TranscriptWrites? Writes { get; init; }

    // Whether each live Copilot session's transcript is read in the background as it is found and
    // as it grows, so a reply reads only what was added since. The first reply after a start read
    // the whole events.jsonl before typing, 0.3 to 0.6 s for 96 to 169 MB, measured (#599).
    public bool PreRead { get; init; } = true;
}

// Types a reply into a session's own input, once, and says what happened. The rules are
// the answer's (AnswerService), with the DeliveryGate shared between them:
//   * Never while agent-ha-bridge's daemon is active for this user (G5), checked when the
//     request arrives and again immediately before typing; never into an elevated process.
//   * One delivery per session at a time (busy), answers and replies alike, held until the
//     keys have gone; one request is typed at most once, its outcome remembered.
//   * Never while the session is asking anything: typed words would answer its question,
//     and Enter accept a permission prompt. Checked against the session and, immediately
//     before typing, its transcript (Copilot) or its screen (Claude Code and Codex, whose
//     prompts the host cannot otherwise see without agent-ha-bridge's hooks: AgentAsync).
//   * Delivered only when the agent records a prompt from the person, after the point
//     read just before typing, that is the reply typed, with every file it carried.
//     Anything else is unconfirmed. Never retyped; for Claude and Codex, Enter alone may be
//     pressed again while the reply still sits in the input box.
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
    // What each session's transcript says is asking, kept between replies so it is read from
    // the start once and then only as it grows. One small entry per live Copilot session with
    // PreRead (dropped when it goes), or per session replied to without.
    readonly ConcurrentDictionary<string, CopilotInput> inputs = new(StringComparer.Ordinal);
    // Sessions waiting for a background read, read one at a time so a start with many large
    // transcripts does not read them all at once.
    readonly Channel<string> preReads = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    readonly ConcurrentDictionary<string, byte> preReading = new(StringComparer.Ordinal);
    readonly AttachmentStore? attachments;

    // Test-only compatibility overload; production must pass the shared gate explicitly.
    internal ReplyService(ISessionSource source, IProcessInspector processes, IAgentHaBridgeProbe probe, ITargetGuard guard,
        IConsoleInjector injector, ReplyOptions options, Action<string>? log = null, DeliveryGate? gate = null,
        AttachmentStore? attachments = null)
        : this(gate ?? new DeliveryGate(), source, processes, probe, guard, injector, options, log, attachments) { }

    public ReplyService(DeliveryGate gate, ISessionSource source, IProcessInspector processes, IAgentHaBridgeProbe probe, ITargetGuard guard,
        IConsoleInjector injector, ReplyOptions options, Action<string>? log = null, AttachmentStore? attachments = null)
    {
        this.source = source;
        this.processes = processes;
        this.probe = probe;
        this.guard = guard;
        this.injector = new GateTrackedInjector(injector, gate);
        this.options = options;
        this.log = log ?? (_ => { });
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.attachments = attachments;
        requests = new OutcomeMemory<ReplyResult>(options.Time, options.RememberFor, this.gate, Busy);
        if (options.PreRead && options.PlatformSupported)
        {
            source.Changed += OnSessionChanged;
            // Writes that change nothing shown (hook and model records) send no upsert.
            if (options.Writes is { } writes) { writes.Heard += OnTranscriptWritten; }
            _ = Task.Run(PreReadAsync);
            // Found before this service was made: the source has already said so.
            foreach (var s in source.Snapshot()) { QueuePreRead(s); }
        }
    }

    string CopilotTranscript(string sessionId) => Path.Combine(options.CopilotHome, "session-state", sessionId, "events.jsonl");

    // Why a session this host started (a launch, a resume, a restore) is not to be typed into yet:
    // its window has not been seen ready, and may still show a startup question (folder trust)
    // that Enter would answer. The resume's transcript already exists, so the no-transcript-yet
    // check below never catches it. Set once the launcher exists (LaunchService.HeldForReply).
    public Func<string, string?>? LaunchHeld { get; set; }

    // Ends the late watches of replies that timed out (#1025) when the host stops, so none outlives it.
    public CancellationToken Stopping { get; set; }

    void OnSessionChanged(object? sender, SessionChange change)
    {
        if (change.Kind == SessionChangeKind.Removed)
        {
            if (Guid.TryParseExact(change.Id, "D", out _)) { inputs.TryRemove(CopilotTranscript(change.Id), out _); }
            return;
        }
        if (change.Session is { } session) { QueuePreRead(session); }
    }

    // Only a Copilot transcript, <CopilotHome>/session-state/<id>/events.jsonl; whether its
    // session is live is asked when it is read.
    void OnTranscriptWritten(string path)
    {
        try
        {
            if (!string.Equals(Path.GetFileName(path), "events.jsonl", StringComparison.OrdinalIgnoreCase)) { return; }
            var id = Path.GetFileName(Path.GetDirectoryName(path));
            if (id is null || !Guid.TryParseExact(id, "D", out _)) { return; }
            if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(CopilotTranscript(id)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) { return; }
            QueuePreRead(id);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { }
    }

    void QueuePreRead(Session session)
    {
        if (session.Agent != AgentKind.Copilot || !Guid.TryParseExact(session.Id, "D", out _)) { return; }
        QueuePreRead(session.Id);
    }

    void QueuePreRead(string id)
    {
        if (preReading.TryAdd(id, 0)) { preReads.Writer.TryWrite(id); }
    }

    // Catches each session's CopilotInput up with its transcript off the typing path: the read a
    // reply makes before typing then covers only what was written since.
    async Task PreReadAsync()
    {
        await foreach (var id in preReads.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            preReading.TryRemove(id, out _);
            // Gone meanwhile: nothing is kept for it.
            if (source.Get(id) is not { Agent: AgentKind.Copilot }) { continue; }
            var path = CopilotTranscript(id);
            try { inputs.GetOrAdd(path, p => new CopilotInput(p)).Read(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            // Removed while it was read: its removal may have come before the input was added.
            finally { if (source.Get(id) is null) { inputs.TryRemove(path, out _); } }
        }
    }

    // Tests: what is kept of a session's transcript.
    internal CopilotInput? InputOf(string sessionId) => inputs.GetValueOrDefault(CopilotTranscript(sessionId));

    public bool Available => options.PlatformSupported && probe.Recent() is null;

    // Whether hello offers sessions.attach: replies are on and there is somewhere to stage files.
    public bool TakesAttachments => attachments is not null && Available;

    // peer: the device the request came from. A reply can only use attachments that device sent.
    public Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default, DeliveryWatch? watch = null)
    {
        if (!Guid.TryParseExact(request.Request, "D", out var key))
        {
            return Task.FromResult(Refused("request must be a UUID"));
        }
        return requests.Once(key, watch, () => new DeliveryTimeline(options.Time, "reply", key, log), t => DeliverAsync(request, key, peer ?? "", t), result =>
            log($"reply {key.ToString()[..8]} to {Short(request.Id)}: {result.Outcome.ToString().ToLowerInvariant()}{(result.Reason is null ? "" : $" ({ReplyReasons.Shared(result.Reason)})")}"));
    }

    async Task<ReplyResult> DeliverAsync(ReplyRequest request, Guid key, string peer, DeliveryTimeline timeline)
    {
        Typed typed;
        try
        {
            if (!options.PlatformSupported) { return Refused("replying is not supported on this platform yet; reply at the machine"); }
            if (probe.Active() is { } active) { return Refused(active); }
            if (Replies.Check(request) is { } invalid) { return Refused(invalid); }
            var refs = request.Attachments ?? [];
            if (refs.Count > 0 && attachments is null) { return Refused("this host does not take attachments; send the text alone, or attach at the machine"); }
            var session = source.Get(request.Id);
            if (session is null) { return Stale("the session has ended"); }
            if (session.Agent is not (AgentKind.Copilot or AgentKind.Claude or AgentKind.Codex))
            {
                return Refused($"replying is not supported for {session.Agent.ToString().ToLowerInvariant()} yet; reply at the machine");
            }
            if (!gate.TryClaim(session.Id, key)) { return Busy(); }
            Claim? claim = null;
            var keysWent = false;
            try
            {
                if (session.Question is { } asking) { return Asking(asking); }
                if (LaunchHeld?.Invoke(session.Id) is { } held) { return Refused(held); }
                if (refs.Count > 0)
                {
                    (claim, var problem) = attachments!.Claim(peer, refs);
                    if (problem is not null) { return Refused(problem); }
                }
                var outcome = session.Agent == AgentKind.Copilot
                    ? await CopilotAsync(session, request.Text ?? "", claim, timeline)
                    : await AgentAsync(session, request.Text ?? "", claim, timeline);
                keysWent = outcome.KeysWent;
                if (outcome.Result is { } result) { return result; }
                typed = outcome.Typed!;
            }
            finally
            {
                // Typed, even in part: the files are the agent's now. Not typed: the staged
                // copies go, and the attachments stay for another try.
                if (claim is not null)
                {
                    if (keysWent) { attachments!.Consume(claim); }
                    else { attachments!.Release(claim); }
                }
                gate.Release(session.Id, key);
            }
        }
        catch (Exception e)
        {
            // Only reached before anything was typed: CopilotAsync owns its own failures
            // once a key may have gone.
            return Failed(AnswerOutcome.Refused, $"reply {key.ToString()[..8]} to {Short(request.Id)} failed before typing", e);
        }

        // The session is free again once the keys have gone: a question it asks next can be
        // answered while this waits for the prompt to be taken, which for a busy session can
        // be a while.
        ReplyResult confirmed;
        try { confirmed = await typed.Confirm(); }
        catch (Exception e) { confirmed = Failed(AnswerOutcome.Unconfirmed, $"reply {key.ToString()[..8]} to {Short(request.Id)} could not be checked", e); }
        Measure(key, request.Id, typed.Seen, timeline, confirmed);
        return confirmed;
    }

    // One line for each reply that reached its confirmation, to measure why replies go
    // unconfirmed (#1025): ids, the agent, whether the session was working when the keys went,
    // when they had all gone, when the agent recorded the reply (its own time, or ~ the host's
    // when its record has none, or never), the timeout, and the outcome, in ms after receipt as
    // the timings line is. Never the reply's words. One that timed out unrecorded is then watched
    // for LateWatch more, and a second line says when it was recorded after all, if it was.
    void Measure(Guid key, string sessionId, Seen seen, DeliveryTimeline timeline, ReplyResult result)
    {
        var id = key.ToString()[..8];
        var (typedAt, working, recorded) = timeline.Stages();
        var when = recorded is { } r ? Ms(r) : seen.At is { } s ? $"~{Ms(s)}" : "never";
        log(FormattableString.Invariant($"reply {id} to {Short(sessionId)} confirm: {seen.Agent.ToString().ToLowerInvariant()}, {(working ? "working" : "idle")}, typed {(typedAt is { } t ? Ms(t) : "?")}, recorded {when}, timeout {options.ConfirmTimeout.TotalSeconds:0} s, {result.Outcome.ToString().ToLowerInvariant()}"));
        if (result.Outcome == AnswerOutcome.Unconfirmed && recorded is null && seen.At is null && !seen.Ended && options.LateWatch > TimeSpan.Zero)
        {
            _ = Task.Run(() => WatchLateAsync(id, seen, timeline));
        }

        static string Ms(long ms) => ms.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    // Looks for the reply's prompt now and then, never claiming it: the outcome stands, and this
    // only says how late the record came. A prompt of the same words typed since, at the machine
    // or by another reply, reads as this one: rare, and only ever early in the measure.
    async Task WatchLateAsync(string id, Seen seen, DeliveryTimeline timeline)
    {
        var started = options.Time.GetTimestamp();
        try
        {
            while (options.Time.GetElapsedTime(started) < options.LateWatch)
            {
                await Task.Delay(options.LateLook, options.Time, Stopping).ConfigureAwait(false);
                if (seen.Find() is (true, var at))
                {
                    var ms = timeline.After(at) is { } agent ? agent.ToString(System.Globalization.CultureInfo.InvariantCulture) : FormattableString.Invariant($"~{timeline.Elapsed()}");
                    log($"reply {id} late: recorded {ms}");
                    return;
                }
            }
            log(FormattableString.Invariant($"reply {id} late: not recorded within {options.LateWatch.TotalSeconds:0} s more"));
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            // Never rethrown: this task has no one awaiting it, and measures only.
            log($"reply {id} late: its transcript could not be read ({e.GetType().Name})");
        }
    }

    // What a reply's confirmation saw of it, for its measurement (#1025): its agent; when the host
    // found the reply's own prompt, in ms after receipt (whatever the outcome then), or null;
    // whether the session ended first; and how to look for that prompt again, without taking it.
    sealed class Seen(AgentKind agent, Func<(bool Found, DateTimeOffset? At)> find)
    {
        public AgentKind Agent => agent;
        public Func<(bool Found, DateTimeOffset? At)> Find => find;
        public long? At { get; set; }
        public bool Ended { get; set; }
    }

    // What a reply that went is confirmed by, and what its confirmation saw.
    sealed record Typed(Func<Task<ReplyResult>> Confirm, Seen Seen);

    // KeysWent: some or all of the reply may have reached the session, whatever the result.
    sealed record Attempt(ReplyResult? Result, Typed? Typed, bool KeysWent = false);

    async Task<Attempt> CopilotAsync(Session session, string text, Claim? claim, DeliveryTimeline timeline)
    {
        // The session id names a folder; only the agent's own id shape is accepted.
        if (!Guid.TryParseExact(session.Id, "D", out _)) { return Done(Refused("the session's id is not a Copilot session id")); }
        var transcript = Path.Combine(options.CopilotHome, "session-state", session.Id, "events.jsonl");
        if (session.ProcessId is not { } pid) { return Done(Refused("the session's process is not known; reply at the machine")); }
        if (!processes.GetAgentProcesses(AgentKind.Copilot).Any(p => p.Id == pid)) { return Done(Stale("the session's process has ended")); }
        if (guard.Refuse(pid) is { } refused) { return Done(Refused(refused)); }
        IReadOnlyList<string> staged = [];
        if (claim is not null)
        {
            if (Stage(claim, "Copilot") is { } failed) { return Done(failed); }
            staged = claim.Staged;
            if (CopilotReply.WithAttachments(text, staged) is not { } line)
            {
                return Done(Refused("an attachment's path has a space in it, which Copilot cannot take as an attachment; attach at the machine"));
            }
            text = line;
        }
        var (keys, words) = CopilotReply.Plan(text);
        // The input box is read before the reply waits for its turn, so one that already has
        // words in it, or is in shell mode, is refused at once; and read again, and held to,
        // once the turn has come (#825). Only a box seen with words in it, or in shell mode, is
        // refused here: a screen without one may be asking something (a permission prompt, or
        // the folder-trust question before a transcript exists), which the checks in the turn
        // name better, and still refuse before any key.
        if (await InputBoxAsync(pid) is { Seen: true, Problem: { } early }) { return Done(Refused(early)); }
        // Caught up before the typing turn is taken, so the first reply to a session with a
        // long transcript reads it without holding up every other delivery; the read in the
        // turn only covers what has been written since.
        var input = inputs.GetOrAdd(transcript, path => new CopilotInput(path));
        input.Read();

        // One delivery typed at a time across sessions, and the last checks are made once its
        // turn has come, not when it arrived.
        timeline.Checked();
        await gate.WaitToTypeAsync();
        timeline.Turn();
        long from;
        InjectionResult result;
        bool working;
        try
        {
            if (probe.Active() is { } active) { return Done(Refused(active)); }
            var now = source.Get(session.Id);
            if (now is null) { return Done(Stale("the session has ended")); }
            if (now.Question is { } asking) { return Done(Asking(asking)); }
            // Again once its turn has come: a launch of it may have begun meanwhile.
            if (LaunchHeld?.Invoke(session.Id) is { } held) { return Done(Refused(held)); }
            working = now.Status == SessionStatus.Working;
            // The transcript, read now, may be ahead of the session source: a question asked a
            // moment ago is there first, and a subagent's is only there. One that cannot be
            // read cannot say nothing is asking.
            var before = input.Read();
            // Copilot (1.0.105, seen live) writes events.jsonl only with the first prompt, and
            // registers its session (inuse.<pid>.lock) while its folder-trust question may still
            // be on screen. So a session with no transcript yet may be asking that, where typed
            // words are dropped and Enter answers "Yes": a reply to one on the test VM trusted
            // the folder. Only a person at the machine answers it, so nothing is typed, and the
            // reason says which case this is rather than calling the transcript unreadable.
            if (!before.Readable && NotYetWritten(transcript, now))
            {
                return Done(Refused("Copilot has not started this session's transcript yet (it does with the first prompt), so the host cannot tell whether it is still asking whether to trust its folder, which a reply would answer; send the first prompt at the machine, or launch with a prompt"));
            }
            if (!before.Readable) { return Done(Refused("the session's transcript could not be read, so nothing is typed; reply at the machine")); }
            if (before.Resumed)
            {
                return Done(Refused("the session was resumed in a new Copilot while a question or approval was open, and its window may still show it, where a reply would answer it; answer or dismiss it at the machine (or type there), then reply"));
            }
            if (before.Asking) { return Done(Asking()); }
            from = before.ScannedTo;
            // Typed only into Copilot's input box, empty and outside shell mode, as the screen
            // shows it now: words left in it would go with the reply, and in shell mode Enter runs
            // what it holds as a shell command. The keys are held to those rows (KeySend.Expect),
            // so words typed at the machine, or shell mode entered, after this read and before the
            // first key, stop them in the worker, with nothing typed (#825). Their Enter is a send
            // of its own, held to the box's bottom edge: a box that moved or went (a dialog drawn
            // in its place while the words were typed) gets no Enter, and the words stay in it.
            var (box, problem, _, _) = await InputBoxAsync(pid);
            if (box is null) { return Done(Refused(problem!)); }
            // Both are held to the footer's mode too, in the same read: the box's rows read the same
            // empty, in vim's normal mode, or in shell mode holding only a space ('!' then a space),
            // and only the footer under them says which. The footer must still be there, and show
            // neither mode; and before the Enter, the box's text must not start with '!' (#843
            // review). What is left is the moment between the worker's read and the CLI taking
            // the key, which no read can close.
            var line = keys.Sends.Single();
            var footer = box.Rows[^1].Row + 1;
            ScreenRowCheck[] mode =
            [
                new(footer, ScreenCheck.WithAny, CopilotInputBoxReader.FooterSigns),
                new(footer, ScreenCheck.WithoutParts, CopilotInputBoxReader.UnsafeFooter),
                // Under 80 columns a turn's row has no mode labels to check, so a turn that starts
                // after the read is refused too, as one running at the read is (Trust's #843
                // re-review).
                .. CopilotInputBoxReader.Narrow(box) ? [new ScreenRowCheck(footer, ScreenCheck.WithNone, CopilotInputBoxReader.TurnSigns)] : Array.Empty<ScreenRowCheck>(),
                // Still at the bottom: nothing but blank rows under the footer, as the reader
                // required. Rows that read like the box and its footer can be printed above the
                // real ones (a draft in them, or a dialog in their place); with only the box and
                // footer rows held, a screen that grew that under them passed (#843 review).
                new(footer + 1, ScreenCheck.BlankFrom, []),
            ];
            keys = new KeyPlan(
            [
                line with { Enter = false, EnterDelayMs = 0, PauseAfterMs = line.EnterDelayMs, Expect = box.Rows, Checks = mode },
                new KeySend("", true, 0, line.PauseAfterMs, [box.Rows[^1]], [.. mode, new(box.Rows[1].Row, ScreenCheck.NotStartingWith, CopilotInputBoxReader.Bang)]),
            ]);

            timeline.InjectStart();
            try { result = await injector.InjectAsync(pid, keys); }
            catch (Exception e)
            {
                return new Attempt(Failed(AnswerOutcome.Unconfirmed, $"the injector failed while typing a reply into {Short(session.Id)}", e), null, KeysWent: true);
            }
            finally { timeline.InjectEnd(); }
        }
        finally { gate.DoneTyping(); }

        // Before the log line, which writes a file: the client hears it as soon as it can.
        if (result.Status == InjectionStatus.Typed) { timeline.Typed(working); }
        log($"typed a reply into {Short(session.Id)} (pid {pid}){(staged.Count > 0 ? $" with {staged.Count} attachment(s)" : "")}: {keys.Describe()} -> {result.Status} {result.Detail}");
        return result.Status switch
        {
            InjectionStatus.NothingTyped => Done(Refused(result.Detail == WorkerInjector.ScreenChanged
                ? "Copilot's input box changed just as the reply was to be typed, so nothing was typed; try again, or reply at the machine"
                : DeliveryReasons.Of(result.Detail))),
            InjectionStatus.Interrupted when result.Detail == WorkerInjector.ScreenChangedPartway =>
                new Attempt(Unconfirmed("the reply was typed into Copilot's input box, but the box changed before its Enter, so it was not sent and its words are still in the box"), null, KeysWent: true),
            InjectionStatus.Interrupted => new Attempt(Unconfirmed($"{DeliveryReasons.Of(result.Detail)}; part of the reply may have reached the session"), null, KeysWent: true),
            _ => Typing(AgentKind.Copilot, () => FirstOf(CopilotPrompts.Since(transcript, from).Prompts, p => CopilotReply.Matches(words, p.Content), p => p.At),
                seen => ConfirmAsync(transcript, from, words, staged, timeline, seen)),
        };

        static Attempt Done(ReplyResult r) => new(r, null);
    }

    // Copilot's input box, as the session's screen shows it now, or why no reply can be typed
    // into it: a screen that cannot be read cannot say the box is empty.
    async Task<(CopilotInputBox? Box, string? Problem, bool Seen, CopilotInputBoxState? State)> InputBoxAsync(int pid)
    {
        ConsoleScreen screen;
        try { screen = await injector.ReadScreenAsync(pid); }
        catch (Exception e) { return (null, Logged($"could not read the screen of pid {pid} for a reply", e), false, null); }
        if (screen.Rows is not { } rows) { return (null, DeliveryReasons.Of(screen.Problem ?? "the host could not read the session's screen; reply at the machine"), false, null); }
        return CopilotInputBoxReader.Read(rows);
    }

    // A reply to Claude Code or Codex, typed as agent-ha-bridge types one (Invoke-DaemonReply):
    // one line, then Enter on its own, 300 ms later. What differs is what guards it. Without
    // agent-ha-bridge's hooks nothing tells the host that a permission prompt, an
    // AskUserQuestion or an approval is waiting, so it reads the screen: the reply is typed
    // only into an empty input box that is on screen, and only while those rows still read so
    // (Composer). Then, as Confirm-DaemonClaudeSubmit did, an Enter the agent took as part of a
    // paste (Claude absorbs it; Codex makes it a new line, seen live) is pressed again, at most
    // twice, and only while the box on screen still holds exactly this reply.
    async Task<Attempt> AgentAsync(Session session, string text, Claim? claim, DeliveryTimeline timeline)
    {
        var agent = session.Agent;
        var name = agent == AgentKind.Claude ? "Claude" : "Codex";
        if (source.Files(session.Id)?.Transcript is not { Length: > 0 } transcript) { return Done(Refused("the session's transcript is not known; reply at the machine")); }
        if (session.ProcessId is not { } pid) { return Done(Refused("the session's process is not known; reply at the machine")); }
        if (!processes.GetAgentProcesses(agent).Any(p => p.Id == pid)) { return Done(Stale("the session's process has ended")); }
        if (guard.Refuse(pid) is { } refused) { return Done(Refused(refused)); }

        IReadOnlyList<string> staged = [];
        if (claim is not null)
        {
            if (agent == AgentKind.Codex && !AttachmentStore.AllPastableImages(claim))
            {
                return Done(Refused("Codex takes only images (PNG, JPEG, GIF or WebP) as attachments; send other files at the machine"));
            }
            if (Stage(claim, name) is { } failed) { return Done(failed); }
            staged = claim.Staged;
        }
        var plan = agent == AgentKind.Claude ? AgentReply.Claude(text, staged) : AgentReply.Codex(text, staged);
        if (plan is null) { return Done(Refused($"an attachment's path has a space in it, which {name} cannot take as an attachment; attach at the machine")); }

        timeline.Checked();
        await gate.WaitToTypeAsync();
        timeline.Turn();
        long from;
        var described = new List<string>();
        InjectionResult result;
        // Whether a send before this one went: a refusal then is no longer "nothing typed".
        var sent = false;
        bool working;
        try
        {
            if (probe.Active() is { } active) { return Done(Refused(active)); }
            var now = source.Get(session.Id);
            if (now is null) { return Done(Stale("the session has ended")); }
            if (now.Question is { } asking) { return Done(Asking(asking)); }
            // Again once its turn has come: a launch of it may have begun meanwhile.
            if (LaunchHeld?.Invoke(session.Id) is { } held) { return Done(Refused(held)); }
            working = now.Status == SessionStatus.Working;
            ConsoleScreen screen;
            try { screen = await injector.ReadScreenAsync(pid); }
            catch (Exception e) { return Done(Failed(AnswerOutcome.Refused, $"could not read the screen of {name} {Short(session.Id)} for a reply", e)); }
            if (screen.Rows is not { } rows) { return Done(Refused(DeliveryReasons.Of(screen.Problem ?? "the host could not read the session's screen; reply at the machine"))); }
            var box = Find(agent, rows);
            if (box is null)
            {
                return Done(Refused($"{name}'s input box is not on its screen, so it may be asking something at the machine; answer it there, or reply at the machine"));
            }
            // Codex shows whether its box is empty in its footer. Claude shows a suggested next
            // prompt in an empty box, which a screen read cannot tell from typed words, so its
            // box is typed into whatever it shows: words someone left there would go with the
            // reply, and Claude's record of them would leave the reply unconfirmed.
            if (agent == AgentKind.Codex && !box.Empty) { return Done(Refused($"something is already typed in {name}'s input box at the machine; send or clear it there first")); }
            // A box whose words start with / or ! holds a command waiting for Enter, and a reply
            // typed after it would send it, with whatever the reply adds. Claude Code drops
            // invisible characters from what it is given and leaves the rest in its box unsent,
            // so a reply "\u3164/" checked as text could leave a bare / there for the next one
            // to complete (#448). A suggested prompt that started with either would be refused
            // too, which only sends the person to the machine.
            if (Invisible.First(box.Content) is '/' or '!')
            {
                return Done(Refused($"{name}'s input box at the machine holds a command that a reply would run; send or clear it there first"));
            }
            // The agent only appends, and a line still being written at this point is not a
            // prompt typed now: reading from here, it does not parse.
            from = Jsonl.Length(transcript);
            result = new InjectionResult(InjectionStatus.Typed, "ok");
            foreach (var send in plan.Sends)
            {
                if (send.After is { } after)
                {
                    try { box = await AwaitBoxAsync(agent, pid, after); }
                    catch (Exception e)
                    {
                        // Keys have gone: the staged files must stay for the agent.
                        return new Attempt(Failed(AnswerOutcome.Unconfirmed, $"could not read the screen of {name} {Short(session.Id)} part way through a reply", e), null, KeysWent: true);
                    }
                    if (box is null)
                    {
                        log($"stopped typing a reply into {name} {Short(session.Id)}: its input box did not take an image path as an image");
                        return new Attempt(Unconfirmed($"{name} did not take an attachment as an image, so its path may be left as words in its input box"), null, KeysWent: true);
                    }
                }
                var keys = new KeyPlan([AgentReply.Key(send, box.Rows)]);
                described.Add(keys.Describe());
                timeline.InjectStart();
                try { result = await injector.InjectAsync(pid, keys); }
                catch (Exception e)
                {
                    return new Attempt(Failed(AnswerOutcome.Unconfirmed, $"the injector failed while typing a reply into {name} {Short(session.Id)}", e), null, KeysWent: true);
                }
                finally { timeline.InjectEnd(); }
                if (result.Status != InjectionStatus.Typed) { break; }
                sent = true;
            }
        }
        finally { gate.DoneTyping(); }

        if (result.Status == InjectionStatus.Typed) { timeline.Typed(working); }
        log($"typed a reply into {name} {Short(session.Id)} (pid {pid}){(staged.Count > 0 ? $" with {staged.Count} attachment(s)" : "")}: {string.Join(" | ", described)} -> {result.Status} {result.Detail}");
        switch (result.Status)
        {
            case InjectionStatus.NothingTyped when !sent:
                return Done(Refused(result.Detail == WorkerInjector.ScreenChanged
                    ? $"{name}'s screen changed just as the reply was to be typed, so nothing was typed; try again, or reply at the machine"
                    : DeliveryReasons.Of(result.Detail)));
            case InjectionStatus.NothingTyped or InjectionStatus.Interrupted:
                return new Attempt(Unconfirmed($"{DeliveryReasons.Of(result.Detail)}; part of the reply may have reached the session"), null, KeysWent: true);
        }

        await SubmitAsync(session.Id, agent, pid, transcript, from, plan, timeline);
        return Typing(agent, () => FirstOf(Prompts(agent, transcript, from).Prompts, plan.Says, p => p.At),
            seen => ConfirmAgentAsync(agent, transcript, from, plan, timeline, seen));

        static Attempt Done(ReplyResult r) => new(r, null);
    }

    // The input box once it shows exactly what the sends so far put in it, or null if it does
    // not within ImageWait.
    async Task<Composer?> AwaitBoxAsync(AgentKind agent, int pid, string shows)
    {
        var started = options.Time.GetTimestamp();
        while (true)
        {
            var screen = await injector.ReadScreenAsync(pid);
            if (screen.Rows is { } rows && Find(agent, rows) is { } box && box.Holds(shows)) { return box; }
            if (options.Time.GetElapsedTime(started) >= options.ImageWait) { return null; }
            await Task.Delay(options.PollInterval, options.Time);
        }
    }

    static Composer? Find(AgentKind agent, IReadOnlyList<string> rows) => agent == AgentKind.Claude ? ClaudeComposer.Find(rows) : CodexComposer.Find(rows);

    // Presses Enter again while the reply is still sitting in the input box unrecorded. A box
    // still empty, or showing only the start of the reply, may not have taken all of it in yet
    // (Codex takes keys in slowly while it streams, seen live), so it is looked at again rather
    // than given up on.
    async Task SubmitAsync(string sessionId, AgentKind agent, int pid, string transcript, long from, AgentReply plan, DeliveryTimeline timeline)
    {
        var pressed = 0;
        var began = options.Time.GetTimestamp();
        while (true)
        {
            var started = options.Time.GetTimestamp();
            while (options.Time.GetElapsedTime(started) < options.SubmitAfter)
            {
                using var written = options.Writes?.Next(transcript);
                if (Prompts(agent, transcript, from).Prompts.Any(plan.Says)) { return; }
                await TranscriptWrites.WaitAsync(written, options.PollInterval, options.Time);
            }
            if (pressed >= options.ExtraEnters) { return; }
            if (options.Time.GetElapsedTime(began) >= options.SubmitPatience)
            {
                log($"not pressing Enter again in {Short(sessionId)}: its input box did not show the whole reply within {options.SubmitPatience.TotalSeconds:0} seconds");
                return;
            }

            await gate.WaitToTypeAsync();
            try
            {
                // An Enter goes wherever the session's input is now: never while agent-ha-bridge
                // runs, and only into the box holding this reply, so never into a prompt.
                if (probe.Active() is not null) { return; }
                if (Prompts(agent, transcript, from).Prompts.Any(plan.Says)) { return; }
                if (!processes.GetAgentProcesses(agent).Any(p => p.Id == pid) || guard.Refuse(pid) is not null) { return; }
                var screen = await injector.ReadScreenAsync(pid);
                // Why not, for the log; never the words on screen.
                var why = screen.Rows is not { } rows ? "its screen could not be read"
                    : Find(agent, rows) is not { } found ? "its input box is not on screen"
                    : found.Empty || found.Begins(plan.Composed) ? null
                    : !found.Holds(plan.Composed) ? $"its input box holds other words ({found.Content.Length} characters, not {plan.Composed.Length})"
                    : "";
                if (why is null) { continue; }
                if (why.Length > 0)
                {
                    log($"not pressing Enter again in {Short(sessionId)}: {why}");
                    return;
                }
                var box = Find(agent, screen.Rows!)!;
                InjectionResult enter;
                try { enter = await injector.InjectAsync(pid, new KeyPlan([new KeySend("", true, 0, 0, box.Rows)])); }
                finally { timeline.InjectEnd(); }
                pressed++;
                log($"pressed Enter again in {Short(sessionId)} (pid {pid}): the reply was still in its input box -> {enter.Status} {enter.Detail}");
                if (enter.Status != InjectionStatus.Typed) { return; }
            }
            catch (Exception e)
            {
                log($"not pressing Enter again in {Short(sessionId)}: {e.Message}");
                return;
            }
            finally { gate.DoneTyping(); }
        }
    }

    static AgentPromptsSince Prompts(AgentKind agent, string transcript, long from) =>
        agent == AgentKind.Claude ? ClaudePrompts.Since(transcript, from) : CodexPrompts.Since(transcript, from);

    // Watches the agent's record for the prompt that is this reply, and the files it took in.
    // Claude records a reply it queues at once, but reads its files only when it takes it.
    async Task<ReplyResult> ConfirmAgentAsync(AgentKind agent, string transcript, long from, AgentReply plan, DeliveryTimeline timeline, Seen seen)
    {
        var name = agent == AgentKind.Claude ? "Claude" : "Codex";
        var started = options.Time.GetTimestamp();
        var other = false;
        long? mine = null;
        while (true)
        {
            // Taken before the look, so a record written between the look and the wait wakes it.
            using var written = options.Writes?.Next(transcript);
            var since = Prompts(agent, transcript, from);
            foreach (var prompt in since.Prompts)
            {
                if (mine is null)
                {
                    if (!plan.Says(prompt)) { other = true; continue; }
                    if (!TakePrompt(transcript, prompt.Offset)) { continue; }
                    mine = prompt.Offset;
                    seen.At ??= timeline.Elapsed();
                    timeline.Recorded(prompt.At);
                }
                if (prompt.Offset != mine) { continue; }
                if (plan.Attached(prompt))
                {
                    timeline.Confirmed();
                    return new ReplyResult(AnswerOutcome.Delivered);
                }
                // Codex records its images with the prompt: missing then is missing for good.
                if (agent == AgentKind.Codex) { return Unconfirmed("Codex recorded the reply but did not attach every image to it"); }
            }
            if (options.Time.GetElapsedTime(started) >= options.ConfirmTimeout)
            {
                return Unconfirmed(mine is not null
                    ? $"{name} recorded the reply but has not read every file it mentions yet"
                    : other
                    ? $"{name} recorded a different message, not this reply"
                    : $"{name} did not record the reply within {options.ConfirmTimeout.TotalSeconds:0} seconds");
            }
            await TranscriptWrites.WaitAsync(written, options.PollInterval, options.Time);
        }
    }

    // Writes a reply's attachments where the agent can read them, or says why it cannot.
    ReplyResult? Stage(Claim claim, string agent)
    {
        // Each agent takes a path up to the first space, and no quoting is known to work.
        if (Path.GetFullPath(attachments!.StagingRoot).Any(char.IsWhiteSpace))
        {
            log($"cannot stage attachments: the folder {attachments.StagingRoot} has whitespace in its path, which an @ mention cannot carry");
            return Refused($"the host's attachment folder has a space in its path, which {agent} cannot take as an attachment; attach at the machine");
        }
        try
        {
            var paths = attachments.Stage(claim);
            log($"staged {paths.Count} attachment(s) ({claim.Bytes / 1024.0:F0} KiB) privately in {attachments.StagingRoot}");
            return null;
        }
        catch (InvalidDataException e) { return Refused(e.Message); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return Failed(AnswerOutcome.Refused, "could not stage attachments", e);
        }
    }

    // Watches the transcript, from where it stood before the keys went, for the person's
    // prompt that is this reply.
    async Task<ReplyResult> ConfirmAsync(string transcript, long from, string words, IReadOnlyList<string> attached, DeliveryTimeline timeline, Seen seen)
    {
        var started = options.Time.GetTimestamp();
        var other = false;
        while (true)
        {
            using var written = options.Writes?.Next(transcript);
            var since = CopilotPrompts.Since(transcript, from);
            foreach (var prompt in since.Prompts)
            {
                if (!CopilotReply.Matches(words, prompt.Content)) { other = true; continue; }
                if (!TakePrompt(transcript, prompt.Offset)) { continue; }
                seen.At ??= timeline.Elapsed();
                timeline.Recorded(prompt.At);
                if (!CopilotReply.Attached(attached, prompt.Attachments))
                {
                    return Unconfirmed("the session recorded the reply but did not attach every file to it");
                }
                timeline.Confirmed();
                return new ReplyResult(AnswerOutcome.Delivered);
            }
            if (since.SessionEnded)
            {
                seen.Ended = true;
                return Unconfirmed("the session ended before it recorded the reply");
            }
            if (options.Time.GetElapsedTime(started) >= options.ConfirmTimeout)
            {
                return Unconfirmed(other
                    ? "the session recorded a different message, not this reply"
                    : $"the session did not record the reply within {options.ConfirmTimeout.TotalSeconds:0} seconds");
            }
            await TranscriptWrites.WaitAsync(written, options.PollInterval, options.Time);
        }
    }

    // Keys have gone: the reply is confirmed by its prompt, which `find` looks for again, untaken,
    // once a confirmation has timed out (#1025).
    static Attempt Typing(AgentKind agent, Func<(bool Found, DateTimeOffset? At)> find, Func<Seen, Task<ReplyResult>> confirm)
    {
        var seen = new Seen(agent, find);
        return new Attempt(null, new Typed(() => confirm(seen), seen), KeysWent: true);
    }

    static (bool Found, DateTimeOffset? At) FirstOf<T>(IEnumerable<T> prompts, Func<T, bool> mine, Func<T, DateTimeOffset?> at)
    {
        foreach (var p in prompts) { if (mine(p)) { return (true, at(p)); } }
        return (false, null);
    }

    static bool NotYetWritten(string transcript, Session session) =>
        session.Activity.TurnCount is null
        && !File.Exists(transcript)
        && Path.GetDirectoryName(transcript) is { } folder && Directory.Exists(folder);

    bool TakePrompt(string transcript, long offset)
    {
        var now = options.Time.GetUtcNow();
        foreach (var old in claimed.Where(c => now - c.Value > options.RememberFor).Select(c => c.Key).ToList()) { claimed.TryRemove(old, out _); }
        return claimed.TryAdd((transcript, offset), now);
    }

    static string Short(string id) => id.Length > 8 ? id[..8] : id;

    // What went wrong goes to the host's log only: the person is given fixed words, never an
    // exception's text (#802, #995).
    ReplyResult Failed(AnswerOutcome outcome, string what, Exception e) => new(outcome, Logged(what, e));

    string Logged(string what, Exception e)
    {
        log($"{what}: {e.GetType().Name}: {e.Message}");
        return DeliveryReasons.CouldNotDeliver;
    }

    static ReplyResult Refused(string reason) => new(AnswerOutcome.Refused, reason);
    static ReplyResult Stale(string reason) => new(AnswerOutcome.Stale, reason);
    static ReplyResult Busy() => new(AnswerOutcome.Busy, "another answer or reply to this session is being delivered");
    static ReplyResult Unconfirmed(string reason) => new(AnswerOutcome.Unconfirmed, reason);
    // Typed words would answer the prompt, and Enter accept it. One that is only answered at
    // the machine (a permission prompt) says so, since answering it first is done there.
    static ReplyResult Asking(Question? question = null) => new(AnswerOutcome.Refused, question?.AnswerAtMachine == true
        ? "the session is waiting at a prompt that can only be answered at the machine; answer it there first"
        : "the session is waiting for an answer to its question; answer that first");
}

// macOS and Linux, until delivery through tmux exists: hello never offers sessions.reply,
// and a reply sent anyway is refused without touching anything.
public sealed class UnsupportedPlatformReplier : ISessionReplier
{
    public bool Available => false;

    public Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default, DeliveryWatch? watch = null) =>
        Task.FromResult(new ReplyResult(AnswerOutcome.Refused, "replying is not supported on this platform yet; reply at the machine"));
}
