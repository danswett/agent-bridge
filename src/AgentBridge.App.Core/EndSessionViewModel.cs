using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// End session at the foot of a card (pwa/js/ui/stop-view.js). The host enforces the rule: a
// session that is not idle takes two presses, the first answering confirm with a token and a
// window, the second carrying the token. Here the first press's answer is shown with the
// seconds left, and a press after the window has lapsed is another first press.
public sealed class EndSessionViewModel : Observable
{
    public const string ConfirmNote = "Press End session again to end it";
    public const string LapsedNote = "End session NOT confirmed";

    readonly Deliveries deliveries;
    readonly TimeProvider time;
    readonly string sessionId;
    readonly string at;
    Session session;
    (string Request, DateTimeOffset At, int Seconds, string Token)? armed;
    string statusText = "";
    Tone statusTone;
    bool pausedAfterBusy;
    string? busyFor;
    ITimer? tick;

    public EndSessionViewModel(Session session, Deliveries deliveries, string? machineName = null)
    {
        this.session = session;
        this.deliveries = deliveries;
        time = deliveries.Time;
        sessionId = session.Id;
        at = string.IsNullOrEmpty(machineName) ? "" : $" on {machineName}";
        deliveries.Changed += OnDelivery;
        Refresh();
    }

    void OnDelivery(Delivery d) { if (d.Key == Key) { Refresh(); } }

    public string Key => $"stop|{sessionId}";
    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public Tone StatusTone { get => statusTone; private set => Set(ref statusTone, value); }
    public bool ShowResend => deliveries.Get(Key)?.State == DeliveryState.Failed;
    public bool CanPress => !Pending && !pausedAfterBusy && session.Status != SessionStatus.Ending;
    public string ButtonName => SecondsLeft is { } left ? $"End session: press again to confirm, {left} seconds left" : "End session";
    bool Pending => deliveries.Get(Key)?.State is DeliveryState.Sending or DeliveryState.Waiting;

    // Seconds left for the second press, or null when no first press is waiting for one.
    public int? SecondsLeft
    {
        get
        {
            var e = deliveries.Get(Key);
            if (armed is not { } a || e is not { State: DeliveryState.Done, Outcome: "confirm" } || e.Request != a.Request) { return null; }
            var ms = (a.At + TimeSpan.FromSeconds(a.Seconds) - time.GetUtcNow()).TotalMilliseconds;
            return ms > 0 ? (int)Math.Ceiling(ms / 1000) : null;
        }
    }

    public void Update(Session next)
    {
        session = next;
        Refresh();
    }

    public void Press()
    {
        if (!CanPress) { return; }
        var confirm = SecondsLeft is not null ? armed!.Value.Token : null;
        deliveries.Submit(Key, Ops.SessionStop, r => new StopRequest { Id = sessionId, Request = r, Confirm = confirm }, _ => false, payload: confirm);
        Refresh();
    }

    public void Resend() => deliveries.Resend(Key, Ops.SessionStop, r => new StopRequest { Id = sessionId, Request = r, Confirm = deliveries.Get(Key)?.Payload as string });

    public void Refresh()
    {
        var e = deliveries.Get(Key);
        if (e is { State: DeliveryState.Done, Outcome: "confirm" } && armed?.Request != e.Request)
        {
            var token = e.Result.ValueKind == JsonValueKind.Object && e.Result.TryGetProperty("confirm", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var seconds = e.Result.ValueKind == JsonValueKind.Object && e.Result.TryGetProperty("confirmSeconds", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : Stops.ConfirmSeconds;
            if (token is not null)
            {
                armed = (e.Request, time.GetUtcNow(), seconds, token);
                tick?.Dispose();
                // Once a second while the window is open, so the countdown moves and the lapse shows.
                tick = time.CreateTimer(_ => deliveries.Ui.Post(() =>
                {
                    if (SecondsLeft is null) { tick?.Dispose(); tick = null; }
                    Refresh();
                }), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
        }
        if (e is { State: DeliveryState.Done, Outcome: "busy" } && !pausedAfterBusy && busyFor != e.Request)
        {
            busyFor = e.Request;
            pausedAfterBusy = true;
            _ = Task.Delay(2000).ContinueWith(_ => deliveries.Ui.Post(() => { pausedAfterBusy = false; Refresh(); }), TaskScheduler.Default);
        }
        (StatusText, StatusTone) = e is null ? ("", Tone.None) : Describe(e);
        Raise(nameof(CanPress));
        Raise(nameof(ShowResend));
        Raise(nameof(SecondsLeft));
        Raise(nameof(ButtonName));
    }

    (string, Tone) Describe(Delivery e)
    {
        switch (e.State)
        {
            case DeliveryState.Sending:
                return (e.Payload is string ? "Ending the session…" : "Sending…", Tone.Progress);
            case DeliveryState.Waiting:
                return (e.Reason == "still being delivered" ? "The machine is still ending it…" : $"Waiting for the Bridge Host{at}. Your press goes as soon as it is back.", Tone.Progress);
            case DeliveryState.Failed:
                return ($"Couldn't send it: {e.Reason ?? "the machine said no"}.", Tone.Bad);
        }
        var forced = e.Result.ValueKind == JsonValueKind.Object && e.Result.TryGetProperty("forced", out var f) && f.ValueKind == JsonValueKind.True;
        return e.Outcome switch
        {
            "confirm" when SecondsLeft is { } left => ($"{ConfirmNote}: {Words.Sentence(e.Reason ?? "this session is not idle")} {left} s left.", Tone.Warn),
            "confirm" => ($"{LapsedNote}: no second press within {armed?.Seconds ?? Stops.ConfirmSeconds} s. The session is still running.", Tone.Warn),
            "ended" => (forced ? "Session ended. It did not exit on its own, so the machine terminated it." : "Session ended.", Tone.Ok),
            "stale" => ("This session had already ended.", Tone.Warn),
            "busy" => ("Not ended: an answer or reply to this session is being delivered, or it is already being ended. Try again in a moment.", Tone.Warn),
            "refused" => ($"The machine refused to end this session{(e.Reason is { } r ? $": {Words.Sentence(r)}" : ".")}", Tone.Bad),
            _ => ($"Couldn't end the session{(e.Reason is { } x ? $": {Words.Sentence(x)}" : ". Check at the machine.")}", Tone.Bad),
        };
    }

    public void Dispose()
    {
        deliveries.Changed -= OnDelivery;
        tick?.Dispose();
    }
}
