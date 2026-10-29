using System.Collections.Concurrent;
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

    Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default);
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

    // How long Claude or Codex is given to record a reply before its Enter is taken to have
    // been swallowed as part of the paste, and pressed again (agent-ha-bridge's
    // Confirm-DaemonClaudeSubmit waited 1.2 s), and how many times at most.
    public TimeSpan SubmitAfter { get; init; } = TimeSpan.FromMilliseconds(1500);

    public int ExtraEnters { get; init; } = 2;

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
    // the start once and then only as it grows. One small entry per session replied to.
    readonly ConcurrentDictionary<string, CopilotInput> inputs = new(StringComparer.Ordinal);
    readonly AttachmentStore? attachments;

    public ReplyService(ISessionSource source, IProcessInspector processes, IAgentHaBridgeProbe probe, ITargetGuard guard,
        IConsoleInjector injector, ReplyOptions options, Action<string>? log = null, DeliveryGate? gate = null,
        AttachmentStore? attachments = null)
    {
        this.source = source;
        this.processes = processes;
        this.probe = probe;
        this.guard = guard;
        this.injector = injector;
        this.options = options;
        this.log = log ?? (_ => { });
        this.gate = gate ?? new DeliveryGate();
        this.attachments = attachments;
        requests = new OutcomeMemory<ReplyResult>(options.Time, options.RememberFor);
    }

    public bool Available => options.PlatformSupported && probe.Active() is null;

    // Whether hello offers sessions.attach: replies are on and there is somewhere to stage files.
    public bool TakesAttachments => attachments is not null && Available;

    // peer: the device the request came from. A reply can only use attachments that device sent.
    public Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParseExact(request.Request, "D", out var key))
        {
            return Task.FromResult(Refused("request must be a UUID"));
        }
        return requests.Once(key, () => DeliverAsync(request, key, peer ?? ""), result =>
            log($"reply {key.ToString()[..8]} to {Short(request.Id)}: {result.Outcome.ToString().ToLowerInvariant()}{(result.Reason is null ? "" : $" ({result.Reason})")}"));
    }

    async Task<ReplyResult> DeliverAsync(ReplyRequest request, Guid key, string peer)
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
                if (refs.Count > 0)
                {
                    (claim, var problem) = attachments!.Claim(peer, refs);
                    if (problem is not null) { return Refused(problem); }
                }
                var outcome = session.Agent == AgentKind.Copilot
                    ? await CopilotAsync(session, request.Text ?? "", claim)
                    : await AgentAsync(session, request.Text ?? "", claim);
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
            return Refused($"the host could not send the reply: {e.Message}");
        }

        // The session is free again once the keys have gone: a question it asks next can be
        // answered while this waits for the prompt to be taken, which for a busy session can
        // be a while.
        try { return await typed.Confirm(); }
        catch (Exception e) { return Unconfirmed($"the reply was typed but could not be checked ({e.Message}); check at the machine"); }
    }

    // What a reply that went is confirmed by.
    sealed record Typed(Func<Task<ReplyResult>> Confirm);

    // KeysWent: some or all of the reply may have reached the session, whatever the result.
    sealed record Attempt(ReplyResult? Result, Typed? Typed, bool KeysWent = false);

    async Task<Attempt> CopilotAsync(Session session, string text, Claim? claim)
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
        // Caught up before the typing turn is taken, so the first reply to a session with a
        // long transcript reads it without holding up every other delivery; the read in the
        // turn only covers what has been written since.
        var input = inputs.GetOrAdd(transcript, path => new CopilotInput(path));
        input.Read();

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
            if (now.Question is { } asking) { return Done(Asking(asking)); }
            // The transcript, read now, may be ahead of the session source: a question asked a
            // moment ago is there first, and a subagent's is only there. One that cannot be
            // read cannot say nothing is asking.
            var before = input.Read();
            if (!before.Readable) { return Done(Refused("the session's transcript could not be read, so nothing is typed; reply at the machine")); }
            if (before.Asking) { return Done(Asking()); }
            from = before.ScannedTo;

            try { result = await injector.InjectAsync(pid, keys); }
            catch (Exception e)
            {
                return new Attempt(Unconfirmed($"the injector failed while typing ({e.Message}); check at the machine"), null, KeysWent: true);
            }
        }
        finally { gate.DoneTyping(); }

        log($"typed a reply into {Short(session.Id)} (pid {pid}){(staged.Count > 0 ? $" with {staged.Count} attachment(s)" : "")}: {keys.Describe()} -> {result.Status} {result.Detail}");
        return result.Status switch
        {
            InjectionStatus.NothingTyped => Done(Refused(result.Detail)),
            InjectionStatus.Interrupted => new Attempt(Unconfirmed($"{result.Detail}; part of the reply may have reached the session, so check at the machine"), null, KeysWent: true),
            _ => new Attempt(null, new Typed(() => ConfirmAsync(transcript, from, words, staged)), KeysWent: true),
        };

        static Attempt Done(ReplyResult r) => new(r, null);
    }

    // A reply to Claude Code or Codex, typed as agent-ha-bridge types one (Invoke-DaemonReply):
    // one line, then Enter on its own, 300 ms later. What differs is what guards it. Without
    // agent-ha-bridge's hooks nothing tells the host that a permission prompt, an
    // AskUserQuestion or an approval is waiting, so it reads the screen: the reply is typed
    // only into an empty input box that is on screen, and only while those rows still read so
    // (Composer). Then, as Confirm-DaemonClaudeSubmit did, an Enter the agent took as part of a
    // paste (Claude absorbs it; Codex makes it a new line, seen live) is pressed again, at most
    // twice, and only while the box on screen still holds exactly this reply.
    async Task<Attempt> AgentAsync(Session session, string text, Claim? claim)
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
            if (agent == AgentKind.Codex && claim.Items.Any(i => !AttachmentFiles.IsPastableImage(i.Bytes)))
            {
                return Done(Refused("Codex takes only images (PNG, JPEG, GIF or WebP) as attachments; send other files at the machine"));
            }
            if (Stage(claim, name) is { } failed) { return Done(failed); }
            staged = claim.Staged;
        }
        var plan = agent == AgentKind.Claude ? AgentReply.Claude(text, staged) : AgentReply.Codex(text, staged);
        if (plan is null) { return Done(Refused($"an attachment's path has a space in it, which {name} cannot take as an attachment; attach at the machine")); }

        await gate.WaitToTypeAsync();
        long from;
        KeyPlan keys;
        InjectionResult result;
        try
        {
            if (probe.Active() is { } active) { return Done(Refused(active)); }
            var now = source.Get(session.Id);
            if (now is null) { return Done(Stale("the session has ended")); }
            if (now.Question is { } asking) { return Done(Asking(asking)); }
            ConsoleScreen screen;
            try { screen = await injector.ReadScreenAsync(pid); }
            catch (Exception e) { return Done(Refused($"the host could not read the session's screen ({e.Message}); reply at the machine")); }
            if (screen.Rows is not { } rows) { return Done(Refused(screen.Problem ?? "the host could not read the session's screen; reply at the machine")); }
            var box = Find(agent, rows);
            if (box is null)
            {
                return Done(Refused($"{name}'s input box is not on its screen, so it may be asking something at the machine; answer it there, or reply at the machine"));
            }
            if (!box.Empty) { return Done(Refused($"something is already typed in {name}'s input box at the machine; send or clear it there first")); }
            keys = plan.Keys(box.Rows);
            // The agent only appends, and a line still being written at this point is not a
            // prompt typed now: reading from here, it does not parse.
            from = Jsonl.Length(transcript);
            try { result = await injector.InjectAsync(pid, keys); }
            catch (Exception e)
            {
                return new Attempt(Unconfirmed($"the injector failed while typing ({e.Message}); check at the machine"), null, KeysWent: true);
            }
        }
        finally { gate.DoneTyping(); }

        log($"typed a reply into {name} {Short(session.Id)} (pid {pid}){(staged.Count > 0 ? $" with {staged.Count} attachment(s)" : "")}: {keys.Describe()} -> {result.Status} {result.Detail}");
        switch (result.Status)
        {
            case InjectionStatus.NothingTyped:
                return Done(Refused(result.Detail == WorkerInjector.ScreenChanged
                    ? $"{name}'s screen changed just as the reply was to be typed, so nothing was typed; try again, or reply at the machine"
                    : result.Detail));
            case InjectionStatus.Interrupted:
                return new Attempt(Unconfirmed($"{result.Detail}; part of the reply may have reached the session, so check at the machine"), null, KeysWent: true);
        }

        await SubmitAsync(session.Id, agent, pid, transcript, from, plan);
        return new Attempt(null, new Typed(() => ConfirmAgentAsync(agent, transcript, from, plan)), KeysWent: true);

        static Attempt Done(ReplyResult r) => new(r, null);
    }

    static Composer? Find(AgentKind agent, IReadOnlyList<string> rows) => agent == AgentKind.Claude ? ClaudeComposer.Find(rows) : CodexComposer.Find(rows);

    // Presses Enter again while the reply is still sitting in the input box unrecorded.
    async Task SubmitAsync(string sessionId, AgentKind agent, int pid, string transcript, long from, AgentReply plan)
    {
        for (var pressed = 0; ; pressed++)
        {
            var started = options.Time.GetTimestamp();
            while (options.Time.GetElapsedTime(started) < options.SubmitAfter)
            {
                if (Prompts(agent, transcript, from).Prompts.Any(plan.Says)) { return; }
                await Task.Delay(options.PollInterval, options.Time);
            }
            if (pressed >= options.ExtraEnters) { return; }

            await gate.WaitToTypeAsync();
            try
            {
                // An Enter goes wherever the session's input is now: never while agent-ha-bridge
                // runs, and only into the box holding this reply, so never into a prompt.
                if (probe.Active() is not null) { return; }
                if (Prompts(agent, transcript, from).Prompts.Any(plan.Says)) { return; }
                if (!processes.GetAgentProcesses(agent).Any(p => p.Id == pid) || guard.Refuse(pid) is not null) { return; }
                var screen = await injector.ReadScreenAsync(pid);
                if (screen.Rows is not { } rows || Find(agent, rows) is not { } box || !box.Holds(plan.Composed))
                {
                    log($"not pressing Enter again in {Short(sessionId)}: its input box does not hold the reply");
                    return;
                }
                var enter = await injector.InjectAsync(pid, new KeyPlan([new KeySend("", true, 0, 0, box.Rows)]));
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
    async Task<ReplyResult> ConfirmAgentAsync(AgentKind agent, string transcript, long from, AgentReply plan)
    {
        var name = agent == AgentKind.Claude ? "Claude" : "Codex";
        var started = options.Time.GetTimestamp();
        var other = false;
        long? mine = null;
        while (true)
        {
            var since = Prompts(agent, transcript, from);
            foreach (var prompt in since.Prompts)
            {
                if (mine is null)
                {
                    if (!plan.Says(prompt)) { other = true; continue; }
                    if (!TakePrompt(transcript, prompt.Offset)) { continue; }
                    mine = prompt.Offset;
                }
                if (prompt.Offset != mine) { continue; }
                if (plan.Attached(prompt)) { return new ReplyResult(AnswerOutcome.Delivered); }
                // Codex records its images with the prompt: missing then is missing for good.
                if (agent == AgentKind.Codex) { return Unconfirmed("Codex recorded the reply but did not attach every image to it; check at the machine"); }
            }
            if (options.Time.GetElapsedTime(started) >= options.ConfirmTimeout)
            {
                return Unconfirmed(mine is not null
                    ? $"{name} recorded the reply but has not read every file it mentions yet; check at the machine"
                    : other
                    ? $"{name} recorded a different message, not this reply; check at the machine"
                    : $"{name} did not record the reply within {options.ConfirmTimeout.TotalSeconds:0} seconds; check at the machine");
            }
            await Task.Delay(options.PollInterval, options.Time);
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
            log($"could not stage attachments: {e.Message}");
            return Refused($"the host could not stage the attachments privately ({e.Message}); nothing was sent");
        }
    }

    // Watches the transcript, from where it stood before the keys went, for the person's
    // prompt that is this reply.
    async Task<ReplyResult> ConfirmAsync(string transcript, long from, string words, IReadOnlyList<string> attached)
    {
        var started = options.Time.GetTimestamp();
        var other = false;
        while (true)
        {
            var since = CopilotPrompts.Since(transcript, from);
            foreach (var prompt in since.Prompts)
            {
                if (!CopilotReply.Matches(words, prompt.Content)) { other = true; continue; }
                if (!TakePrompt(transcript, prompt.Offset)) { continue; }
                return CopilotReply.Attached(attached, prompt.Attachments)
                    ? new ReplyResult(AnswerOutcome.Delivered)
                    : Unconfirmed("the session recorded the reply but did not attach every file to it; check at the machine");
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

    bool TakePrompt(string transcript, long offset)
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

    public Task<ReplyResult> ReplyAsync(ReplyRequest request, string? peer = null, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ReplyResult(AnswerOutcome.Refused, "replying is not supported on this platform yet; reply at the machine"));
}
