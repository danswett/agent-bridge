using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using AgentBridge.AgentEnvelope;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Lite;

// L5 of the temporary AP5-lite mode (#1353): types a verified own-agent message into an idle
// session. It refuses anything it is not sure of and types nothing then; there is no queue and no
// retry. The caller (L4) has verified the signature; the text is re-bound to the envelope here
// because this is the last step before keys go to a live agent.
public sealed class LiteDeliver(
    IReadOnlyList<IReplyTargetGate> gates,
    ISessionSource source,
    ISessionReplier replier,
    IConsoleInjector injector,
    Action<string> log,
    TimeProvider? clock = null) : ILiteDeliver
{
    public const int MaxChars = 4096;
    public const int MaxPerMinute = 6;
    static readonly string Brand = Product.Name;

    readonly TimeProvider time = clock ?? TimeProvider.System;
    readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> recent = new(StringComparer.Ordinal);

    public async Task<LiteDelivery> DeliverAsync(VerifiedEnvelope envelope, string text, CancellationToken ct)
    {
        var body = envelope.Body;
        var target = body.Target.Session ?? "";
        var outcome = await DecideAsync(envelope, text, target, ct);
        log($"lite deliver: from {Clean(body.Principal.Machine)}/{Clean(body.Principal.Session)} to {Clean(target)} request {Clean(body.Request)}: {(outcome.Typed ? "typed" : outcome.Code)}");
        return outcome;
    }

    async Task<LiteDelivery> DecideAsync(VerifiedEnvelope envelope, string text, string target, CancellationToken ct)
    {
        var body = envelope.Body;
        string safe;
        try { safe = Escape(text); }
        catch (ArgumentException) { return Refused(LiteCodes.NotReplyTarget); } // not valid Unicode
        if (string.IsNullOrEmpty(text) || text.Length > MaxChars || !body.BindsData(Encoding.UTF8.GetBytes(text)) || target.Length == 0 || !Guid.TryParseExact(body.Request, "D", out _)) { return Refused(LiteCodes.NotReplyTarget); }
        if (source.Get(target) is not { } session) { return Refused(LiteCodes.NoSuchSession); }
        if (!await AllowedAsync(envelope, target)) { return Refused(LiteCodes.NotReplyTarget); }
        if (session.Agent != AgentKind.Copilot || session.ProcessId is not { } pid) { return Refused(LiteCodes.NotReplyTarget); }
        if (!Idle(session)) { return Refused(LiteCodes.Busy); }
        if (!await ComposerEmptyAsync(pid, ct)) { return Refused(LiteCodes.ComposerNotEmpty); }
        // Idle again right before the typing; the replier then holds the keys to the box it read.
        if (source.Get(target) is not { } now || now.ProcessId != pid) { return Refused(LiteCodes.NoSuchSession); }
        if (!Idle(now)) { return Refused(LiteCodes.Busy); }
        if (!WithinRate(target)) { return Refused(LiteCodes.Busy); }
        var reply = new ReplyRequest { Id = target, Request = body.Request, Text = Frame(body.Principal, body.Request, safe) };
        var result = await replier.ReplyAsync(reply, null, ct);
        return result.Outcome switch
        {
            AnswerOutcome.Delivered or AnswerOutcome.Unconfirmed => new LiteDelivery(true, null),
            AnswerOutcome.Busy => Refused(LiteCodes.Busy),
            AnswerOutcome.Stale => Refused(LiteCodes.NoSuchSession),
            _ => Refused(result.Refusal switch { ReplyRefusal.Asking => LiteCodes.Busy, ReplyRefusal.ComposerUnverified => LiteCodes.ComposerNotEmpty, _ => LiteCodes.NotReplyTarget }),
        };
    }

    // Any gate allowing is enough (forced-ask OR allowlisted); one that throws allows nothing.
    async Task<bool> AllowedAsync(VerifiedEnvelope envelope, string target)
    {
        foreach (var gate in gates)
        {
            try { if ((await gate.CheckAsync(envelope, target)).Allowed) { return true; } }
            catch (Exception e) when (e is not OperationCanceledException) { log($"lite deliver: a gate failed: {e.GetType().Name}"); }
        }
        return false;
    }

    static bool Idle(Session s) => s.Status == SessionStatus.Idle && s.Question is null;

    bool WithinRate(string target)
    {
        var now = time.GetUtcNow();
        var queue = recent.GetOrAdd(target, _ => new Queue<DateTimeOffset>());
        lock (queue)
        {
            while (queue.Count > 0 && now - queue.Peek() >= TimeSpan.FromMinutes(1)) { queue.Dequeue(); }
            if (queue.Count >= MaxPerMinute) { return false; }
            queue.Enqueue(now);
            return true;
        }
    }

    async Task<bool> ComposerEmptyAsync(int pid, CancellationToken ct)
    {
        try
        {
            var screen = await injector.ReadScreenAsync(pid, ct);
            return screen.Rows is { } rows && CopilotInputBoxReader.Read(rows).Box is not null;
        }
        catch (Exception e) when (e is not OperationCanceledException) { return false; }
    }

    // The provenance line: who sent it, from where, and that the user did not type it.
    static string Frame(AgentPrincipal from, string request, string escaped) =>
        $"[{Brand} · from an agent on {Clean(from.Machine)} · session {Clean(from.Session)} · request {Clean(request)} · not typed by your user] {escaped}";

    // Nothing in the body may pass as part of the header or as a key or an attachment.
    static string Escape(string text)
    {
        var s = string.Concat(text.Normalize(NormalizationForm.FormKC).Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.Format))
            .Replace("[" + Brand, "(" + Brand, StringComparison.OrdinalIgnoreCase);
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            // The typing path turns control characters into spaces, so they also start a token; format characters (zero-width, bidi) are dropped.
            var atTokenStart = i == 0 || char.IsWhiteSpace(s[i - 1]) || char.GetUnicodeCategory(s[i - 1]) is UnicodeCategory.Control or UnicodeCategory.OtherNotAssigned;
            sb.Append(s[i] == '@' && atTokenStart ? '\uFF20' : s[i]);
        }
        return sb.ToString();
    }

    static string Clean(string? value)
    {
        var sb = new StringBuilder();
        foreach (var c in value ?? "")
        {
            var category = char.GetUnicodeCategory(c);
            if (c is '[' or ']' or '"' or '·') { sb.Append(' '); }
            else if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator or UnicodeCategory.PrivateUse or UnicodeCategory.OtherNotAssigned) { }
            else { sb.Append(c); }
        }
        return sb.ToString().Trim();
    }

    static LiteDelivery Refused(string code) => new(false, code);
}
