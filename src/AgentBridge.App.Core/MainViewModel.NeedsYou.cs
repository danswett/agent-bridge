using System.Collections.ObjectModel;

namespace AgentBridge.App.Core;

// Needs you · N (#165, ui.md §6.1): every pending question across every machine, whatever the
// picker shows, oldest first, above the list. The lane refers to cards and never moves them. Its
// count is Waiting, which the tray's tooltip and the Sessions badge show, so the three agree.
// Kept apart from the rest of MainViewModel, whose markup #180 rewrites.
public sealed partial class MainViewModel
{
    public ObservableCollection<NeedsYouEntry> NeedsYou { get; } = [];
    public bool HasNeedsYou => NeedsYou.Count > 0;
    public string NeedsYouHeader => $"Needs you · {NeedsYou.Count}";
    public int Waiting => NeedsYou.Count;

    // The lane from the cards as they are: an entry per question, kept while its question and what
    // it offers stay the same, so a button with focus is not replaced under it.
    void SyncNeedsYou()
    {
        var now = Time.GetUtcNow();
        var several = machines.Count > 1;
        // Only questions that can be answered now: another machine's kept while it is away, or not
        // yet confirmed by its snapshot, may be gone, and are not counted as waiting on you (#269).
        var want = byId
            .Where(p => p.Value.Session.Question is not null && owner.TryGetValue(p.Value, out var m) && m.Current)
            .Select(p => (Key: p.Key, Card: p.Value, Machine: owner[p.Value]))
            .OrderBy(e => e.Card.Session.Question!.AskedAt)
            .ThenBy(e => e.Machine.Name, StringComparer.Ordinal)
            .ThenBy(e => e.Machine.Key, StringComparer.Ordinal)
            .ThenBy(e => e.Card.Id, StringComparer.Ordinal)
            .ToList();
        var had = NeedsYou.Count;
        var current = NeedsYou.ToDictionary(e => e.Key);
        var entries = new List<NeedsYouEntry>();
        foreach (var (key, card, m) in want)
        {
            var q = card.Session.Question!;
            current.TryGetValue(key, out var existing);
            // What an entry says is parsed once per message: an upsert that leaves the question's
            // words alone (most are another session's activity) parses nothing. A same-id update
            // that changes them (a budget-trimmed message restored, say) rebuilds the entry, as
            // does a change in what it offers.
            var sameWords = existing is not null && existing.QuestionId == q.Id && string.Equals(existing.Message, q.Message, StringComparison.Ordinal);
            var whole = sameWords ? existing!.Whole : NeedsYouEntry.WholeApproval(q);
            if (existing is not null && (!sameWords || existing.QuestionModel != card.Question || existing.Mode != NeedsYouEntry.ModeOf(q, card.Agent, card.Question, whole)))
            {
                existing.Detach();
                NeedsYou.Remove(existing);
                existing = null;
            }
            var entry = existing ?? new NeedsYouEntry(this, key, m.Key, card);
            entry.Refresh(several ? m.Name : null, now);
            entries.Add(entry);
        }
        var kept = entries.ToHashSet();
        foreach (var gone in NeedsYou.Where(e => !kept.Contains(e)).ToList())
        {
            gone.Detach();
            NeedsYou.Remove(gone);
        }
        for (var i = 0; i < entries.Count; i++)
        {
            if (i < NeedsYou.Count && NeedsYou[i] == entries[i]) { continue; }
            var at = NeedsYou.IndexOf(entries[i]);
            if (at < 0) { NeedsYou.Insert(i, entries[i]); }
            else { NeedsYou.Move(at, i); }
        }
        if (had != NeedsYou.Count)
        {
            Raise(nameof(HasNeedsYou));
            Raise(nameof(NeedsYouHeader));
            Raise(nameof(Waiting));
        }
    }

    // "3m" moves on with the clock.
    void RefreshNeedsYou()
    {
        var now = Time.GetUtcNow();
        var several = machines.Count > 1;
        foreach (var e in NeedsYou) { e.Refresh(several ? MachineOf(e.MachineKey)?.Name : null, now); }
    }
}
