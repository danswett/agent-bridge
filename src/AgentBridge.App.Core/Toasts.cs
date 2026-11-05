using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// A toast to show: its XML (Windows' ToastGeneric schema), the tag that keeps one per session,
// and whether it should pop up or go quietly into Notification Center.
public sealed record ToastNotice(string Tag, string Xml, bool Quiet);

// Where toasts go: Windows' notifier in the app, a list in tests.
public interface IToastSink
{
    void Show(ToastNotice notice);
    void Withdraw(string tag);
}

public enum ToastKind { Question, Finished, Outcome }

// When the app raises a toast, following the host's Web Push rules (docs/protocol/
// v1-sessions.md, Notifications) so the desktop and the phone agree: a question still pending
// after 2 s, once per question; a turn that went from working to idle with a response, still
// idle 3 s later, once per turn; a question answered anywhere (or a session gone) withdraws its
// toast. Nothing while the app's window is in front: the card is already there.
public sealed class ToastPlanner
{
    public static readonly TimeSpan QuestionDelay = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan FinishedDelay = TimeSpan.FromSeconds(3);

    readonly IToastSink sink;
    readonly TimeProvider time;
    readonly Dictionary<string, Tracked> sessions = new(StringComparer.Ordinal);
    // Sessions with a finished toast up, kept past their end, for MachineGone.
    readonly HashSet<string> finished = new(StringComparer.Ordinal);

    sealed class Tracked
    {
        public Session? Last;
        public string? Shown;
        // What the toast up says, to replace it if its question comes back otherwise.
        public Question? ShownQuestion;
        public HashSet<string> Notified = new(StringComparer.Ordinal);
        public string? FinishedKey;
        public (ToastKind Kind, DateTimeOffset At, string? Question)? Due;
        // While a delegate's question is pending (NoteWait).
        public bool WaitFromTurn;
        public string? ResponseBeforeWait;
        // The question whose toast was withdrawn while the machine was away (Away): raised again
        // quietly, if still asked.
        public string? Again;
    }

    public ToastPlanner(IToastSink sink, TimeProvider? time = null)
    {
        this.sink = sink;
        this.time = time ?? TimeProvider.System;
    }

    public Func<bool> Enabled { get; init; } = () => true;
    public Func<bool> FinishedEnabled { get; init; } = () => true;
    // The app's window is in front: the card shows it, so no toast.
    public Func<bool> WindowActive { get; init; } = () => false;
    // Windows says the person is busy (presentation, full screen, quiet hours): toasts go to
    // Notification Center without popping up.
    public Func<bool> Quiet { get; init; } = () => false;
    public Func<IReadOnlyList<string>> Capabilities { get; init; } = () => [];
    public Func<string?> MachineName { get; init; } = () => null;
    // Another machine's relay device id: its toasts keep tags of their own, and a click says
    // which machine to answer. Null for this machine's.
    public string? MachineKey { get; init; }
    // Set by another machine's Machine (#269): its sessions are not its host's word now (it is
    // away, or its snapshot has not confirmed them), so nothing is raised for them until they are.
    public Func<bool> Held { get; internal set; } = () => false;

    // The sessions as the app first hears of them: a turn that finished before is not news, but
    // a question already pending is notified, as the host does after a restart. Sessions it
    // knew before a reconnect are brought up to date: a toast whose question was answered
    // meanwhile is withdrawn, and a question that arrived meanwhile is notified.
    public void Baseline(IEnumerable<Session> live)
    {
        var now = time.GetUtcNow();
        foreach (var s in live)
        {
            if (!sessions.TryGetValue(s.Id, out var t))
            {
                t = sessions[s.Id] = new Tracked { Last = s };
                NoteWait(t, null, s);
                if (s.Question is { } first) { t.Due = (ToastKind.Question, now + QuestionDelay, first.Id); }
                continue;
            }
            NoteWait(t, t.Last, s);
            t.Last = s;
            if (t.Shown is { } shown && s.Question?.Id != shown)
            {
                sink.Withdraw(TagOf(s.Id));
                t.Shown = null;
            }
            else { Reword(t, s); }
            if (s.Question is { } q && !t.Notified.Contains(q.Id))
            {
                if (t.Due is not { Kind: ToastKind.Question } d || d.Question != q.Id) { t.Due = (ToastKind.Question, now + QuestionDelay, q.Id); }
            }
            else if (t.Due is { Kind: ToastKind.Question } pending && s.Question?.Id != pending.Question) { t.Due = null; }
            else if (t.Due is { Kind: ToastKind.Finished } && s.Status != SessionStatus.Idle) { t.Due = null; }
        }
    }

    public void Upsert(Session s)
    {
        var now = time.GetUtcNow();
        if (!sessions.TryGetValue(s.Id, out var t))
        {
            sessions[s.Id] = t = new Tracked();
        }
        var before = t.Last;
        t.Last = s;
        NoteWait(t, before, s);
        if (t.Shown is { } shown && s.Question?.Id != shown)
        {
            sink.Withdraw(TagOf(s.Id));
            t.Shown = null;
        }
        else { Reword(t, s); }
        if (s.Question is { } q && !t.Notified.Contains(q.Id))
        {
            // Counted from when it was first seen: a question whose card keeps changing is not
            // pushed back by each change.
            if (t.Due is not { Kind: ToastKind.Question } d0 || d0.Question != q.Id) { t.Due = (ToastKind.Question, now + QuestionDelay, q.Id); }
        }
        else if (before is not null && EndsTurn(t, before, s) && s.Status == SessionStatus.Idle && !string.IsNullOrEmpty(MainResponse(s)))
        {
            t.Due = (ToastKind.Finished, now + FinishedDelay, null);
        }
        else if (t.Due is { Kind: ToastKind.Finished } && s.Status != SessionStatus.Idle) { t.Due = null; }
        else if (t.Due is { Kind: ToastKind.Question } d && s.Question?.Id != d.Question) { t.Due = null; }
    }

    // The toast up for a question that came back under its id saying something else, or answerable
    // otherwise (a host updated to refuse what it used to offer, #220): replaced, quietly, so its
    // buttons are never what the question no longer allows.
    void Reword(Tracked t, Session s)
    {
        if (t.Shown is not { } shown || s.Question is not { } q || q.Id != shown || t.ShownQuestion is not { } was || was.Id != q.Id || QuestionViewModel.SaysTheSame(q, was)) { return; }
        // Not put up again where no toast would be put up now (notifications turned off, the window in
        // front): withdrawn, so its old buttons go with it.
        if (!Enabled() || WindowActive())
        {
            sink.Withdraw(TagOf(s.Id));
            t.Shown = null;
            t.ShownQuestion = null;
            return;
        }
        t.ShownQuestion = q;
        sink.Show(new ToastNotice(TagOf(s.Id), ToastXml.Question(s, Capabilities(), MachineName(), MachineKey), true));
    }

    // A wait on a delegate's question (waitingOn) as it begins: whether the main agent's turn was
    // running, and its own response then. Answering a delegate while the main agent stayed idle
    // ends no turn (v1-sessions.md, Notifications, `finished`): its old response is not news.
    static void NoteWait(Tracked t, Session? before, Session s)
    {
        if (s.Status != SessionStatus.Waiting || s.WaitingOn is null || before is { Status: SessionStatus.Waiting, WaitingOn: not null }) { return; }
        // Waiting on its own question is mid-turn too: a delegate's question straight after it
        // still belongs to that turn.
        t.WaitFromTurn = before is { Status: SessionStatus.Working } or { Status: SessionStatus.Waiting, WaitingOn: null };
        t.ResponseBeforeWait = MainResponse(before ?? s);
    }

    // A turn of the main agent's own ended: it was working, or waiting on its own question, or on
    // a delegate's that it asked mid-turn or answered with new words of its own.
    static bool EndsTurn(Tracked t, Session before, Session s) =>
        before.Status == SessionStatus.Working
        || before.Status == SessionStatus.Waiting && (before.WaitingOn is null || t.WaitFromTurn || MainResponse(s) != t.ResponseBeforeWait);

    // A session that ended keeps its finished toast: the response is still worth reading.
    public void Remove(string id)
    {
        if (!sessions.Remove(id, out var t)) { return; }
        if (t.Shown is not null) { sink.Withdraw(TagOf(id)); }
    }

    // The machine left: every toast it raised goes, finished ones too (even of sessions that ended
    // before), since none of them can be acted on now.
    public void MachineGone()
    {
        foreach (var (id, t) in sessions) { if (t.Shown is not null) { finished.Add(id); } }
        foreach (var id in finished) { sink.Withdraw(TagOf(id)); }
        finished.Clear();
        sessions.Clear();
    }

    // The machine went away (#269): a question it toasted may be gone by the time it is back, and a
    // toast's buttons would answer it, so its question toasts go. Each one still asked once the
    // machine's snapshot confirms it is raised again, quietly: it was news when it first came.
    public void Away()
    {
        foreach (var (id, t) in sessions)
        {
            if (t.Shown is not { } q) { continue; }
            sink.Withdraw(TagOf(id));
            t.Shown = null;
            t.Notified.Remove(q);
            t.Again = q;
        }
    }

    // Called every second or so: raises what is due.
    public void Tick()
    {
        // Kept due, not dropped: raised once the machine's sessions are confirmed, if still so.
        if (Held()) { return; }
        var now = time.GetUtcNow();
        foreach (var (id, t) in sessions)
        {
            if (t.Due is not { } due || due.At > now || t.Last is not { } s) { continue; }
            t.Due = null;
            if (due.Kind == ToastKind.Question)
            {
                if (s.Question is not { } q || q.Id != due.Question) { continue; }
                t.Notified.Add(q.Id);
                var again = t.Again == q.Id;
                t.Again = null;
                if (!Enabled() || WindowActive()) { continue; }
                t.Shown = q.Id;
                t.ShownQuestion = q;
                sink.Show(new ToastNotice(TagOf(id), ToastXml.Question(s, Capabilities(), MachineName(), MachineKey), Quiet() || again));
            }
            else
            {
                var key = $"{s.Activity.TurnCount}|{MainResponse(s)}";
                // The main agent's words may have gone in the wait (a delegate wrote last, or the
                // budget left them out): an empty toast says nothing.
                if (s.Status != SessionStatus.Idle || t.FinishedKey == key || string.IsNullOrEmpty(MainResponse(s))) { continue; }
                t.FinishedKey = key;
                if (!Enabled() || !FinishedEnabled() || WindowActive()) { continue; }
                finished.Add(id);
                sink.Show(new ToastNotice(TagOf(id), ToastXml.Finished(s, MachineName(), MachineKey), Quiet()));
            }
        }
    }

    string TagOf(string sessionId) => Tag(sessionId, MachineKey);

    // A finished turn is the main agent's, read from its own response: a delegate's latest text in
    // the top-level field (activity.source) is never the preview, and a delegate finishing alone is
    // never a finished turn (v1-sessions.md, Notifications, `finished`).
    internal static string? MainResponse(Session s) => Attribution.MainVoiceOf(s).Response;

    // A session on another machine is tagged with that machine too.
    public static string Tag(string sessionId, string? machineKey) => Tag(machineKey is null ? sessionId : $"{machineKey}/{sessionId}");

    // Toast tags are at most 64 characters; a session id is a UUID, but nothing promises it.
    public static string Tag(string sessionId) => sessionId.Length <= 60 ? sessionId : Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(sessionId)))[..60];

    // What became of an answer given on a toast: a tag of its own, so withdrawing the question's
    // toast once it is answered leaves this one.
    public static string OutcomeTag(string sessionId, string? machineKey = null) => Tag(sessionId, machineKey) + "~a";
}

// The toasts' XML. Arguments are a query string the activator reads back (ToastAction.Parse);
// everything from a session is escaped, as text and as attribute values.
public static partial class ToastXml
{
    // Windows shows at most five buttons and five inputs.
    const int MaxActions = 5;

    // Carried by every answer button this build draws, and required before a click answers
    // anything (ToastResponder). Builds before #372 put Approve once and Deny on an approval's
    // toast, and Notification Center keeps such a toast after an update: its buttons, without
    // this, only open the question.
    public const string AnswerVersionKey = "v";
    public const string AnswerVersion = "2";

    public static string Question(Session s, IReadOnlyList<string> capabilities, string? machine, string? machineKey = null)
    {
        var q = s.Question!;
        var (answerable, _) = QuestionViewModel.Answerability(q, capabilities, s.Agent);
        var what = q.AnswerAtMachine == true ? "waiting at the machine" : q.Kind == QuestionKind.Approval ? "waiting for your approval" : "waiting on you";
        var body = q.Kind == QuestionKind.Approval ? Words(ShownText.Reveal(q.Message, rightToLeft: true), 300) : Plain(q.Message, 300);
        var open = Args(machineKey, ("action", "open"), ("session", s.Id));
        var b = Start(open, Title(s), $"{Machine(machine)}{what}", body.Length > 0 ? body : "This session is waiting for an answer.");
        b.Append("<actions>");
        var basic = Args(machineKey, ("action", "answer"), ("session", s.Id), ("question", q.Id), (AnswerVersionKey, AnswerVersion));
        if (q.Kind == QuestionKind.Approval)
        {
            // An approval is only ever answered where the whole command is shown, with its
            // invisible, bidi and right-to-left characters refused or escaped (#165, #220). The
            // OS draws a toast's body as plain text and cuts it at about 300 characters, so a
            // toast can show neither: it opens the question (#253 decision 7, #372).
            b.Append(Action("Open", open));
        }
        else if (answerable && q.Fields.Count == 0)
        {
            // Free text: a box and Send.
            b.Append(Input("text", "Type your answer"));
            b.Append(Action("Send", $"{basic}&{Args(("kind", "text"))}", input: "text"));
        }
        else if (answerable && q.Fields.Count == 1 && q.Fields[0].Type == FieldType.Choice && q.Fields[0].Options is { Count: > 0 } options)
        {
            var f = q.Fields[0];
            var ordered = FieldViewModel.Order(options).ToList();
            var other = f.AllowOther == true;
            var room = MaxActions - (other ? 1 : 0);
            if (other) { b.Append(Input("other", "Type your own answer")); }
            if (ordered.Count <= room)
            {
                foreach (var o in ordered)
                {
                    b.Append(Action(o.Label ?? o.Value, $"{basic}&{Args(("kind", "option"), ("field", f.Name), ("value", o.Value))}"));
                }
            }
            else if (ordered.Count <= MaxActions)
            {
                b.Append(Selection("choice", ordered));
                b.Append(Action("Send", $"{basic}&{Args(("kind", "choice"), ("field", f.Name))}"));
            }
            else
            {
                b.Append(Action("Open", open));
            }
            if (other) { b.Append(Action("Send", $"{basic}&{Args(("kind", "other"), ("field", f.Name))}", input: "other")); }
        }
        else
        {
            // Forms, multi-select and anything answered at the machine: the card has it.
            b.Append(Action("Open", open));
        }
        b.Append("</actions>");
        return End(b);
    }

    public static string Finished(Session s, string? machine, string? machineKey = null)
    {
        var preview = Plain(ToastPlanner.MainResponse(s), 300);
        return End(Start(Args(machineKey, ("action", "open"), ("session", s.Id)), Title(s), $"{Machine(machine)}finished", preview));
    }

    // The first start after an update (AutoUpdater.Announce): what changed, from the release's
    // notes. A click opens the app.
    public static string Updated(string version, string from, string? notes)
    {
        var body = Plain(notes, 300);
        return End(Start(Args(("action", "open")), $"{Product.Name} updated to {version}", $"from {from}", body.Length > 0 ? body : null));
    }

    // What became of an answer given on a toast, replacing that toast.
    public static string Outcome(string sessionId, string title, string text, string? machineKey = null) =>
        End(Start(Args(machineKey, ("action", "open"), ("session", sessionId)), title, text, null));

    // Every toast closes here, silent (#754): a toast with no <audio> plays Windows' default
    // sound, and with only Outcome's silenced, question toasts dinged all day. Any sound comes
    // back only as an owner-approved spec in docs/design/ui.md.
    static string End(StringBuilder b) => b.Append("<audio silent=\"true\"/></toast>").ToString();

    static StringBuilder Start(string launch, string title, string line, string? body)
    {
        var b = new StringBuilder();
        b.Append($"<toast launch=\"{Attr(launch)}\"><visual><binding template=\"ToastGeneric\">");
        b.Append($"<text>{Text(title)}</text><text>{Text(line)}</text>");
        if (!string.IsNullOrEmpty(body)) { b.Append($"<text>{Text(body)}</text>"); }
        b.Append("</binding></visual>");
        return b;
    }

    static string Title(Session s) => string.IsNullOrEmpty(s.Title) ? $"{s.Agent switch { AgentKind.Copilot => "Copilot", AgentKind.Claude => "Claude", AgentKind.Codex => "Codex", _ => "Agent" }} session" : Limits.KeepStart(s.Title, 120)!;

    static string Machine(string? machine) => string.IsNullOrEmpty(machine) ? "" : $"{machine} · ";

    static string Input(string id, string placeholder) => $"<input id=\"{id}\" type=\"text\" placeHolderContent=\"{Attr(placeholder)}\"/>";

    static string Selection(string id, IEnumerable<Option> options) =>
        $"<input id=\"{id}\" type=\"selection\">{string.Concat(options.Select(o => $"<selection id=\"{Attr(o.Value)}\" content=\"{Attr(Limits.KeepStart(o.Label ?? o.Value, 120)!)}\"/>"))}</input>";

    static string Action(string content, string arguments, string? input = null) =>
        $"<action content=\"{Attr(Limits.KeepStart(content, 60)!)}\" arguments=\"{Attr(arguments)}\"{(input is null ? "" : $" hint-inputId=\"{input}\"")}/>";

    // With the machine the session is on, when it is another one.
    static string Args(string? machineKey, params (string Key, string Value)[] pairs) =>
        machineKey is null ? Args(pairs) : $"{Args(pairs)}&{Args(("machine", machineKey))}";

    public static string Args(params (string Key, string Value)[] pairs) =>
        string.Join("&", pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));

    static string Text(string s) => SecurityElement.Escape(Clean(s)) ?? "";
    static string Attr(string s) => SecurityElement.Escape(Clean(s)) ?? "";

    // XML 1.0 cannot carry most control characters at all.
    static string Clean(string s) => ControlChars().Replace(s, " ");

    // Markdown read as words for a toast: whitespace runs collapsed, the commonest marks gone.
    public static string Plain(string? markdown, int max)
    {
        if (string.IsNullOrEmpty(markdown)) { return ""; }
        var text = Marks().Replace(markdown, "");
        text = Spaces().Replace(text, " ").Trim();
        return Cut(text, max);
    }

    static string Cut(string text, int max) => text.Length <= max ? text : text[..(char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)].TrimEnd() + "…";

    // An approval read as the card reads it, as words: the prose as it is drawn (the agent's own
    // words, which a host escapes, without their backslashes, #212) and code exactly as written,
    // never with its * and _ taken for marks. Whitespace runs collapsed.
    public static string Words(string? markdown, int max)
    {
        if (string.IsNullOrEmpty(markdown)) { return ""; }
        var b = new StringBuilder();
        void Inlines(IEnumerable<MdInline> inlines)
        {
            foreach (var i in inlines) { b.Append(i switch { MdText t => t.Text, MdLink l => l.Text == l.Url ? l.Text : $"{l.Text} ({l.Url})", _ => " " }); }
            b.Append(' ');
        }
        void Blocks(IEnumerable<MdBlock> blocks)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case MdParagraph p: Inlines(p.Inlines); break;
                    case MdHeading h: Inlines(h.Inlines); break;
                    case MdCode c: b.Append(c.Text).Append(' '); break;
                    case MdQuote q: Blocks(q.Blocks); break;
                    case MdList l: foreach (var item in l.Items) { Blocks(item); } break;
                    case MdTable t: foreach (var cell in t.Rows.SelectMany(r => r)) { Inlines(cell); } break;
                }
            }
        }
        Blocks(MarkdownModel.Parse(markdown));
        // Revealed again once parsed: parsing decodes a reference an older host left in an approval
        // (&#x5D0;, &nbsp;) into the character it stands for, which would reorder the preview or be
        // collapsed with the spaces around it.
        return Cut(Spaces().Replace(ShownText.Reveal(b.ToString(), rightToLeft: true), " ").Trim(), max);
    }

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\uFFFE\uFFFF]")]
    private static partial Regex ControlChars();

    [GeneratedRegex(@"(^|\s)#{1,6}\s|[*_`>]+")]
    private static partial Regex Marks();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

// A click on a toast, read back: open a session, or answer its question.
public sealed record ToastAction(string Action, string? Session, string? Question, string? Kind, string? Field, string? Value, string? Machine = null, string? Version = null)
{
    // A button of this build's (ToastXml.AnswerVersion): one from an older toast only opens.
    public bool Current => Version == ToastXml.AnswerVersion;

    public static ToastAction Parse(string? arguments)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in (arguments ?? "").Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = pair.IndexOf('=');
            if (i <= 0) { continue; }
            map[Uri.UnescapeDataString(pair[..i])] = Uri.UnescapeDataString(pair[(i + 1)..]);
        }
        return new ToastAction(map.GetValueOrDefault("action") ?? "open", map.GetValueOrDefault("session"), map.GetValueOrDefault("question"),
            map.GetValueOrDefault("kind"), map.GetValueOrDefault("field"), map.GetValueOrDefault("value"), map.GetValueOrDefault("machine"),
            map.GetValueOrDefault(ToastXml.AnswerVersionKey));
    }

    // The session.answer this click gives, or why it gives none (an empty Other box, say).
    // inputs: what the toast's text boxes and selection held.
    public (AnswerRequest? Answer, string? Problem) Answer(IReadOnlyDictionary<string, string> inputs, string request)
    {
        if (Action != "answer" || Session is null || Question is null) { return (null, null); }
        var answer = new AnswerRequest { Id = Session, Question = Question, Request = request };
        string? Typed(string id) => inputs.TryGetValue(id, out var v) && !string.IsNullOrWhiteSpace(v) ? QuestionViewModel.WellFormed(v) : null;
        switch (Kind)
        {
            case "text":
                return Typed("text") is { } text ? (answer with { Text = text }, null) : (null, "type an answer first");
            case "option" when Field is not null && Value is not null:
                return (answer with { Fields = new Dictionary<string, JsonElement> { [Field] = JsonSerializer.SerializeToElement(Value) } }, null);
            case "choice" when Field is not null:
                return inputs.TryGetValue("choice", out var chosen) && chosen.Length > 0
                    ? (answer with { Fields = new Dictionary<string, JsonElement> { [Field] = JsonSerializer.SerializeToElement(chosen) } }, null)
                    : (null, "choose an option first");
            case "other" when Field is not null:
                return Typed("other") is { } other
                    ? (answer with { Fields = new Dictionary<string, JsonElement> { [Field] = JsonSerializer.SerializeToElement(new { other }, ProtocolJson.Options) } }, null)
                    : (null, "type your own answer first");
            default:
                return (null, "this toast's button is not one the app knows");
        }
    }
}
