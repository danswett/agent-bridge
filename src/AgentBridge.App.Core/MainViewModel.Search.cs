namespace AgentBridge.App.Core;

// The title bar's search box (ui.md §7, §9: Ctrl+K focuses it): the sessions whose title,
// repository, branch, agent or machine hold every word typed, titles that start with the query
// first, then the most recently updated. Every machine's, whatever the tabs show, and in an order
// that does not depend on them: listed rows first once let the cap drop a newer match the tabs
// hid. Opening one the tabs hide shows every machine again (Open). A query that names a session's id (the full GUID, or a prefix of 4 or more hex digits,
// any case, with or without dashes) finds it too, whatever its title says (owner request 09:33 PT Nov 24).
public sealed partial class MainViewModel
{
    public IReadOnlyList<SearchHit> Search(string? query, int most = 8)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) { return []; }
        var several = machines.Count > 1;
        var listed = byId.Values.OrderByDescending(c => c.Session.UpdatedAt).ThenBy(c => c.Title, StringComparer.CurrentCulture).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
        var hits = new List<(SearchHit Hit, bool Prefix)>();
        foreach (var card in listed)
        {
            var m = owner[card];
            var s = card.Session;
            var haystack = string.Join(' ', new[] { card.Title, s.Repository, s.Branch, Words.LastSegment(s.Cwd), card.AgentName, m.Name });
            if (!words.All(w => haystack.Contains(w, StringComparison.CurrentCultureIgnoreCase)) && !NamesId(query!, card.Id)) { continue; }
            var detail = string.Join(" · ", new[] { several ? m.Name : null, s.Repository ?? Words.LastSegment(s.Cwd) }.Where(x => !string.IsNullOrEmpty(x)));
            hits.Add((new SearchHit(m.IsLocal ? null : m.Key, card.Id, card.Title, detail), card.Title.StartsWith(query!.Trim(), StringComparison.CurrentCultureIgnoreCase)));
        }
        return [.. hits.OrderByDescending(h => h.Prefix).Select(h => h.Hit).Take(most)];
    }

    // Whether the query is the start of the id: hex digits and dashes only (no spaces), at least 4 digits once the dashes go, in any case.
    const int MinIdDigits = 4;
    static bool NamesId(string query, string id)
    {
        var q = query.Trim();
        if (q.Length == 0 || !q.All(c => c == '-' || char.IsAsciiHexDigit(c))) { return false; }
        var digits = q.Replace("-", "");
        return digits.Length >= MinIdDigits && id.Replace("-", "").StartsWith(digits, StringComparison.OrdinalIgnoreCase);
    }
    public void Open(SearchHit hit) => Open(hit.Machine, hit.SessionId);
}

// One session the search box found. Machine: null for this machine's.
public sealed record SearchHit(string? Machine, string SessionId, string Title, string Detail)
{
    public string Label => Detail.Length > 0 ? $"{Title}, {Detail}" : Title;
    public override string ToString() => Title;
}
