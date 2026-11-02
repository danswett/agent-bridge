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
        var want = byId
            .Where(p => p.Value.Session.Question is not null && owner.ContainsKey(p.Value))
            .Select(p => (Key: p.Key, Card: p.Value, Machine: owner[p.Value]))
            .OrderBy(e => e.Card.Session.Question!.AskedAt)
            .ThenBy(e => e.Machine.Name, StringComparer.Ordinal)
            .ThenBy(e => e.Machine.Key, StringComparer.Ordinal)
            .ThenBy(e => e.Card.Id, StringComparer.Ordinal)
            .ToList();
        var had = NeedsYou.Count;
        var entries = new List<NeedsYouEntry>();
        foreach (var (key, card, m) in want)
        {
            var existing = NeedsYou.FirstOrDefault(e => e.Key == key);
            var q = card.Session.Question!;
            var prompt = q.Kind == AgentBridge.Protocol.QuestionKind.Approval && NeedsYouEntry.WholePrompt(q) ? q.Message : null;
            // A same-id update can change what the entry says (a budget-trimmed message restored,
            // say), not only what it offers: either rebuilds it.
            if (existing is not null && (existing.QuestionId != q.Id || existing.QuestionModel != card.Question || existing.Mode != NeedsYouEntry.ModeOf(q, card.Agent, card.Question)
                || existing.Prompt != prompt || existing.Line != NeedsYouEntry.LineOf(q)))
            {
                existing.Detach();
                NeedsYou.Remove(existing);
                existing = null;
            }
            var entry = existing ?? new NeedsYouEntry(this, key, m.Key, card);
            entry.Refresh(several ? m.Name : null, now);
            entries.Add(entry);
        }
        foreach (var gone in NeedsYou.Except(entries).ToList())
        {
            gone.Detach();
            NeedsYou.Remove(gone);
        }
        for (var i = 0; i < entries.Count; i++)
        {
            var at = NeedsYou.IndexOf(entries[i]);
            if (at < 0) { NeedsYou.Insert(i, entries[i]); }
            else if (at != i) { NeedsYou.Move(at, i); }
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
