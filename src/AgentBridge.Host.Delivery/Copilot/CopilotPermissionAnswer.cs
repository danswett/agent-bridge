using System.Text.Json;
using AgentBridge.Host.Core.Parsing;

namespace AgentBridge.Host.Delivery.Copilot;

// What a session's transcript says just before an approval is typed: whether it could be read,
// the request asked about if it is still waiting (null once completed, or if it never was),
// how many permission requests and ask_user calls are waiting in all, and the line boundary the
// answer is looked for after.
public sealed record CopilotPermissionState(bool Readable, CopilotPermissionPrompt? Request, int PendingPermissions, int PendingAsks, long ScannedTo);

// What the transcript recorded for one permission request after a point: the result kind of
// its permission.completed, whether that carried feedback, and whether the session ended.
public sealed record CopilotPermissionResult(string? Kind, bool Feedback, bool SessionEnded, long ScannedTo);

// Reads Copilot's own record of its permission prompts, by request id and never by time.
public static class CopilotPermissionTranscript
{
    // The whole transcript, start to end: a request waits until its permission.completed, or
    // until the session's own abort or shutdown. ask_user calls are counted as CopilotInput
    // counts them, since either kind of prompt holding the session's input changes what a key
    // typed now would answer.
    public static CopilotPermissionState Before(string path, string requestId)
    {
        var permissions = new Dictionary<string, CopilotPermissionPrompt?>(StringComparer.Ordinal);
        var asks = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (Jsonl.Lines(path, 0, skipFirst: false, Line) is not { } to)
        {
            return new CopilotPermissionState(false, null, 0, 0, 0);
        }
        permissions.TryGetValue(requestId, out var request);
        return new CopilotPermissionState(true, request, permissions.Count, asks.Count, to);

        void Line(ReadOnlySpan<byte> bytes, long start)
        {
            var ask = bytes.IndexOf("\"type\":\"tool.execution_start\""u8) >= 0 && bytes.IndexOf("\"ask_user\""u8) >= 0;
            var permission = bytes.IndexOf("\"type\":\"permission."u8) >= 0;
            var ending = bytes.IndexOf("\"type\":\"abort\""u8) >= 0 || bytes.IndexOf("\"type\":\"session.shutdown\""u8) >= 0;
            var answer = asks.Count > 0 && (bytes.IndexOf("\"type\":\"tool.execution_complete\""u8) >= 0 || bytes.IndexOf("\"type\":\"human_response.recorded\""u8) >= 0);
            if (!ask && !permission && !ending && !answer) { return; }
            if (CopilotPrompts.Parse(bytes) is not { } e) { return; }
            using (e.Doc)
            {
                var data = e.Data;
                switch (e.Type)
                {
                    case "tool.execution_start" when CopilotPrompts.Str(data, "toolName") == "ask_user" && CopilotPrompts.Str(data, "toolCallId") is { } id:
                        asks[id] = e.AgentId;
                        break;
                    case "tool.execution_complete" or "human_response.recorded" when CopilotPrompts.Str(data, "toolCallId") is { } id:
                        asks.Remove(id);
                        break;
                    case "permission.requested" when CopilotPrompts.Str(data, "requestId") is { } id:
                        // Only the request asked about is described; the others are counted.
                        permissions[id] = id == requestId ? CopilotPermission.Describe(data) : null;
                        break;
                    case "permission.completed" when CopilotPrompts.Str(data, "requestId") is { } id:
                        permissions.Remove(id);
                        break;
                    case "abort" or "session.shutdown" when e.AgentId is null:
                        asks.Clear();
                        permissions.Clear();
                        break;
                    case "abort" or "session.shutdown":
                        foreach (var id in asks.Where(a => a.Value == e.AgentId).Select(a => a.Key).ToList()) { asks.Remove(id); }
                        break;
                }
            }
        }
    }

    public static CopilotPermissionResult Since(string path, string requestId, long from)
    {
        string? kind = null;
        var feedback = false;
        var ended = false;
        var scannedTo = Jsonl.Lines(path, from, skipFirst: false, Line) ?? from;
        return new CopilotPermissionResult(kind, feedback, ended, scannedTo);

        void Line(ReadOnlySpan<byte> bytes, long start)
        {
            if (kind is not null) { return; }
            var completed = bytes.IndexOf("\"type\":\"permission.completed\""u8) >= 0;
            if (!completed && bytes.IndexOf("\"type\":\"session.shutdown\""u8) < 0) { return; }
            if (CopilotPrompts.Parse(bytes) is not { } e) { return; }
            using (e.Doc)
            {
                if (e.Type == "session.shutdown" && e.AgentId is null) { ended = true; return; }
                if (e.Type != "permission.completed" || CopilotPrompts.Str(e.Data, "requestId") != requestId) { return; }
                var result = e.Data.TryGetProperty("result", out var r) && r.ValueKind == JsonValueKind.Object ? r : default;
                kind = CopilotPrompts.Str(result, "kind") ?? "";
                feedback = CopilotPrompts.Str(result, "feedback") is { Length: > 0 };
            }
        }
    }
}

// Where on the session's screen the prompt's answers are, read from the rows themselves. The
// options of a Copilot permission prompt vary with its kind and with what the CLI may offer
// (a "don't ask again" or "remember this path" entry, a read-only grant, sometimes none), and
// the one selected when it opens is not always the first, so nothing here assumes a position:
//
//   * The options are the last block of numbered rows on screen ("1. Yes", "2. ..."), numbered
//     from 1 without a gap, the way both of the CLI's option lists draw them.
//   * Approve is the one row whose label is exactly "Yes": the CLI's approve-once in every
//     permission prompt. Every "Yes, and ..." grants more than this request and is never
//     chosen. The key is that row's digit, which commits it directly in both lists whatever
//     is highlighted; Enter, which commits the highlighted row, is never pressed.
//   * Deny is Escape, and only when the last option, the "No" one, is labelled as Escape's
//     ("(Esc)" or "(Esc to stop)"): both lists then deny with no message to Copilot.
//
// Anything else (no options, more than nine, no plain Yes or two, a last option that is not a
// No, nothing above the options showing what the prompt is for) is not a prompt this host
// can read, and nothing is typed.
public static class CopilotPermissionScreen
{
    // Key: what to type. Row: the option it answers. Hold: every row of the options, as read,
    // which the worker checks again when the key goes, so a change of highlight anywhere in
    // the list (the No row's message box opened at the machine, say) stops it.
    public sealed record Choice(string Key, int Row, IReadOnlyList<ScreenRow> Hold);

    // probes: CopilotPermissionPrompt.Probes, what this prompt is about.
    public static (Choice? Choice, string? Problem) Locate(IReadOnlyList<string> rows, bool approve, IReadOnlyList<string> probes)
    {
        var numbered = new List<(int Row, int Number, string Label, bool Selected)>();
        for (var i = 0; i < rows.Count; i++)
        {
            if (Item(rows[i]) is { } item) { numbered.Add((i, item.Number, item.Label, item.Selected)); }
        }
        if (numbered.Count == 0) { return (null, "the session's screen shows no permission prompt"); }

        // From the last numbered row up, each the one before it, no blank row between.
        var block = new List<(int Row, int Number, string Label, bool Selected)> { numbered[^1] };
        for (var k = numbered.Count - 2; k >= 0 && block[0].Number > 1; k--)
        {
            var (row, number, _, _) = numbered[k];
            if (number != block[0].Number - 1 || block[0].Row - row > 4 || Blank(rows, row + 1, block[0].Row)) { break; }
            block.Insert(0, numbered[k]);
        }
        if (block[0].Number != 1 || block.Count < 2) { return (null, "the prompt's options on the session's screen could not be read"); }
        if (block.Count > 9) { return (null, "the prompt has more options than can be chosen by a digit"); }

        var last = block[^1];
        var (lastLabel, end) = Continued(rows, last.Row, last.Label);
        // What the prompt is about, drawn above its options: the nearest rows that together
        // show one of the probes, whitespace aside, since a long command wraps. Held from
        // there down, so another prompt with the same options in its place (the next path
        // request, say) stops the key.
        var wanted = probes.Select(Squeeze).Where(s => s.Length > 0).ToList();
        var top = -1;
        var above = "";
        for (var i = block[0].Row - 1; i >= Math.Max(0, block[0].Row - 30) && top < 0; i--)
        {
            above = Squeeze(Clean(rows[i]).Text) + above;
            if (wanted.Any(w => above.Contains(w, StringComparison.Ordinal))) { top = i; }
        }
        if (top < 0) { return (null, "the session's screen does not show what this permission prompt is for"); }
        var hold = Enumerable.Range(top, end - top + 1).Select(r => new ScreenRow(r, rows[r])).ToList();
        if (!lastLabel.StartsWith("No", StringComparison.Ordinal)) { return (null, "the prompt on the session's screen does not end with a No"); }
        // The No row selected is its message box open at the machine: a digit would be typed
        // into it, and Escape would send whatever is there.
        if (last.Selected) { return (null, "someone at the machine has the prompt's No selected"); }

        if (!approve)
        {
            if (!lastLabel.Contains("(Esc", StringComparison.Ordinal)) { return (null, "the prompt on the session's screen does not take Escape for No"); }
            return (new Choice(KeyPlan.Escape, last.Row, hold), null);
        }
        // A plain "Yes" that runs on to the next row is a longer label wrapped, not this one.
        var yes = block.Take(block.Count - 1)
            .Where((b, i) => b.Label == "Yes" && block[i + 1].Row == b.Row + 1)
            .ToList();
        if (yes.Count != 1) { return (null, "the prompt on the session's screen has no single plain Yes to approve once"); }
        var chosen = yes[0];
        return (new Choice(chosen.Number.ToString(System.Globalization.CultureInfo.InvariantCulture), chosen.Row, hold), null);
    }

    static (int Number, string Label, bool Selected)? Item(string row)
    {
        var (s, selected) = Clean(row);
        var dot = s.IndexOf(". ", StringComparison.Ordinal);
        if (dot is < 1 or > 2 || !s[..dot].All(char.IsAsciiDigit)) { return null; }
        var number = int.Parse(s[..dot], System.Globalization.CultureInfo.InvariantCulture);
        return number < 1 ? null : (number, s[(dot + 2)..].Trim(), selected);
    }

    // The last option's label with the rows it wraps onto, up to a blank row or three rows,
    // and the last of those rows.
    static (string Label, int End) Continued(IReadOnlyList<string> rows, int row, string label)
    {
        var end = row;
        for (var i = row + 1; i < rows.Count && i <= row + 3; i++)
        {
            var more = Clean(rows[i]).Text;
            if (more.Length == 0 || Item(rows[i]) is not null) { break; }
            label += " " + more;
            end = i;
        }
        return (label, end);
    }

    static bool Blank(IReadOnlyList<string> rows, int from, int to)
    {
        for (var i = from; i < to; i++)
        {
            if (Clean(rows[i]).Text.Length == 0) { return true; }
        }
        return false;
    }

    // A row without its frame and selection marker: box-drawing characters, the pointer the
    // CLI draws beside the highlighted row (U+276F), or the screen-reader text that replaces
    // it. Selected says the row carried the pointer.
    static (string Text, bool Selected) Clean(string row)
    {
        var s = row.AsSpan().Trim();
        while (s.Length > 0 && (char.IsWhiteSpace(s[0]) || Box(s[0]))) { s = s[1..]; }
        const string Reader = "Current selection:";
        var selected = s.StartsWith("\u276F", StringComparison.Ordinal) || s.StartsWith(Reader, StringComparison.Ordinal);
        if (s.StartsWith(Reader, StringComparison.Ordinal)) { s = s[Reader.Length..]; }
        while (s.Length > 0 && (char.IsWhiteSpace(s[0]) || Decoration(s[0]))) { s = s[1..]; }
        while (s.Length > 0 && (char.IsWhiteSpace(s[^1]) || Box(s[^1]))) { s = s[..^1]; }
        return (s.ToString(), selected);
    }

    static string Squeeze(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    static bool Box(char c) => c is >= '\u2500' and <= '\u259F';

    static bool Decoration(char c) => Box(c) || c is '\u276F' or '\u203A' or '>' or '\u25B6' or '\u25BA' or '\u27A4' or '\u2192' or '\u25CF' or '\u25C9' or '\u25CB' or '\u25CE' or '\u2022' or '*' or '\u00B7' or '|';
}
