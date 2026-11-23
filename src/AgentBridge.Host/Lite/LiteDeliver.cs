using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using AgentBridge.AgentEnvelope;
using AgentBridge.HomeAssistant;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.AgentReplies;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Lite;

// L5 of the temporary AP5-lite mode (#1353): types a verified own-agent message into an idle
// session, or offers it to the host-only lane adapter when supplied. No production lane is wired.
// The caller (L4) has verified the signature; the text is re-bound to the envelope here
// because this is the last step before keys go to a live agent.
public sealed class LiteDeliver(
    IReadOnlyList<IReplyTargetGate> gates,
    ISessionSource source,
    ISessionReplier replier,
    IConsoleInjector injector,
    Action<string> log,
    TimeProvider? clock = null,
    Func<string, Func<bool>>? targetFence = null,
    Func<string?>? account = null) : ILiteDeliver, ILiteReplayQuery
{
    public const int MaxChars = 4096;
    public const int MaxPerMinute = 6;
    static readonly string Brand = Product.Name;

    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> recent = new(StringComparer.Ordinal);
    internal DeliveryLaneScheduler? Lane { get; init; }
    // claudeBusyQueue (docs/message-delivery.md, #3828): read afresh per send; null or throwing means off.
    internal Func<bool>? ClaudeBusyQueue { get; init; }
    // The host's record of who drives each session; unset, a relayed prompt is not marked as the agent's.
    public SessionDrivers? Drivers { get; init; }

    public async Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct)
    {
        var body = envelope.Body;
        var target = body.Target.Session ?? "";
        var outcome = await DecideAsync(envelope, text, target, ct);
        var sender = body.Principal.Kind == "script" ? Clean(body.Principal.SenderId) : $"{Clean(body.Principal.Machine)}/{Clean(body.Principal.Session)}";
        log($"lite deliver: from {sender} to {Clean(target)} request {Clean(body.Request)}: {(outcome.Typed ? "typed" : outcome.Code)}");
        return outcome;
    }

    Task<LiteDelivery> ILiteReplayQuery.QueryAsync(VerifiedEnvelope envelope, string text, CancellationToken ct) =>
        DecideAsync(envelope, text, envelope.Body.Target.Session ?? "", ct, query: true);

    async Task<LiteDelivery> DecideAsync(VerifiedEnvelope envelope, string text, string target, CancellationToken ct, bool query = false)
    {
        ct.ThrowIfCancellationRequested();
        if (query && Lane is null) { return Refused(LiteReceiver.Replay); }
        var body = envelope.Body;
        string safe;
        try { safe = Escape(Lane is null ? text : DeliveryEnvelope.NormalizeBody(text)); }
        catch (ArgumentException) { return Refused(LiteCodes.NotReplyTarget); } // not valid Unicode
        if (string.IsNullOrEmpty(text) || text.Length > MaxChars || !body.BindsData(Encoding.UTF8.GetBytes(text)) || target.Length == 0 || !Guid.TryParseExact(body.Request, "D", out _)) { return Refused(LiteCodes.NotReplyTarget); }
        var id = target;
        Session? session = null;
        var byNode = false;
        void Resolve()
        {
            session = source.Get(target);
            if (session is null && FromNodeId(target) is { } resolved) { id = resolved; byNode = true; session = source.Get(id); }
        }
        if (Lane is null) { Resolve(); }
        var sender = new DeliverySender(body.Principal.Kind == "script" ? body.Principal.SenderId :
            $"{body.Principal.Machine}/{body.Principal.SenderId}");
        var request = Guid.Parse(body.Request);
        // Journal NFKC must not restore an escaped attachment token.
        var laneBody = safe.Replace("\uFF20", "(at)", StringComparison.Ordinal);
        // The text was signed for one account, and a local send checks it only before this await (#3595): an account
        // switch (or no account) while the composer is read or the typing slot is awaited types nothing, here and at both replier fences.
        bool Signed() => account is null || account() == body.Account;
        if (!Signed()) { return Refused(LiteReceiver.NotSameAccount); }
        if (Lane is { } history)
        {
            // A known UUID's authenticated target is history, not renewed node routing.
            if (history.BoundTarget(request, sender) is { } bound && HaContract.NodeId(bound) == target) { id = bound; }
            var prior = history.Query(request, id, sender);
            // A false fence forbids new admission while Offer verifies the existing body's HMAC.
            if (prior.State != DeliveryOfferState.Refused) { return FromLane(history.Offer(request, id, sender, laneBody, () => false)); }
            if (query) { return FromLane(prior); }
        }
        if (Lane is not null) { Resolve(); }
        if (session is null) { return Refused(UnresolvedCode(target)); }
        var stillAllowed = targetFence?.Invoke(id);
        if (!await AllowedAsync(envelope, id)) { return Refused(LiteCodes.NotReplyTarget); }
        // Copilot and Claude only; Codex and unknown stay refused. The lane's handoff is Copilot's alone (the replier refuses it for Claude).
        if (session.Agent is not (AgentKind.Copilot or AgentKind.Claude) || session.ProcessId is not { } pid || Lane is not null && session.Agent != AgentKind.Copilot)
        { return Refused(LiteCodes.NotReplyTarget); }
        if (Lane is { } lane)
        {
            if (stillAllowed is null) { return Refused(LiteCodes.NotReplyTarget); }
            if (session.Question is not null || session.Status is not (SessionStatus.Idle or SessionStatus.Working))
            { return Refused(LiteCodes.Busy); }
            var incarnation = source.ProcessOf(id) is { Id: > 0, StartedAt: not null } captured && captured.Id == pid ? captured : null;
            bool Revoked() => account?.Invoke() is { Length: > 0 } signedIn && signedIn != body.Account ||
                incarnation is not null && source.ProcessOf(id) is { Id: > 0, StartedAt: not null } live && live != incarnation;
            bool Current() => Signed() && stillAllowed() && (!byNode || FromNodeId(target) == id) &&
                source.Get(id) is { Agent: AgentKind.Copilot, Question: null } live && live.ProcessId == pid &&
                live.Status is SessionStatus.Idle or SessionStatus.Working;
            if (!WithinRate(id)) { return Refused(LiteCodes.Busy); }
            return FromLane(lane.Offer(request, id, sender, laneBody, Current, Revoked));
        }
        // claudeBusyQueue (#3828): a working Claude is typed once and Claude Code queues it. Read once per send; a pending question still refuses.
        var busyQueue = session.Agent == AgentKind.Claude && BusyQueueOn();
        if (!Ready(session, busyQueue)) { return Refused(LiteCodes.Busy); }
        if (!await ComposerEmptyAsync(session.Agent, pid, ct)) { return Refused(LiteCodes.ComposerNotEmpty); }
        // Check before handing off; RequireIdle also rechecks once the replier holds the typing slot.
        // A node id is resolved again: a sibling or a hold that appeared while the composer was read
        // makes it ambiguous now, whatever it named a moment ago.
        if (source.Get(id) is not { } now || now.ProcessId != pid || (byNode && FromNodeId(target) != id)) { return Refused(byNode && source.Held is not null ? LiteCodes.SessionResolutionIncomplete : LiteCodes.NoSuchSession); }
        if (!Ready(now, busyQueue)) { return Refused(LiteCodes.Busy); }
        if (!WithinRate(id)) { return Refused(LiteCodes.Busy); }
        // ReplyService runs StillCurrent again after waiting for the shared typing slot and once more
        // just before the injector starts. The host rechecks kill, remote, current grant, process generation and account at both fences.
        var framed = Frame(body.Principal, body.Request, safe);
        // The prompt this types is the sender's: armed, once the typing slot is held and the keys are
        // about to go, with the exact words and a label from the verified principal, never from the body,
        // so userMessageFrom=agent is set by the host alone. A delivery that typed nothing (refused,
        // busy, or already delivered earlier) never arms, so a prompt the owner types in the same words
        // stays theirs; one not confirmed puts back an arm still waiting, for the same reason.
        var driver = Drivers?.For(id);
        DriverArm? armed = null;
        var reply = new ReplyRequest { Id = id, Request = body.Request, Text = framed, RequireIdle = !busyQueue,
            StillCurrent = byNode || stillAllowed is not null || account is not null
                ? () => Signed() && (!byNode || FromNodeId(target) == id) && (stillAllowed?.Invoke() ?? true) : null,
            KeysGoing = driver is null ? null : () =>
            {
                var (saved, generation) = driver.ArmAgent(time.GetUtcNow(), Limits.KeepStart(Principal(body.Principal), Limits.DriverPrincipal), framed, SenderLabel(body.Principal));
                armed = new DriverArm(saved, generation);
            } };
        void Unarm() { if (driver is not null && armed is { } arm) { driver.Restore(arm.Before, arm.Generation); } }
        ReplyResult result;
        try { result = await replier.ReplyAsync(reply, null, ct); }
        catch { Unarm(); throw; }
        if (result.Outcome != AnswerOutcome.Delivered) { Unarm(); }
        return result.Outcome switch
        {
            AnswerOutcome.Delivered or AnswerOutcome.Unconfirmed => new LiteDelivery(true, null),
            AnswerOutcome.Busy => Refused(LiteCodes.Busy),
            AnswerOutcome.Stale => Refused(Signed() ? (byNode && source.Held is not null ? LiteCodes.SessionResolutionIncomplete : LiteCodes.NoSuchSession) : LiteReceiver.NotSameAccount),
            _ => Refused(result.Refusal switch { ReplyRefusal.Asking or ReplyRefusal.Busy => LiteCodes.Busy, ReplyRefusal.ComposerUnverified or ReplyRefusal.Occupied => LiteCodes.ComposerNotEmpty, _ => LiteCodes.NotReplyTarget }),
        };
    }

    static LiteDelivery FromLane(DeliveryOfferStatus status) => new(status.State == DeliveryOfferState.Delivered, status.Reason)
    {
        Outcome = status.State switch
        {
            DeliveryOfferState.Pending => LiteKind.Pending,
            DeliveryOfferState.Queued => LiteKind.Queued,
            DeliveryOfferState.Unconfirmed => LiteKind.Unconfirmed,
            DeliveryOfferState.Delivered => LiteKind.Delivered,
            _ => LiteKind.Refused,
        },
    };

    // Any gate allowing is enough (forced-ask OR allowlisted); one that throws allows nothing.
    async Task<bool> AllowedAsync(VerifiedEnvelope envelope, string target)
    {
        foreach (var gate in gates)
        {
            try
            {
                var decision = await gate.CheckAsync(envelope, target);
                log($"lite deliver: to {Clean(target)} request {Clean(envelope.Body.Request)} gate {gate.GetType().Name}: {(decision.Allowed ? "allowed" : "refused")}{(decision.Reason is { } reason ? ": " + Clean(reason) : "")}");
                if (decision.Allowed) { return true; }
            }
            catch (Exception e) when (e is not OperationCanceledException) { log($"lite deliver: to {Clean(target)} request {Clean(envelope.Body.Request)} gate {gate.GetType().Name}: failed: {e.GetType().Name}"); }
        }
        return false;
    }

    // The roster and fleet.find publish only the lossy HA node id, so a fleet sender can name no
    // other target (#3399). Accept it only when exactly one live session maps to it: guessing
    // between several would type into a session the sender never chose. "Exactly one" is only
    // known while discovery is not held (ISessionSource.Held): a held pass may not list a second
    // live session with the same node id. So Held is read before the one materialised snapshot and
    // again after scanning it, as FleetPublisher does; either refuses. Exact ids never come here.
    // A node id that did not resolve while discovery is held may name a live session the pass did not list, so the
    // sender is told to retry, not that the session does not exist (#1396).
    string UnresolvedCode(string target) =>
        IsNodeId(target) && source.Held is not null ? LiteCodes.SessionResolutionIncomplete : LiteCodes.NoSuchSession;

    static bool IsNodeId(string target) =>
        target.Length == "agent_bridge_".Length + 16 && target.StartsWith("agent_bridge_", StringComparison.Ordinal)
        && !target.AsSpan("agent_bridge_".Length).ContainsAnyExcept("0123456789abcdef");

    string? FromNodeId(string target)
    {
        const string prefix = "agent_bridge_";
        if (target.Length != prefix.Length + 16 || !target.StartsWith(prefix, StringComparison.Ordinal)
            || target.AsSpan(prefix.Length).ContainsAnyExcept("0123456789abcdef")) { return null; }
        if (source.Held is not null) { return null; }
        string? found = null;
        foreach (var s in source.Snapshot())
        {
            if (!string.Equals(HaContract.NodeId(s.Id), target, StringComparison.Ordinal)) { continue; }
            if (found is not null && !string.Equals(found, s.Id, StringComparison.Ordinal)) { return null; }
            found = s.Id;
        }
        return source.Held is null ? found : null;
    }

    static bool Ready(Session s, bool busyQueue) => s.Question is null && (s.Status == SessionStatus.Idle || busyQueue && s.Status == SessionStatus.Working);

    bool BusyQueueOn()
    {
        try { return ClaudeBusyQueue?.Invoke() == true; }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    bool WithinRate(string target)
    {
        var now = time.GetUtcNow();
        var queue = recent.GetOrAdd(target, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= TimeSpan.FromMinutes(1)) { queue.Dequeue(); }
            if (queue.Count >= MaxPerMinute) { return false; }
            queue.Enqueue(now);
            return true;
        }
    }

    // Claude's box is found by its rules; it shows a suggested prompt in an empty box that a screen read
    // cannot tell from typed words, so it is held to what the replier checks: on screen, no command waiting.
    async Task<bool> ComposerEmptyAsync(AgentKind agent, int pid, CancellationToken ct)
    {
        try
        {
            var screen = await injector.ReadScreenAsync(pid, ct);
            return screen.Rows is { } rows && (agent == AgentKind.Claude
                ? ClaudeComposer.Find(rows) is { } box && !box.Content.StartsWith('/') && !box.Content.StartsWith('!')
                : CopilotInputBoxReader.Read(rows).Box is not null);
        }
        catch (Exception e) when (e is not OperationCanceledException) { return false; }
    }

    // The arm this delivery made, to put back only while it is still the one waiting.
    readonly record struct DriverArm(SessionDriver.Saved Before, long Generation);

    // Both from the verified principal's own fields, one line, nothing hidden (Clean), and cut to the
    // protocol's byte limits where they are shown. The sender's title and the body are not read.
    static string Principal(AgentPrincipal from) =>
        from.Kind == "script" ? $"script:{Clean(from.Machine)}/{Clean(from.SenderId)}" : $"agent:{Clean(from.Machine)}/{Clean(from.Session)}";

    static string? SenderLabel(AgentPrincipal from) => Limits.KeepStart(
        from.Kind == "script" ? $"script {Clean(from.SenderId)}" : $"{Clean(from.Session)} on {Clean(from.Machine)}", Limits.UserMessageLabel);

    // The provenance line: who sent it, from where, and that the user did not type it.
    static string Frame(AgentPrincipal from, string request, string escaped) =>
        from.Kind == "script"
            ? $"[{Brand} · from script {Clean(from.SenderId)} · request {Clean(request)} · not typed by your user] {escaped}"
            : $"[{Brand} · from an agent on {Clean(from.Machine)} · session {Clean(from.Session)} · request {Clean(request)} · not typed by your user] {escaped}";

    // Nothing in the body may pass as part of the header or as a key or an attachment.
    static string Escape(string text)
    {
        var s = string.Concat(text.Normalize(NormalizationForm.FormKC).Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format))
            .Replace("[" + Brand, "(" + Brand, StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            // The typing path turns control characters into spaces, so they also start a token; format characters (zero-width, bidi) are dropped.
            var atTokenStart = i == 0 || char.IsWhiteSpace(s[i - 1]) || char.GetUnicodeCategory(s[i - 1]) is UnicodeCategory.Control or UnicodeCategory.OtherNotAssigned;
            sb.Append(s[i] == '@' && atTokenStart ? '\uFF20' : s[i]);
        }
        return sb.ToString();
    }

    static string Clean(string? value)
    {
        var sb = new StringBuilder();
        foreach (var c in value ?? "")
        {
            var category = char.GetUnicodeCategory(c);
            if (c is '[' or ']' or '"' or '·') { sb.Append(' '); }
            else if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned) { }
            else { sb.Append(c); }
        }
        return sb.ToString().Trim();
    }

    static LiteDelivery Refused(string code) => new(false, code);
}
