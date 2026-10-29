using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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
        ["delivered"] = _ => "Delivered. The session has your answer.",
        ["unconfirmed"] = at => $"Sent, but not confirmed. Check at the machine{at}.",
        ["stale"] = _ => "Already answered or gone: the session is no longer asking this.",
        ["busy"] = _ => "Another answer to this session is being delivered. Try again in a moment.",
        ["refused"] = _ => "The machine refused this answer.",
    };

    readonly Deliveries deliveries;
    readonly string sessionId;
    readonly string at;
    string freeText = "";
    bool otherOpen;
    string? error;
    string statusText = "";
    Tone statusTone;
    bool pausedAfterBusy;

    public QuestionViewModel(Question question, string sessionId, AgentKind agent, IReadOnlyList<string> capabilities, Deliveries deliveries, string? machineName = null)
    {
        Question = question;
        this.sessionId = sessionId;
        this.deliveries = deliveries;
        at = string.IsNullOrEmpty(machineName) ? "" : $" on {machineName}";
        (Answerable, Why) = Answerability(question, capabilities, agent);
        Fields = [.. question.Fields.Select(f => new FieldViewModel(f, question.Kind == QuestionKind.Approval))];
        Tap = question.Fields.Count == 1 && question.Fields[0].Type == FieldType.Choice;
        otherOpen = Tap && Fields[0].OtherChosen;
        deliveries.Changed += OnDelivery;
        Refresh();
    }

    void OnDelivery(Delivery d) { if (d.Key == Key) { Refresh(); } }

    // The card let go of this question: it no longer follows the delivery.
    public void Detach() => deliveries.Changed -= OnDelivery;

    public Question Question { get; }
    public string Id => Question.Id;
    public bool Approval => Question.Kind == QuestionKind.Approval;
    public string Heading => Approval ? "Permission requested" : "Waiting on you";
    public string Message => Question.Message;
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
        "atMachine" => $"This prompt can only be answered at the machine{at}.",
        _ => $"This machine isn't taking answers from the app right now. Answer at the machine{at}.",
    };

    // A prompt answered at the machine shows what it asks, not choices nobody can make here.
    public bool ShowReadOnlyFields => !Answerable && Fields.Count > 0 && Why is not ("atMachine" or "approvalOff");

    public string FreeText { get => freeText; set { if (Set(ref freeText, value)) { Error = null; } } }

    public bool OtherOpen { get => otherOpen; private set { if (Set(ref otherOpen, value)) { Raise(nameof(ShowSend)); } } }

    public string? Error { get => error; private set { if (Set(ref error, value)) { Raise(nameof(HasError)); if (value is null) { foreach (var f in Fields) { f.Invalid = false; } } } } }
    public bool HasError => error is not null;

    public string StatusText { get => statusText; private set => Set(ref statusText, value); }
    public Tone StatusTone { get => statusTone; private set => Set(ref statusTone, value); }

    // Whether the person may answer (again): nothing sent yet, or what was sent was not typed.
    public bool CanAnswer
    {
        get
        {
            var e = deliveries.Get(Key);
            return !pausedAfterBusy && (e is null || (e.State == DeliveryState.Done && e.Outcome is "busy" or "refused"));
        }
    }

    // A tap question has no Send until Other is opened.
    public bool ShowSend => !Tap || OtherOpen;
    public bool ShowResend => deliveries.Get(Key)?.State == DeliveryState.Failed;

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
    }

    // A click on one of a tap question's options is the whole answer.
    public void Choose(OptionViewModel option)
    {
        if (!Tap) { return; }
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
        Error = null;
        deliveries.Submit(Key, Ops.SessionAnswer, request => answer with { Request = request }, outcome => outcome is not ("busy" or "refused"), payload: answer);
    }

    public void Resend() => deliveries.Resend(Key, Ops.SessionAnswer, request => ((AnswerRequest)deliveries.Get(Key)!.Payload!) with { Request = request });

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
        (StatusText, StatusTone) = e is null ? ("", Tone.None) : Describe(e, at);
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
    }

    public static (string Text, Tone Tone) Describe(Delivery e, string at) => e.State switch
    {
        DeliveryState.Sending => (e.Attempts > 1 ? "Sending again…" : "Sending…", Tone.Progress),
        DeliveryState.Waiting => (e.Reason == "still being delivered" ? "The machine is still delivering it…" : $"Waiting for the Bridge Host{at}. Your answer goes as soon as it is back.", Tone.Progress),
        DeliveryState.Failed => ($"Couldn't send it: {e.Reason ?? "the machine said no"}.", Tone.Bad),
        _ => Outcome(e, at),
    };

    static (string, Tone) Outcome(Delivery e, string at)
    {
        var outcome = e.Outcome is { } o && OutcomeText.ContainsKey(o) ? o : "unconfirmed";
        var text = OutcomeText[outcome](at);
        if (outcome == "refused" && e.Reason is { } r) { text = $"The machine refused this answer: {r}."; }
        if (outcome == "unconfirmed" && e.Reason is { } u) { text += $" ({u})"; }
        return (text, outcome == "delivered" ? Tone.Ok : outcome == "refused" ? Tone.Bad : Tone.Warn);
    }

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
        if (q.Truncated == true) { return (false, "truncated"); }
        return (true, null);
    }

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
