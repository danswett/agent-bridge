using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;

namespace AgentBridge.HomeAssistant;

// A reply from a card, for the host's own reply delivery (session.reply): the words, and the
// files that go with them, already fetched from Home Assistant's image store or decoded from the
// card's publish. Request is one id per press or publish, so a resend is answered from the first.
public sealed record HaReply(string SessionId, string Request, string? Text, IReadOnlyList<HaReplyFile> Files);

public sealed record HaReplyFile(string Name, string? Type, byte[] Bytes);

// Stage 3: replies from a card with no question on it (docs/ha-connector.md), read the way
// agent-ha-bridge's daemon reads them: the reply card's publish first (Send-DaemonCardPayload),
// with its images from Home Assistant's image store and its files inline, then the reply box
// and its Send button (Send-DaemonReplyBoxText). What the card is told is the daemon's own
// wording (ha-replies.json). The words go to the host's reply delivery, which stages the files
// and types them in; delivery can take a minute to confirm, so it runs beside the passes, and
// what came of it is said on the next one.
public sealed partial class HaConnector
{
    // What a session's reply controls held when this host started reading them, and what it has
    // used since, so nothing is sent twice and nothing already there is taken for new.
    sealed class ReplyState
    {
        // Whether the reply card's publish and Send have been read once: what they held then is
        // not a reply. A card taken over from agent-ha-bridge, or from this host before a
        // restart, may hold one it already sent.
        public bool Baselined;
        // The last change the watch had reported when the baseline was read: nothing reported up
        // to then is new, whatever the sensor showed (two publishes during a question, say).
        public long Boundary;
        public string? HandledPress;
        // Reply-card publishes used or passed over, most recent last: one read again, from the
        // sensor or from the watch, is not new.
        public readonly List<string> Handled = [];
        // Publishes the watch reported that are not yet read, in the order they came. The sensor
        // shows only the latest, so two sent while a reply was being delivered would otherwise
        // be one.
        public readonly List<HaState> Waiting = [];
        // A Send pressed at an empty box, waiting for the box's words (PendingSubmitAt).
        public string? PendingPress;
        public DateTimeOffset PendingSince;
        // The card is read again at once after a delivery, for anything sent meanwhile.
        public bool ReadAgain;
        // When the last reply-card publish went in (LastPayloadDeliveredAt).
        public DateTimeOffset? PayloadDeliveredAt;

        public bool IsHandled(string stamp) => Handled.Contains(stamp, StringComparer.Ordinal);

        public void Handle(string stamp)
        {
            if (IsHandled(stamp)) { return; }
            Handled.Add(stamp);
            if (Handled.Count > 64) { Handled.RemoveAt(0); }
        }
    }

    enum CardRead { Nothing, Taken, Delivering }

    // The most publishes kept waiting for one card: more than a person or an agent sends while one
    // reply goes in.
    const int MaxWaiting = 16;

    // Arm: the driver arm the reply made when it started, so what came of it touches that arm
    // and never a newer one (an answer given meanwhile, say).
    sealed record ReplyDone(long Delivery, string SessionId, string Shown, bool Box, string? Text, IReadOnlyList<string> Images,
        DateTimeOffset? Arm, ReplyResult Result, bool Failed);

    readonly Func<HaReply, CancellationToken, Task<ReplyResult>>? reply;
    readonly ConcurrentQueue<ReplyDone> replies = new();
    readonly ConcurrentDictionary<Task, bool> delivering = new();
    // The delivery each session has under way, by number: nothing else is read for that card until
    // it is done. Kept by the connector, not with the card's state, which a pause rebuilds while
    // a delivery may still be running.
    readonly ConcurrentDictionary<string, long> underWay = new(StringComparer.Ordinal);
    long deliveries;
    readonly CancellationTokenSource replyStop = new();
    DateTimeOffset nextReplySweep;

    // Whether replies from the cards are served: only with the host's reply delivery, and only
    // where the cards' controls are (they come with answers).
    bool ServesReplies => reply is not null && ServesAnswers;

    // Every delivery and image delete started so far, finished. For tests.
    internal Task DeliveredAsync() => Task.WhenAll(delivering.Keys);

    // Deliveries are told to stop and given a moment; one that does not is left to finish on its
    // own rather than holding the host's shutdown, and nothing is said of it.
    async Task StopRepliesAsync()
    {
        await replyStop.CancelAsync();
        try { await Task.WhenAll(delivering.Keys).WaitAsync(options.ReplyStopWait); }
        catch (TimeoutException) { log("Home Assistant connector stopped with a reply still being delivered"); return; }
        catch (Exception) { }
        replyStop.Dispose();
    }

    // The cards to read for a reply this pass, besides those the watch reported: one not yet
    // baselined or with a Send waiting on its words, and every few seconds every card, since a
    // press made while the watch was down reports nothing.
    void ReplyCards(Dictionary<string, List<(long Seq, HaTrigger Hit)>> touched, bool sweep)
    {
        if (!ServesReplies) { return; }
        lock (gate)
        {
            foreach (var (id, p) in published)
            {
                if (p.Armed is not null) { continue; }
                // A card rebuilt while its delivery runs (after a pause) is baselined meanwhile,
                // so what is sent after that is still new once the delivery is done.
                if (!p.Reply.Baselined) { touched.TryAdd(p.Node, []); continue; }
                if (underWay.ContainsKey(id)) { continue; }
                if (sweep || p.Reply.ReadAgain || p.Reply.PendingPress is not null) { touched.TryAdd(p.Node, []); }
            }
        }
    }

    // What came of the deliveries that finished since the last pass, said on their cards.
    async Task RepliesDoneAsync(DateTimeOffset now, CancellationToken ct)
    {
        while (replies.TryPeek(out var done))
        {
            Published? p;
            lock (gate) { p = published.TryGetValue(done.SessionId, out var found) ? found : null; }
            if (p is not null) { await ReplyDoneAsync(p, done, now, ct); }
            else if (done.Result.Outcome is AnswerOutcome.Delivered or AnswerOutcome.Unconfirmed) { DeleteImages(done.Images); }
            // Only once said: a pass that fails part-way says it again rather than not at all.
            replies.TryDequeue(out _);
            underWay.TryRemove(new KeyValuePair<string, long>(done.SessionId, done.Delivery));
            if (p is not null) { p.Reply.ReadAgain = true; }
        }
    }

    async Task ReplyDoneAsync(Published p, ReplyDone done, DateTimeOffset now, CancellationToken ct)
    {
        var result = done.Result;
        var short8 = done.SessionId[..Math.Min(8, done.SessionId.Length)];
        // Still the arm this reply made, not consumed by its turn nor replaced since.
        var ours = done.Arm is { } arm && p.DriverPendingAt == arm;
        switch (result.Outcome)
        {
            case AnswerOutcome.Delivered:
                // The arm's window starts when the words went in (Update-DaemonDriverPendingStamp).
                if (ours) { p.DriverPendingAt = now; }
                if (!done.Box) { p.Reply.PayloadDeliveredAt = now; }
                await TransientAsync(p, "Reply sent", new JsonObject
                {
                    ["sent"] = done.Shown,
                    ["at"] = time.GetLocalNow().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                }, ct);
                log($"Home Assistant connector delivered a reply to {short8}");
                break;
            case AnswerOutcome.Unconfirmed:
                // The keys may have gone in: treated as sent, so nothing is sent again by itself.
                if (!done.Box) { p.Reply.PayloadDeliveredAt = now; }
                await TransientAsync(p, "Reply unconfirmed - check the terminal", new JsonObject { ["error"] = result.Reason ?? "" }, ct);
                break;
            default:
                // No turn is coming, so the arm would only mark whatever is typed next.
                if (ours) { p.DriverPendingAt = null; }
                var extra = new JsonObject { ["error"] = result.Reason ?? result.Outcome.ToString() };
                // A failure to fetch or decode what came with it is the daemon's own sentence,
                // with nothing tried; one the delivery refused names the words it did not send.
                if (!done.Failed) { extra["unsent"] = done.Shown; }
                await TransientAsync(p, "Reply NOT sent", extra, ct);
                log($"Home Assistant connector reply to {short8} not sent: {result.Outcome}");
                break;
        }
        // Images go from Home Assistant's store once used; one that reached nothing is kept, as
        // the card was told ("source images were kept").
        if (result.Outcome is AnswerOutcome.Delivered or AnswerOutcome.Unconfirmed) { DeleteImages(done.Images); }
        // The reply box is cleared either way, so a failed reply is not sent again by itself; but
        // only once it is seen still holding what was sent, never words typed since. The daemon
        // also clears a box it cannot read; here the press is already spent, so a box left as it
        // is sends nothing again, and a follow-up typed meanwhile is never lost.
        if (done.Box && done.Text is { } sent)
        {
            var box = await ObserveAsync(SessionEntities.Reply(p.Node), ct);
            if (box.Present && box.Value == sent) { await ResetReplyBoxAsync(p.Node, ct); }
            else { log($"Home Assistant connector kept a follow-up reply typed for {short8} while the previous one was delivering"); }
        }
    }

    // Used images go from Home Assistant's store beside the passes, as deliveries run: each
    // delete may take a command's whole timeout, and the passes (the heartbeat among them) must
    // not wait on them. Best effort, still G5-guarded, and waited for at stop like a delivery.
    void DeleteImages(IReadOnlyList<string> images)
    {
        if (images.Count == 0) { return; }
        var token = replyStop.Token;
        Track(Task.Run(async () =>
        {
            foreach (var id in images)
            {
                try { await ha.DeleteImageAsync(id, token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
                catch (AgentHaBridgeRunning) { log($"Home Assistant connector left image {id} in the store: agent-ha-bridge is running"); return; }
                // A command that timed out on its own is a failure like any other.
                catch (Exception e) { log($"Home Assistant connector could not delete image {id}: {Describe(e)}"); }
            }
        }, CancellationToken.None));
    }

    void Track(Task task)
    {
        delivering[task] = true;
        task.ContinueWith(t => delivering.TryRemove(t, out _), CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    // What the controls hold now is what was there before: never a reply. One that cannot be read,
    // or reads unavailable, leaves it to a later pass: taken as empty, what it held would look new
    // once it was back.
    async Task<bool> BaselineRepliesAsync(Published p, CancellationToken ct)
    {
        // Before the states are read: a change reported after this is read again, and is new only
        // if the state read did not already include it.
        var boundary = Interlocked.Read(ref hitSeq);
        var payload = await ObserveAsync(SessionEntities.ReplyPayload(p.Node), ct);
        var submit = await ObserveAsync(SessionEntities.Submit(p.Node), ct);
        if (payload.Kind == "unknown" || submit.Kind == "unknown") { return false; }
        if (payload.Present) { p.Reply.Handle(payload.Value); }
        p.Reply.HandledPress = submit.Present ? submit.Value : null;
        p.Reply.PendingPress = null;
        p.Reply.Waiting.Clear();
        p.Reply.Boundary = boundary;
        p.Reply.Baselined = true;
        return true;
    }

    // A card with no question on it: its reply card's publishes, in the order they came, then
    // what its sensor shows now (a publish made while the watch was down), else its reply box and
    // Send. One delivery at a time; publishes reported meanwhile wait their turn.
    async Task ReplyAsync(string sessionId, Published p, IReadOnlyList<(long Seq, HaTrigger Hit)> seen, DateTimeOffset now, CancellationToken ct)
    {
        if (!p.Reply.Baselined)
        {
            await BaselineRepliesAsync(p, ct);
            return;
        }
        foreach (var (seq, hit) in seen)
        {
            if (seq <= p.Reply.Boundary || hit.EntityId != SessionEntities.ReplyPayload(p.Node) || !IsStamp(hit.State.State) || p.Reply.IsHandled(hit.State.State)
                || p.Reply.Waiting.Any(w => w.State == hit.State.State))
            {
                continue;
            }
            if (p.Reply.Waiting.Count >= MaxWaiting)
            {
                log($"Home Assistant connector: more than {MaxWaiting} replies waiting for {sessionId[..Math.Min(8, sessionId.Length)]}; the sensor's latest is read when they have gone");
                break;
            }
            p.Reply.Waiting.Add(hit.State);
        }
        if (underWay.ContainsKey(sessionId)) { return; }
        p.Reply.ReadAgain = false;
        while (p.Reply.Waiting.Count > 0)
        {
            var next = p.Reply.Waiting[0];
            var read = await CardReplyAsync(sessionId, p, next, ct);
            p.Reply.Waiting.RemoveAt(0);
            if (read == CardRead.Delivering) { return; }
        }
        var current = await ObserveAsync(SessionEntities.ReplyPayload(p.Node), ct);
        if (current.Present && await CardReplyAsync(sessionId, p, current.Snapshot!, ct) != CardRead.Nothing) { return; }
        await BoxReplyAsync(sessionId, p, now, ct);
    }

    static bool IsStamp(string state) => !string.IsNullOrWhiteSpace(state) && state is not ("unknown" or "unavailable");

    // Send-DaemonCardPayload: one publish from the reply card, unless it was read before. Says
    // whether it was nothing to send, taken without a delivery (refused, or words for a question),
    // or is being delivered.
    async Task<CardRead> CardReplyAsync(string sessionId, Published p, HaState state, CancellationToken ct)
    {
        if (!IsStamp(state.State) || p.Reply.IsHandled(state.State)) { return CardRead.Nothing; }
        var stamp = state.State;
        var a = state.Attributes;
        var attributes = a.ValueKind == JsonValueKind.Object;
        var requested = 0;
        var images = new List<(string Id, string Name, string? Type)>();
        var files = new List<(string Name, string? Type, string Base64)>();
        if (attributes)
        {
            // Anything but a list where one belongs is one attachment that cannot be read, as the
            // daemon's @(...) makes it: the metadata is incomplete, never silently dropped.
            if (a.TryGetProperty("images", out var im) && im.ValueKind != JsonValueKind.Null)
            {
                requested += im.ValueKind == JsonValueKind.Array ? im.GetArrayLength() : 1;
                foreach (var i in im.ValueKind == JsonValueKind.Array ? im.EnumerateArray() : Enumerable.Empty<JsonElement>())
                {
                    if (Str(i, "id") is { } id && !string.IsNullOrWhiteSpace(id)) { images.Add((id, Str(i, "name") ?? "", Str(i, "content_type"))); }
                }
            }
            if (a.TryGetProperty("files", out var fs) && fs.ValueKind != JsonValueKind.Null)
            {
                requested += fs.ValueKind == JsonValueKind.Array ? fs.GetArrayLength() : 1;
                foreach (var f in fs.ValueKind == JsonValueKind.Array ? fs.EnumerateArray() : Enumerable.Empty<JsonElement>())
                {
                    if (Str(f, "b64") is { } b64 && !string.IsNullOrWhiteSpace(b64)) { files.Add((Str(f, "name") ?? "", Str(f, "content_type"), b64)); }
                }
            }
        }
        var text = attributes ? Str(a, "text") ?? "" : "";
        var usable = !string.IsNullOrWhiteSpace(text) || images.Count > 0 || files.Count > 0;
        // Nothing to send, and nothing it meant to: not a reply, and left unread.
        if (!usable && requested == 0) { return CardRead.Nothing; }
        // Words for a question, published as it went: never a reply in their own right, and no
        // reason to pass over a Send pressed meanwhile.
        if (attributes && Str(a, "decision_id") is { Length: > 0 })
        {
            p.Reply.Handle(stamp);
            return CardRead.Nothing;
        }
        // Each said before the publish is marked used: if saying it fails, the next pass reads the
        // same publish again rather than finding it used with nothing done.
        if (usable) { p.ArmDriver(attributes && Str(a, "driver") == "agent" ? "agent" : "human", time.GetUtcNow()); }
        if (!usable || images.Count + files.Count != requested)
        {
            p.DriverPendingAt = null;
            await TransientAsync(p, "Reply NOT sent", new JsonObject { ["error"] = "The attachment metadata is incomplete; no reply was sent." }, ct);
            p.Reply.Handle(stamp);
            return CardRead.Taken;
        }
        // More than a reply carries is refused before anything is fetched, not after.
        if (images.Count + files.Count > AttachmentLimits.PerReply)
        {
            p.DriverPendingAt = null;
            await TransientAsync(p, "Reply NOT sent", new JsonObject { ["error"] = $"A reply carries at most {AttachmentLimits.PerReply} attachments; no reply was sent." }, ct);
            p.Reply.Handle(stamp);
            return CardRead.Taken;
        }
        await TransientAsync(p, "Sending...", [], ct);
        p.Reply.Handle(stamp);
        var shown = Preview(text, images.Count + files.Count);
        var request = RequestId(sessionId, "reply", "card:" + stamp);
        Deliver(p, sessionId, shown, box: false, text: null, [.. images.Select(i => i.Id)], async token =>
        {
            var staged = new List<HaReplyFile>();
            foreach (var (id, name, type) in images)
            {
                byte[]? bytes = null;
                // Any failure but the host stopping, a timeout included, is an image not fetched:
                // nothing was tried, so the originals stay in the store.
                try { bytes = HaImageId.IsValid(id) ? await ha.DownloadImageAsync(id, options.ImageMaxBytes, token) : null; }
                catch (Exception e) when (!token.IsCancellationRequested) { log($"Home Assistant connector could not fetch image {id}: {Describe(e)}"); }
                if (bytes is not { Length: > 0 }) { return Failure("An image could not be staged privately; no reply was sent and source images were kept."); }
                staged.Add(new HaReplyFile(string.IsNullOrWhiteSpace(name) ? $"{id}.png" : name, type, bytes));
            }
            foreach (var (name, type, b64) in files)
            {
                byte[]? bytes = null;
                try { bytes = Convert.FromBase64String(b64); }
                catch (FormatException) { }
                if (bytes is not { Length: > 0 } || bytes.Length > options.InlineFileMaxBytes)
                {
                    return Failure("A file could not be staged privately; no reply was sent and source images were kept.");
                }
                staged.Add(new HaReplyFile(string.IsNullOrWhiteSpace(name) ? "file" : name, type, bytes));
            }
            return (await reply!(new HaReply(sessionId, request, string.IsNullOrWhiteSpace(text) ? null : text, staged), token), false);
        });
        log($"Home Assistant connector took a reply-card reply for {sessionId[..Math.Min(8, sessionId.Length)]}" +
            (images.Count + files.Count > 0 ? $" with {images.Count + files.Count} attachment(s)" : ""));
        return CardRead.Delivering;
    }

    static (ReplyResult, bool) Failure(string why) => (new ReplyResult(AnswerOutcome.Refused, why), true);

    // Send-DaemonReplyBoxText: a new press of Send, with the reply box's words.
    async Task BoxReplyAsync(string sessionId, Published p, DateTimeOffset now, CancellationToken ct)
    {
        var press = await ObserveAsync(SessionEntities.Submit(p.Node), ct);
        if (!press.Present || press.Value == p.Reply.HandledPress) { return; }
        var first = press.Value != p.Reply.PendingPress;
        // The arm as it was before this press, which arming for it replaces.
        var before = p.Drive.Save();
        if (first) { p.ArmDriver(DriverOf(press.Snapshot?.UserId), now, PrincipalOf(press.Snapshot?.UserId)); }
        var box = await ObserveAsync(SessionEntities.Reply(p.Node), ct);
        if (box.Kind == "unknown" && box.Snapshot is null) { return; }
        // The tap on Send is what commits the box, so the press usually arrives first: give its
        // words a moment, on the first read of a press only, before saying anything.
        for (var i = 0; first && i < options.ReplyCommitAttempts && Blank(box); i++)
        {
            await Task.Delay(options.ReplyCommitWait, time, ct);
            var again = await ObserveAsync(SessionEntities.Reply(p.Node), ct);
            if (again.Snapshot is not null || again.Kind != "unknown") { box = again; }
        }
        if (Blank(box))
        {
            if (first && DriverOf(press.Snapshot?.UserId) == "agent")
            {
                // An agent sets the box through the API, where it commits at once: empty is empty.
                // Unless its reply-card publish already went, and this is that message's Send.
                if (before.Pending is { } at && now - at < options.DriverArm && before.Driver == "agent"
                    && p.Reply.PayloadDeliveredAt is { } went && now - went < options.DriverArm)
                {
                    p.Drive.Restore(before);
                    p.Reply.HandledPress = press.Value;
                    p.Reply.PendingPress = null;
                    log($"Home Assistant connector: send for {sessionId[..Math.Min(8, sessionId.Length)]} had an empty box because its payload had already gone; its arm kept as it was");
                    return;
                }
                p.DriverPendingAt = null;
                await TransientAsync(p, "Nothing to send", [], ct);
                p.Reply.HandledPress = press.Value;
                p.Reply.PendingPress = null;
                return;
            }
            if (first)
            {
                await TransientAsync(p, "Waiting for your text", new JsonObject { ["hint"] = "Press Enter in the box, or tap outside it, and this sends on its own." }, ct);
                p.Reply.PendingPress = press.Value;
                p.Reply.PendingSince = now;
                return;
            }
            if (now - p.Reply.PendingSince < options.SubmitArm) { return; }
            await TransientAsync(p, "Nothing to send", new JsonObject { ["hint"] = "Type a reply first, then press Send." }, ct);
            p.Reply.HandledPress = press.Value;
            p.Reply.PendingPress = null;
            return;
        }
        var text = box.Value;
        // A press that waited for its words is armed again now: something else (an agent's
        // publish, say) may have armed the next turn meanwhile, and these words are the press's.
        if (!first) { p.ArmDriver(DriverOf(press.Snapshot?.UserId), now, PrincipalOf(press.Snapshot?.UserId)); }
        await TransientAsync(p, "Sending...", [], ct);
        p.Reply.HandledPress = press.Value;
        p.Reply.PendingPress = null;
        var request = RequestId(sessionId, "reply", "box:" + press.Value);
        Deliver(p, sessionId, Preview(text, 0), box: true, text, [], async token =>
            (await reply!(new HaReply(sessionId, request, text, []), token), false));
    }

    // Runs a delivery beside the passes; what came of it waits for the next one.
    void Deliver(Published p, string sessionId, string shown, bool box, string? text, IReadOnlyList<string> images,
        Func<CancellationToken, Task<(ReplyResult Result, bool Failed)>> send)
    {
        var number = Interlocked.Increment(ref deliveries);
        underWay[sessionId] = number;
        var arm = p.DriverPendingAt;
        var token = replyStop.Token;
        var task = Task.Run(async () =>
        {
            (ReplyResult Result, bool Failed) outcome;
            try { outcome = await send(token); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { outcome = (new ReplyResult(AnswerOutcome.Unconfirmed, "the host stopped while the reply was being delivered"), false); }
            catch (Exception e) { outcome = (new ReplyResult(AnswerOutcome.Unconfirmed, $"delivery failed: {e.GetType().Name}"), false); }
            replies.Enqueue(new ReplyDone(number, sessionId, shown, box, text, images, arm, outcome.Result, outcome.Failed));
        }, CancellationToken.None);
        Track(task);
    }

    static bool Blank(Observation box) => !box.Present || string.IsNullOrWhiteSpace(box.Value);

    // What the card says was sent: the words, whitespace collapsed, at most 60 characters; a
    // reply of attachments alone says how many.
    static string Preview(string text, int attachments)
    {
        var shown = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (shown.Length == 0 && attachments > 0) { shown = attachments == 1 ? "(1 attachment)" : $"({attachments} attachments)"; }
        return shown.Length > 60 ? shown[..57] + "..." : shown;
    }

    static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
