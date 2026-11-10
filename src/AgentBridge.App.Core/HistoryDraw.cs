using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// One stored turn as the conversation draws it (ui.md 6.4 History, H0): what started it, the main
// agent's response as blocks, and a meta line when the turn could not be read in full.
public sealed record HistoryRow(string Id, HistoryDisclosure? Disclosure, string? Prompt, IReadOnlyList<ConversationBlock> Blocks, string? Note, HistoryTimes? Times = null)
{
    // Where each time is drawn (ui.md 6.4 Time on messages): a prompt you didn't type has its time on
    // its label line, yours under it at the trailing edge; the reply's is above its text. Null draws nothing.
    public MessageStamp? PromptBeside => Disclosure is null ? null : Times?.Prompt;
    public MessageStamp? PromptUnder => Disclosure is null ? Times?.Prompt : null;
    public MessageStamp? Reply => Times?.Reply;
}

// A message's time as drawn, and its exact local time with seconds: what a tap, hover or focus
// reveals, and the accessible name (fixed, so a screen reader never hears it tick).
public sealed record MessageStamp(string Text, string Exact);

public sealed record HistoryTimes(MessageStamp? Prompt, MessageStamp? Reply);
// A prompt the person didn't type, collapsed as the live card's is (#1611): its origin line and one
// line of it, and Show prompt for the rest. Open or shut is its own, kept by the turn's id while
// the turn is drawn again.
public sealed class HistoryDisclosure(PromptOrigin origin) : Observable
{
    bool open;

    public PromptOrigin Origin { get; } = origin;
    public bool IsDriven => !Origin.Schedule;
    public bool HasBy => Origin.By is not null;

    public bool Open
    {
        get => open;
        set { if (open != value) { open = value; Raise(); Raise(nameof(ToggleName)); Raise(nameof(ToggleText)); } }
    }

    public string ToggleName => Origin.DisclosureName(open);
    public string ToggleText => open ? "Hide prompt" : "Show prompt";
}

// What the pane draws from SessionHistory, read-only (#292 W2a): the stored turns oldest first, at
// most HistoryLimits.Kept of them, without the latest exchange the pane already draws from the live card.
public static class HistoryDraw
{
    public const string Empty = "No messages yet";
    public const string Loading = "Loading conversation…";
    public const string Failed = "Couldn't load earlier messages";
    public const string NoHistory = "Earlier messages aren't available from this machine";
    public const string Unreadable = "A turn here couldn't be read";
    public const string CutShort = "Cut short · see the machine";

    // The turns to draw. The newest held turn is the live exchange when its prompt is the card's
    // (the card draws it, so it is not drawn twice); no live prompt, as for a session just begun, drops nothing.
    public static IReadOnlyList<HistoryRow> Rows(IReadOnlyList<HistoryTurn> turns, string? livePrompt, IDictionary<string, HistoryDisclosure>? kept = null, MessageClock? clock = null)
    {
        clock ??= MessageClock.Local;
        var count = turns.Count;
        if (count > 0 && !string.IsNullOrWhiteSpace(livePrompt) && turns[^1].Prompt is { } last && last.Text.Trim() == livePrompt.Trim()) { count--; }
        var from = Math.Max(0, count - HistoryLimits.Kept);
        var rows = new List<HistoryRow>(count - from);
        for (var i = from; i < count; i++) { rows.Add(Row(turns[i], kept, clock)); }
        return rows;
    }

    static HistoryRow Row(HistoryTurn turn, IDictionary<string, HistoryDisclosure>? kept, MessageClock clock)
    {
        if (turn.Unreadable == true) { return new(turn.Id, null, null, [], Unreadable); }
        var blocks = new List<ConversationBlock>();
        var cut = turn.Truncated == true || turn.Prompt?.Truncated == true;
        DateTimeOffset? replyAt = null;
        foreach (var part in turn.Parts)
        {
            if (part.Kind != HistoryPartKind.Response || string.IsNullOrEmpty(part.Text)) { continue; }
            cut |= part.Truncated == true;
            replyAt ??= part.At;
            blocks.AddRange(MarkdownModel.Parse(part.Text).Select(b => new ConversationBlock(b)));
        }
        // A prompt has no time of its own and takes its turn's; a reply takes its first part's.
        var times = new HistoryTimes(turn.Prompt is null ? null : clock.Stamp(turn.At), replyAt is { } at ? clock.Stamp(at) : null);
        if (PromptOrigin.OfHistory(turn.Prompt) is not { } origin) { return new(turn.Id, null, turn.Prompt?.Text, blocks, cut ? CutShort : null, times); }
        if (kept is null || !kept.TryGetValue(turn.Id, out var disclosure) || disclosure.Origin.Text != origin.Text || disclosure.Origin.Label != origin.Label)
        {
            disclosure = new HistoryDisclosure(origin);
            if (kept is not null) { kept[turn.Id] = disclosure; }
        }
        return new(turn.Id, disclosure, null, blocks, cut ? CutShort : null, times);
    }

    // The one meta line above the stored turns: what the history holds, or why it holds nothing.
    // Turns held are always drawn, so a machine that has gone offline still shows what the store has.
    public static string? Line(HistoryLoad state, int drawn, bool hasLivePrompt, HistoryStart? start) => state switch
    {
        HistoryLoad.Loading when drawn == 0 => Loading,
        HistoryLoad.Failed => Failed,
        HistoryLoad.NoHistory => NoHistory,
        HistoryLoad.StartReached when start?.Limited == true => "Start of available history",
        HistoryLoad.StartReached when start?.At is { } at => $"Start of session · {at.ToLocalTime().ToString("ddd, MMM d, h:mm tt", System.Globalization.CultureInfo.CurrentCulture)}",
        HistoryLoad.StartReached => null,
        _ when drawn == 0 && !hasLivePrompt && state == HistoryLoad.Idle => Empty,
        _ => null,
    };
}
