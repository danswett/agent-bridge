// Bridge Protocol v1, session history (#292). The contract is docs/protocol/v1-history.md; these
// records mirror it and serialize to exactly that JSON with ProtocolJson.Options.
namespace AgentBridge.Protocol;

// What a history part is. A client skips a part whose kind it does not know: drawn as some other
// kind, a newer host's part would be shown as something it is not.
[UnknownAs(Unknown)]
public enum HistoryPartKind { Response, Reasoning, Tool, Delegate, Question, Unknown }

// A delegate starting, or reporting back to the main agent (#661). Unknown is skipped.
[UnknownAs(Unknown)]
public enum DelegateEventKind { Started, Reported, Unknown }

// How a question or an approval in history ended. A value a client does not know is Unknown,
// never Approved: an approval is shown as given only when the host said it was.
[UnknownAs(Unknown)]
public enum QuestionOutcome { Answered, Approved, Denied, Cancelled, Unknown }

public static class HistoryLimits
{
    public const int DefaultTurns = 20;
    public const int MaxTurns = 50;
    // The newest turns of a session a host serves (H3); past them a page says start.limited.
    public const int Kept = 500;
    // Every turn as sent fits the session budget, so a page of one always fits a message.
    public const int MaxTurnBytes = SessionBudget.MaxSessionBytes;
    public const int Prompt = 8 * 1024;
    public const int Response = Limits.Response;
    public const int Reasoning = Limits.Reasoning;
    public const int Answer = 2 * 1024;
    public const int ToolsPerTurn = Limits.History;
    public const int DelegatesPerTurn = 50;
    public const int QuestionsPerTurn = 20;
    // Response parts, and reasoning parts, each.
    public const int TextsPerTurn = 50;
    public const int MaxCursorBytes = 256;
    public const int MaxIdBytes = 64;
    public const int MaxReasonBytes = 256;
    // Room for {"re":n,"ok":true,"result":{"turns":[…],"before":"…","start":{…},"partial":true}}.
    public const int Envelope = 128 + MaxCursorBytes + 128;
}

// session.history's request, read strictly by the host. Before: the before of the last page the
// client received for this session. Limit: 1 to MaxTurns, absent for DefaultTurns.
public sealed record HistoryRequest
{
    public required string Id { get; init; }
    public string? Before { get; init; }
    public int? Limit { get; init; }
}

// One page of a session's history, or why there is none: Missing (the session is not listed),
// Stale (Before cannot be continued from) or Unavailable (why, in words). Turns are oldest first.
// Before is absent exactly when Start is present.
public sealed record HistoryResult
{
    public IReadOnlyList<HistoryTurn>? Turns { get; init; }
    // Strict: read as absent, a page with more before it would read as the start of history.
    [Strict(Strictness.Identity)]
    public string? Before { get; init; }
    [Strict(Strictness.Identity)]
    public HistoryStart? Start { get; init; }
    public bool? Partial { get; init; }
    public bool? Missing { get; init; }
    public bool? Stale { get; init; }
    public string? Unavailable { get; init; }

    public static readonly HistoryResult Gone = new() { Missing = true };
    public static readonly HistoryResult StaleCursor = new() { Stale = true };
    public static HistoryResult NotAvailable(string why) => new() { Unavailable = Limits.KeepStart(why, HistoryLimits.MaxReasonBytes) };
}

// The start of what a host serves. At: the session's first record, for "Start of session · date"
// (H6). Limited: the host serves nothing older though the session has it, "Start of available
// history".
public sealed record HistoryStart
{
    public DateTimeOffset? At { get; init; }
    public bool? Limited { get; init; }
}

// A prompt and everything until the next prompt (H7). Unreadable: a record that starts a turn here
// could not be read; it holds only Id and At.
public sealed record HistoryTurn
{
    [Strict(Strictness.Identity)]
    public required string Id { get; init; }
    public required DateTimeOffset At { get; init; }
    // Strict: a prompt whose sender cannot be read refuses its turn, never shown as the person's.
    [Strict(Strictness.Security)]
    public HistoryPrompt? Prompt { get; init; }
    public IReadOnlyList<HistoryPart> Parts { get; init; } = [];
    public int? PartsMore { get; init; }
    public bool? Truncated { get; init; }
    public bool? Unreadable { get; init; }
}

// What started a turn. From and Label as activity.userMessageFrom and userMessageLabel (#489):
// from the agent's own record, never the text.
public sealed record HistoryPrompt
{
    public required string Text { get; init; }
    public bool? Truncated { get; init; }
    [Strict(Strictness.Security)]
    public UserMessageFrom? From { get; init; }
    public string? Label { get; init; }
}

// One thing in a turn after its prompt. Which fields it has depends on Kind: Text (response,
// reasoning), Tool, Delegate, or Question with Outcome, Answer and AnsweredAt.
public sealed record HistoryPart
{
    [Strict(Strictness.Identity)]
    public required string Id { get; init; }
    public required HistoryPartKind Kind { get; init; }
    public required DateTimeOffset At { get; init; }
    public string? Text { get; init; }
    public bool? Truncated { get; init; }
    public ToolCall? Tool { get; init; }
    public DelegateEvent? Delegate { get; init; }
    public Question? Question { get; init; }
    // Strict: an approval is never shown as given, or refused, on an outcome nobody could read.
    [Strict(Strictness.Security)]
    public QuestionOutcome? Outcome { get; init; }
    public string? Answer { get; init; }
    public DateTimeOffset? AnsweredAt { get; init; }
}

// One of the main agent's own delegates starting or reporting back (#661).
public sealed record DelegateEvent
{
    [Strict(Strictness.Identity)]
    public required string Agent { get; init; }
    public required DelegateEventKind Event { get; init; }
    public string? Name { get; init; }
    public SubagentKind? Kind { get; init; }
    public SubagentOutcome? Outcome { get; init; }
}

public static class HistoryProtocol
{
    public const string Op = "session.history";
    public const string Capability = "sessions.history";

    // Null when the host takes the request; otherwise why not.
    public static string? Check(HistoryRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Id)) { return "session.history needs a session id"; }
        if (r.Limit is { } n && n is < 1 or > HistoryLimits.MaxTurns) { return $"limit must be 1 to {HistoryLimits.MaxTurns}"; }
        if (r.Before is { } b && (b.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(b) > HistoryLimits.MaxCursorBytes)) { return $"a cursor is 1 to {HistoryLimits.MaxCursorBytes} bytes"; }
        return null;
    }
}

// Every turn as sent fits HistoryLimits.MaxTurnBytes (v1-history.md, Caps): tools, reasoning,
// earlier responses and delegate events go first, oldest first; then questions' messages and
// fields and answers; then the last response to 4 KiB and the prompt to 2 KiB. Ids, times, the
// prompt's sender, outcomes and flags are never trimmed. Everything dropped is counted in
// PartsMore, and the turn marked Truncated.
public static class HistoryBudget
{
    public static HistoryTurn Fit(HistoryTurn turn, int maxBytes = HistoryLimits.MaxTurnBytes)
    {
        if (ProtocolJson.ByteCount(turn) <= maxBytes) { return turn; }
        var parts = turn.Parts.ToList();
        var sizes = parts.Select(p => ProtocolJson.ByteCount(p) + 1).ToList();
        var dropped = 0;
        // The turn's own fields and flags as they will be once something is dropped.
        var shell = turn with { Parts = [], PartsMore = (turn.PartsMore ?? 0) + 1, Truncated = true };
        var baseBytes = ProtocolJson.ByteCount(shell) + 8;
        bool Fits() => baseBytes + sizes.Sum() <= maxBytes;

        void DropOldest(Func<HistoryPart, int, bool> which)
        {
            for (var i = 0; i < parts.Count && !Fits();)
            {
                if (!which(parts[i], i)) { i++; continue; }
                parts.RemoveAt(i);
                sizes.RemoveAt(i);
                dropped++;
            }
        }

        void Change(Func<HistoryPart, bool> which, Func<HistoryPart, HistoryPart> change)
        {
            for (var i = 0; i < parts.Count && !Fits(); i++)
            {
                if (!which(parts[i])) { continue; }
                parts[i] = change(parts[i]);
                sizes[i] = ProtocolJson.ByteCount(parts[i]) + 1;
            }
        }

        var lastResponse = parts.FindLastIndex(p => p.Kind == HistoryPartKind.Response);
        var lastResponseId = lastResponse >= 0 ? parts[lastResponse].Id : null;
        DropOldest((p, _) => p.Kind == HistoryPartKind.Tool);
        DropOldest((p, _) => p.Kind == HistoryPartKind.Reasoning);
        DropOldest((p, _) => p.Kind == HistoryPartKind.Response && p.Id != lastResponseId);
        DropOldest((p, _) => p.Kind == HistoryPartKind.Delegate);
        Change(p => p.Question is not null, p => p with { Question = p.Question! with { Message = Limits.KeepStart(p.Question.Message, 2 * 1024)!, Truncated = true } });
        Change(p => p.Question is { Fields.Count: > 0 }, p => p with { Question = p.Question! with { Fields = [], Truncated = true } });
        Change(p => p.Answer is not null, p => p with { Answer = null, Truncated = true });
        Change(p => p.Id == lastResponseId, p =>
        {
            var (text, cut) = Limits.KeepEnd(p.Text, 4 * 1024);
            return p with { Text = text, Truncated = cut || p.Truncated == true ? true : null };
        });

        var prompt = turn.Prompt;
        var more = (turn.PartsMore ?? 0) + dropped;
        var result = turn with { Parts = parts, PartsMore = more > 0 ? more : null, Truncated = true };
        if (ProtocolJson.ByteCount(result) > maxBytes && prompt is not null)
        {
            var text = Limits.KeepStart(prompt.Text, 2 * 1024)!;
            result = result with { Prompt = prompt with { Text = text, Truncated = text.Length < prompt.Text.Length || prompt.Truncated == true ? true : null } };
        }
        // Whatever still overflows (only a pathological mix of ids and flags could) goes from the
        // oldest part on, so the turn always fits.
        while (ProtocolJson.ByteCount(result) > maxBytes && result.Parts.Count > 0)
        {
            result = result with { Parts = [.. result.Parts.Skip(1)], PartsMore = (result.PartsMore ?? 0) + 1 };
        }
        return result;
    }

    // The turns that fit one message, newest first as given, at least one: a page holds the newest
    // turns before its cursor, so the oldest are the ones left for the next page.
    public static int FitsInPage(IReadOnlyList<HistoryTurn> newestFirst, int maxBytes = ProtocolJson.MaxMessageBytes)
    {
        var used = HistoryLimits.Envelope;
        var n = 0;
        foreach (var t in newestFirst)
        {
            var size = ProtocolJson.ByteCount(t) + 1;
            if (n > 0 && used + size > maxBytes) { break; }
            used += size;
            n++;
        }
        return n;
    }
}
