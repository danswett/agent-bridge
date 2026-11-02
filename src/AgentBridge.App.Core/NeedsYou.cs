using System.Text.RegularExpressions;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// What an entry in Needs you offers (ui.md §6.1, #165): Approve once and Deny for an approval
// this app can answer and whose whole prompt the entry shows; "Answer at the machine" and nothing
// to press for a prompt no host can answer from here; otherwise its session is opened, where the
// card says what it asks and why.
public enum NeedsYouMode { Approve, Open, AtMachine }

// One pending question in the Needs you lane, the session's own or a delegate's (pwa/js/needs-you.js
// holds the PWA to the same rules). It refers to its card and never moves it. Approve once and Deny
// are the card's own buttons, pressed through its QuestionViewModel, so they send the same request,
// share its progress and outcomes (stale, busy, refused), and the card shows the answer going too.
public sealed partial class NeedsYouEntry : Observable
{
    const int LineMax = 140;
    // How much of an approval's prompt the lane shows whole. Approve once allows everything the
    // prompt describes (a command, a URL, a tool's arguments), so it is offered in the lane only
    // when all of it is on screen, drawn as the card draws it, code literal. A longer one (an
    // edit's diff, a long command) opens its card instead.
    public const int PromptMaxChars = 600;
    public const int PromptMaxLines = 12;

    readonly MainViewModel main;
    QuestionViewModel? question;
    string meta = "";
    string? delegateName;
    // The title last said, so a session renamed while it asks renames its entry.
    string? title;

    internal NeedsYouEntry(MainViewModel main, string key, string machineKey, SessionCardViewModel card)
    {
        this.main = main;
        Key = key;
        MachineKey = machineKey;
        Card = card;
        question = card.Question;
        if (question is not null) { question.PropertyChanged += OnQuestion; }
        var q = card.Session.Question!;
        QuestionId = q.Id;
        AskedAt = q.AskedAt;
        Line = LineOf(q);
        Prompt = q.Kind == QuestionKind.Approval && WholePrompt(q) ? q.Message : null;
        delegateName = DelegateOf(card.Session);
        Mode = ModeOf(q, card.Agent, question);
    }

    public string Key { get; }
    public string MachineKey { get; }
    public SessionCardViewModel Card { get; }
    internal QuestionViewModel? QuestionModel => question;
    public string QuestionId { get; }
    public DateTimeOffset AskedAt { get; }
    public NeedsYouMode Mode { get; }
    public bool Approvable => Mode == NeedsYouMode.Approve;
    public bool AtMachine => Mode == NeedsYouMode.AtMachine;
    public string Title => Card.Title;
    // The question's first line of prose.
    public string Line { get; }
    // An approval's prompt in full (Markdown), when it fits: what Approve once allows, all of it.
    public string? Prompt { get; }
    public bool HasPrompt => Prompt is not null;
    public bool ShowLine => Line.Length > 0 && Prompt is null;
    // The delegate that asked, by its name in `subagents` (ui.md §2.2 rule 2); null for the session's
    // own. Read again on every refresh: a later upsert can name a delegate the list had left out.
    public string? Delegate { get => delegateName; private set => Set(ref delegateName, value); }
    public string Meta { get => meta; private set => Set(ref meta, value); }
    public string OpenName => $"{Title}: open the session";
    public string ApproveName => $"Approve once: {Title}";
    public string DenyName => $"Deny: {Title}";
    public string ApproveLabel => question?.SentValue == "approve" ? "✓ Approve once" : "Approve once";
    public string DenyLabel => question?.SentValue == "deny" ? "✓ Deny" : "Deny";
    public bool CanAnswer => Approvable && question?.CanAnswer == true;
    public string StatusText => Approvable ? question?.StatusText ?? "" : "";
    public Tone StatusTone => question?.StatusTone ?? Tone.None;
    public bool HasStatus => StatusText.Length > 0;
    // The machine failed the op (an error, not an outcome): the card's Send again, the same request.
    public bool ShowResend => Approvable && question?.ShowResend == true;

    public void Approve() => Decide("approve");
    public void Deny() => Decide("deny");
    public void Resend() { if (ShowResend) { question!.Resend(); } }

    // Its card, open and on screen, and keyboard focus goes with it: the next Tab is in the card,
    // not back in the lane.
    public void Open()
    {
        main.Open(MachineKey, Card.Id);
        Card.WantsFocus = true;
    }

    // The card's own tap, as if its button had been clicked.
    void Decide(string value)
    {
        if (!Approvable || question?.Fields.FirstOrDefault()?.Options.FirstOrDefault(o => o.Value == value) is not { } option) { return; }
        question.Choose(option);
    }

    internal void Refresh(string? machineName, DateTimeOffset now)
    {
        if (title != Title)
        {
            title = Title;
            Raise(nameof(Title));
            Raise(nameof(OpenName));
            Raise(nameof(ApproveName));
            Raise(nameof(DenyName));
        }
        Delegate = DelegateOf(Card.Session);
        Meta = string.Join(" · ", new[] { machineName, Card.AgentName, Delegate is { } d ? $"{d} asks" : null, Words.Duration(AskedAt, now) }.Where(s => !string.IsNullOrEmpty(s)));
    }

    void OnQuestion(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        Raise(nameof(StatusText));
        Raise(nameof(StatusTone));
        Raise(nameof(HasStatus));
        Raise(nameof(CanAnswer));
        Raise(nameof(ShowResend));
        Raise(nameof(ApproveLabel));
        Raise(nameof(DenyLabel));
    }

    internal void Detach()
    {
        if (question is not null) { question.PropertyChanged -= OnQuestion; }
    }

    // What an entry offers, by the same answerability the card's question has, and only when the
    // whole prompt is in the entry. Truncated is read from the question as it is now: a later,
    // budget-trimmed upsert can mark it so under the same id, which the card's model doesn't see.
    // Only Copilot's prompts are answered here: a host writes them whole and literal
    // (CopilotPermission, code in a fence the command cannot close), while a Codex approval's command
    // reaches the host cut short with nothing saying so, and is answered on its card.
    public static NeedsYouMode ModeOf(Question q, AgentKind agent, QuestionViewModel? vm) =>
        q.AnswerAtMachine == true ? NeedsYouMode.AtMachine
        : agent == AgentKind.Copilot && q.Truncated != true && vm is { Answerable: true, Approval: true, Tap: true } && WholePrompt(q)
            && vm.Fields[0].Options.Any(o => o.Value == "approve") && vm.Fields[0].Options.Any(o => o.Value == "deny") ? NeedsYouMode.Approve
        : NeedsYouMode.Open;

    // A table scrolls sideways, so a prompt with one is not whole on screen.
    public static bool WholePrompt(Question q)
    {
        var m = q.Message.Trim();
        return m.Length > 0 && m.Length <= PromptMaxChars && m.Split('\n').Length <= PromptMaxLines && !HasTable(MarkdownModel.Parse(m));
    }

    static bool HasTable(IEnumerable<MdBlock> blocks) => blocks.Any(b => b switch
    {
        MdTable => true,
        MdQuote q => HasTable(q.Blocks),
        MdList l => l.Items.Any(HasTable),
        _ => false,
    });

    // Who asked: null for the session's own question, else the delegate's name. One the list left out
    // (the host's limit or the session budget) is still a delegate, just unnamed.
    public static string? DelegateOf(Session s)
    {
        if (s.Question is not { } q || (q.Agent ?? s.WaitingOn) is not { } id) { return null; }
        return s.Subagents?.FirstOrDefault(a => a.Id == id)?.Name is { Length: > 0 } name ? name : "A delegate";
    }

    // A question's first line of prose, never a code fence's.
    public static string LineOf(Question q) =>
        PlainLine(q.Message.Split('\n').FirstOrDefault(l => l.Trim().Length > 0 && !Fence().IsMatch(l)) ?? "");

    // Markdown down to one line of plain text: no emphasis, code or heading marks, whitespace
    // collapsed, cut with an ellipsis (pwa/js/needs-you.js plainLine). For prose only: code is shown
    // literal, in the prompt.
    public static string PlainLine(string text)
    {
        var line = LeadingMark().Replace(text, "");
        line = Marks().Replace(line, "");
        line = Emphasis().Replace(line, "$1$2");
        line = Link().Replace(line, "$1");
        line = Spaces().Replace(line, " ").Trim();
        return line.Length > LineMax ? line[..(LineMax - 1)].TrimEnd() + "…" : line;
    }

    [GeneratedRegex(@"^\s{0,3}(`{3,}|~{3,})")]
    private static partial Regex Fence();
    [GeneratedRegex(@"^\s{0,3}(?:#{1,6}\s+|>\s?|[-*+]\s+|\d+[.)]\s+)")]
    private static partial Regex LeadingMark();
    [GeneratedRegex(@"(\*\*|__|`+)")]
    private static partial Regex Marks();
    [GeneratedRegex(@"(^|[\s(])[*_]([^*_\s][^*_]*?)[*_](?=[\s).,!?:;]|$)")]
    private static partial Regex Emphasis();
    [GeneratedRegex(@"\[([^\]]*)\]\([^)]*\)")]
    private static partial Regex Link();
    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
