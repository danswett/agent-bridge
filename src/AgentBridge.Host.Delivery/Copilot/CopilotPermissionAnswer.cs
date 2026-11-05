using System.Text.Json;
using AgentBridge.Host.Core.Parsing;

namespace AgentBridge.Host.Delivery.Copilot;

// What a session's transcript says just before an approval is typed: whether it could be read,
// the request asked about if it is still waiting (null once completed, or if it never was),
// how many permission requests and ask_user calls are waiting in all, and the line boundary the
// answer is looked for after. Others: every other permission request waiting, in the order
// asked, each described (null where it cannot be read), so the screen can be checked for
// them too. First: the request asked about is the earliest of those waiting (#690).
public sealed record CopilotPermissionState(bool Readable, CopilotPermissionPrompt? Request, int PendingPermissions, int PendingAsks, long ScannedTo)
{
    public IReadOnlyList<(string RequestId, CopilotPermissionPrompt? Prompt)> Others { get; init; } = [];
    public bool First { get; init; }
}

// What the transcript recorded for one permission request after a point: the result kind of
// its permission.completed, whether that carried feedback, whether the session's own turn was
// aborted by the person after it, and whether the session ended.
public sealed record CopilotPermissionResult(string? Kind, bool Feedback, bool SessionEnded, long ScannedTo, bool StoppedAfter = false);

// Reads Copilot's own record of its permission prompts, by request id and never by time.
public static class CopilotPermissionTranscript
{
    // The whole transcript, start to end: a request waits until its permission.completed, or
    // until the session's own abort, shutdown or resume in a new CLI. ask_user calls are counted as CopilotInput
    // counts them, since either kind of prompt holding the session's input changes what a key
    // typed now would answer.
    public static CopilotPermissionState Before(string path, string requestId)
    {
        // In the order asked: a Dictionary's enumeration order is not kept once one is removed.
        var permissions = new List<(string Id, CopilotPermissionPrompt? Prompt)>();
        var asks = new Dictionary<string, string?>(StringComparer.Ordinal);
        if (Jsonl.Lines(path, 0, skipFirst: false, Line) is not { } to)
        {
            return new CopilotPermissionState(false, null, 0, 0, 0);
        }
        var at = permissions.FindIndex(p => p.Id == requestId);
        return new CopilotPermissionState(true, at < 0 ? null : permissions[at].Prompt, permissions.Count, asks.Count, to)
        {
            Others = [.. permissions.Where(p => p.Id != requestId)],
            First = at == 0,
        };

        void Line(ReadOnlySpan<byte> bytes, long start)
        {
            var ask = bytes.IndexOf("\"type\":\"tool.execution_start\""u8) >= 0 && bytes.IndexOf("\"ask_user\""u8) >= 0;
            var permission = bytes.IndexOf("\"type\":\"permission."u8) >= 0;
            var ending = bytes.IndexOf("\"type\":\"abort\""u8) >= 0 || bytes.IndexOf("\"type\":\"session.shutdown\""u8) >= 0 || bytes.IndexOf("\"type\":\"session.resume\""u8) >= 0;
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
                        // Every one is described: the screen is checked for the others too.
                        permissions.RemoveAll(p => p.Id == id);
                        permissions.Add((id, CopilotPermission.Describe(data)));
                        break;
                    case "permission.completed" when CopilotPrompts.Str(data, "requestId") is { } id:
                        permissions.RemoveAll(p => p.Id == id);
                        break;
                    case "abort" or "session.shutdown" or "session.resume" when e.AgentId is null:
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
        var stopped = false;
        var scannedTo = Jsonl.Lines(path, from, skipFirst: false, Line) ?? from;
        return new CopilotPermissionResult(kind, feedback, ended, scannedTo, stopped);

        void Line(ReadOnlySpan<byte> bytes, long start)
        {
            // Escape on the "No (Esc to stop)" row does not record a denial: Copilot 1.0.105
            // records the request as cancelled ("Session aborted") and then aborts the turn, by
            // the user. Seen live on 2026-10-28, so the abort that follows is looked for too.
            if (kind is not null)
            {
                if (!stopped && kind == "cancelled" && bytes.IndexOf("\"type\":\"abort\""u8) >= 0 && CopilotPrompts.Parse(bytes) is { } a)
                {
                    using (a.Doc)
                    {
                        stopped = a.Type == "abort" && a.AgentId is null;
                    }
                }
                return;
            }
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

    // Another permission request waiting beside this one: a name for a person, everything Copilot
    // was given for it as written (CopilotPermissionPrompt.Drawn, empty if unreadable), and the
    // dialog Copilot draws for it, if known.
    public sealed record Other(string Name, IReadOnlyList<string> Drawn, CopilotDialog? Dialog = null);

    // While others wait: the dialog Copilot draws for this prompt, if known, what it was given,
    // and the others.
    public sealed record Crowd(CopilotDialog? Dialog, IReadOnlyList<string> Drawn, IReadOnlyList<Other> Others);

    // probes: CopilotPermissionPrompt.Probes, what this prompt is about. crowd: set when other
    // permission requests are waiting too.
    public static (Choice? Choice, string? Problem) Locate(IReadOnlyList<string> rows, bool approve, IReadOnlyList<string> probes, Crowd? crowd = null)
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
        var top = Top(rows, block[0].Row, probes);
        if (crowd is { Others.Count: > 0 })
        {
            var (start, why) = Crowded(rows, block[0].Row, end, crowd);
            if (why is not null) { return (null, why); }
            top = start;
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

    // Copilot can have several requests waiting and draws one of them (#690). Neither the order
    // asked nor a probe says which: a probe is agent-written text, which another request's command
    // can end in, and a probe not found says nothing of where another is drawn. While others wait,
    // the prompt on screen is identified positively, as Copilot CLI 1.0.105 draws it
    // (CopilotDialog), or nothing is typed:
    //   * no waiting request holds a line-drawing character, a tab or anything drawn other than
    //     as it runs (ShownText.Misleads), so nothing given to the agent can draw a part of
    //     Copilot's frame or hide what it draws;
    //   * the options are inside one round frame, every row from its top edge to the options
    //     inside it, the edge on screen;
    //   * and every non-blank row in it above the options is this prompt's dialog: its title,
    //     the rule, each part of its body (a box's lines one to a row, exactly as given, so a
    //     wrapped line is never taken for two), and its question, with nothing else.
    // Start: the frame's top edge, where the key's hold begins.
    static (int Start, string? Problem) Crowded(IReadOnlyList<string> rows, int options, int end, Crowd crowd)
    {
        foreach (var other in crowd.Others)
        {
            if (other.Drawn.Count == 0) { return (-1, $"another waiting permission request ({other.Name}) cannot be checked against the session's screen"); }
        }
        if (crowd.Others.Select(o => o.Drawn).Prepend(crowd.Drawn).Any(d => d.Any(CopilotDialogs.Unsafe)))
        {
            return (-1, "a waiting permission holds characters that could be drawn as part of Copilot's own dialog or hide what it draws, so the screen cannot say which is shown");
        }
        if (crowd.Dialog is not { } mine) { return (-1, "the host cannot tell this kind of permission prompt from the others waiting on the session's screen"); }
        // One that would be drawn the same cannot be told from this one however the screen reads:
        // a box wraps a long line hard at its width, so one line drawn on two rows reads like two
        // lines, and two identical requests read alike (Trust review of #769).
        if (crowd.Others.FirstOrDefault(o => o.Dialog is { } theirs && CopilotDialogs.Alike(mine, theirs)) is { } twin)
        {
            return (-1, $"another waiting permission ({twin.Name}) would be drawn the same, so the screen cannot say which is shown");
        }
        if (Frame(rows, options, end) is not { } frame) { return (-1, "the session's screen does not show the prompt's whole frame, so it cannot say which waiting permission it is"); }
        if (Shows(rows, frame, options, mine)) { return (frame.Top, null); }
        var shown = crowd.Others.FirstOrDefault(o => o.Dialog is { } theirs && Shows(rows, frame, options, theirs));
        return (-1, shown is not null
            ? $"the machine is showing a different permission ({shown.Name})"
            : "the session's screen does not show this permission as Copilot draws it, so it cannot say which waiting permission it is");
    }

    // The round frame around the options: its left and right edge columns, the same on every row
    // from its top edge (a corner, a rule and a corner, nothing else on the row) down through the
    // options. Null if any row of it is not so, or its top edge is not on screen.
    static (int Top, int Left, int Right)? Frame(IReadOnlyList<string> rows, int options, int end)
    {
        var first = rows[options].TrimEnd();
        var left = first.IndexOf('\u2502');
        var right = first.Length - 1;
        if (left < 0 || right < left + 4 || first[right] != '\u2502' || first[..left].Trim().Length > 0) { return null; }
        bool Edged(string row, char l, char r, Func<string, bool> inside)
        {
            var t = row.TrimEnd();
            return t.Length == right + 1 && t[left] == l && t[right] == r && t[..left].Trim().Length == 0 && inside(t[(left + 1)..right]);
        }
        bool Framed(string row) => Edged(row, '\u2502', '\u2502', _ => true);
        for (var i = options; i <= end; i++)
        {
            if (!Framed(rows[i])) { return null; }
        }
        var top = options - 1;
        while (top >= 0 && Framed(rows[top])) { top--; }
        return top >= 0 && Edged(rows[top], '\u256D', '\u256E', mid => mid.All(c => c == '\u2500')) ? (top, left, right) : null;
    }

    // Whether the frame holds exactly this dialog above its options, as a screen can show it
    // (CopilotDialogs.Visible: a text part's blank rows are not told from the gaps between parts).
    static bool Shows(IReadOnlyList<string> rows, (int Top, int Left, int Right) frame, int options, CopilotDialog drawn)
    {
        var dialog = CopilotDialogs.Visible(drawn);
        var mids = Enumerable.Range(frame.Top + 1, options - frame.Top - 1).Select(r => rows[r].TrimEnd()[(frame.Left + 1)..frame.Right]).ToList();
        var i = 0;
        var title = new List<string>();
        for (; i < mids.Count && !Rule(mids[i]); i++)
        {
            if (mids[i].Trim().Length > 0) { title.Add(mids[i]); }
        }
        if (i == mids.Count || !Reads(title, dialog.Title)) { return false; }
        i++;
        foreach (var part in dialog.Body)
        {
            while (i < mids.Count && mids[i].Trim().Length == 0) { i++; }
            if (part.Boxed)
            {
                if (i == mids.Count || !Edge(mids[i], '\u256D', '\u256E')) { return false; }
                var lines = new List<string>();
                for (i++; i < mids.Count && !Edge(mids[i], '\u2570', '\u256F'); i++)
                {
                    if (Boxed(mids[i]) is not { } line) { return false; }
                    lines.Add(line);
                }
                if (i == mids.Count || !lines.SequenceEqual(part.Lines.Select(l => l.TrimEnd()), StringComparer.Ordinal)) { return false; }
                i++;
            }
            else if (!Reads(Text(), part.Lines[0])) { return false; }
        }
        while (i < mids.Count && mids[i].Trim().Length == 0) { i++; }
        if (!Reads(Text(), dialog.Question)) { return false; }
        while (i < mids.Count && mids[i].Trim().Length == 0) { i++; }
        return i == mids.Count;

        // The rows of a part drawn as text, up to a blank row.
        List<string> Text()
        {
            var text = new List<string>();
            for (; i < mids.Count && mids[i].Trim().Length > 0; i++) { text.Add(mids[i]); }
            return text;
        }
    }

    // Text wrapped onto rows reads as it was given, its runs of white space aside.
    static bool Reads(List<string> drawn, string text) =>
        string.Equals(Collapse(string.Join(' ', drawn)), Collapse(text), StringComparison.Ordinal);

    static string Collapse(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    static bool Rule(string mid) => mid.Trim() is { Length: >= 3 } s && s.All(c => c == '\u2500');

    static bool Edge(string mid, char l, char r) => mid.Trim() is { Length: >= 2 } s && s[0] == l && s[^1] == r && s[1..^1].All(c => c == '\u2500');

    // One row of a box inside the frame: its line, after the box's one column of padding.
    static string? Boxed(string mid)
    {
        var m = mid.TrimEnd();
        var a = m.IndexOf('\u2502');
        if (a < 0 || m[..a].Trim().Length > 0 || m.Length < a + 2 || m[^1] != '\u2502' || m.Length - 1 == a) { return null; }
        var inner = m[(a + 1)..^1];
        return inner.Length == 0 ? "" : inner[0] == ' ' ? inner[1..].TrimEnd() : null;
    }

    // The nearest row above the options from which the rows down to them show one of the
    // probes, within 30 rows; -1 if none does.
    static int Top(IReadOnlyList<string> rows, int options, IReadOnlyList<string> probes)
    {
        var wanted = probes.Select(Squeeze).Where(s => s.Length > 0).ToList();
        if (wanted.Count == 0) { return -1; }
        var above = "";
        for (var i = options - 1; i >= Math.Max(0, options - 30); i--)
        {
            above = Squeeze(Clean(rows[i]).Text) + above;
            if (wanted.Any(w => above.Contains(w, StringComparison.Ordinal))) { return i; }
        }
        return -1;
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
