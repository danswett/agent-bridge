namespace AgentBridge.Host.Core.Watch;

// The ONE person notice per machine per pass (docs/design/session-watch.md, "Delivery"): counts by rule, the first ten names, "and N more".
// Words are host-built from the rule and a label the caller chooses (launch name or node id, never a session title); durations and
// timestamps stay out so a clock skew between hosts cannot show.
public static class WatchDigest
{
    public const int Listed = 10;

    public static (string Title, string Body)? Compose(IReadOnlyList<WatchNotice> notices, Func<string, string> label)
    {
        if (notices.Count == 0) { return null; }
        var counts = new List<string>();
        var prompts = notices.Count(n => n.Rule == WatchRule.Prompt);
        var idle = notices.Count(n => n.Rule == WatchRule.Idle);
        if (prompts > 0) { counts.Add($"{prompts} waiting at a prompt"); }
        if (idle > 0) { counts.Add($"{idle} idle with work"); }
        var names = string.Join(", ", notices.Take(Listed).Select(n => label(n.Session)));
        var more = notices.Count > Listed ? $" and {notices.Count - Listed} more" : "";
        var last = notices.Any(n => n.Final) ? " (last reminder)" : "";
        var title = notices.Count == 1 ? "1 session needs you" : $"{notices.Count} sessions need you";
        return (title, $"{string.Join(", ", counts)}: {names}{more}{last}");
    }
}
