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
        public HashSet<string> Notified = new(StringComparer.Ordinal);
        public string? FinishedKey;
        public (ToastKind Kind, DateTimeOffset At, string? Question)? Due;
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
                if (s.Question is { } first) { t.Due = (ToastKind.Question, now + QuestionDelay, first.Id); }
                continue;
            }
            t.Last = s;
            if (t.Shown is { } shown && s.Question?.Id != shown)
            {
                sink.Withdraw(TagOf(s.Id));
                t.Shown = null;
            }
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
        if (t.Shown is { } shown && s.Question?.Id != shown)
        {
            sink.Withdraw(TagOf(s.Id));
            t.Shown = null;
        }
        if (s.Question is { } q && !t.Notified.Contains(q.Id))
        {
            // Counted from when it was first seen: a question whose card keeps changing is not
            // pushed back by each change.
            if (t.Due is not { Kind: ToastKind.Question } d0 || d0.Question != q.Id) { t.Due = (ToastKind.Question, now + QuestionDelay, q.Id); }
        }
        else if (before is not null && before.Status is SessionStatus.Working or SessionStatus.Waiting && s.Status == SessionStatus.Idle && !string.IsNullOrEmpty(s.Activity.Response))
        {
            t.Due = (ToastKind.Finished, now + FinishedDelay, null);
        }
        else if (t.Due is { Kind: ToastKind.Finished } && s.Status != SessionStatus.Idle) { t.Due = null; }
        else if (t.Due is { Kind: ToastKind.Question } d && s.Question?.Id != d.Question) { t.Due = null; }
    }

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

    // Called every second or so: raises what is due.
    public void Tick()
    {
        var now = time.GetUtcNow();
        foreach (var (id, t) in sessions)
        {
            if (t.Due is not { } due || due.At > now || t.Last is not { } s) { continue; }
            t.Due = null;
            if (due.Kind == ToastKind.Question)
            {
                if (s.Question is not { } q || q.Id != due.Question) { continue; }
                t.Notified.Add(q.Id);
                if (!Enabled() || WindowActive()) { continue; }
                t.Shown = q.Id;
                sink.Show(new ToastNotice(TagOf(id), ToastXml.Question(s, Capabilities(), MachineName(), MachineKey), Quiet()));
            }
            else
            {
                var key = $"{s.Activity.TurnCount}|{s.Activity.Response}";
                if (s.Status != SessionStatus.Idle || t.FinishedKey == key) { continue; }
                t.FinishedKey = key;
                if (!Enabled() || !FinishedEnabled() || WindowActive()) { continue; }
                finished.Add(id);
                sink.Show(new ToastNotice(TagOf(id), ToastXml.Finished(s, MachineName(), MachineKey), Quiet()));
            }
        }
    }

    string TagOf(string sessionId) => Tag(sessionId, MachineKey);

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

    public static string Question(Session s, IReadOnlyList<string> capabilities, string? machine, string? machineKey = null)
    {
        var q = s.Question!;
        var (answerable, _) = QuestionViewModel.Answerability(q, capabilities, s.Agent);
        var what = q.AnswerAtMachine == true ? "waiting at the machine" : q.Kind == QuestionKind.Approval ? "waiting for your approval" : "waiting on you";
        var body = Plain(q.Message, 300);
        var open = Args(machineKey, ("action", "open"), ("session", s.Id));
        var b = Start(open, Title(s), $"{Machine(machine)}{what}", body.Length > 0 ? body : "This session is waiting for an answer.");
        b.Append("<actions>");
        var basic = Args(machineKey, ("action", "answer"), ("session", s.Id), ("question", q.Id));
        if (answerable && q.Fields.Count == 0)
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
        b.Append("</actions></toast>");
        return b.ToString();
    }

    public static string Finished(Session s, string? machine, string? machineKey = null)
    {
        var preview = Plain(s.Activity.Response, 300);
        var b = Start(Args(machineKey, ("action", "open"), ("session", s.Id)), Title(s), $"{Machine(machine)}finished", preview);
        b.Append("</toast>");
        return b.ToString();
    }

    // The first start after an update (AutoUpdater.Announce): what changed, from the release's
    // notes. A click opens the app.
    public static string Updated(string version, string from, string? notes)
    {
        var body = Plain(notes, 300);
        return Start(Args(("action", "open")), $"Agent Bridge updated to {version}", $"from {from}", body.Length > 0 ? body : null).Append("</toast>").ToString();
    }

    // What became of an answer given on a toast, replacing that toast.
    public static string Outcome(string sessionId, string title, string text, string? machineKey = null) =>
        Start(Args(machineKey, ("action", "open"), ("session", sessionId)), title, text, null).Append("<audio silent=\"true\"/></toast>").ToString();

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
        return text.Length <= max ? text : text[..(char.IsHighSurrogate(text[max - 1]) ? max - 1 : max)].TrimEnd() + "…";
    }

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F\uFFFE\uFFFF]")]
    private static partial Regex ControlChars();

    [GeneratedRegex(@"(^|\s)#{1,6}\s|[*_`>]+")]
    private static partial Regex Marks();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}

// A click on a toast, read back: open a session, or answer its question.
public sealed record ToastAction(string Action, string? Session, string? Question, string? Kind, string? Field, string? Value, string? Machine = null)
{
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
            map.GetValueOrDefault("kind"), map.GetValueOrDefault("field"), map.GetValueOrDefault("value"), map.GetValueOrDefault("machine"));
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
