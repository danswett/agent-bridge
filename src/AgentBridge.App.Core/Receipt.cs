using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// The receipt of a reply or an answer on its way (#600; ui.md §6.4, §6.5 and §9, as the owner
// decided them on #617, revised the same day to "quiet unless something's wrong").
//
// The normal path is quiet: Send's ring says a request is open, and its check (about a second)
// that it was delivered; the line under the box stays empty through Sending, Sent, Passed to the
// agent and Up next, and a reply's Delivered. A screen reader still hears each stage. The line has
// words only for exceptions: the stalls, the waits, and an outcome other than Delivered; an answer
// resolves its card to "You answered · value". The stages are the machine's (sessions.progress,
// Delivery.Stage); a machine without it goes from Sending to the outcome, and nothing here invents
// a stage nobody reported. What Receipt decides is when each is said: nothing for the first 400 ms,
// then the furthest stage reached, at most one a second, an outcome at once. The PWA's
// pwa/js/ui/receipt.js is the same, word for word.

public enum ReceiptMark { None, Ring, Still, Check }

// Key: the state, which the dwell holds apart (a stall changes the words, not the state). Text:
// the line's words, empty on the normal path. More: the secondary words, after " · ". Mark: what
// Send shows: its ring, the ring standing still (nothing is moving), or its check. Speech: what a
// screen reader hears, or null.
public sealed record ReceiptLine(string Key, string Text, string? More, Tone Tone, ReceiptMark Mark, string? Speech)
{
    public string Whole => More is null ? Text : $"{Text} · {More}";
    public static readonly ReceiptLine None = new("none", "", null, Tone.None, ReceiptMark.None, null);
    internal static readonly ReceiptLine Quiet = new("quiet", "", null, Tone.None, ReceiptMark.None, null);
}

// In one place so they can be tuned once the owner has seen them live (#617, "Per platform").
public static class ReceiptTiming
{
    // Nothing on the line for this long after Send.
    public static readonly TimeSpan Quiet = TimeSpan.FromMilliseconds(400);
    // An in-between state stays at least this long before the next replaces it.
    public static readonly TimeSpan Dwell = TimeSpan.FromMilliseconds(400);
    // How long Send (an answer's button) shows its check once delivered, before it rests (sendCheckHold).
    public static readonly TimeSpan SendCheckHold = TimeSpan.FromSeconds(1);
    // Nothing heard from the machine: "Still sending…".
    public static readonly TimeSpan StillSending = TimeSpan.FromSeconds(3);
    // Sent, and not passed to the agent: "· still waiting on machine".
    public static readonly TimeSpan SentStall = TimeSpan.FromSeconds(5);
    // Passed to an idle agent, and not picked up: "· not picked up yet". Up next has none: the
    // agent is busy, and waiting is expected.
    public static readonly TimeSpan PassedStall = TimeSpan.FromSeconds(10);
    // At most one announcement this often; of two inside it, only the later is spoken.
    public static readonly TimeSpan AnnounceGap = TimeSpan.FromSeconds(1);
}

public static class ReceiptWords
{
    const int ValueMax = 40;
    // A word break before the 21st character would leave too little of the value: cut at ValueMax instead.
    const int MinCut = 20;

    // The room a reply's receipt keeps from Send to the outcome (ui.md §6.5): the longest outcome's
    // words, which the view lays out unseen under the line, so it wraps them as it would wrap the
    // line (two lines at phone width, one on a wide window). A longer refusal may still grow it.
    // An answer's is its own (QuestionViewModel.Room).
    public static string Room(string? machineName) => Unconfirmed(machineName);

    // A reply not confirmed (ui.md §6.5): the machine by its name, and a reply may be sent again.
    public static string Unconfirmed(string? machineName) =>
        $"Sent, but not confirmed. Check at {(string.IsNullOrEmpty(machineName) ? "the machine" : machineName)} before sending it again.";

    // An answer not confirmed (ui.md §6.5, #617): never "before sending it again", as an answer is
    // final and never resent. Said with the reply's words, it invited a second answer (#625,
    // Engineering 6676371806).
    public const string AnswerUnconfirmed = "Sent, but not confirmed. Check at the machine.";

    static string? StageText(string? stage) => stage switch
    {
        DeliveryProgress.Received => "Sent",
        DeliveryProgress.Typed => "Passed to the agent",
        Deliveries.Queued => "Up next",
        _ => null,
    };

    // An answer or a reply still on its way (Sending or Waiting): no words on the normal path, only
    // what a screen reader hears; words for a stall or a wait.
    public static ReceiptLine InFlight(bool answer, Delivery e, string? machineName, bool stalled)
    {
        var subject = answer ? "Answer" : "Reply";
        var m = string.IsNullOrEmpty(machineName) ? "the machine" : machineName;
        // An answer never carries working (a question holds the session): there is no Up next for one.
        var stage = answer && e.Stage == Deliveries.Queued ? DeliveryProgress.Typed : e.Stage;
        if (e.State == DeliveryState.Waiting)
        {
            // Held on this device, with no connection to the machine: nothing is moving, so the ring is still.
            if (e.Reason == Deliveries.NotConnected) { return new("held", $"Waiting for {m}", $"it goes when {m} is back", Tone.Progress, ReceiptMark.Still, $"{subject} waiting for {m}"); }
            // A resend met the first copy still in flight: the stage it had reached stays.
            if (e.Reason == Deliveries.StillBeingDelivered) { return new($"{stage ?? "sending"}+delivering", StageText(stage) ?? "Sending…", "still being delivered", Tone.Progress, ReceiptMark.Ring, $"{subject} still being delivered"); }
            // Connected, but no answer came back (a lost response, a timeout): it goes again.
            return new("retrying", "Still sending…", null, Tone.Progress, ReceiptMark.Ring, $"{subject} still sending");
        }
        // A stall's words keep the stage's key: the line fades them in, and out again when the next
        // stage or the outcome makes them untrue.
        return stage switch
        {
            DeliveryProgress.Received => stalled
                ? new("received", "Sent", $"still waiting on {m}", Tone.Progress, ReceiptMark.Ring, $"{subject} sent, still waiting on {m}")
                : Quietly("received", $"{subject} sent"),
            DeliveryProgress.Typed => stalled
                ? new("typed", "Passed to the agent", "not picked up yet", Tone.Progress, ReceiptMark.Ring, $"{subject} passed to the agent, not picked up yet")
                : Quietly("typed", $"{subject} passed to the agent"),
            Deliveries.Queued => Quietly("queued", $"{subject} up next, after the agent's current step"),
            _ => stalled
                ? new("sending", "Still sending…", null, Tone.Progress, ReceiptMark.Ring, $"{subject} still sending")
                : Quietly("sending", $"Sending {subject.ToLowerInvariant()}"),
        };
    }

    // The normal path: Send's ring, and nothing on the line but what a screen reader hears.
    static ReceiptLine Quietly(string key, string speech) => new(key, "", null, Tone.Progress, ReceiptMark.Ring, speech);

    // What a confirmed answer says it was, after "You answered · " (the owner's decision on #617):
    // a tap question's option by its label; free text by its first line, cut to about 40
    // characters; nothing for a form of several fields. Its hidden and bidi characters are drawn
    // as escapes (HiddenText, #546), so a value can't reorder or hide the words around it.
    public static string? AnsweredValue(Question? question, AnswerRequest? answer)
    {
        if (answer?.Text is { } text) { return FirstLine(text); }
        if (question is null || answer is null) { return null; }
        if (question.Fields.Count != 1 || answer.Fields is null || !answer.Fields.TryGetValue(question.Fields[0].Name, out var v)) { return null; }
        var field = question.Fields[0];
        if (v.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            var s = v.GetString()!;
            // A choice by its whole label, never cut: options that share their first 40 characters
            // would read the same. Only free text is cut to its first line.
            if (field.Type == FieldType.Choice) { return HiddenText.Reveal(field.Options?.FirstOrDefault(o => o.Value == s)?.Label ?? s); }
            return FirstLine(s);
        }
        if (field.Type == FieldType.Choice && v.ValueKind == System.Text.Json.JsonValueKind.Object && v.TryGetProperty("other", out var other) && other.ValueKind == System.Text.Json.JsonValueKind.String)
        {
            return FirstLine(other.GetString()!);
        }
        return null;
    }

    // ui.md §6.5: the first line, runs of spaces and tabs as one space, cut at the last word break
    // within 40 characters (or at 40, with no break after the 20th), and "…" when there is more.
    // Only spaces, tabs and line breaks: any other blank-looking character (U+00A0 and the like)
    // is left for Reveal to escape. Normalised as white space, "yes\u00A0no" read "yes no" and a
    // leading one vanished (#625, Copilot 4487937096).
    static string? FirstLine(string text)
    {
        var lines = text.Trim(Blank).Split(["\r\n", "\r", "\n", "\u0085", "\u2028", "\u2029"], StringSplitOptions.None);
        var first = string.Join(' ', lines[0].Split(Spaces, StringSplitOptions.RemoveEmptyEntries));
        if (first.Length == 0) { return null; }
        var chars = first.EnumerateRunes().ToList();
        var cut = chars.Count > ValueMax;
        var shown = first;
        if (cut)
        {
            var space = chars.Take(ValueMax + 1).ToList().FindLastIndex(r => r.Value == ' ');
            // The same spaces at the cut: a plain TrimEnd took a U+00A0 there, and the whole of a
            // value of nothing else, before Reveal could escape it (#625, Engineering 6675224949).
            shown = string.Concat(chars.Take(space >= MinCut ? space : ValueMax)).TrimEnd(Spaces);
        }
        return HiddenText.Reveal(cut || lines.Length > 1 ? $"{shown}…" : shown);
    }

    static readonly char[] Spaces = [' ', '\t'];
    static readonly char[] Blank = [' ', '\t', '\r', '\n', '\u0085', '\u2028', '\u2029'];

    // Drawn in a direction of its own (ui.md §6.5): an answer in Hebrew or Arabic can't carry the
    // words or the "·" around it into its own direction. The isolates are layout only: nothing
    // spoken carries them.
    public static string Isolated(string value) => $"\u2068{value}\u2069";
}

// Times one line's receipt. describe(delivery, stalled): its words now. show(line): puts them on
// the line, called on the UI when they change. announce(speech, urgent): what a screen reader
// hears, rate-limited here, and whether it is a failure or a refusal (said assertively).
public sealed class Receipt : IDisposable
{
    // What a request's receipt has been through, kept apart from any one view model: a question's
    // is made afresh when its machine's capabilities change, and the quiet window, the dwell, the
    // stalls and what was announced belong to the request. So does its announcement's timer: one
    // still waiting out the rate limit when its view model goes is said by another receipt showing
    // the request (Voices), and never on a line showing another request (a reply box moved on to
    // the next reply), which would name that line by the last one's outcome. With no receipt of the
    // request left, it goes unsaid: a live region is an element, and none is showing it.
    sealed class History
    {
        public TimeSpan Start;
        public string? Stage;
        public TimeSpan StageAt;
        public TimeSpan? SendingSince;
        // The last wait was a resend meeting the first copy in flight (Tick).
        public bool Delivering;
        public ShownLine? Shown;
        // What has been spoken, and what is to be once the limit allows (the later of two inside it).
        public readonly HashSet<string> Said = [];
        public string? Pending;
        // Whether that is a failure or a refusal: one the person must act on, said assertively
        // (ui.md §9.1 A6; #625, Copilot 4478422096). Progress and success are polite.
        public bool PendingUrgent;
        public TimeSpan SaidAt = TimeSpan.MinValue;
        public ITimer? SayTimer;
        // Which SayTimer a posted Flush came from: disposing a timer doesn't take back a Flush it
        // already posted, which then said the next announcement inside the same second (#625,
        // Copilot 4487937120). Moved on by every flush and every timer set.
        public int SayGeneration;
        public readonly List<Receipt> Voices = [];
        // Last seen still on its way: its quiet window and stalls are still being timed.
        public bool InFlight;
    }

    sealed record ShownLine(string Key, TimeSpan At, ReceiptLine Line, bool InFlight);

    // Histories no receipt shows and no request still needs, kept for a receipt made again (a card
    // refreshed); past Keep the oldest of those goes. One a receipt shows is never let go: dropped,
    // a card refreshed after 50 other requests made it afresh and said "Answer delivered" again, and
    // a request in flight lost its quiet window and stalls (#625, Copilot 4478422027). So the
    // histories are at most twice Keep, plus those receipts show.
    const int Keep = 50;
    static readonly Lock Gate = new();
    static readonly Dictionary<string, History> Histories = [];
    static readonly List<string> Order = [];

    readonly TimeProvider time;
    readonly IUiDispatcher ui;
    readonly Func<Delivery, bool, ReceiptLine> describe;
    readonly Action<ReceiptLine> show;
    readonly Action<string, bool> announce;
    Delivery? entry;
    ReceiptLine? instead;
    ITimer? timer;
    // The history of the request this receipt shows, whose announcements it may say.
    History? voiceOf;
    string? drawn;
    bool disposed;

    public Receipt(TimeProvider time, IUiDispatcher ui, Func<Delivery, bool, ReceiptLine> describe, Action<ReceiptLine> show, Action<string, bool> announce)
    {
        (this.time, this.ui, this.describe, this.show, this.announce) = (time, ui, describe, show, announce);
    }

    // In whole ticks: through a double, a timestamp of today's ticks (about 6e17) loses tens of
    // them, and a timer set from it fires that late.
    TimeSpan Now() => TimeSpan.FromTicks((long)((Int128)time.GetTimestamp() * TimeSpan.TicksPerSecond / time.TimestampFrequency));

    // instead: a line that isn't the delivery's (the machine is not current, files being read),
    // shown as it is until the next Show without one.
    public void Show(Delivery? e, ReceiptLine? instead = null)
    {
        (entry, this.instead) = (e, instead);
        Tick();
    }

    public void Dispose()
    {
        disposed = true;
        timer?.Dispose();
        Voice(null);
    }

    void Voice(History? h)
    {
        if (h == voiceOf) { return; }
        lock (Gate)
        {
            voiceOf?.Voices.Remove(this);
            voiceOf = h;
            h?.Voices.Add(this);
        }
    }

    static History HistoryFor(string request, TimeSpan now)
    {
        lock (Gate)
        {
            if (Histories.TryGetValue(request, out var h)) { return h; }
            h = new History { Start = now, StageAt = now };
            Histories[request] = h;
            Order.Add(request);
            if (Order.Count > Keep)
            {
                // Never the one just made, which its receipt is about to show.
                var spare = Order.FindIndex(0, Order.Count - 1, r => Histories[r] is { Voices.Count: 0, InFlight: false });
                // Past twice Keep, an unshown request in flight goes too: one whose outcome never
                // came would otherwise be kept for the life of the app.
                if (spare < 0 && Order.Count > 2 * Keep) { spare = Order.FindIndex(0, Order.Count - 1, r => Histories[r].Voices.Count == 0); }
                if (spare >= 0)
                {
                    Histories.Remove(Order[spare]);
                    Order.RemoveAt(spare);
                }
            }
            return h;
        }
    }

    static bool Uploading(Delivery e) => e.Progress is { Total: > 0 } p && p.Sent < p.Total;

    // When the delivery's current state stalls, or null for one that doesn't.
    static TimeSpan? StallDue(Delivery e, History h)
    {
        if (e.State != DeliveryState.Sending || Uploading(e)) { return null; }
        // A send that went again (a lost response, a reconnect) has been going a while: Still sending….
        if (e.Stage is null) { return e.Attempts > 1 ? h.StageAt : h.SendingSince + ReceiptTiming.StillSending; }
        return e.Stage switch
        {
            DeliveryProgress.Received => h.StageAt + ReceiptTiming.SentStall,
            DeliveryProgress.Typed => h.StageAt + ReceiptTiming.PassedStall,
            _ => null,
        };
    }

    void Tick()
    {
        if (disposed) { return; }
        timer?.Dispose();
        timer = null;
        if (instead is not null)
        {
            // An override that speaks (an answer to a question since reworded) is said as the
            // request's own words are; one that doesn't (reading a file, offline) stays silent.
            if (entry is { } held && instead.Speech is not null)
            {
                var h0 = HistoryFor(held.Request, Now());
                Voice(h0);
                Put(instead);
                Speak(h0, instead);
            }
            else { Voice(null); Put(instead); }
            return;
        }
        if (entry is not { } e) { Voice(null); Put(ReceiptLine.None); return; }
        var t = Now();
        var h = HistoryFor(e.Request, t);
        Voice(h);
        var inFlight = e.State is DeliveryState.Sending or DeliveryState.Waiting;
        h.InFlight = inFlight;
        if (!inFlight)
        {
            // An outcome is never held, and replaces whatever shows.
            Shown(h, describe(e, false), t, inFlight: false);
            return;
        }
        if (e.Stage != h.Stage) { (h.Stage, h.StageAt, h.Delivering) = (e.Stage, t, false); }
        // A resend that met the first copy in flight goes again every couple of seconds, and is
        // busy again until the first copy settles: between, it still is being delivered, not newly
        // sent, so the line doesn't flicker between the two and say both each time.
        if (e.State == DeliveryState.Waiting) { h.Delivering = e.Reason == Deliveries.StillBeingDelivered; }
        if (h.Delivering && e.State == DeliveryState.Sending) { e = e with { State = DeliveryState.Waiting, Reason = Deliveries.StillBeingDelivered }; }
        if (Uploading(e)) { h.SendingSince = null; }
        else if (e.State == DeliveryState.Sending && e.Stage is null) { h.SendingSince ??= t; }
        var due = StallDue(e, h);
        var line = describe(e, due is { } d0 && t >= d0);
        TimeSpan? wake = due is { } d && t < d ? d : null;
        if (t < h.Start + ReceiptTiming.Quiet)
        {
            Put(ReceiptLine.Quiet);
            wake = h.Start + ReceiptTiming.Quiet;
        }
        else if (h.Shown is { InFlight: true } s && s.Key != line.Key && t < s.At + ReceiptTiming.Dwell)
        {
            // The state showing hasn't been up long enough to read: it stays, then the furthest
            // reached. What is said doesn't wait for it: the latest stage wins.
            Put(s.Line);
            Speak(h, line);
            wake = wake is { } w && w < s.At + ReceiptTiming.Dwell ? w : s.At + ReceiptTiming.Dwell;
        }
        else
        {
            Shown(h, line, t, inFlight: true);
        }
        if (wake is { } at)
        {
            var delay = at - t;
            timer = time.CreateTimer(_ => ui.Post(Tick), null, delay > TimeSpan.Zero ? delay : TimeSpan.Zero, Timeout.InfiniteTimeSpan);
        }
    }

    void Shown(History h, ReceiptLine line, TimeSpan t, bool inFlight)
    {
        h.Shown = h.Shown is { } s && s.Key == line.Key ? s with { Line = line } : new ShownLine(line.Key, t, line, inFlight);
        Put(line);
        Speak(h, line);
    }

    // Each change is said once for the request, however often its view model is made afresh, and
    // only the latest: a state since replaced, or already said, is never said late.
    void Speak(History h, ReceiptLine line)
    {
        h.Pending = line.Speech is { } speech && !h.Said.Contains(speech) ? speech : null;
        h.PendingUrgent = line.Tone == Tone.Bad;
        Announce(h);
    }

    void Put(ReceiptLine line)
    {
        // The key too: on the normal path the words stay empty while the state moves on.
        var signature = $"{line.Key}|{line.Tone}|{line.Mark}|{line.Whole}";
        if (signature != drawn)
        {
            drawn = signature;
            show(line);
        }
    }

    void Announce(History h)
    {
        if (h.Pending is null) { return; }
        var wait = h.SaidAt == TimeSpan.MinValue ? TimeSpan.Zero : h.SaidAt + ReceiptTiming.AnnounceGap - Now();
        if (wait <= TimeSpan.Zero) { Flush(h); }
        else if (h.SayTimer is null)
        {
            var mine = ++h.SayGeneration;
            h.SayTimer = time.CreateTimer(_ => ui.Post(() => { if (h.SayGeneration == mine) { Flush(h); } }), null, wait, Timeout.InfiniteTimeSpan);
        }
    }

    // The request's timer, not this receipt's: said by whichever receipt shows the request then.
    void Flush(History h)
    {
        h.SayTimer?.Dispose();
        h.SayTimer = null;
        h.SayGeneration++;
        if (h.Pending is not { } text) { return; }
        h.Pending = null;
        h.Said.Add(text);
        h.SaidAt = Now();
        if (h.Voices.Count > 0) { h.Voices[^1].announce(text, h.PendingUrgent); }
    }
}

// Whether a receipt line's announcement, queued to be raised on a later dispatcher turn
// (ReceiptMarkView), is still the line's when that turn comes. A line is reused: a session switch
// rebinds it, hushed, to the next card's last announcement, and a callback that held only the line
// then raised LiveRegionChanged for that card's old news, as if it had just happened (#625,
// Copilot 4485379160). Each change of the line's announcement and each unload starts a new
// generation, and a queued announcement is raised only in its own.
public sealed class ReceiptAnnouncer
{
    int generation;

    // The line's announcement changed: the ticket of the one to raise, or null when nothing is to be
    // said (no words, or what was said before: the line not drawn yet, or rebound under a hush).
    public int? Changed(string said, bool loaded, bool hushed)
    {
        generation++;
        return said.Length > 0 && loaded && !hushed ? generation : null;
    }

    public void Unloaded() => generation++;

    public bool Raise(int ticket, bool loaded) => loaded && ticket == generation;
}
