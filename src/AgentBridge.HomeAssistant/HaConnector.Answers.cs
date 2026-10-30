using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;

namespace AgentBridge.HomeAssistant;

// Stage 2: answering a session's question from its card (docs/ha-connector.md). The card's
// controls are agent-ha-bridge's, armed exactly as its daemon arms them (HaDecision); what they
// hold is read the way Read-DaemonDecisionAnswer and Read-DaemonFormAnswer read it, and the
// answer goes to the host's own delivery (session.answer), which checks it against the question
// and types it in. Replies, stopping and launching are later stages: a press for one of those is
// said to be unserved on the card rather than silently dropped.
public sealed partial class HaConnector
{
    // What a session's card is armed with, and what its answer channels held at that moment:
    // anything they hold later is an answer only if it is determinately different.
    sealed class Armed
    {
        public required Question Question;
        public required DecisionPlan Plan;
        public required Observation PayloadBaseline;
        public required Observation SubmitBaseline;
        public required Observation ReplyBaseline;
        // A press spent at an answer that was not complete yet, so the tap that completes it
        // is not taken for a Send (Set-DaemonPressSpent).
        public string? SpentPress;
        // A press or a publish already used, so it is never delivered twice.
        public string? UsedPress;
        public string? UsedStamp;
        // An answer reached, or may have reached, the terminal: never typed again.
        public bool Settled;
        // Answers that reached nothing. Each new attempt is a request of its own, and nothing that
        // failed is sent again by itself: words in the reply box or from the reply card go again
        // only with a Send (or box) hit the watch reported after the failure, and a tap only once
        // the selector has been seen waiting again. Boundary is the hit number at arming, then at
        // each failure: nothing reported before it is a press for what came after.
        public int Attempts;
        public long Boundary;
        public string? FailedBox;
        // A reply-card publish whose words may fill the form but cannot submit it by themselves:
        // it reached nothing, or came while a field was still unanswered.
        public string? HeldStamp;
        public bool AwaitTap;
        // Send as read when an answer failed: a later press read over REST differs from it, so a
        // press made while the watch was down still counts as fresh.
        public Observation? SubmitAtFailure;
        // Field slots arming could not reset (slot, what it showed then, what it was to show): until
        // they change, what they show is the last question's, not an answer to this one.
        public Dictionary<int, (string? Stale, string Intended)> StaleSlots = [];
        // The reply box when reply-card words were held: box words changed since then are newer,
        // and replace the held ones.
        public string? BoxAtHold;
        // Said once, not on every hit.
        public string? Told;
    }

    // Get-CopilotDecisionChannelObservation: present (with a value), absent (determinately
    // nothing: no entity, or Home Assistant's own "unknown"), or unknown (nobody can tell).
    internal sealed record Observation(string Kind, string Value, HaState? Snapshot, bool Exists)
    {
        public static readonly Observation Unknown = new("unknown", "", null, false);
        public bool Present => Kind == "present";
    }

    readonly ConcurrentQueue<(long Seq, HaTrigger Hit)> hits = new();
    long hitSeq;
    long watchVersion;
    DateTimeOffset nextSweep;

    // Card controls changed: the watch reports them here, numbered in the order they came, and
    // the next pass reads the card.
    internal void Observe(HaTrigger hit) => hits.Enqueue((Interlocked.Increment(ref hitSeq), hit));

    // The entities a watch should cover: every published session's answer controls.
    internal IReadOnlyCollection<string> WatchedEntities()
    {
        lock (gate)
        {
            return [.. published.Values.SelectMany(p => Controls(p.Node)).Order(StringComparer.Ordinal)];
        }
    }

    static IEnumerable<string> Controls(string node)
    {
        yield return SessionEntities.Decision(node);
        yield return SessionEntities.Reply(node);
        yield return SessionEntities.ReplyPayload(node);
        yield return SessionEntities.Submit(node);
        for (var i = 1; i <= HaContract.MaxFields; i++) { yield return SessionEntities.Field(node, i); }
    }

    // The watch: one subscription to every session's controls, rebuilt when the set changes and
    // again after a dropped connection, with a growing pause so a Home Assistant that is down is
    // not hammered (the daemon's watch backoff).
    async Task WatchLoopAsync(CancellationToken token)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!token.IsCancellationRequested)
        {
            var version = Interlocked.Read(ref watchVersion);
            var entities = started && pausedFor is null ? WatchedEntities() : [];
            if (entities.Count == 0)
            {
                try { await Task.Delay(options.Tick, time, token); } catch (OperationCanceledException) { return; }
                continue;
            }
            using var changed = CancellationTokenSource.CreateLinkedTokenSource(token);
            var monitor = Task.Run(async () =>
            {
                while (!changed.IsCancellationRequested && Interlocked.Read(ref watchVersion) == version && started && pausedFor is null)
                {
                    try { await Task.Delay(options.Tick, time, changed.Token); } catch (OperationCanceledException) { return; }
                }
                try { await changed.CancelAsync(); } catch (ObjectDisposedException) { }
            }, CancellationToken.None);
            try
            {
                await foreach (var hit in ha.WatchAsync(entities, changed.Token)) { Observe(hit); backoff = TimeSpan.FromSeconds(2); }
            }
            catch (OperationCanceledException) when (changed.IsCancellationRequested) { }
            catch (Exception e)
            {
                Problem("watch", $"Home Assistant connector watch failed: {Describe(e)}; watching again in {backoff.TotalSeconds:F0}s");
                try { await Task.Delay(backoff, time, token); } catch (OperationCanceledException) { return; }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(60).Ticks));
            }
            await changed.CancelAsync();
            await monitor;
        }
    }

    void WatchSetChanged() => Interlocked.Increment(ref watchVersion);

    // A session's question as its card should now show it: armed for a new one, cleared once
    // it has gone. Run from SessionAsync, so it follows the host's own view of the question.
    async Task QuestionAsync(Session full, Published p, string name, DateTimeOffset now, CancellationToken ct)
    {
        if (!ServesAnswers) { return; }
        var question = full.Question;
        if (question is not null && p.Armed?.Question.Id == question.Id) { return; }
        if (question is null)
        {
            if (p.Armed is null && p.Cleared) { return; }
            if (p.Armed is null && !await StaleQuestionAsync(p.Node, ct))
            {
                // A card just published, or taken over with nothing armed: already idle.
                p.Cleared = true;
                p.ArmedSlots = Math.Max(p.ArmedSlots, 0);
                return;
            }
            var slots = p.ArmedSlots >= 0 ? p.ArmedSlots : await ArmedSlotsAsync(p.Node, ct);
            await RunAsync(HaDecision.Clear(p.Node, name, options.MachineName, slots), p.Node, ct);
            p.Armed = null;
            p.Cleared = true;
            p.ArmedSlots = 0;
            return;
        }
        // What the answer channels hold before anything about this question is on screen: an
        // answer given after it is shown can then never be taken for what was already there.
        var payload = await ObserveAsync(SessionEntities.ReplyPayload(p.Node), ct);
        var submit = await ObserveAsync(SessionEntities.Submit(p.Node), ct);
        var reply = await ObserveAsync(SessionEntities.Reply(p.Node), ct);
        if (payload.Kind == "unknown" || submit.Kind == "unknown" || reply.Kind == "unknown")
        {
            // No snapshot, no question on screen (Set-CopilotMqttDecision); tried again next pass.
            // The reply box too: armed without its baseline, it could never be read for this one.
            lock (gate) { dirty.Add(full.Id); }
            return;
        }
        var plan = HaDecision.Plan(question, full.Agent);
        var armedSlots = p.ArmedSlots >= 0 ? p.ArmedSlots : await ArmedSlotsAsync(p.Node, ct);
        var unset = await RunAsync(HaDecision.Arm(plan, p.Node, name, options.MachineName, armedSlots, time.GetLocalNow()), p.Node, ct);
        // Everything the card is told about this question is out; nothing reported before now was
        // made seeing it.
        var boundary = Interlocked.Read(ref hitSeq);
        p.ArmedSlots = plan.UsesFieldSlots ? plan.Fields.Count : 0;
        p.Cleared = false;
        p.Armed = new Armed
        {
            Question = question, Plan = plan, PayloadBaseline = payload, SubmitBaseline = submit, ReplyBaseline = reply,
            Boundary = boundary,
            // A tap counts only once the selector is seen waiting under this question: one left
            // from the last question, or made while this one was being put on screen, was made
            // looking at something else.
            AwaitTap = plan.AnswerOnTap && !await ShowsWaitingAsync(p.Node, question.Id, ct),
            StaleSlots = await StaleSlotsAsync(plan, p.Node, unset, ct),
        };
        log($"Home Assistant connector armed {p.Node} for its question{(plan.TerminalOnly ? " (answered at the machine)" : "")}");
    }

    // Whether the selector waits under this question; if it shows a choice, it is set waiting again.
    async Task<bool> ShowsWaitingAsync(string node, string questionId, CancellationToken ct)
    {
        HaState? shown = null;
        try { shown = await ha.GetStateAsync(SessionEntities.Decision(node), ct); }
        catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { }
        if (shown?.State == HaDecision.Awaiting) { return DecisionIdOf(shown) == questionId; }
        await SelectAsync(SessionEntities.Decision(node), HaDecision.Awaiting, ct);
        return false;
    }

    static string? DecisionIdOf(HaState? state) =>
        state?.Attributes is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("decision_id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;

    // The choice slots arming could not reset, and what each showed instead.
    async Task<Dictionary<int, (string? Stale, string Intended)>> StaleSlotsAsync(DecisionPlan plan, string node, Dictionary<string, string> unset, CancellationToken ct)
    {
        var stale = new Dictionary<int, (string? Stale, string Intended)>();
        if (!plan.UsesFieldSlots) { return stale; }
        for (var i = 1; i <= plan.Fields.Count; i++)
        {
            if (plan.Fields[i - 1].IsText || !unset.TryGetValue(SessionEntities.Field(node, i), out var intended)) { continue; }
            string? shown = null;
            try { shown = (await ha.GetStateAsync(SessionEntities.Field(node, i), ct))?.State; }
            catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { }
            if (shown is HaDecision.Choose or HaDecision.Idle || shown == intended) { continue; }
            stale[i] = (shown, intended);
        }
        return stale;
    }

    // Whether the card still shows a question nobody is asking: one armed before a restart, or by
    // agent-ha-bridge before this host took the machine over, and answered meanwhile.
    async Task<bool> StaleQuestionAsync(string node, CancellationToken ct)
    {
        try
        {
            var state = await ha.GetStateAsync(SessionEntities.Decision(node), ct);
            if (state is null) { return false; }
            var armedAttributes = state.Attributes.ValueKind == JsonValueKind.Object && state.Attributes.TryGetProperty("decision_id", out _);
            return armedAttributes || state.State is not (HaDecision.Idle or "unknown" or "unavailable" or "");
        }
        catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { return true; }
    }

    // Get-CopilotMqttArmedFieldCount: the decision entity's field_count, or every slot when it
    // cannot be read.
    async Task<int> ArmedSlotsAsync(string node, CancellationToken ct)
    {
        try
        {
            var state = await ha.GetStateAsync(SessionEntities.Decision(node), ct);
            if (state?.Attributes is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("field_count", out var c)
                && c.TryGetInt32(out var count) && count >= 0 && count <= HaContract.MaxFields)
            {
                return count;
            }
        }
        catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { }
        return -1;
    }

    // The steps in order; returns the selects Home Assistant would not set, with what they were
    // to show.
    async Task<Dictionary<string, string>> RunAsync(IReadOnlyList<DecisionStep> steps, string node, CancellationToken ct)
    {
        var unset = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var step in steps)
        {
            switch (step)
            {
                case PublishStep s:
                    await ha.PublishAsync(s.Topic, s.Payload, s.Retain, ct);
                    break;
                case PinFieldsStep f:
                    await Task.Delay(options.FieldPinDelay, time, ct);
                    await PinNowAsync(Enumerable.Range(1, f.Slots).ToDictionary(i => $"{node}_f{i}", i => SessionEntities.Field(node, i)), time.GetUtcNow(), ct);
                    break;
                case SelectStep s:
                    if (!await SelectAsync(s.EntityId, s.Option, ct)) { unset[s.EntityId] = s.Option; }
                    break;
            }
        }
        return unset;
    }

    // Set-CopilotMqttSelectOption: a select only takes an option once Home Assistant has taken
    // the discovery config that offers it, so wait (bounded) for that, then select. A failure is
    // not fatal, as for the daemon: the card treats a missing value as unanswered. Says whether
    // Home Assistant took it.
    async Task<bool> SelectAsync(string entityId, string option, CancellationToken ct)
    {
        for (var attempt = 0; attempt < options.SelectAttempts; attempt++)
        {
            try
            {
                var state = await ha.GetStateAsync(entityId, ct);
                if (state?.Attributes is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("options", out var o)
                    && o.ValueKind == JsonValueKind.Array && o.EnumerateArray().Any(x => x.ValueKind == JsonValueKind.String && x.GetString() == option))
                {
                    break;
                }
            }
            catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { }
            await Task.Delay(options.SelectDelay, time, ct);
        }
        try
        {
            await ha.CallServiceAsync("select", "select_option", new JsonObject { ["entity_id"] = entityId, ["option"] = option }, ct);
            return true;
        }
        catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning))
        {
            log($"Home Assistant connector could not set {entityId} to {option}: {Describe(e)}");
            return false;
        }
    }

    async Task<Observation> ObserveAsync(string entityId, CancellationToken ct)
    {
        HaState? state;
        try { state = await ha.GetStateAsync(entityId, ct); }
        catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { return Observation.Unknown; }
        if (state is null) { return new Observation("absent", "", null, false); }
        if (state.State == "unavailable") { return new Observation("unknown", "", state, true); }
        if (state.State == "unknown" || string.IsNullOrWhiteSpace(state.State)) { return new Observation("absent", "", state, true); }
        return new Observation("present", state.State, state, true);
    }

    // Test-DaemonChannelChanged: only present-and-different counts.
    static bool Changed(Observation baseline, Observation current) =>
        current.Present && baseline.Kind != "unknown" && (baseline.Kind == "absent" || !string.Equals(current.Value, baseline.Value, StringComparison.Ordinal));

    // Every session a press arrived for since the last pass, and every few seconds every card
    // with an open question (a press made while the watch was down reports nothing), read and
    // acted on.
    async Task AnswersAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!ServesAnswers || !options.ConsumeAnswers) { hits.Clear(); return; }
        var touched = new Dictionary<string, List<(long Seq, HaTrigger Hit)>>(StringComparer.Ordinal);
        while (hits.TryDequeue(out var item))
        {
            if (NodeOf(item.Hit.EntityId) is { } node)
            {
                if (!touched.TryGetValue(node, out var list)) { touched[node] = list = []; }
                list.Add(item);
            }
        }
        if (now >= nextSweep)
        {
            nextSweep = now + options.AnswerSweep;
            lock (gate)
            {
                foreach (var p in published.Values)
                {
                    if (p.Armed is { Settled: false, Plan.TerminalOnly: false }) { touched.TryAdd(p.Node, []); }
                }
            }
        }
        foreach (var (node, seen) in touched)
        {
            (string Id, Published P)? entry;
            lock (gate) { entry = published.Where(x => x.Value.Node == node).Select(x => ((string, Published)?)(x.Key, x.Value)).FirstOrDefault(); }
            if (entry is not { } e) { continue; }
            try { await CardAsync(e.Id, e.P, seen, now, ct); }
            catch
            {
                // Read again on the next pass, with the numbers they came with.
                foreach (var item in seen) { hits.Enqueue(item); }
                if (seen.Count == 0) { nextSweep = now; }
                throw;
            }
        }
    }

    static string? NodeOf(string entityId)
    {
        var dot = entityId.IndexOf('.');
        var name = dot < 0 ? entityId : entityId[(dot + 1)..];
        foreach (var suffix in new[] { "_decision", "_reply_payload", "_reply", "_submit" })
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal)) { return name[..^suffix.Length]; }
        }
        var f = name.LastIndexOf("_f", StringComparison.Ordinal);
        return f > 0 && int.TryParse(name[(f + 2)..], NumberStyles.None, CultureInfo.InvariantCulture, out _) ? name[..f] : null;
    }

    // What one session's card now says, and what to do about it. seen is what the watch reported
    // for it since the last pass, empty when the card is read by the sweep.
    async Task CardAsync(string sessionId, Published p, IReadOnlyList<(long Seq, HaTrigger Hit)> seen, DateTimeOffset now, CancellationToken ct)
    {
        var armed = p.Armed;
        if (armed is null || armed.Plan.TerminalOnly || armed.Settled)
        {
            if (seen.Count > 0) { await UnservedAsync(p, seen[^1].Hit, ct); }
            return;
        }
        var since = seen.Where(h => h.Seq > armed.Boundary).Select(h => h.Hit).ToList();
        var outcome = await ReadAnswerAsync(sessionId, p, armed, since, ct);
        if (outcome is null) { return; }
        if (outcome.Notice is { } notice)
        {
            if (outcome.Spend is { } spent) { armed.SpentPress = spent; }
            if (outcome.HoldStamp is { } held)
            {
                armed.HeldStamp = held;
                armed.BoxAtHold = await BoxValueAsync(p.Node, ct);
            }
            // A Cancel is undone every time it is chosen, though said only once: left on Cancel,
            // the selector would hide every later answer.
            if (outcome.ResetDecision) { await SelectAsync(SessionEntities.Decision(p.Node), HaDecision.Awaiting, ct); }
            var key = notice.Summary + "|" + outcome.NoticeKey;
            if (armed.Told == key) { return; }
            armed.Told = key;
            await TransientAsync(p, notice.Summary, notice.Extra, ct);
            return;
        }
        if (outcome.Request is not { } request || answer is null) { return; }

        // Said before anything is marked used: if saying it fails, the next pass reads the same
        // press again rather than finding it spent with nothing delivered.
        await TransientAsync(p, "Sending answer...", new JsonObject { ["answer"] = outcome.Shown }, ct);
        armed.UsedPress = outcome.Press ?? armed.UsedPress;
        armed.UsedStamp = outcome.Stamp ?? armed.UsedStamp;
        AnswerResult result;
        try { result = await answer(request, ct); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            result = new AnswerResult(AnswerOutcome.Unconfirmed, $"delivery failed: {e.GetType().Name}");
        }
        if (result.Outcome is AnswerOutcome.Delivered or AnswerOutcome.Unconfirmed)
        {
            // Who sent it (the press carries the account behind it, a publish names its driver),
            // and which reply-card words were used, only for an answer that may have arrived.
            p.ArmDriver(outcome.Driver, now);
            if (outcome.Stamp is { } stamp) { p.LastPayloadStamp = stamp; }
        }
        switch (result.Outcome)
        {
            case AnswerOutcome.Delivered:
                armed.Settled = true;
                await ResetReplyBoxAsync(p.Node, ct);
                var extra = new JsonObject
                {
                    ["answer"] = Shown(outcome.Shown),
                    ["at"] = time.GetLocalNow().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                };
                // Names the reply-card publish these words came from, so the card that sent them
                // can tell its own words were used from the question being answered elsewhere.
                if (outcome.Stamp is { } used) { extra["answer_consumed_at"] = used; }
                await TransientAsync(p, "Answer sent", extra, ct);
                log($"Home Assistant connector delivered an answer to {sessionId[..Math.Min(8, sessionId.Length)]}");
                break;
            case AnswerOutcome.Unconfirmed:
                armed.Settled = true;
                await TransientAsync(p, "Answer unconfirmed - check the terminal", new JsonObject { ["error"] = result.Reason ?? "" }, ct);
                break;
            default:
                // Nothing reached the terminal, so the question is answerable again: by a new
                // press, publish or tap (the one that failed stays used, so it is never retried
                // on its own), or for words already given, by a press of Send after this.
                armed.Attempts++;
                armed.Boundary = Interlocked.Read(ref hitSeq);
                armed.SubmitAtFailure = await ObserveAsync(SessionEntities.Submit(p.Node), ct);
                if (outcome.Box)
                {
                    armed.UsedPress = null;
                    armed.FailedBox = outcome.Press;
                }
                if (outcome.Stamp is { } failedStamp)
                {
                    armed.UsedStamp = null;
                    armed.HeldStamp = failedStamp;
                    armed.BoxAtHold = await BoxValueAsync(p.Node, ct);
                }
                if (outcome.Tap) { armed.AwaitTap = true; }
                await TransientAsync(p, "Answer NOT sent", new JsonObject
                {
                    ["error"] = result.Reason ?? result.Outcome.ToString(),
                    ["hint"] = "nothing reached the terminal",
                }, ct);
                // Choosing an option it already shows changes nothing in Home Assistant, so a tap
                // can only be made again once the selector is waiting.
                if (outcome.Tap) { await SelectAsync(SessionEntities.Decision(p.Node), HaDecision.Awaiting, ct); }
                log($"Home Assistant connector answer to {sessionId[..Math.Min(8, sessionId.Length)]} not sent: {result.Outcome}");
                break;
        }
    }

    static string Shown(string answer)
    {
        var shown = string.Join(' ', answer.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return shown.Length > 60 ? shown[..57] + "..." : shown;
    }

    // A press at a card with nothing to answer: a reply, which a later stage serves. Said, once
    // per press, rather than dropped.
    async Task UnservedAsync(Published p, HaTrigger hit, CancellationToken ct)
    {
        var words = hit.EntityId == SessionEntities.ReplyPayload(p.Node)
            ? (hit.State.Attributes is { ValueKind: JsonValueKind.Object } a && a.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null)
              ?? (HasAttachments(hit.State) ? "(attachments)" : null)
            : hit.EntityId == SessionEntities.Reply(p.Node) && hit.State.State != HaDecision.ReplyBlank ? hit.State.State : null;
        if (hit.EntityId == SessionEntities.Submit(p.Node))
        {
            var box = await ObserveAsync(SessionEntities.Reply(p.Node), ct);
            words = box.Present && !string.IsNullOrWhiteSpace(box.Value) ? box.Value : null;
        }
        if (string.IsNullOrWhiteSpace(words)) { return; }
        var key = hit.EntityId + "|" + hit.State.State;
        if (p.ToldUnserved == key) { return; }
        p.ToldUnserved = key;
        await TransientAsync(p, "Reply NOT sent", new JsonObject
        {
            ["error"] = "this machine's host does not take replies from the dashboard yet",
            ["hint"] = "reply at the machine",
        }, ct);
    }

    static bool HasAttachments(HaState state) =>
        state.Attributes is { ValueKind: JsonValueKind.Object } a
        && new[] { "images", "files" }.Any(n => a.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.Array && v.GetArrayLength() > 0);

    async Task ResetReplyBoxAsync(string node, CancellationToken ct)
    {
        try { await ha.CallServiceAsync("text", "set_value", new JsonObject { ["entity_id"] = SessionEntities.Reply(node), ["value"] = HaDecision.ReplyBlank }, ct); }
        catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning)) { }
    }

    // Set-DaemonTransientActivity: a status line about what was just done, without losing what
    // the card was showing.
    async Task TransientAsync(Published p, string summary, JsonObject extra, CancellationToken ct)
    {
        var detail = p.Detail?.DeepClone().AsObject() ?? [];
        foreach (var key in new[] { "error", "hint", "waiting_on", "sent", "unsent", "recorded", "answer", "at" }) { detail.Remove(key); }
        foreach (var (key, value) in extra) { detail[key] = value?.DeepClone(); }
        var t = HaContract.Topics(p.Node);
        await ha.PublishAsync(t.ActivityState, HaPayloads.CapState(summary), true, ct);
        await PublishAsync(t.ActivityAttributes, detail, true, ct);
        // The next change of the session's own activity replaces it, as the daemon's does.
        p.Activity = null;
    }

    sealed record Notice(string Summary, JsonObject Extra);

    sealed record Read
    {
        public Notice? Notice { get; init; }
        public string? NoticeKey { get; init; }
        public string? Spend { get; init; }
        public bool ResetDecision { get; init; }
        public AnswerRequest? Request { get; init; }
        public string Shown { get; init; } = "";
        public string? Stamp { get; init; }
        public string? Press { get; init; }
        public string Driver { get; init; } = "human";
        // Words from the reply box, and an approval tap: how a failed one may be given again.
        public bool Box { get; init; }
        public bool Tap { get; init; }
        // A reply-card publish to hold (HeldStamp) rather than let submit later by itself.
        public string? HoldStamp { get; init; }
    }

    // Read-DaemonDecisionAnswer: what the card holds as the answer, a reason it cannot be sent,
    // or nothing yet.
    async Task<Read?> ReadAnswerAsync(string sessionId, Published p, Armed armed, IReadOnlyList<HaTrigger> since, CancellationToken ct)
    {
        var plan = armed.Plan;
        var node = p.Node;
        // A press of Send (or of the box) the watch reported after the last failure: what lets
        // words that failed be given again.
        var fresh = since.Any(h => h.EntityId == SessionEntities.Submit(node) || h.EntityId == SessionEntities.Reply(node))
            || await PressedSinceFailureAsync(node, armed, ct);
        var decision = await ha.GetStateAsync(SessionEntities.Decision(node), ct);
        if (decision?.State == HaDecision.Cancel)
        {
            // The host has no way to withdraw a question; the selector goes back to waiting.
            return new Read
            {
                Notice = new Notice("Answer NOT sent", new JsonObject { ["error"] = "a question cannot be cancelled from here", ["hint"] = "cancel it at the machine" }),
                NoticeKey = "cancel",
                ResetDecision = true,
            };
        }
        var card = await CardTextAsync(node, armed, ct);
        if (card is { Retry: true } && !plan.AnswerOnTap)
        {
            // Words typed into the reply box since reply-card words were held are newer: they
            // replace them, rather than the held words being sent in their place.
            var newer = await ObserveAsync(SessionEntities.Reply(node), ct);
            if (newer.Present && !string.IsNullOrWhiteSpace(newer.Value) && Changed(armed.ReplyBaseline, newer) && newer.Value != armed.BoxAtHold) { card = null; }
        }

        if (plan.AnswerOnTap)
        {
            // The tap is the answer: Approve or Deny.
            var tapped = decision?.State;
            var mine = armed.Question.Id;
            if (armed.AwaitTap)
            {
                // Only a tap made once the selector waits under this question counts: seen waiting
                // now, or reported waiting since (a quick tap).
                if (!(tapped == HaDecision.Awaiting && DecisionIdOf(decision) == mine)
                    && !since.Any(h => h.EntityId == SessionEntities.Decision(node) && h.State.State == HaDecision.Awaiting && DecisionIdOf(h.State) == mine))
                {
                    return null;
                }
                armed.AwaitTap = false;
                armed.UsedPress = null;
            }
            if (tapped is not ("Approve" or "Deny")) { return null; }
            // A selector naming another question was not showing this one.
            if (DecisionIdOf(decision) != mine) { return null; }
            // One tap, however often it is read.
            var tap = "tap:" + tapped;
            if (tap == armed.UsedPress) { return null; }
            var field = armed.Question.Fields.FirstOrDefault()?.Name ?? "decision";
            var value = tapped == "Approve" ? "approve" : "deny";
            return Answer(sessionId, armed, new Dictionary<string, JsonElement> { [field] = JsonSerializer.SerializeToElement(value) }, null, tapped, null, tap, DriverOf(decision!.UserId)) with { Tap = true };
        }

        if (!plan.UsesFieldSlots)
        {
            // A freeform question: words from the reply card, or the reply box. Words that
            // failed go again only on a press after the failure.
            if (card is { } c) { return c.Retry && !fresh ? null : Answer(sessionId, armed, null, c.Text, c.Text, c.Stamp, null, c.Driver); }
            var box = await ObserveAsync(SessionEntities.Reply(node), ct);
            if (box.Present && !string.IsNullOrWhiteSpace(box.Value) && Changed(armed.ReplyBaseline, box) && box.Value != armed.UsedPress
                && (box.Value != armed.FailedBox || fresh))
            {
                return Answer(sessionId, armed, null, box.Value, box.Value, null, box.Value, DriverOf(box.Snapshot?.UserId)) with { Box = true };
            }
            return null;
        }

        // Read-DaemonFormAnswer: one slot per field; a free-text field from the reply card or box.
        var fields = plan.Fields;
        var takesText = fields.Any(f => f.IsText);
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var picked = new List<string>();
        var missing = new List<string>();
        string? problem = null;
        for (var i = 0; i < fields.Count; i++)
        {
            var field = fields[i];
            var source = field.Source!;
            if (field.IsText)
            {
                var text = card?.Text;
                if (string.IsNullOrWhiteSpace(text))
                {
                    // Only words typed since the question was shown: the box may still hold the last
                    // question's after a restart, a takeover or a reset that did not take.
                    var box = await ObserveAsync(SessionEntities.Reply(node), ct);
                    text = box.Present && !string.IsNullOrWhiteSpace(box.Value) && Changed(armed.ReplyBaseline, box) ? box.Value : "";
                }
                picked.Add(text);
                if (source.Type == FieldType.Number)
                {
                    if (text.Length == 0) { continue; }
                    // The number as typed when it is a JSON number, so nothing is rounded through a
                    // double (9007199254740993 stays itself); otherwise as .NET reads it.
                    if (ExactNumber(text.Trim()) is { } exact) { values[source.Name] = exact; continue; }
                    if (!double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
                    {
                        problem ??= $"{field.Label} needs a number";
                        continue;
                    }
                    values[source.Name] = JsonSerializer.SerializeToElement(number);
                }
                else { values[source.Name] = JsonSerializer.SerializeToElement(text); }
                continue;
            }
            var slot = (await ha.GetStateAsync(SessionEntities.Field(node, i + 1), ct))?.State ?? "";
            if (armed.StaleSlots.TryGetValue(i + 1, out var stale))
            {
                if (stale.Stale is not null && slot != stale.Stale) { armed.StaleSlots.Remove(i + 1); }
                else
                {
                    // Still what the last question left: not an answer. Reset it again.
                    if (await SelectAsync(SessionEntities.Field(node, i + 1), stale.Intended, ct)) { armed.StaleSlots.Remove(i + 1); }
                    missing.Add(field.Label);
                    continue;
                }
            }
            if (slot is HaDecision.Choose or HaDecision.Idle or "unknown" or "unavailable" or "")
            {
                missing.Add(field.Label);
                continue;
            }
            if (field.MultiSelect)
            {
                var indexes = MultiSelect.Resolve(field, slot);
                if (indexes.Count == 0 && !MultiSelect.IsEmptyChoice(field, slot)) { missing.Add(field.Label); continue; }
                picked.Add(indexes.Count == 0 ? MultiSelect.EmptyLabel : MultiSelect.Label(field, indexes));
                values[source.Name] = JsonSerializer.SerializeToElement(indexes.Select(x => source.Options![x].Value).ToArray());
                continue;
            }
            var index = field.Options.ToList().IndexOf(slot);
            if (index < 0) { missing.Add(field.Label); continue; }
            picked.Add(slot);
            values[source.Name] = source.Type == FieldType.Boolean
                ? JsonSerializer.SerializeToElement(index == 0)
                : JsonSerializer.SerializeToElement(source.Options![index].Value);
        }

        // A lone choice answered in words, through the prompt's own "Other" entry. Words for a
        // question that takes only options are said to be wrong once and then held, so they never
        // stand in the way of the options and Send.
        if (fields.Count == 1 && !takesText && missing.Count == 1 && !fields[0].MultiSelect && card is { } other)
        {
            if (!plan.TakesTypedAnswer)
            {
                if (!other.Retry)
                {
                    return new Read
                    {
                        Notice = new Notice("Not sent - this question takes options", new JsonObject { ["typed"] = other.Text }),
                        NoticeKey = other.Stamp,
                        HoldStamp = other.Stamp,
                    };
                }
            }
            else if (!other.Retry || fresh)
            {
                var answer = new Dictionary<string, JsonElement> { [fields[0].Source!.Name] = JsonSerializer.SerializeToElement(new { other = other.Text }) };
                return Answer(sessionId, armed, answer, null, other.Text, other.Stamp, null, other.Driver);
            }
        }

        // Send is what answers; a press is one only if it differs from the arm-time snapshot, and
        // was not spent at an incomplete form or used already.
        var submit = await ObserveAsync(SessionEntities.Submit(node), ct);
        if (submit.Kind == "absent" && !submit.Exists)
        {
            return new Read
            {
                Notice = new Notice("Not sent - no Send button on this session", new JsonObject { ["answer_in"] = "the terminal" }),
                NoticeKey = "missing",
            };
        }
        // ...nor the Send the card showed when an answer failed: pressed while it was being
        // delivered, it belongs to the attempt that failed, not to a new one.
        var pressed = Changed(armed.SubmitBaseline, submit) && submit.Value != armed.SpentPress && submit.Value != armed.UsedPress
            && !(armed.SubmitAtFailure is { Present: true } atFailure && atFailure.Value == submit.Value);
        var complete = missing.Count == 0;
        // A lone choice that takes another answer, typed into the reply box and sent with Send.
        if (pressed && fields.Count == 1 && !takesText && missing.Count == 1 && !fields[0].MultiSelect && plan.TakesTypedAnswer && card is null)
        {
            var typed = await ObserveAsync(SessionEntities.Reply(node), ct);
            if (typed.Present && !string.IsNullOrWhiteSpace(typed.Value) && Changed(armed.ReplyBaseline, typed))
            {
                var typedAnswer = new Dictionary<string, JsonElement> { [fields[0].Source!.Name] = JsonSerializer.SerializeToElement(new { other = typed.Value }) };
                return Answer(sessionId, armed, typedAnswer, null, typed.Value, null, submit.Value, DriverOf(submit.Snapshot?.UserId));
            }
        }
        if (pressed && !complete)
        {
            return new Read
            {
                Notice = new Notice("Not sent - answer every field", new JsonObject { ["waiting_on"] = string.Join(", ", missing) }),
                NoticeKey = submit.Value,
                Spend = submit.Value,
                HoldStamp = takesText && card is { Retry: false } alongside ? alongside.Stamp : null,
            };
        }
        // Words from the reply card for a form not yet complete: said, and held, so finishing the
        // form later does not send them without a press of Send.
        if (takesText && card is { Retry: false } early && !complete)
        {
            return new Read
            {
                Notice = new Notice("Not sent - answer every field", new JsonObject { ["waiting_on"] = string.Join(", ", missing) }),
                NoticeKey = early.Stamp,
                HoldStamp = early.Stamp,
            };
        }
        if (card is { Retry: false } words && !takesText)
        {
            return new Read
            {
                Notice = new Notice("Not sent - this question takes options", new JsonObject { ["typed"] = words.Text }),
                NoticeKey = words.Stamp,
                HoldStamp = words.Stamp,
            };
        }
        // Send on the reply card submits a complete form too: its publish is the press. Words from
        // a publish that failed still fill the form, but only a new press of Send submits them.
        var cardSubmits = takesText && card is { Retry: false };
        if (!(pressed || cardSubmits) || !complete) { return null; }
        if (problem is not null)
        {
            return new Read
            {
                Notice = new Notice("Not sent - " + problem, []),
                NoticeKey = submit.Value + "|" + card?.Stamp,
                Spend = pressed ? submit.Value : null,
            };
        }
        var driver = pressed ? DriverOf(submit.Snapshot?.UserId) : card!.Value.Driver;
        return Answer(sessionId, armed, values, null, string.Join(" + ", picked), takesText && card is { } used ? used.Stamp : null, pressed ? submit.Value : null, driver);
    }

    // What the reply card published for the armed question, if anything new
    // (Read-DaemonDecisionCardText): words only, tagged for this question or not at all.
    async Task<(string Text, string Stamp, string Driver, bool Retry)?> CardTextAsync(string node, Armed armed, CancellationToken ct)
    {
        if (armed.PayloadBaseline.Kind == "unknown") { return null; }
        var current = await ObserveAsync(SessionEntities.ReplyPayload(node), ct);
        if (!Changed(armed.PayloadBaseline, current) || current.Value == armed.UsedStamp) { return null; }
        if (current.Snapshot is not { } state || state.Attributes.ValueKind != JsonValueKind.Object) { return null; }
        // An image cannot be typed into a prompt; replies with attachments are a later stage.
        if (HasAttachments(state)) { return null; }
        var a = state.Attributes;
        if (a.TryGetProperty("decision_id", out var tag) && tag.ValueKind == JsonValueKind.String && tag.GetString() is { Length: > 0 } taggedFor
            && taggedFor != armed.Question.Id)
        {
            return null;
        }
        var text = a.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
        if (string.IsNullOrWhiteSpace(text)) { return null; }
        // A publish names who sent it, as the daemon reads it (Get-BridgeReplyPayload's Driver).
        var driver = a.TryGetProperty("driver", out var d) && d.ValueKind == JsonValueKind.String && d.GetString() is { Length: > 0 } named ? named : "human";
        // A held publish: its words, which cannot answer by themselves.
        return (text, current.Value, driver, current.Value == armed.HeldStamp);
    }

    Read Answer(string sessionId, Armed armed, IReadOnlyDictionary<string, JsonElement>? fields, string? text, string shown, string? stamp, string? press, string driver) => new()
    {
        Request = new AnswerRequest
        {
            Id = sessionId,
            Question = armed.Question.Id,
            // One request per press or publish, so the host returns the first outcome for a
            // resend instead of typing it again; and one per attempt, so words given again after
            // a refusal are tried again rather than answered from the refusal.
            Request = RequestId(sessionId, armed.Question.Id, $"{press ?? stamp ?? shown}#{armed.Attempts}"),
            Fields = fields,
            Text = text,
        },
        Shown = shown,
        Stamp = stamp,
        Press = press,
        Driver = driver,
    };

    async Task<string?> BoxValueAsync(string node, CancellationToken ct)
    {
        var box = await ObserveAsync(SessionEntities.Reply(node), ct);
        return box.Present ? box.Value : null;
    }

    static JsonElement? ExactNumber(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            var e = doc.RootElement;
            return e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out var d) && double.IsFinite(d) ? e.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    // Whether Send was pressed since the last failure, read over REST: the watch may have been
    // down. A press made while that answer was being delivered is part of the failure, not after it.
    async Task<bool> PressedSinceFailureAsync(string node, Armed armed, CancellationToken ct)
    {
        if (armed.Attempts == 0 || armed.SubmitAtFailure is not { } before || before.Kind == "unknown") { return false; }
        var now = await ObserveAsync(SessionEntities.Submit(node), ct);
        return now.Present && (!before.Present || !string.Equals(now.Value, before.Value, StringComparison.Ordinal));
    }

    // Get-BridgeDriverFromState: "agent" when the account behind a press is one of the agents'
    // (Test-BridgeAgentUserId, homeAssistant.agentUserIds), else "human".
    string DriverOf(string? userId) =>
        !string.IsNullOrWhiteSpace(userId) && options.AgentUserIds.Contains(userId.Trim(), StringComparer.Ordinal) ? "agent" : "human";

    static string RequestId(string sessionId, string questionId, string what)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"ha|{sessionId}|{questionId}|{what}"));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }
}
