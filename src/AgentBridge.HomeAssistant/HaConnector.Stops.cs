using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;

namespace AgentBridge.HomeAssistant;

// End session's words and hints, as agent-ha-bridge says them (decision-mqtt.ps1,
// Get-DaemonStopConfirmHint; Fixtures/ha-stops.json).
public static class HaStop
{
    public const string ConfirmNote = "Press End session again to end it";
    public const string LapsedNote = "End session NOT confirmed";
    public const string Ending = "Ending session...";
    public const string Failed = "Could not end session";

    // The line under the confirmation, naming what a second press would interrupt, from the
    // status the card showed when it was pressed.
    public static string Hint(string status, int seconds)
    {
        var doing = status switch
        {
            "" or "working" => "this session is still working",
            "waiting" => "this session is waiting on you",
            "agents" => "this session is waiting for background agents it started",
            "shell" => "this session is waiting for background commands it started",
            _ => $"this session is {status}",
        };
        return $"{doing} - the confirmation lapses in {seconds.ToString(CultureInfo.InvariantCulture)}s";
    }

    public static string NotConfirmedHint(int seconds) => $"no second press within {seconds.ToString(CultureInfo.InvariantCulture)}s";
}

// Stage 4: End session from the card (docs/ha-connector.md), as the daemon's
// Invoke-PendingStops takes it. The two presses themselves are the host's own (session.stop):
// a session that is not idle is only armed by a first press, and the host hands back a token
// that a second press, within its window, sends to end it. The connector carries the token
// between the two presses, keeps the question on the card while it is open
// (Get-DaemonCardSummary), says when it lapses (Clear-DaemonExpiredStopArms), and says what the
// ending came to. Ending can take a while (the session is given time to exit), so it runs beside
// the passes, as a reply's delivery does.
public sealed partial class HaConnector
{
    sealed class StopState
    {
        // Whether the button has been read once: a press already on it (from agent-ha-bridge, or
        // before a restart) is never taken for a new one.
        public bool Baselined;
        public string? Handled;
        // A first press the host armed: the token a second press sends, until when, and what the
        // card says meanwhile.
        public string? Token;
        public DateTimeOffset Until;
        public string Hint = "";
        public int Seconds = Stops.ConfirmSeconds;
        public DateTimeOffset RetryAt;
        // A press being acted on.
        public bool Busy;

        public bool Asking(DateTimeOffset now) => Token is not null && now < Until;
    }

    // Status: what the card showed when the press came, for the hint. Ending: whether the card
    // was already told the session is ending. Sent: when the request went to the host, which
    // starts any confirmation window no later than that.
    sealed record StopDone(string SessionId, string Node, string Name, Session? Session, string Status, bool Ending, DateTimeOffset Sent, StopResult Result);

    readonly Func<StopRequest, CancellationToken, Task<StopResult>>? stopper;
    readonly ConcurrentQueue<StopDone> stopsDone = new();

    // Whether End session is served: only with the host's stop delivery, and only where the
    // cards' controls are.
    bool ServesStops => stopper is not null && ServesAnswers;

    // Cards whose End session button has not been read yet, every card asking for a second press
    // (each pass, so a press is never read only once the window has closed: the sweep is slower
    // than the window, and the watch can be down), and every card on a sweep.
    void StopCards(Dictionary<string, List<(long Seq, HaTrigger Hit)>> touched, bool sweep)
    {
        if (!ServesStops) { return; }
        lock (gate)
        {
            foreach (var p in published.Values)
            {
                if (sweep || !p.Stop.Baselined || p.Stop.Token is not null) { touched.TryAdd(p.Node, []); }
            }
        }
    }

    // Invoke-PendingStops: a new press of End session.
    async Task StopPressAsync(string sessionId, Published p, DateTimeOffset now, CancellationToken ct)
    {
        var press = await ObserveAsync(SessionEntities.Stop(p.Node), ct);
        if (!p.Stop.Baselined)
        {
            if (press.Kind == "unknown") { return; }
            p.Stop.Handled = press.Present ? press.Value : null;
            p.Stop.Baselined = true;
            return;
        }
        if (!press.Present || press.Value == p.Stop.Handled) { return; }
        // A press while the session is being ended is not a second end, and is used up here: left
        // unread, it would be taken once the first is done, retrying an end that failed or was
        // refused without anyone pressing again.
        if (p.Stop.Busy)
        {
            p.Stop.Handled = press.Value;
            return;
        }
        // A press is a time; anything else on the button is not one.
        if (!DateTimeOffset.TryParse(press.Value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var pressedAt))
        {
            p.Stop.Handled = press.Value;
            return;
        }
        var session = source.Get(sessionId);
        var status = session is null ? "" : HaPayloads.Status(session);
        // A second press within the window sends the first's token; after it, a press is a first
        // press again.
        var token = p.Stop.Asking(now) ? p.Stop.Token : null;
        if (token is not null)
        {
            // Only a press made once the question was on the card answers it: a double press, the
            // second landing just before the card asked, must not end a working session. Both
            // times are Home Assistant's, the press's and the card's last change to the question.
            var card = await ObserveAsync(SessionEntities.Activity(p.Node), ct);
            if (card.Kind == "unknown") { return; }
            if (card.Snapshot is not { State: HaStop.ConfirmNote, LastChanged: { } asked } || pressedAt < asked)
            {
                p.Stop.Handled = press.Value;
                log($"Home Assistant connector: a press of end for {sessionId[..Math.Min(8, sessionId.Length)]} came before its card asked for it; not taken as the confirmation");
                return;
            }
        }
        // What the host will do: end an idle session, or one confirmed; anything else asks. Said
        // first, as the daemon says it, when it is about to end it; corrected if the host asks.
        var ending = token is not null || status == "idle";
        if (ending)
        {
            if (session is not null)
            {
                var t = HaContract.Topics(p.Node);
                await ha.PublishAsync(t.StatusState, "ending", true, ct);
                await PublishAsync(t.StatusAttributes, HaPayloads.StatusAttributes(session, p.Name, options.MachineName, now), true, ct);
                p.Status = null;
            }
            await TransientAsync(p, HaStop.Ending, [], ct);
        }
        p.Stop.Handled = press.Value;
        // The confirmation is used by this press: no longer asked for, and never said to have
        // lapsed while the host ends the session (which can take longer than the window).
        if (token is not null) { p.Stop.Token = null; }
        p.Stop.Busy = true;
        var request = new StopRequest { Id = sessionId, Request = RequestId(sessionId, "stop", press.Value), Confirm = token };
        var cancel = replyStop.Token;
        log($"Home Assistant connector: end requested for {sessionId[..Math.Min(8, sessionId.Length)]}{(token is null ? "" : " (confirmed)")}");
        Track(Task.Run(async () =>
        {
            StopResult result;
            // Read here, not from the pass: what the pass did before this took time the host's
            // window does not.
            var sent = time.GetUtcNow();
            try { result = await stopper!(request, cancel); }
            catch (OperationCanceledException) when (cancel.IsCancellationRequested) { result = new StopResult(StopOutcome.Failed, "the host stopped while ending the session"); }
            catch (Exception e) { result = new StopResult(StopOutcome.Failed, $"ending failed: {e.GetType().Name}"); }
            stopsDone.Enqueue(new StopDone(sessionId, p.Node, p.Name, session, status, ending, sent, result));
        }, CancellationToken.None));
    }

    // What came of the presses acted on since the last pass, said on their cards.
    async Task StopsDoneAsync(DateTimeOffset now, CancellationToken ct)
    {
        while (stopsDone.TryPeek(out var done))
        {
            Published? p;
            lock (gate) { p = published.TryGetValue(done.SessionId, out var found) ? found : null; }
            Observation? before = null;
            if (p is not null)
            {
                // What the button holds before the card says what came of this press was pressed
                // before it did, even if its report is still on the way: it neither confirms the
                // question about to be put nor retries an end that failed. Read first, so a press
                // made once the card has said so is taken as ever.
                before = await ObserveAsync(SessionEntities.Stop(p.Node), ct);
                await StopDoneAsync(p, done, now, ct);
            }
            else if (done.Result.Outcome == StopOutcome.Ended && IsRetiring(done.Node))
            {
                // Ended and already gone from the list, as the host drops it at once: the card says
                // so until it goes (Invoke-PendingStops publishes "ended" before the retire).
                await PublishEndedAsync(done.Node, done.Name, done.Session, now, ct);
            }
            stopsDone.TryDequeue(out _);
            if (p is not null)
            {
                p.Stop.Busy = false;
                if (before is { Kind: not "unknown" })
                {
                    p.Stop.Handled = before.Present ? before.Value : null;
                }
                else
                {
                    // Unreadable: read again as a baseline, later. A press made in between is lost,
                    // never taken for one that was not made.
                    p.Stop.Baselined = false;
                }
            }
        }
    }

    async Task StopDoneAsync(Published p, StopDone done, DateTimeOffset now, CancellationToken ct)
    {
        var result = done.Result;
        var short8 = done.SessionId[..Math.Min(8, done.SessionId.Length)];
        var t = HaContract.Topics(p.Node);
        if (result.Outcome != StopOutcome.Confirm) { p.Stop.Token = null; }
        // The card said "ending" ahead of an answer that was not that: shown as it is again.
        if (done.Ending && result.Outcome is not (StopOutcome.Ended or StopOutcome.Stale or StopOutcome.Failed)) { Refresh(done.SessionId, p); }
        switch (result.Outcome)
        {
            case StopOutcome.Confirm:
                var seconds = result.ConfirmSeconds ?? Stops.ConfirmSeconds;
                // The host asks for what it sees; a session the card shows idle has something going
                // on that only the host's own words name.
                var hint = done.Status == "idle" && result.Reason is { Length: > 0 } why ? why : HaStop.Hint(done.Status, seconds);
                // The host's window began when it gave the token, after the request went; this one
                // ends no later than the host's, and a question already past it is never put.
                var until = done.Sent + TimeSpan.FromSeconds(seconds);
                // The clock now, not the pass's: the publishes and the read before this take time.
                if (time.GetUtcNow() >= until)
                {
                    log($"Home Assistant connector: end of {short8} asked for a second press too late to be given one");
                    Refresh(done.SessionId, p);
                    break;
                }
                p.Stop.Token = result.Confirm;
                p.Stop.Until = until;
                p.Stop.Seconds = seconds;
                p.Stop.Hint = hint;
                p.Stop.RetryAt = DateTimeOffset.MinValue;
                try { await TransientAsync(p, HaStop.ConfirmNote, new JsonObject { ["hint"] = hint }, ct); }
                catch (Exception e) when (!ct.IsCancellationRequested)
                {
                    // Never shown, so never confirmable, and not tried again either: the press that
                    // follows, because this one looked dead, is a first press and asks, rather than
                    // ending the session the moment the question finally appears.
                    p.Stop.Token = null;
                    log($"Home Assistant connector: end of {short8} could not ask for a second press ({e.GetType().Name}); the next press asks again");
                    break;
                }
                log($"Home Assistant connector: end of {short8} waits for a second press");
                break;
            case StopOutcome.Ended:
                await PublishEndedAsync(p.Node, p.Name, done.Session, now, ct);
                p.Status = null;
                log($"Home Assistant connector ended {short8}{(result.Forced == true ? " (terminated)" : "")}");
                break;
            case StopOutcome.Stale:
                // Gone already: the card goes with it.
                break;
            case StopOutcome.Failed:
                await TransientAsync(p, HaStop.Failed, new JsonObject { ["error"] = result.Reason ?? "" }, ct);
                await ha.PublishAsync(t.StatusState, "error", true, ct);
                if (done.Session is { } failed) { await PublishAsync(t.StatusAttributes, HaPayloads.StatusAttributes(failed, p.Name, options.MachineName, now), true, ct); }
                p.Status = null;
                log($"Home Assistant connector could not end {short8}: {result.Reason}");
                break;
            default:
                await TransientAsync(p, HaStop.Failed, new JsonObject { ["error"] = result.Reason ?? result.Outcome.ToString() }, ct);
                log($"Home Assistant connector could not end {short8}: {result.Outcome} {result.Reason}");
                break;
        }
    }

    async Task PublishEndedAsync(string node, string name, Session? session, DateTimeOffset now, CancellationToken ct)
    {
        var t = HaContract.Topics(node);
        await ha.PublishAsync(t.StatusState, "ended", true, ct);
        if (session is not null) { await PublishAsync(t.StatusAttributes, HaPayloads.StatusAttributes(session, name, options.MachineName, now), true, ct); }
    }

    bool IsRetiring(string node)
    {
        lock (gate) { return retiring.ContainsKey(node); }
    }

    // A session's status and activity published again as they are.
    void Refresh(string sessionId, Published p)
    {
        p.Status = null;
        p.Activity = null;
        lock (gate) { dirty.Add(sessionId); }
    }

    // Clear-DaemonExpiredStopArms: a first press nobody confirmed in time is said to have lapsed;
    // only once that is shown is it forgotten, so the card never goes on asking for a press that
    // no longer confirms anything.
    async Task StopLapsesAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!ServesStops) { return; }
        List<Published> lapsed;
        lock (gate) { lapsed = [.. published.Values.Where(p => p.Stop.Token is not null && now >= p.Stop.Until && now >= p.Stop.RetryAt)]; }
        foreach (var p in lapsed)
        {
            try
            {
                await TransientAsync(p, HaStop.LapsedNote, new JsonObject { ["hint"] = HaStop.NotConfirmedHint(p.Stop.Seconds) }, ct);
                p.Stop.Token = null;
            }
            catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning))
            {
                p.Stop.RetryAt = now + options.StopLapseRetry;
            }
        }
    }
}
