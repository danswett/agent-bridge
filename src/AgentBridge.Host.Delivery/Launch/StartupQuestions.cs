using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Launch;

// A question an agent can stop on at startup, before its session registers, which only a person
// at the machine answers. This host never types into one: it reads the window, says so, and
// turns other launches away (busy) while a trust question waits, as agent-ha-bridge does for
// Claude's. Labels are matched as each agent draws them.
// BypassPermissions: Claude's one-time "Bypass Permissions mode" warning, which
// --dangerously-skip-permissions (Allow all) raises. It is not a folder-trust question, and is
// never shown or answered as one (#149): a person sent to the machine for a trust question found
// this riskier one instead.
public enum StartupQuestion { None, Trust, HooksReview, BypassPermissions }

public static class StartupQuestions
{
    public static StartupQuestion Read(IReadOnlyList<string>? rows)
    {
        if (rows is null || rows.Count == 0) { return StartupQuestion.None; }
        var screen = string.Join('\n', rows);
        bool Has(string s) => screen.Contains(s, StringComparison.Ordinal);
        // Copilot CLI's folder trust dialog ("Confirm folder trust", 1.0.105).
        if (Has("Do you trust the files in this folder?") || Has("Confirm folder trust")) { return StartupQuestion.Trust; }
        // Claude's folder trust question.
        if (Has("Yes, I trust this folder")) { return StartupQuestion.Trust; }
        // The Bypass Permissions warning that --dangerously-skip-permissions itself raises
        // ("WARNING: Claude Code running in Bypass Permissions mode", No, exit / Yes, I accept).
        if (Has("Yes, I accept") && Has("No, exit")) { return StartupQuestion.BypassPermissions; }
        // Codex's folder trust question ("Folder access ... Trust this folder? ... 1. Trust and
        // continue / 2. Quit"), drawn before its session exists: seen live on a resume into a
        // folder Codex had not been trusted in, which went unrecognised and so was not busy.
        if (Has("Trust this folder?") && Has("Trust and continue")) { return StartupQuestion.Trust; }
        // Codex asks, in its window, for hooks to be trusted (agent-ha-bridge's BlockingPrompt).
        if (Has("Hooks need review")) { return StartupQuestion.HooksReview; }
        return StartupQuestion.None;
    }

    // Whether Copilot's window is ready for a prompt: its input box (a row opened by '╻' over
    // one of '┃') or its key footer ("/ commands"), with no startup question over it. Neither
    // is drawn while its folder-trust question is (1.0.105, seen live).
    public static bool CopilotReady(IReadOnlyList<string>? rows)
    {
        if (rows is null || Read(rows) != StartupQuestion.None) { return false; }
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i].TrimStart();
            if (row.Contains(" / commands ", StringComparison.Ordinal)) { return true; }
            if (row.StartsWith('\u257B') && i + 1 < rows.Count && rows[i + 1].TrimStart().StartsWith('\u2503')) { return true; }
        }
        return false;
    }

    // A folder-trust question read in full, to show a person and to answer: the agent's own
    // words, its options as they are drawn (rows of the window), which is highlighted, and which
    // one trusts. Hold is what must still be on the screen, row for row, when a key is sent: the
    // folder and every option, highlight included.
    public sealed record TrustScreen(string? Root, string? RootNote, string? Warning, IReadOnlyList<int> Options, int Selected, int TrustAt, IReadOnlyList<ScreenRow> Hold)
    {
        // The agent's words in full, which Warning may cut: what tells two questions apart.
        public string? Words { get; init; }
    }

    // Each agent's options, as drawn after its highlight and number: the one that trusts, and
    // every other it offers. Copilot's "Yes" trusts for this session only; remembering the folder
    // for later sessions is left to a person at the machine.
    static (string Trust, string[] All)? OptionsOf(AgentKind kind) => kind switch
    {
        AgentKind.Copilot => ("Yes", ["Yes", "Yes, and remember this folder for future sessions", "No (Esc)"]),
        AgentKind.Claude => ("Yes, I trust this folder", ["Yes, I trust this folder", "No, exit"]),
        AgentKind.Codex => ("Trust and continue", ["Trust and continue", "Quit"]),
        _ => null,
    };

    // What each agent says trusting lets it do, every sentence of it as it draws it (live, Copilot
    // 1.0.105, Claude Code, Codex 0.165): a read missing any of it, its end included, is not the
    // whole question, and a person is not asked to answer part of one. An agent that changes its
    // words leaves its question to be answered at the machine until this learns them.
    static string[] WarningOf(AgentKind kind) => kind switch
    {
        AgentKind.Copilot => ["Copilot can read files in this folder and, with your permission, edit them or run code and shell commands.",
            "It will remember your permissions for the rest of this session.", "Do you trust the files in this folder?"],
        AgentKind.Claude => ["Quick safety check: Is this a project you created or one you trust?",
            "(Like your own code, a well-known open source project, or work from your team).",
            "If not, take a moment to review what's in this folder first.", "be able to read, edit, and execute files here."],
        AgentKind.Codex => ["Trust this folder? Codex can read, edit, and run files here, subject to your permission settings.",
            "Folder settings can run code automatically, even without a model request.", "Continue only if you trust these files.",
            "Your trust decision will be saved."],
        _ => [],
    };

    // Links and footers drawn beside the question, which are not its words.
    static readonly string[] NotWarning = ["Security guide"];

    // The question in the window, when it is the folder-trust question for exactly this folder
    // and every part of it reads as expected; otherwise null, and it is answered at the machine.
    // Claude's bypass-permissions warning is not a folder question, and is never read as one.
    public static TrustScreen? ReadTrust(AgentKind kind, IReadOnlyList<string>? rows, string folder)
    {
        if (rows is null || Read(rows) != StartupQuestion.Trust || OptionsOf(kind) is not { } known || folder.Length == 0) { return null; }
        var clean = rows.Select(Clean).ToList();

        // The options: one block of rows, each one the agent offers exactly once, exactly one
        // highlighted.
        var options = new List<int>();
        var labels = new HashSet<string>(StringComparer.Ordinal);
        var selected = -1;
        var trustAt = -1;
        for (var i = 0; i < clean.Count; i++)
        {
            if (Option(clean[i]) is not { } option) { continue; }
            if (!known.All.Contains(option.Label, StringComparer.Ordinal))
            {
                // A numbered or highlighted row that is not one of its options: the question is
                // not the one known.
                if (option.Numbered || option.Highlighted) { return null; }
                continue;
            }
            if (!labels.Add(option.Label)) { return null; }
            if (options.Count > 0 && options[^1] != i - 1) { return null; }
            if (option.Highlighted)
            {
                if (selected >= 0) { return null; }
                selected = options.Count;
            }
            if (option.Label == known.Trust) { trustAt = options.Count; }
            options.Add(i);
        }
        if (options.Count != known.All.Length || selected < 0 || trustAt < 0) { return null; }

        // Paragraphs above the options: rows between blank ones.
        var paragraphs = new List<(int First, int Last)>();
        for (var i = 0; i < options[0]; i++)
        {
            if (clean[i].Length == 0) { continue; }
            var first = i;
            while (i + 1 < options[0] && clean[i + 1].Length > 0) { i++; }
            paragraphs.Add((first, i));
        }
        // The folder, drawn whole on rows of its own (wrapped, if it is long), under its heading
        // or apart from it.
        var at = -1;
        var from = -1;
        for (var k = 0; k < paragraphs.Count && at < 0; k++)
        {
            // Never the repository root under Codex's note: for a launch at the root, a question
            // about a folder under it would otherwise read as this folder's.
            if (Enumerable.Range(paragraphs[k].First, paragraphs[k].Last - paragraphs[k].First + 1).Any(r => clean[r].EndsWith("repository root:", StringComparison.Ordinal))) { continue; }
            for (var s = paragraphs[k].First; s <= paragraphs[k].Last; s++)
            {
                if (string.Equals(Joined(clean, s, paragraphs[k].Last), folder, StringComparison.OrdinalIgnoreCase)) { at = k; from = s; break; }
            }
        }
        if (at < 0) { return null; }

        string? root = null, note = null;
        var warning = new List<string>();
        for (var k = at + 1; k < paragraphs.Count; k++)
        {
            var (first, last) = paragraphs[k];
            // Codex: "Note: ... Trusting will apply to the repository root:" and that folder.
            var noteEnd = Enumerable.Range(first, last - first + 1).FirstOrDefault(r => clean[r].EndsWith("repository root:", StringComparison.Ordinal), -1);
            if (noteEnd >= 0)
            {
                note = string.Join(' ', clean.Skip(first).Take(noteEnd - first + 1));
                // The root is drawn under the note, in the same block; a note without it is not
                // a question a person can be shown in full.
                root = noteEnd < last ? Joined(clean, noteEnd + 1, last) : null;
                if (root is not { Length: > 0 }) { return null; }
                continue;
            }
            var para = string.Join(' ', clean.Skip(first).Take(last - first + 1));
            if (!NotWarning.Contains(para, StringComparer.Ordinal)) { warning.Add(para); }
        }
        var words = string.Join("\n", warning);
        if (warning.Count == 0 || WarningOf(kind).Any(w => !words.Contains(w, StringComparison.Ordinal))) { return null; }
        var text = Limits.KeepStart(words, TrustProtocol.MaxText);
        // Everything from the folder to the last option, blank rows too: the folder, the root, the
        // agent's words and every option, highlight included, as they were read.
        var hold = Enumerable.Range(from, options[^1] - from + 1).Select(r => new ScreenRow(r, rows[r].TrimEnd())).ToList();
        return new TrustScreen(root, note, text, options, selected, trustAt, hold) { Words = words };
    }

    // Whether two reads are the same question to a person: the same repository root, note and
    // words (the folder is the launch's own). Where the highlight is does not matter.
    public static bool SameQuestion(TrustScreen? a, TrustScreen? b) =>
        a is not null && b is not null && a.Root == b.Root && a.RootNote == b.RootNote && a.Words == b.Words;

    // A row without the frame some agents draw around the question, or the spaces around it.
    static string Clean(string row)
    {
        var s = row.AsSpan().Trim();
        while (s.Length > 0 && Frame.Contains(s[0])) { s = s[1..].TrimStart(); }
        while (s.Length > 0 && Frame.Contains(s[^1])) { s = s[..^1].TrimEnd(); }
        return s.ToString();
    }

    const string Frame = "│┃║╭╮╰╯─━═╻╹┌┐└┘";

    // Rows of a wrapped line, as one: a path is drawn without spaces, and wrapping adds none.
    static string Joined(List<string> clean, int first, int last) => string.Concat(clean.Skip(first).Take(last - first + 1));

    // An option row: its highlight (› ❯ >), its number, and its label.
    static (bool Highlighted, bool Numbered, string Label)? Option(string row)
    {
        var s = row.AsSpan();
        var highlighted = s.Length > 0 && s[0] is '›' or '❯' or '>' or '▸';
        if (highlighted) { s = s[1..].TrimStart(); }
        var digits = 0;
        while (digits < s.Length && char.IsAsciiDigit(s[digits])) { digits++; }
        var numbered = digits > 0 && digits + 1 < s.Length && s[digits] == '.' && s[digits + 1] == ' ';
        if (numbered) { s = s[(digits + 1)..].TrimStart(); }
        return s.Length == 0 ? null : (highlighted, numbered, s.ToString());
    }
}
