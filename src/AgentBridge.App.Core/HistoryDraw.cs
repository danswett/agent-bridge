using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// One stored turn as the conversation draws it (ui.md 6.4 History, H0): what started it, the main
// agent's response as blocks, and a meta line when the turn could not be read in full.
public sealed record HistoryRow(string Id, string? PromptLabel, string? Prompt, IReadOnlyList<ConversationBlock> Blocks, string? Note);

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
    public static IReadOnlyList<HistoryRow> Rows(IReadOnlyList<HistoryTurn> turns, string? livePrompt)
    {
        var count = turns.Count;
        if (count > 0 && !string.IsNullOrWhiteSpace(livePrompt) && turns[^1].Prompt is { } last && last.Text.Trim() == livePrompt.Trim()) { count--; }
        var from = Math.Max(0, count - HistoryLimits.Kept);
        var rows = new List<HistoryRow>(count - from);
        for (var i = from; i < count; i++) { rows.Add(Row(turns[i])); }
        return rows;
    }

    static HistoryRow Row(HistoryTurn turn)
    {
        if (turn.Unreadable == true) { return new(turn.Id, null, null, [], Unreadable); }
        var blocks = new List<ConversationBlock>();
        var cut = turn.Truncated == true || turn.Prompt?.Truncated == true;
        foreach (var part in turn.Parts)
        {
            if (part.Kind != HistoryPartKind.Response || string.IsNullOrEmpty(part.Text)) { continue; }
            cut |= part.Truncated == true;
            blocks.AddRange(MarkdownModel.Parse(part.Text).Select(b => new ConversationBlock(b)));
        }
        var label = turn.Prompt?.From switch
        {
            UserMessageFrom.Schedule => "Scheduled prompt",
            UserMessageFrom.Agent => "Sent by an agent",
            UserMessageFrom.Other => "Automatic prompt",
            _ => null,
        };
        return new(turn.Id, label, turn.Prompt?.Text, blocks, cut ? CutShort : null);
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
