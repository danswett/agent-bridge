using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// A pending question on a card: answerable when the host takes answers and the question came
// whole, otherwise shown read-only with why. The form follows pwa/js/answers.js and
// question-view.js: recommended options first, a single-choice question answers on a click,
// Other opens a text box, Ctrl+Enter sends, and what is sent is checked with the host's own
// Answers.Check first, so the host never sees an answer it would refuse for its shape.
public sealed partial class QuestionViewModel : Observable
{
    static readonly Dictionary<string, Func<string, string>> OutcomeText = new()
    {
        // Said as "You answered · value" with the check badge (Describe).
        ["delivered"] = _ => "You answered",
        // Final: never "before sending it again" (ui.md §6.5, #967).
        ["unconfirmed"] = _ => ReceiptWords.AnswerUnconfirmed,
        ["stale"] = _ => "Already answered or gone: the session is no longer asking this.",
        ["busy"] = _ => "Another answer to this session is being delivered. Try again in a moment.",
        ["refused"] = _ => "The machine refused this answer.",
        // Held by Deliveries (Machine.Current, #269): nothing went.
        ["offline"] = at => $"Not sent: the machine{at} is not connected now, or has not yet said whether it still asks this. Open the session once it is back.",
    };

    readonly Deliveries deliveries;
    readonly string sessionId;
    readonly string at;
    readonly string? machine;
    readonly Func<bool> current;
    string freeText = "";
    bool otherOpen;
    string? error;
    string statusText = "";
    Tone statusTone;
    ReceiptMark statusMark;
    bool pausedAfterBusy;
    // Times the status line's words, as the receipt of the answer (Receipt, #600, #617).
    readonly Receipt receipt;

    // current: whether the machine's rows are its host's word now (Machine.Current, #269). While
    // not, the question is shown but can't be answered: it may have ended meanwhile.
    public QuestionViewModel(Question question, string sessionId, AgentKind agent, IReadOnlyList<string> capabilities, Deliveries deliveries, string? machineName = null, Func<bool>? current = null)
    {
        Question = question;
        this.sessionId = sessionId;
        this.deliveries = deliveries;
        this.current = current ?? (() => true);
        at = string.IsNullOrEmpty(machineName) ? "" : $" on {machineName}";
        machine = machineName;
        this.machineName = machineName;
        this.capabilities = capabilities;
        (Answerable, Why) = Answerability(question, capabilities, agent);
        Fields = [.. question.Fields.Select(f => new FieldViewModel(f, question.Kind == QuestionKind.Approval))];
        Tap = question.Fields.Count == 1 && question.Fields[0].Type == FieldType.Choice;
        otherOpen = Tap && Fields[0].OtherChosen;
        StatusRoom = Room(machineName);
        receipt = new Receipt(deliveries.Time, deliveries.Ui, (e, stalled) => Describe(e, machineName, stalled), Shown, Said);
        deliveries.Changed += OnDelivery;
        Refresh();
    }

    void Shown(ReceiptLine line)
    {
        StatusMark = line.Mark;
        (StatusText, StatusTone) = (line.Whole, line.Tone);
        Unsay();
    }

    // The words changed: the line is named by them again until their own announcement, so a reader
    // moving to it never hears what an earlier state said.
    void Unsay()
    {
        if (Announcement.Length == 0) { return; }
        Announcement = "";
        Raise(nameof(Announcement));
    }

    // Cleared first, so the same words said again (the next reply delivered) are a change too.
    void Said(string speech, bool urgent)
    {
        AnnouncementUrgent = urgent;
        Raise(nameof(AnnouncementUrgent));
        Announcement = "";
        Raise(nameof(Announcement));
        Announcement = speech;
        Raise(nameof(Announcement));
    }

    void OnDelivery(Delivery d) { if (d.Key == Key) { Refresh(); } }

    // The answer this view model saw on its way, whose delivery its button acknowledges with the
    // check for about a second (ui.md §6.5); not one already delivered when it was made.
    string? watched;
    bool sendCheck;
    ITimer? checkTimer;

    // The button that sent the answer shows its check, not its ring or label's rest, for
    // ReceiptTiming.SendCheckHold.
    public bool SendCheck => sendCheck;

    void SetCheck(bool on)
    {
        if (sendCheck == on) { return; }
        sendCheck = on;
        Raise(nameof(SendCheck));
    }

    // The card let go of this question: it no longer follows the delivery, and answers nothing.
    // Whatever still holds it (a view mid-click) must not answer a question that went (#269).
    public void Detach()
    {
        deliveries.Changed -= OnDelivery;
        receipt.Dispose();
        checkTimer?.Dispose();
        detached = true;
        RaiseControls();
    }

    bool detached;
    readonly string? machineName;
    readonly IReadOnlyList<string> capabilities;

    // ---- an approval the host escaped anything in (#803, ui.md §6.4, "Escaped approvals") ----

    // Signs its confirmation for this machine, (session, question, shown, request), or returns null
    // when it cannot now; null on a machine reached through this machine's pipe, which never takes
    // one. The card's session title, for the sheet's second line.
    // (session, question, shown, request, pressed): signed as of when the sheet's button was pressed.
    public Func<string, string, string, string, DateTimeOffset, SignedCommand?>? SignEscaped { get; init; }
    public string? SessionTitle { get; init; }
    public string? MachineName => machineName;

    // How Approve is given for it from here: null for a question with nothing escaped, "confirm"
    // (the sheet), "device" (this app is not a device the machine approved), "many" (too many
    // escapes for the question to mark, so Approve is the machine's) or "machine" (the
    // machine takes it only there).
    public string? EscapedMode => !Approval || Question.Escaped is not { } e ? null
        // Marks that do not each cover exactly one escape are not the host's: the sheet could not
        // draw what it approves exactly, so Approve is the machine's (#836 review).
        : e.Approve == EscapedApproval.TooMany ? "many"
        : e.Approve != EscapedApproval.Confirm || !EscapedApprovalView.MarksExact(Question.Message, e.Marks) ? "machine"
        : capabilities.Contains(EscapedApproval.Capability) && SignEscaped is not null ? "confirm"
        : "device";

    // Where Approve once was, why it is not offered here.
    public string? EscapedWhy => EscapedMode switch
    {
        "machine" => $"{machineName ?? "The machine"} takes this approval only at the machine.",
        "many" => $"Too many hidden characters to show here. Approve this at {machineName ?? "the machine"}.",
        "device" => $"Approve this at {machineName ?? "the machine"}, or from a device approved there.",
        _ => null,
    };

    public const string EscapedHelp = "At the machine, open this app's Settings, turn on answering folder-trust questions from devices, and approve the device: a device approved for those can approve these too while that is on, unless the machine lists its own (permissions.escapedApprovalDevices in its config.json).";

    // Asked by the card when "Approve once…" is pressed: the view opens the sheet.
    public event Action? EscapedConfirmRequested;

    public void RequestEscapedConfirm()
    {
        if (EscapedMode == "confirm" && Answerable && CanAnswer) { EscapedConfirmRequested?.Invoke(); }
    }

    // The sheet's own button: Approve once, with a confirmation signed now for the answer's request,
    // the question, and the digest of the message as this app has it. Null once sent; else why not.
    public string? ConfirmEscaped()
    {
        if (EscapedMode != "confirm" || !Answerable || !CanAnswer) { return "This can't be approved from here now."; }
        if (Question.Escaped is not { } e || EscapedApproval.Shown(Question.Message) != e.Shown || !EscapedApprovalView.MarksExact(Question.Message, e.Marks))
        {
            return "This approval didn't arrive as the machine sent it. Close this and read it again.";
        }
        var field = Fields[0];
        // The confirmation is the person's at this moment: it is signed as of now, whenever the key
        // to sign it with is there, and lasts EscapedApproval.MaxLifetime from now (#836).
        var pressed = deliveries.Time.GetUtcNow();
        var answer = new AnswerRequest
        {
            Id = sessionId, Question = Question.Id, Request = "00000000-0000-4000-8000-000000000000",
            Fields = new Dictionary<string, JsonElement> { [field.Name] = JsonSerializer.SerializeToElement("approve") },
        };
        var data = EscapedData(answer, e.Shown, pressed);
        // Held (the machine gone since CanAnswer was read): nothing is sent or kept, so the sheet must
        // not close as if it went. It stays open saying so, its button the Try again (#836 review
        // 5780082443).
        // Waited for, while it never went, only for its lifetime from the press: then it fails as
        // Expired and the press is asked for again, rather than reading unconfirmed once the give-up
        // has passed (#850).
        // The words chosen, as for any answer (Delivery.Chosen, #600): its receipt says "You answered · Approve once".
        if (deliveries.Submit(Key, Ops.SessionAnswer, data, outcome => outcome is not ("busy" or "refused"), payload: answer,
                chosen: ReceiptWords.AnsweredValue(Question, answer), lapse: (pressed + EscapedApproval.MaxLifetime, Expired)) is { Outcome: Deliveries.Held } held)
        {
            return $"Not sent: {held.Reason}. Press again once it's back.";
        }
        escapedAnswer = data;
        field.SelectOnly("approve");
        field.OtherChosen = false;
        return null;
    }

    Func<string, object>? escapedAnswer;

    // Why a confirmation never signed in its lifetime failed: nothing went, so Approve once… is
    // offered again rather than a Try again that could only fail the same way.
    internal const string Expired = "the confirmation expired before the machine was back, so nothing was sent; press Approve once… again";
    static bool ExpiredUnsent(Delivery e) => e.State == DeliveryState.Failed && e.Reason == Expired;

    // The answer for a request, its confirmation signed once for that request: a resend of the same
    // request carries the same confirmation. Signed as each attempt is made, which can be while the
    // machine's link is down (Deliveries evaluates the data before the call checks the connection),
    // when there is no key to sign for: no confirmation then is never kept or sent. The attempt goes
    // as not connected, so it waits for the link and signs once it is back. Kept, the null was sent
    // on every resend of that request, and the host refused the Approve (#836 review 5780340459).
    // Signed as of the press, never of the link's return: once MaxLifetime has passed since the
    // press with nothing signed, nothing has gone, and nothing will. It fails as Expired, and the
    // card asks for the press again (Trust's review of 07388e7e). Once signed, it may have gone, so
    // a resend still goes: the host gives the first copy's outcome, or refuses it as expired.
    Func<string, object> EscapedData(AnswerRequest answer, string shown, DateTimeOffset pressed)
    {
        string? signedFor = null;
        SignedCommand? signed = null;
        return request =>
        {
            if (signedFor != request || signed is null)
            {
                if (deliveries.Time.GetUtcNow() - pressed >= EscapedApproval.MaxLifetime) { throw new HostErrorException(Expired); }
                signed = SignEscaped?.Invoke(sessionId, Question.Id, shown, request, pressed);
                signedFor = signed is null ? null : request;
            }
            return signed is null
                ? throw new HostNotRunningException("not connected to the machine, so its confirmation could not be signed yet")
                : answer with { Request = request, Confirm = signed };
        };
    }

    public Question Question { get; private set; }
    public string Id => Question.Id;
    public bool Approval => Question.Kind == QuestionKind.Approval;
    public string Heading => Approval ? "Permission requested" : "Waiting on you";
    // Copilot asked several permissions at once and shows them one at a time, this one first
    // (#690): each is its own question, so an answer to this one never reaches the next.
    public string? Queue => Approval && Question.Waiting is > 1 and var n ? $"1 of {n} permissions waiting" : null;

    internal void UpdateWaiting(int? waiting)
    {
        if (Question.Waiting == waiting) { return; }
        Question = Question with { Waiting = waiting };
        Raise(nameof(Queue));
    }

    // An approval's text with anything not drawn where it runs, and its right-to-left letters, as
    // visible escapes (#220): a host escapes them, and an older one's prompt is drawn so too. The card
    // draws it exactly as written, as one block nothing in it is read as Markdown in (#323,
    // MarkdownModel.Approval), so it is never wrapped in a fence of its own: one would be drawn too.
    public string Message => !Approval ? Question.Message : Shown(Question);
    // What the approval's escapes stand for, in words above it (ui.md §6.4): "Contains hidden
    // characters", "Contains right-to-left text".
    public IReadOnlyList<string> Notes => Approval ? ShownText.Notes(Question.Message) : [];
    public bool Answerable { get; }
    public string? Why { get; }
    public bool Tap { get; }
    public IReadOnlyList<FieldViewModel> Fields { get; }
    public string SendLabel => Approval ? "Send" : "Send answer";
    public string Key => $"answer|{sessionId}|{Question.Id}";

    public string ReadOnlyNote => Why switch
    {
        "truncated" => $"Part of this question didn't fit in the message from the machine, so it can't be answered from here. Answer at the machine{at}.",
        "approvalOff" => $"Approving permission prompts from the app and other devices is turned off on this machine (Settings). Answer at the machine{at}.",
        "atMachine" when AtMachineExplanation is not null => string.IsNullOrEmpty(machine) ? "Answer at the machine" : $"Answer at the machine · {machine}",
        "atMachine" => $"This prompt can only be answered at the machine{at}.",
        _ => $"This machine isn't taking answers from the app right now. Answer at the machine{at}.",
    };

    public string? AtMachineExplanation => Why == "atMachine" && Question.AnswerAtMachineReason == "shape"
        ? "This kind of question can only be answered at the machine." : null;

    // A prompt answered at the machine shows what it asks, not choices nobody can make here.
    public bool ShowReadOnlyFields => !Answerable && Fields.Count > 0 && Why is not ("atMachine" or "approvalOff");

    public string FreeText { get => freeText; set { if (Set(ref freeText, value)) { Error = null; } } }

    public bool OtherOpen { get => otherOpen; private set { if (Set(ref otherOpen, value)) { Raise(nameof(ShowSend)); } } }

    public string? Error { get => error; private set { if (Set(ref error, value)) { Raise(nameof(HasError)); if (value is null) { foreach (var f in Fields) { f.Invalid = false; } } } } }
    public bool HasError => error is not null;

    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public Tone StatusTone { get => statusTone; private set => Set(ref statusTone, value); }
    // The ring, a still ring or the check badge beside the words (ui.md §6.5).
    public ReceiptMark StatusMark { get => statusMark; private set => Set(ref statusMark, value); }
    // What a screen reader is to hear of the status line now: raised once for each, at most one a
    // second (Receipt); the view raises LiveRegionChanged with it.
    public string Announcement { get; private set; } = "";
    // Whether that is a failure or a refusal, which a screen reader hears at once (assertively);
    // set before Announcement is raised, so the view reads it with the words.
    public bool AnnouncementUrgent { get; private set; }
    // The longest outcome's words, which the view keeps room for (Room).
    public string StatusRoom { get; }

    // Whether the person may answer (again): nothing sent yet, or what was sent was not typed.
    public bool CanAnswer
    {
        get
        {
            var e = deliveries.Get(Key);
            return !pausedAfterBusy && !detached && current() && Unsent(e);
        }
    }

    // Nothing of the person's is on its way, or what went was not taken, or what failed to go was an
    // answer to what the question said before (Reworded).
    bool Unsent(Delivery? e) => e is null || (e.State == DeliveryState.Done && e.Outcome is "busy" or "refused") || (e.State == DeliveryState.Failed && e.Request == earlier) || ExpiredUnsent(e);

    // The request of an answer that failed to go, or was still going, while the question said
    // something else.
    string? earlier;

    // The same question, come back saying something else or answerable otherwise (#220): an answer
    // that failed to go was chosen for what it said then, and an option's value can mean something
    // else now. It is not sent again; what became of it is still said, and a fresh answer is asked for.
    internal void Reworded()
    {
        if (deliveries.Get(Key) is { State: not DeliveryState.Done } e) { earlier = e.Request; }
        Refresh();
    }

    // A tap question has no Send until Other is opened.
    public bool ShowSend => !Tap || OtherOpen;
    public bool ShowResend => !detached && current() && deliveries.Get(Key) is { State: DeliveryState.Failed } e && e.Request != earlier && !ExpiredUnsent(e);
    // The answer is on its way (ui.md §5.3, Send and Answer): the button that sent it shows a
    // progress ring, which stops with the request.
    public bool InFlight => deliveries.Get(Key)?.State is DeliveryState.Sending or DeliveryState.Waiting;

    // The option a tap question sent, while that answer stands.
    public string? SentValue
    {
        get
        {
            var e = deliveries.Get(Key);
            if (!Tap || e is null || CanAnswer || e.Payload is not AnswerRequest r || r.Fields is null) { return null; }
            return r.Fields.TryGetValue(Fields[0].Name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        }
    }

    public void ToggleOther()
    {
        if (!Tap || !Fields[0].AllowOther) { return; }
        OtherOpen = !OtherOpen;
        Fields[0].OtherChosen = OtherOpen;
        Error = null;
    }

    // The same question, rebuilt because the host's capabilities changed: what was chosen and
    // typed carries over rather than vanishing from under the person.
    internal void TakeDraft(QuestionViewModel old)
    {
        if (old.Question.Id != Question.Id) { return; }
        FreeText = old.FreeText;
        foreach (var f in Fields)
        {
            if (old.Fields.FirstOrDefault(o => o.Name == f.Name) is { } was) { f.TakeDraft(was); }
        }
        OtherOpen = Tap && Fields[0].OtherChosen;
        // An answer that failed to go for what the question said before stays one not to send again
        // (Reworded), whatever else the question is rebuilt for.
        earlier = old.earlier;
        // An escaped approval's confirmation, signed for the request it went as: Try again resends
        // it with its confirmation, not without, which the host would refuse (#836 review). Only
        // the same question saying the same (SaysTheSame, Escaped included) is rebuilt with it.
        escapedAnswer = old.escapedAnswer;
        Refresh();
    }

    // A click on one of a tap question's options is the whole answer.
    public void Choose(OptionViewModel option)
    {
        if (!Tap) { return; }
        // Approve once never goes on a tap for one the host escaped anything in: only the sheet sends it.
        if (EscapedMode is not null && option.Value == "approve") { RequestEscapedConfirm(); return; }
        var f = Fields[0];
        f.SelectOnly(option.Value);
        f.OtherChosen = false;
        OtherOpen = false;
        Send();
    }

    public void Send()
    {
        if (!Answerable || !CanAnswer) { return; }
        var (answer, problem, field) = Build();
        if (problem is null)
        {
            problem = Answers.Check(Question, answer);
            field = problem is null ? null : ProblemField(problem);
        }
        if (problem is not null) { Fail(problem, field); return; }
        if (EscapedMode is not null && answer.Fields?.Values.Any(v => v.ValueKind == JsonValueKind.String && v.GetString() == "approve") == true)
        {
            RequestEscapedConfirm();
            return;
        }
        Error = null;
        escapedAnswer = null;
        // The words chosen, as this question shows them now (Delivery.Chosen).
        var entry = deliveries.Submit(Key, Ops.SessionAnswer, request => answer with { Request = request }, outcome => outcome is not ("busy" or "refused"), payload: answer, chosen: ReceiptWords.AnsweredValue(Question, answer));
        // Watched from here, not only from a change seen on its way: a fast delivery can be done
        // before the first change is posted, and its button still owes it the check.
        if (entry.State is DeliveryState.Sending or DeliveryState.Waiting) { watched = entry.Request; }
    }

    public void Resend()
    {
        if (!ShowResend) { return; }
        deliveries.Resend(Key, Ops.SessionAnswer, escapedAnswer ?? (request => ((AnswerRequest)deliveries.Get(Key)!.Payload!) with { Request = request }));
    }

    void Fail(string message, string? field)
    {
        Error = char.ToUpperInvariant(message[0]) + message[1..] + ".";
        if (field is not null && Fields.FirstOrDefault(f => f.Name == field) is { } f) { f.Invalid = true; }
    }

    // What the form says, as session.answer's request, or why it cannot be one.
    internal (AnswerRequest Answer, string? Problem, string? Field) Build()
    {
        // A stand-in request, the same length as the UUID the delivery gives it, so the size
        // check counts the same bytes.
        var request = new AnswerRequest { Id = sessionId, Question = Question.Id, Request = "00000000-0000-4000-8000-000000000000" };
        if (Fields.Count == 0) { return (request with { Text = WellFormed(FreeText) }, null, null); }
        var fields = new Dictionary<string, JsonElement>();
        foreach (var f in Fields)
        {
            var (value, problem) = f.Value();
            if (problem is not null) { return (request, problem, f.Name); }
            if (value is { } v) { fields[f.Name] = v; }
        }
        return (request with { Fields = fields }, null, null);
    }

    string? ProblemField(string message)
    {
        var m = QuotedLabel().Match(message);
        return m.Success ? Fields.FirstOrDefault(f => f.Label == m.Groups[1].Value)?.Name : null;
    }

    [GeneratedRegex("^\"([^\"]*)\"")]
    private static partial Regex QuotedLabel();

    // Text the person typed that has not gone to the machine, which the card keeps if the
    // question changes under them.
    public string UnsentText()
    {
        var e = deliveries.Get(Key);
        var unsent = e is null || e.State == DeliveryState.Failed || (e.State == DeliveryState.Done && e.Outcome is "busy" or "refused");
        if (!unsent) { return ""; }
        if (Fields.Count == 0) { return string.IsNullOrWhiteSpace(FreeText) ? "" : FreeText; }
        var bits = Fields.Select(f => (f, typed: f.TypedText())).Where(x => x.typed.Length > 0)
            .Select(x => Fields.Count > 1 ? $"{x.f.Label}: {x.typed}" : x.typed);
        return string.Join("\n", bits);
    }

    // The delivery changed (or the card re-rendered): status, controls and the busy pause.
    public void Refresh()
    {
        var e = deliveries.Get(Key);
        if (e?.State is DeliveryState.Sending or DeliveryState.Waiting) { watched = e.Request; }
        else if (e is { State: DeliveryState.Done, Outcome: "delivered" } && e.Request == watched)
        {
            watched = null;
            checkTimer?.Dispose();
            SetCheck(true);
            checkTimer = deliveries.Time.CreateTimer(_ => deliveries.Ui.Post(() => SetCheck(false)), null, ReceiptTiming.SendCheckHold, Timeout.InfiniteTimeSpan);
        }
        receipt.Show(e, !current() && (Unsent(e) || e!.State == DeliveryState.Failed)
            ? new ReceiptLine("offline", $"Waiting for the {Product.HostName}{at}. Answer once it is back.", null, Tone.Progress, ReceiptMark.None, null)
            // An answer to what the question said before (Reworded) is not sent again: a fresh one is asked for.
            : e is { State: DeliveryState.Failed } && e.Request == earlier
            ? Changed(Describe(e, machine))
            : null);
        if (e is { State: DeliveryState.Done, Outcome: "busy" } && !pausedAfterBusy)
        {
            pausedAfterBusy = true;
            _ = Task.Delay(2000).ContinueWith(_ => deliveries.Ui.Post(() => { pausedAfterBusy = false; RaiseControls(); }), TaskScheduler.Default);
        }
        RaiseControls();
    }

    void RaiseControls()
    {
        Raise(nameof(CanAnswer));
        Raise(nameof(ShowResend));
        Raise(nameof(SentValue));
        Raise(nameof(InFlight));
    }

    // What an answer's progress or outcome says, on the card, in the Needs you lane and in the
    // card's receipt once the question has gone: the receipt's words (Receipt; ui.md §6.4, decided
    // on #617, "quiet unless something's wrong"). On its way: nothing on the line but a stall or a
    // wait (the button's ring says it goes), never Up next. Confirmed: "You answered · value" in
    // text2, the value as it was chosen (Delivery.Chosen), never from the question as it is now.
    // Any other outcome keeps its words. stalled: whether the state it is in has stalled (Receipt).
    public static ReceiptLine Describe(Delivery e, string? machineName, bool stalled = false)
    {
        var at = string.IsNullOrEmpty(machineName) ? "" : $" on {machineName}";
        return e.State switch
        {
            DeliveryState.Sending or DeliveryState.Waiting => ReceiptWords.InFlight(answer: true, e, machineName, stalled),
            DeliveryState.Failed => Said("failed", $"Couldn't send it: {e.Reason ?? "the machine said no"}.", Tone.Bad),
            _ => Outcome(e, machineName, at),
        };
    }

    // The room an answer's receipt keeps (ui.md §6.5; ReceiptWords.Room): the longest of the
    // outcomes it usually ends in. Its unconfirmed sentence is shorter than a reply's, which it
    // used to keep room for; busy is now the longest. A longer refusal may still grow it.
    public static string Room(string? machineName)
    {
        var at = string.IsNullOrEmpty(machineName) ? "" : $" on {machineName}";
        return new[] { "unconfirmed", "stale", "busy", "refused" }.Select(o => OutcomeText[o](at)).MaxBy(t => t.Length)!;
    }

    static ReceiptLine Changed(ReceiptLine failed) =>
        new("changed", $"{failed.Text} The question changed since, so answer it again.", null, Tone.Bad, ReceiptMark.None, $"Answer: {failed.Text} The question changed since, so answer it again.");

    static ReceiptLine Said(string key, string text, Tone tone) => new(key, text, null, tone, ReceiptMark.None, $"Answer: {text}");

    static ReceiptLine Outcome(Delivery e, string? machineName, string at)
    {
        var outcome = e.Outcome is { } o && OutcomeText.ContainsKey(o) ? o : "unconfirmed";
        if (outcome == "delivered")
        {
            var value = e.Chosen;
            // In text2, with no badge: the answer's button says it was delivered, with its check.
            return new("delivered", "You answered", value is null ? null : ReceiptWords.Isolated(value), Tone.Progress, ReceiptMark.Check, value is null ? "Answer delivered. You answered" : $"Answer delivered. You answered {value}");
        }
        var text = OutcomeText[outcome](at);
        if (outcome == "refused" && e.Reason is { } r) { text = $"The machine refused this answer: {r}."; }
        // A reason with fixed words of its own, the same in every client (#908).
        if (outcome == "refused" && e.Result is { ValueKind: JsonValueKind.Object } result && result.TryGetProperty("code", out var c)
            && c.ValueKind == JsonValueKind.String && c.GetString() is { } code && AnswerReasons.Known(code))
        {
            text = AnswerReasons.NotSent + AnswerReasons.Text(code);
        }
        if (outcome == "unconfirmed" && e.Reason is { } u) { text += $" ({u})"; }
        return Said(outcome, text, outcome == "refused" ? Tone.Bad : Tone.Warn);
    }

    // Whether a question still says what it said, as controls were built for: its words, kind, flags
    // and fields, each with everything that shapes what can be given (they arrive as new lists on
    // every update, so compared by what they hold). The same id can come back otherwise: a host
    // updated to refuse what it used to offer (#220), or a budget that had to cut it.
    public static bool SaysTheSame(Question a, Question b) =>
        a.Kind == b.Kind && a.Message == b.Message && a.Truncated == b.Truncated && a.AnswerAtMachine == b.AnswerAtMachine && a.Agent == b.Agent
        && a.AnswerAtMachineReason == b.AnswerAtMachineReason
        && SameEscaped(a.Escaped, b.Escaped)
        && a.Fields.Count == b.Fields.Count && a.Fields.Zip(b.Fields).All(p => SameField(p.First, p.Second));

    // The escaped text, policy, digest and every mark: escaped added after a host update, or its
    // policy changed, rebuilds the controls, which Approve once would otherwise keep (#836 review).
    static bool SameEscaped(EscapedText? a, EscapedText? b) =>
        a is null ? b is null
        : b is not null && a.Approve == b.Approve && a.Shown == b.Shown
            && a.Marks.Count == b.Marks.Count && a.Marks.Zip(b.Marks).All(p => p.First.SequenceEqual(p.Second));

    static bool SameField(Field a, Field b) =>
        a.Name == b.Name && a.Type == b.Type && a.Title == b.Title && a.Description == b.Description
        && a.AllowOther == b.AllowOther && a.OtherUnavailable == b.OtherUnavailable && a.Required == b.Required && a.Default?.GetRawText() == b.Default?.GetRawText()
        && (a.Options ?? []).SequenceEqual(b.Options ?? []);

    // Whether this client may answer, and if not, why (pwa/js/answers.js, answerability).
    public static (bool Ok, string? Why) Answerability(Question q, IReadOnlyList<string> capabilities, AgentKind agent)
    {
        if (q.AnswerAtMachine == true)
        {
            var off = agent == AgentKind.Copilot && q.Kind == QuestionKind.Approval
                && capabilities.Contains(Capabilities.SessionsAnswer) && !capabilities.Contains(Capabilities.SessionsApprove);
            return (false, off ? "approvalOff" : "atMachine");
        }
        if (!capabilities.Contains(Capabilities.SessionsAnswer)) { return (false, "machine"); }
        // Before the shape: a cut approval usually ends inside its command's fence, and the person is
        // told that part of it is missing, not only that it is answered at the machine.
        if (q.Truncated == true) { return (false, "truncated"); }
        // Approve once allows what runs, not what is drawn. An approval's hidden characters and
        // right-to-left letters are drawn here as escapes, with notes saying so, so it is still
        // offered (#253, decision 3); one Markdown would not draw as written is answered at the
        // machine (DrawnAsWritten).
        if (q.Kind == QuestionKind.Approval && !DrawnAsWritten(q)) { return (false, "atMachine"); }
        if (NotWhole(q, agent)) { return (false, "atMachine"); }
        return (true, null);
    }

    // A CRLF is the line ending it is, as MarkdownModel.Approval and the PWA take it (#423); its CR is
    // still in what runs, so the notes, read from the message as it came, say so.
    static string Shown(Question q) => ShownText.Reveal(q.Message.Replace("\r\n", "\n"), rightToLeft: true);

    // Whether Markdown would draw an approval as written: a character reference (&#x202E;) would be
    // decoded to the character it stands for, and the agent's own words, before a host escapes them
    // (#212), can close the command's fence early or show a link's words where `curl ... | sh` runs
    // (ApprovalShape). This card draws an approval exactly as written since #323; the hold is kept
    // all the same, the rule Home Assistant's card, which draws Markdown, holds to as well.
    static bool DrawnAsWritten(Question q) => !ShownText.HasMisleadingReference(q.Message) && ApprovalShape.DrawsAsWritten(Shown(q));

    // A Codex approval is answered here only as a host sends it from #209: the whole command in one
    // block nothing in it can close (ApprovalShape.IsFencedCommand). An older host sent
    // "Needs approval: " and the hook's 300-character preview, cut to 297 and "..." with nothing
    // marking it truncated, so Approve once would run more than the card shows (#209 security review).
    static bool NotWhole(Question q, AgentKind agent) =>
        agent == AgentKind.Codex && q.Kind == QuestionKind.Approval && !ApprovalShape.IsFencedCommand(q.Message);

    // A lone surrogate cannot be read by the host (System.Text.Json refuses it), so text goes
    // well-formed, with U+FFFD in its place.
    public static string WellFormed(string s)
    {
        StringBuilder? b = null;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var lone = char.IsHighSurrogate(c) ? !(i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) : char.IsLowSurrogate(c) && !(i > 0 && char.IsHighSurrogate(s[i - 1]));
            if (lone) { (b ??= new StringBuilder(s, 0, i, s.Length)).Append('\uFFFD'); }
            else { b?.Append(c); }
        }
        return b?.ToString() ?? s;
    }
}

public sealed partial class FieldViewModel : Observable
{
    static readonly Dictionary<FieldType, string> TypeLabels = new()
    {
        [FieldType.Choice] = "Choose one",
        [FieldType.Multi] = "Choose any",
        [FieldType.Boolean] = "Yes or no",
        [FieldType.Text] = "Text answer",
        [FieldType.Number] = "Number",
    };

    readonly Field spec;
    bool otherChosen;
    string otherText = "";
    bool? boolValue;
    string text = "";
    bool invalid;
    bool touched;

    public FieldViewModel(Field spec, bool approval)
    {
        this.spec = spec;
        var options = spec.Options ?? [];
        var known = options.Select(o => o.Value).ToHashSet(StringComparer.Ordinal);
        var defaults = Defaults(spec.Default);
        Options = [.. Order(options).Select(o => new OptionViewModel(this, o, approval ? Tone(o) : "neutral",
            spec.Type is FieldType.Choice or FieldType.Multi && defaults.Contains(o.Value)))];
        // A default that is not one of the options can only be offered as Other text.
        if (spec.AllowOther == true && defaults.FirstOrDefault(d => !known.Contains(d) && !string.IsNullOrWhiteSpace(d)) is { } other)
        {
            otherChosen = true;
            otherText = other;
            if (spec.Type == FieldType.Choice) { foreach (var o in Options) { o.SetSelected(false); } }
        }
        if (spec.Type == FieldType.Choice && Options.Count(o => o.IsSelected) > 1) { foreach (var o in Options.Skip(1)) { o.SetSelected(false); } }
        boolValue = spec.Default is { ValueKind: JsonValueKind.True } ? true : spec.Default is { ValueKind: JsonValueKind.False } ? false : null;
        text = spec.Type switch
        {
            FieldType.Number => spec.Default is { ValueKind: JsonValueKind.Number } n ? n.GetRawText() : spec.Default is { ValueKind: JsonValueKind.String } s && double.TryParse(s.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? s.GetString()!.Trim() : "",
            FieldType.Text => spec.Default is { ValueKind: JsonValueKind.String } t ? t.GetString()! : spec.Default is { ValueKind: JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False } x ? x.GetRawText() : "",
            _ => "",
        };
    }

    public string Name => spec.Name;
    public string Label => spec.Title ?? spec.Name;
    public string? Description => spec.Description;
    public FieldType Type => spec.Type;
    public string TypeLabel => TypeLabels[spec.Type];
    public bool Required => spec.Required == true;
    public bool AllowOther => spec.AllowOther == true && spec.Type is FieldType.Choice or FieldType.Multi;
    // Other the agent offers and this machine cannot send yet (#908): shown, disabled, with this
    // sentence under the choices and as their description. Null when Other is offered or absent.
    public string? OtherUnavailable => !AllowOther && spec.Type is FieldType.Choice && AnswerReasons.OnField(spec.OtherUnavailable)
        ? AnswerReasons.Text(spec.OtherUnavailable!) : null;
    public ObservableCollection<OptionViewModel> Options { get; }

    public bool OtherChosen
    {
        get => otherChosen;
        set
        {
            if (!Set(ref otherChosen, value)) { return; }
            if (value && spec.Type == FieldType.Choice) { foreach (var o in Options) { o.SetSelected(false); } }
            touched = true;
        }
    }

    // Typing in Other chooses it: nobody types an answer they don't mean to give.
    public string OtherText
    {
        get => otherText;
        set
        {
            if (!Set(ref otherText, value)) { return; }
            if (value.Length > 0 && !OtherChosen) { OtherChosen = true; }
        }
    }

    // Clicking the chosen one again clears it, so an optional answer can be left out.
    public bool? BoolValue { get => boolValue; set => Set(ref boolValue, value); }
    public void ToggleBool(bool value) => BoolValue = BoolValue == value ? null : value;

    public string Text { get => text; set => Set(ref text, value); }
    public bool Invalid { get => invalid; set => Set(ref invalid, value); }

    internal void OptionChanged(OptionViewModel option, bool selected)
    {
        touched = true;
        if (selected && spec.Type == FieldType.Choice)
        {
            foreach (var o in Options.Where(o => o != option)) { o.SetSelected(false); }
            OtherChosen = false;
        }
    }

    internal void SelectOnly(string value)
    {
        foreach (var o in Options) { o.SetSelected(o.Value == value); }
    }

    // What the person had chosen and typed in the same field of the question's previous view
    // model (the host's capabilities changed under the card).
    internal void TakeDraft(FieldViewModel old)
    {
        foreach (var o in Options) { o.SetSelected(old.Options.Any(x => x.Value == o.Value && x.IsSelected)); }
        Set(ref otherChosen, old.otherChosen, nameof(OtherChosen));
        Set(ref otherText, old.otherText, nameof(OtherText));
        Set(ref boolValue, old.boolValue, nameof(BoolValue));
        Set(ref text, old.text, nameof(Text));
        touched = old.touched;
    }

    // The field's value for session.answer, or null to leave it out, or why it cannot be read.
    internal (JsonElement? Value, string? Problem) Value()
    {
        switch (spec.Type)
        {
            case FieldType.Choice:
                if (OtherChosen) { return (Json(new { other = QuestionViewModel.WellFormed(OtherText) }), null); }
                return Options.FirstOrDefault(o => o.IsSelected) is { } chosen ? (Json(chosen.Value), null) : (null, null);
            case FieldType.Multi:
                {
                    // In the agent's order, whatever order they were ticked in.
                    var list = new List<object>(spec.Options?.Where(o => Options.Any(x => x.Value == o.Value && x.IsSelected)).Select(o => (object)o.Value) ?? []);
                    if (OtherChosen) { list.Add(new { other = QuestionViewModel.WellFormed(OtherText) }); }
                    // Nothing ticked is an answer when the person cleared ticks, cleared a
                    // default, or must answer; an optional list nobody touched is left out.
                    var hadDefault = spec.Default is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) };
                    return list.Count > 0 || touched || hadDefault || Required ? (Json(list), null) : (null, null);
                }
            case FieldType.Boolean:
                return BoolValue is { } b ? (Json(b), null) : (null, null);
            case FieldType.Number:
                {
                    var t = Text.Trim();
                    if (t.Length == 0) { return (null, null); }
                    return double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) && double.IsFinite(n)
                        ? (Json(n), null)
                        : (null, $"\"{Label}\": expected a number");
                }
            default:
                return string.IsNullOrWhiteSpace(Text) ? (null, null) : (Json(QuestionViewModel.WellFormed(Text)), null);
        }
    }

    internal string TypedText()
    {
        var typed = spec.Type is FieldType.Choice or FieldType.Multi ? (OtherChosen ? OtherText : "") : spec.Type == FieldType.Boolean ? "" : Text;
        return string.IsNullOrWhiteSpace(typed) ? "" : typed;
    }

    static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value, ProtocolJson.Options);

    static HashSet<string> Defaults(JsonElement? d)
    {
        if (d is not { } v || v.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) { return []; }
        IEnumerable<JsonElement> items = v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : [v];
        return [.. items.Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.ValueKind == JsonValueKind.Object && x.TryGetProperty("other", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString()! : x.GetRawText())];
    }

    [GeneratedRegex(@"\(recommended\)", RegexOptions.IgnoreCase)]
    private static partial Regex RecommendedPattern();

    [GeneratedRegex(@"^\s*(no\b|deny|denied|reject|decline|abort|cancel|don't|do not|never)", RegexOptions.IgnoreCase)]
    private static partial Regex DenyPattern();

    [GeneratedRegex(@"^\s*(yes\b|approve|allow|accept|ok\b|proceed|always)", RegexOptions.IgnoreCase)]
    private static partial Regex AllowPattern();

    public static bool IsRecommended(Option o) => RecommendedPattern().IsMatch(o.Label ?? o.Value);

    // Recommended options first, as ask_user prompts put them; otherwise the agent's order.
    public static IEnumerable<Option> Order(IEnumerable<Option> options)
    {
        var list = options.ToList();
        return list.Where(IsRecommended).Concat(list.Where(o => !IsRecommended(o)));
    }

    // How an approval's fixed choices look: allow, refuse, or neither.
    public static string Tone(Option o)
    {
        var label = o.Label ?? o.Value;
        if (DenyPattern().IsMatch(label) || DenyPattern().IsMatch(o.Value)) { return "deny"; }
        if (AllowPattern().IsMatch(label) || AllowPattern().IsMatch(o.Value)) { return "allow"; }
        return "neutral";
    }
}

public sealed class OptionViewModel : Observable
{
    readonly FieldViewModel owner;
    bool selected;

    public OptionViewModel(FieldViewModel owner, Option option, string tone, bool selected)
    {
        this.owner = owner;
        Value = option.Value;
        Label = option.Label ?? option.Value;
        Recommended = FieldViewModel.IsRecommended(option);
        Tone = tone;
        IsDefault = selected;
        this.selected = selected;
    }

    public string Value { get; }
    public string Label { get; }
    public bool Recommended { get; }
    // allow, deny or neutral: how an approval's button is coloured.
    public string Tone { get; }
    public bool IsDefault { get; }

    public bool IsSelected
    {
        get => selected;
        set
        {
            if (Set(ref selected, value)) { owner.OptionChanged(this, value); }
        }
    }

    internal void SetSelected(bool value) => Set(ref selected, value, nameof(IsSelected));
}
