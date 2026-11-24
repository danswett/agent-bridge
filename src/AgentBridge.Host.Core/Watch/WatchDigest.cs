using System.Globalization;
using System.Text;

namespace AgentBridge.Host.Core.Watch;

// The ONE person notice per machine per pass (docs/design/session-watch.md, "Delivery"): counts by rule, the first ten names, "and N more".
// Words are host-built from the rule and a label the caller chooses (launch name or node id, never a session title); durations and
// timestamps stay out so a clock skew between hosts cannot show.
public static class WatchDigest
{
    public const int Listed = 10, LabelChars = 24;

    public static (string Title, string Body)? Compose(IReadOnlyList<WatchNotice> notices, Func<string, string> label)
    {
        if (notices.Count == 0) { return null; }
        var counts = new List<string>();
        var prompts = notices.Count(n => n.Rule == WatchRule.Prompt);
        var idle = notices.Count(n => n.Rule == WatchRule.Idle);
        if (prompts > 0) { counts.Add($"{prompts} waiting at a prompt"); }
        if (idle > 0) { counts.Add($"{idle} idle with work"); }
        // Sorted by name so the same set always reads the same (the one slot a newer digest replaces must not flap), then the first ten.
        var listed = notices.Select(n => (Notice: n, Name: Cut(label(n.Session)))).OrderBy(x => x.Name, StringComparer.Ordinal).Take(Listed).ToList();
        var names = string.Join(", ", listed.Select(x => x.Name));
        var more = notices.Count > Listed ? $" and {notices.Count - Listed} more" : "";
        // Only the names the person can read: a count that includes names under "and N more" would point at nobody.
        var finals = listed.Count(x => x.Notice.Final);
        var last = finals == 0 ? "" : finals == notices.Count ? " (last reminder)" : $" (last reminder for {finals})";
        var title = notices.Count == 1 ? "1 session needs you" : $"{notices.Count} sessions need you";
        return (title, $"{string.Join(", ", counts)}: {names}{more}{last}");
    }

    // The label is the caller's text and goes to a phone as is, so it is cleaned first: control characters and the invisible direction marks and overrides
    // (U+200E/F, U+202A-202E, U+2066-2069, U+061C) are dropped, runs of blanks become one space, and an empty or missing name reads "a session".
    // A long name must not push "and N more" past the push size limit, where PushNotice.Fit would cut the tail first; it is cut at whole characters
    // as a person sees them (never between the halves of an emoji or before a combining mark), so the cut is at most LabelChars UTF-16 units.
    static string Cut(string? label)
    {
        var clean = new StringBuilder();
        foreach (var c in label ?? "")
        {
            if (char.IsControl(c) || c is '‎' or '‏' or '؜' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩')) { clean.Append(' '); }
            else { clean.Append(c); }
        }
        var text = string.Join(' ', clean.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (text.Length == 0) { return "a session"; }
        if (text.Length <= LabelChars) { return text; }
        var cut = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext() && cut.Length + ((string)elements.Current).Length <= LabelChars) { cut.Append((string)elements.Current); }
        return cut.Length == 0 ? "a session" : cut.ToString();
    }
}
