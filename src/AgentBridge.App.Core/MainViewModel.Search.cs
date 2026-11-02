namespace AgentBridge.App.Core;

// The title bar's search box (ui.md §7, §9: Ctrl+K focuses it): the sessions whose title,
// repository, branch, agent or machine hold every word typed, in the order the list shows
// them, titles that start with the query first. Every machine's, whatever the picker shows:
// opening one the picker hides shows every machine again (Open).
public sealed partial class MainViewModel
{
    public IReadOnlyList<SearchHit> Search(string? query, int most = 8)
    {
        var words = (query ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) { return []; }
        var several = machines.Count > 1;
        var listed = Sessions.Concat(byId.Values.Except(Sessions)).ToList();
        var hits = new List<(SearchHit Hit, bool Prefix)>();
        foreach (var card in listed)
        {
            var m = owner[card];
            var s = card.Session;
            var haystack = string.Join(' ', new[] { card.Title, s.Repository, s.Branch, Words.LastSegment(s.Cwd), card.AgentName, m.Name });
            if (!words.All(w => haystack.Contains(w, StringComparison.CurrentCultureIgnoreCase))) { continue; }
            var detail = string.Join(" · ", new[] { several ? m.Name : null, s.Repository ?? Words.LastSegment(s.Cwd) }.Where(x => !string.IsNullOrEmpty(x)));
            hits.Add((new SearchHit(m.IsLocal ? null : m.Key, card.Id, card.Title, detail), card.Title.StartsWith(query!.Trim(), StringComparison.CurrentCultureIgnoreCase)));
        }
        return [.. hits.OrderByDescending(h => h.Prefix).Select(h => h.Hit).Take(most)];
    }

    public void Open(SearchHit hit) => Open(hit.Machine, hit.SessionId);
}

// One session the search box found. Machine: null for this machine's.
public sealed record SearchHit(string? Machine, string SessionId, string Title, string Detail)
{
    public string Label => Detail.Length > 0 ? $"{Title}, {Detail}" : Title;
    public override string ToString() => Title;
}
