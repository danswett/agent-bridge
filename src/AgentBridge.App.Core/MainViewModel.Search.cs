namespace AgentBridge.App.Core;

// The title bar's search box (ui.md §7, §9: Ctrl+K focuses it): the sessions whose title,
// repository, branch, agent or machine hold every word typed, titles that start with the query
// first, then the most recently updated. Every machine's, whatever the tabs show, and in an order
// that does not depend on them: listed rows first once let the cap drop a newer match the tabs
// hid. Opening one the tabs hide shows every machine again (Open).
public sealed partial class MainViewModel
{
    public IReadOnlyList<SearchHit> Search(string? query, int most = 8)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) { return []; }
        var several = machines.Count > 1;
        var hits = new List<(SessionCardViewModel Card, SearchHit Hit, bool Prefix)>();
        foreach (var card in byId.Values)
        {
            var m = owner[card];
            var s = card.Session;
            var haystack = string.Join(' ', new[] { card.Title, s.Repository, s.Branch, Words.LastSegment(s.Cwd), card.AgentName, m.Name });
            if (!words.All(w => haystack.Contains(w, StringComparison.CurrentCultureIgnoreCase))) { continue; }
            var detail = string.Join(" · ", new[] { several ? m.Name : null, s.Repository ?? Words.LastSegment(s.Cwd) }.Where(x => !string.IsNullOrEmpty(x)));
            hits.Add((card, new SearchHit(m.IsLocal ? null : m.Key, card.Id, card.Title, detail), card.Title.StartsWith(query!.Trim(), StringComparison.CurrentCultureIgnoreCase)));
        }
        return [.. hits.OrderByDescending(h => h.Prefix)
            .ThenByDescending(h => h.Card.Session.UpdatedAt)
            .ThenBy(h => h.Card.Title, StringComparer.CurrentCulture)
            .ThenBy(h => h.Card.Id, StringComparer.Ordinal).Select(h => h.Hit).Take(most)];
    }

    public void Open(SearchHit hit) => Open(hit.Machine, hit.SessionId);
}

// One session the search box found. Machine: null for this machine's.
public sealed record SearchHit(string? Machine, string SessionId, string Title, string Detail)
{
    public string Label => Detail.Length > 0 ? $"{Title}, {Detail}" : Title;
    public override string ToString() => Title;
}
