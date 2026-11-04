using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

public enum DeliveryState { Sending, Waiting, Failed, Done }

// One answer, reply or press of End session on its way to exactly one outcome.
public sealed record Delivery
{
    public required string Key { get; init; }
    public required string Request { get; init; }
    public DeliveryState State { get; init; }
    // The host's outcome (delivered, refused, confirm, ended...), once Done.
    public string? Outcome { get; init; }
    public string? Reason { get; init; }
    // The whole result, for outcomes that say more (End session's confirm token).
    public JsonElement Result { get; init; }
    public int Attempts { get; init; }
    // What the op has done so far: bytes of a reply's files sent, of all of them.
    public (long Sent, long Total)? Progress { get; init; }
    // How far the host has got with an answer or a reply on its way, where it says
    // (sessions.progress, #600): received (it has it), typed (the keys have gone) or queued
    // (typed into a working session, which takes it at its next step). It only moves forward,
    // across resends of the same request. Null from a host that doesn't say.
    public string? Stage { get; init; }
    public object? Payload { get; init; }
    // Whether a copy whose outcome was lost may go again to a host that has since restarted.
    // The host remembers request ids only in memory, so only ops it guards across restarts
    // otherwise (answers: its attempt ledger; End session: its two-press rule) may.
    public bool ReplayAfterRestart { get; init; } = true;
    // How long one attempt waits for the host's outcome: a launch waits for its session to start.
    public TimeSpan Timeout { get; init; } = Deliveries.DefaultTimeout;
    // Whether busy on a resend is the first copy still being delivered (answers, replies) rather
    // than an outcome of its own (a launch: the host answers a resend with the first's result).
    public bool ResendOnBusy { get; init; } = true;
    // The host process the op last went to, kept with the entry so a Try again after a failure
    // goes only to that process too, for an op never replayed after a restart.
    public string? SentTo { get; init; }
    // When the op itself first went to a host, which remembers its request id for only so long
    // after that. Null while it never went: files that failed to go ahead of a reply are not it.
    public DateTimeOffset? FirstSent { get; init; }
    // The same moment on the monotonic clock (TimeProvider.GetTimestamp), which every elapsed
    // check uses: a wall clock stepped back must not reopen the window and send a launch twice.
    public long? FirstSentAt { get; init; }
    // How long it goes on being sent, when not the Deliveries' own GiveUp: a session transfer
    // waits longer for its outcome, and is remembered longer by the host (v1-transfer.md).
    public TimeSpan? GiveUp { get; init; }
}

// Sends answers, replies and stops (session.answer, session.reply, session.stop), each to
// exactly one outcome, as the PWA's AnswerService does: a fresh request UUID per thing the
// person does, and the same one again after the connection dropped under it, which the host
// answers with the first outcome instead of acting twice. A host that restarted meanwhile has
// forgotten the request: answers and stops go again (the host guards those across restarts),
// a reply does not. One per key at a time; a busy that answers a resend of our own request is
// waited out, not shown.
// A host that offers sessions.progress is asked for it on every answer and reply, and says how
// far each has got before its outcome (Delivery.Stage). Each answer and reply also keeps timing
// marks on this device (#600): the tap, each send, each stage and the outcome, in ms from the
// tap, written to the app's log as one line with the host's own timings beside them, so where
// the time went can be read without a debugger. Nothing of it is sent anywhere.
public sealed class Deliveries
{
    const string NoOutcome = "no outcome came back from the machine in time";
    const string TooOld = "too long has passed since it was first sent for the machine to still recognise it; check at the machine";
    const string Restarted = $"the {Product.HostName} restarted before saying whether it went";
    const int MaxBusyResends = 5;
    // Deliveries whose timing marks are kept until their outcome: one that never has one (the app
    // closing mid-send aside, a key replaced) must not keep its marks for ever.
    const int MaxMarked = 50;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);
    // Delivery.Stage for typed into a working session.
    public const string Queued = "queued";

    readonly HostConnection host;
    readonly IUiDispatcher ui;
    readonly TimeProvider time;
    readonly Func<string> uuid;
    readonly Dictionary<string, Delivery> entries = new(StringComparer.Ordinal);
    // The timing marks of each answer and reply on its way, by request.
    readonly Dictionary<string, Marks> marks = new(StringComparer.Ordinal);
    readonly object gate = new();

    sealed record Marks(string Kind, long Start, List<(string What, long Ms)> At);

    public Deliveries(HostConnection host, IUiDispatcher ui, TimeProvider? time = null, Func<string>? uuid = null)
    {
        this.host = host;
        this.ui = ui;
        this.time = time ?? TimeProvider.System;
        this.uuid = uuid ?? (() => Guid.NewGuid().ToString());
    }

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(2);
    internal IUiDispatcher Ui => ui;
    internal TimeProvider Time => time;
    public TimeSpan GiveUp { get; init; } = TimeSpan.FromMinutes(9);

    // Set by another machine's Machine (#269): why nothing new may go now for an op, or null. Every
    // answer, reply and End session passes here, a toast's answer and Needs you's included, so none
    // can act on a session kept from before its machine went away. What is already on its way goes
    // on: it went while the session was current, and its request id lets the host act on it once.
    internal Func<string, string?>? Hold { get; set; }

    // Whether Hold would refuse the op now.
    public bool Holds(string op) => Hold?.Invoke(op) is not null;

    // The outcome of a submit Hold refused: nothing sent, nothing kept.
    public const string Held = "offline";

    // Raised on the UI thread whenever an entry changes.
    public event Action<Delivery>? Changed;

    public Delivery? Get(string key) { lock (gate) { return entries.GetValueOrDefault(key); } }

    // op: the request to send. data(request uuid): its data. final(outcome): whether an outcome
    // stands, so doing it again sends nothing. before: work on the connection ahead of the op
    // (a reply's files), given a progress callback.
    public Delivery Submit(string key, string op, Func<string, object> data, Func<string, bool> final, object? payload = null,
        Func<Func<string, object?, TimeSpan?, Task<JsonElement>>, Action<long, long>, Task>? before = null, bool replayAfterRestart = true, TimeSpan? timeout = null, bool resendOnBusy = true, TimeSpan? giveUp = null)
    {
        Delivery entry;
        var held = Hold?.Invoke(op);
        lock (gate)
        {
            if (entries.TryGetValue(key, out var existing)
                && (existing.State is DeliveryState.Sending or DeliveryState.Waiting || (existing.State == DeliveryState.Done && final(existing.Outcome!))))
            {
                return existing;
            }
            if (held is not null) { return new Delivery { Key = key, Request = "", State = DeliveryState.Done, Outcome = Held, Reason = held, Payload = payload }; }
            entries[key] = entry = new Delivery { Key = key, Request = uuid(), State = DeliveryState.Sending, Payload = payload, ReplayAfterRestart = replayAfterRestart, Timeout = timeout ?? DefaultTimeout, ResendOnBusy = resendOnBusy, GiveUp = giveUp };
            if (KindOf(op) is { } kind)
            {
                if (marks.Count >= MaxMarked) { marks.Remove(marks.MinBy(m => m.Value.Start).Key); }
                marks[entry.Request] = new Marks(kind, time.GetTimestamp(), []);
            }
        }
        Publish(entry);
        _ = RunAsync(entry, op, data, before, resend: false);
        return entry;
    }

    // After Failed: the same request again, so the host still acts on it at most once.
    public void Resend(string key, string op, Func<string, object> data, Func<Func<string, object?, TimeSpan?, Task<JsonElement>>, Action<long, long>, Task>? before = null)
    {
        Delivery? entry;
        lock (gate) { entry = entries.GetValueOrDefault(key); }
        if (entry?.State != DeliveryState.Failed || Hold?.Invoke(op) is not null) { return; }
        _ = RunAsync(entry, op, data, before, resend: true);
    }

    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    async Task RunAsync(Delivery entry, string op, Func<string, object> data, Func<Func<string, object?, TimeSpan?, Task<JsonElement>>, Action<long, long>, Task>? before, bool resend)
    {
        // Two limits, both on the monotonic clock. This run gives up after GiveUp. An op never replayed
        // after a restart also goes, and is waited on, only for GiveUp from its first send, whichever
        // run or Try again: once the host may have forgotten the request id, another copy could act twice.
        var limited = !entry.ReplayAfterRestart;
        var started = time.GetTimestamp();
        var firstSent = entry.FirstSentAt;
        var giveUp = entry.GiveUp ?? GiveUp;
        TimeSpan RunLeft() => giveUp - time.GetElapsedTime(started);
        TimeSpan? WindowLeft() => limited && firstSent is { } f ? giveUp - time.GetElapsedTime(f) : null;
        TimeSpan Left() => WindowLeft() is { } w && w < RunLeft() ? w : RunLeft();
        string Late() => WindowLeft() is { } w && w <= RunLeft() ? TooOld : NoOutcome;
        var kind = KindOf(op);
        var busy = 0;
        var attempts = entry.Attempts;
        // The host the op itself last went to, if it went: files sent ahead are not the op. Carried
        // over from an earlier run of this request (a resend after it failed).
        var sentTo = entry.SentTo;
        while (true)
        {
            if (Left() <= TimeSpan.Zero)
            {
                var late = Late();
                Update(entry.Key, e => e with { State = DeliveryState.Done, Outcome = "unconfirmed", Reason = late, Progress = null });
                return;
            }
            attempts++;
            Update(entry.Key, e => e with { State = DeliveryState.Sending, Attempts = attempts, Reason = null });
            JsonElement result;
            // A copy of an op that already went is sent, and waited on, only until the host may forget
            // the request: a send still queued then is dropped unencrypted and unsent (SecureChannel), and
            // a wait cut short reads unconfirmed. Either way it can only lose an outcome, never act twice.
            using var bound = WindowLeft() is { } remaining ? new CancellationTokenSource(Max(remaining, TimeSpan.Zero), time) : null;
            try
            {
                if (before is not null)
                {
                    await before((o, d, t) => host.CallAsync(o, d, t), (sent, total) => Update(entry.Key, e => e with { Progress = (sent, total) }));
                    // Files can take minutes to go: the request goes only while the host still knows it.
                    // The host remembers a reply's request for 15 minutes and a launch's for 25, so a frame
                    // already being written at 9 (written whole, or the relay socket's keepalive aborts it)
                    // still lands inside that.
                    if (WindowLeft() <= TimeSpan.Zero)
                    {
                        Update(entry.Key, e => e with { State = DeliveryState.Done, Outcome = "unconfirmed", Reason = TooOld, Progress = null });
                        return;
                    }
                }
                // Which host it went to is read with the connection it goes on. An op never replayed
                // after a restart waits until that host is known, and goes again only to the same one.
                // An answer or a reply asks a host offering it for its progress; an older one is asked
                // nothing new.
                var request = data(entry.Request);
                if (kind is not null && host.Can(Capabilities.SessionsProgress)) { request = AskProgress(request); }
                var attempt = attempts;
                Action<JsonElement>? heard = kind is null ? null : p => Heard(entry.Key, entry.Request, attempt, p);
                Mark(entry.Request, "sent");
                var call = host.CallAsync(op, request, entry.Timeout, knownHost: !entry.ReplayAfterRestart,
                    onlyTo: entry.ReplayAfterRestart ? null : sentTo, out var to, bound?.Token ?? default, heard);
                if (to != sentTo) { sentTo = to; Update(entry.Key, e => e with { SentTo = to }); }
                if (to is not null && firstSent is null)
                {
                    var at = time.GetTimestamp();
                    var now = time.GetUtcNow();
                    firstSent = at;
                    Update(entry.Key, e => e with { FirstSent = now, FirstSentAt = at });
                }
                result = await call;
            }
            catch (HostRestartedException)
            {
                // A replacement host has no memory of the request, so for a reply or a launch sending
                // it again could do it twice: what became of it is the machine's to say.
                Update(entry.Key, x => x with { State = DeliveryState.Done, Outcome = "unconfirmed", Reason = Restarted, Progress = null });
                return;
            }
            catch (OperationCanceledException) when (bound?.IsCancellationRequested == true)
            {
                Update(entry.Key, x => x with { State = DeliveryState.Done, Outcome = "unconfirmed", Reason = TooOld, Progress = null });
                return;
            }
            catch (HostErrorException e)
            {
                Update(entry.Key, x => x with { State = DeliveryState.Failed, Reason = e.Message });
                return;
            }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException)
            {
                // No outcome: the host went (or was never there). It may have acted, so the
                // same request goes again once it is back.
                Update(entry.Key, x => x with { State = DeliveryState.Waiting, Reason = e is HostNotRunningException ? "not connected" : e.Message });
                // Waited on only for what is left of the limits, so a Try again pressed late does not hold
                // the page past them while the machine is away.
                if (Left() is var left && left > TimeSpan.Zero)
                {
                    try { await host.WhenConnectedAsync().WaitAsync(left, time); }
                    catch (TimeoutException) { }
                }
                await Task.Delay(Min(RetryDelay, Max(Left(), TimeSpan.Zero)));
                // Seen early, before any files go again to a host that would not know them; the call
                // itself checks again on the connection it goes on.
                if (!entry.ReplayAfterRestart && sentTo is not null && host.HostInstance is { } now && now != sentTo)
                {
                    Update(entry.Key, x => x with { State = DeliveryState.Done, Outcome = "unconfirmed", Reason = Restarted, Progress = null });
                    return;
                }
                continue;
            }
            var outcome = result.TryGetProperty("outcome", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : "unconfirmed";
            var reason = result.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null;
            // A resend can reach the host while it still delivers the first copy, which says
            // busy: that busy is our own, so ask again with the same request, a few times.
            if (outcome == "busy" && entry.ResendOnBusy && (attempts > 1 || resend))
            {
                if (++busy > MaxBusyResends)
                {
                    Update(entry.Key, x => x with { State = DeliveryState.Done, Outcome = "unconfirmed", Reason = reason ?? NoOutcome });
                    return;
                }
                Update(entry.Key, x => x with { State = DeliveryState.Waiting, Reason = "still being delivered" });
                await Task.Delay(RetryDelay);
                continue;
            }
            var raw = result.Clone();
            Update(entry.Key, x => x with { State = DeliveryState.Done, Outcome = outcome, Reason = reason, Result = raw, Progress = null });
            return;
        }
    }

    void Update(string key, Func<Delivery, Delivery> change)
    {
        Delivery next;
        bool done;
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var e)) { return; }
            next = change(e);
            // Nothing changed (a stage behind one reached): nothing to tell.
            if (ReferenceEquals(next, e)) { return; }
            entries[key] = next;
            done = next.State == DeliveryState.Done && e.State != DeliveryState.Done;
        }
        if (done) { Finished(next); }
        Publish(next);
    }

    static string? KindOf(string op) => op == Ops.SessionReply ? "reply" : op == Ops.SessionAnswer ? "answer" : null;

    // The host measures an answer, flag and all, against its 16 KiB (Answers.Check): an answer
    // that fits only without "progress": true goes without it, and shows Sending until its
    // outcome, rather than be refused for asking how it went.
    static object AskProgress(object data) => data switch
    {
        AnswerRequest a when JsonSerializer.SerializeToUtf8Bytes(a with { Progress = true }, ProtocolJson.Options).Length <= Limits.AnswerRequest => a with { Progress = true },
        ReplyRequest r => r with { Progress = true },
        _ => data,
    };

    static int Rank(string? stage) => stage switch
    {
        DeliveryProgress.Received => 1,
        DeliveryProgress.Typed or Queued => 2,
        _ => 0,
    };

    // A progress frame for the attempt numbered attempt. One from an attempt given up on, a stage
    // this app doesn't know (from a newer host), or one behind the stage reached changes nothing:
    // a resend's repeat replays the stages so far, and what was typed stays typed.
    void Heard(string key, string request, int attempt, JsonElement progress)
    {
        if (progress.ValueKind != JsonValueKind.Object || !progress.TryGetProperty("stage", out var s) || s.ValueKind != JsonValueKind.String) { return; }
        var stage = s.GetString() switch
        {
            DeliveryProgress.Received => DeliveryProgress.Received,
            DeliveryProgress.Typed => progress.TryGetProperty("working", out var w) && w.ValueKind == JsonValueKind.True ? Queued : DeliveryProgress.Typed,
            _ => null,
        };
        if (stage is null) { return; }
        bool Current(Delivery e) => e.Request == request && e.State == DeliveryState.Sending && e.Attempts == attempt;
        var moved = false;
        Update(key, e =>
        {
            if (!Current(e) || stage == e.Stage || Rank(stage) < Rank(e.Stage)) { return e; }
            moved = true;
            return e with { Stage = stage };
        });
        // Marked only when the stage moves on: a repeat replaying the stages so far, or a host
        // saying the same again, adds nothing to the timing line.
        if (moved) { Mark(request, stage); }
    }

    // A timing mark for an answer or a reply on its way, in ms since the tap. A second of the same
    // (a resend's send, a repeat's stages) is numbered: "sent 2".
    void Mark(string request, string what)
    {
        lock (gate)
        {
            if (!marks.TryGetValue(request, out var run)) { return; }
            var again = run.At.Count(m => m.What == what || m.What.StartsWith(what + " ", StringComparison.Ordinal));
            run.At.Add((again == 0 ? what : $"{what} {again + 1}", (long)time.GetElapsedTime(run.Start).TotalMilliseconds));
        }
    }

    // An answer's or a reply's outcome: its marks and the host's timings (the result's, ms since the
    // host received it), as one line in the app's log, named by the request's first 8 hex digits
    // as the host's own log names it.
    void Finished(Delivery done)
    {
        Marks? run;
        lock (gate)
        {
            if (!marks.Remove(done.Request, out run)) { return; }
        }
        var at = run.At.Select(m => $"{m.What} {m.Ms} ms").Append($"result {(long)time.GetElapsedTime(run.Start).TotalMilliseconds} ms");
        var line = $"{run.Kind} {done.Request[..Math.Min(8, done.Request.Length)]} {done.Outcome}: {string.Join(", ", at)}";
        if (done.Result.ValueKind == JsonValueKind.Object && done.Result.TryGetProperty("timings", out var timings) && timings.ValueKind == JsonValueKind.Object)
        {
            var stages = timings.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt64(out var ms) && ms >= 0).Select(p => $"{p.Name} {p.Value.GetInt64()} ms").ToList();
            if (stages.Count > 0) { line += $"; host: {string.Join(", ", stages)}"; }
        }
        host.Log(line);
    }

    void Publish(Delivery entry) => ui.Post(() => Changed?.Invoke(entry));
}
