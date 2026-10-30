using System.Text.Json;
using AgentBridge.Local;

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
}

// Sends answers, replies and stops (session.answer, session.reply, session.stop), each to
// exactly one outcome, as the PWA's AnswerService does: a fresh request UUID per thing the
// person does, and the same one again after the connection dropped under it, which the host
// answers with the first outcome instead of acting twice. A host that restarted meanwhile has
// forgotten the request: answers and stops go again (the host guards those across restarts),
// a reply does not. One per key at a time; a busy that answers a resend of our own request is
// waited out, not shown.
public sealed class Deliveries
{
    const string NoOutcome = "no outcome came back from the machine in time";
    const string TooOld = "too long has passed since it was first sent for the machine to still recognise it; check at the machine";
    const string Restarted = "the Bridge Host restarted before saying whether it went";
    const int MaxBusyResends = 5;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(90);

    readonly HostConnection host;
    readonly IUiDispatcher ui;
    readonly TimeProvider time;
    readonly Func<string> uuid;
    readonly Dictionary<string, Delivery> entries = new(StringComparer.Ordinal);
    readonly object gate = new();

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

    // Raised on the UI thread whenever an entry changes.
    public event Action<Delivery>? Changed;

    public Delivery? Get(string key) { lock (gate) { return entries.GetValueOrDefault(key); } }

    // op: the request to send. data(request uuid): its data. final(outcome): whether an outcome
    // stands, so doing it again sends nothing. before: work on the connection ahead of the op
    // (a reply's files), given a progress callback.
    public Delivery Submit(string key, string op, Func<string, object> data, Func<string, bool> final, object? payload = null,
        Func<Func<string, object?, TimeSpan?, Task<JsonElement>>, Action<long, long>, Task>? before = null, bool replayAfterRestart = true, TimeSpan? timeout = null, bool resendOnBusy = true)
    {
        Delivery entry;
        lock (gate)
        {
            if (entries.TryGetValue(key, out var existing)
                && (existing.State is DeliveryState.Sending or DeliveryState.Waiting || (existing.State == DeliveryState.Done && final(existing.Outcome!))))
            {
                return existing;
            }
            entries[key] = entry = new Delivery { Key = key, Request = uuid(), State = DeliveryState.Sending, Payload = payload, ReplayAfterRestart = replayAfterRestart, Timeout = timeout ?? DefaultTimeout, ResendOnBusy = resendOnBusy };
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
        if (entry?.State != DeliveryState.Failed) { return; }
        _ = RunAsync(entry, op, data, before, resend: true);
    }

    async Task RunAsync(Delivery entry, string op, Func<string, object> data, Func<Func<string, object?, TimeSpan?, Task<JsonElement>>, Action<long, long>, Task>? before, bool resend)
    {
        // An op never replayed after a restart is counted from its first send, not from this Try
        // again: once the host may have forgotten the request id, another copy could act twice.
        var fromFirst = resend && !entry.ReplayAfterRestart && entry.FirstSent is not null;
        var started = fromFirst ? entry.FirstSent!.Value : time.GetUtcNow();
        var firstSent = entry.FirstSent;
        var busy = 0;
        var attempts = entry.Attempts;
        // The host the op itself last went to, if it went: files sent ahead are not the op. Carried
        // over from an earlier run of this request (a resend after it failed).
        var sentTo = entry.SentTo;
        while (true)
        {
            if (time.GetUtcNow() - started > GiveUp)
            {
                Update(entry.Key, e => e with { State = DeliveryState.Done, Outcome = "unconfirmed", Reason = fromFirst && attempts == entry.Attempts ? TooOld : NoOutcome });
                return;
            }
            attempts++;
            Update(entry.Key, e => e with { State = DeliveryState.Sending, Attempts = attempts, Reason = null });
            JsonElement result;
            try
            {
                if (before is not null)
                {
                    await before((o, d, t) => host.CallAsync(o, d, t), (sent, total) => Update(entry.Key, e => e with { Progress = (sent, total) }));
                }
                // Which host it went to is read with the connection it goes on. An op never replayed
                // after a restart waits until that host is known, and goes again only to the same one.
                var call = host.CallAsync(op, data(entry.Request), entry.Timeout, knownHost: !entry.ReplayAfterRestart,
                    onlyTo: entry.ReplayAfterRestart ? null : sentTo, out var to);
                if (to != sentTo) { sentTo = to; Update(entry.Key, e => e with { SentTo = to }); }
                if (to is not null && firstSent is null)
                {
                    var now = time.GetUtcNow();
                    firstSent = now;
                    Update(entry.Key, e => e with { FirstSent = now });
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
                try { await host.WhenConnectedAsync().WaitAsync(GiveUp, time); }
                catch (TimeoutException) { }
                await Task.Delay(RetryDelay);
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
        lock (gate)
        {
            if (!entries.TryGetValue(key, out var e)) { return; }
            entries[key] = next = change(e);
        }
        Publish(next);
    }

    void Publish(Delivery entry) => ui.Post(() => Changed?.Invoke(entry));
}
