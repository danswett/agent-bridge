using System.Collections.ObjectModel;

namespace AgentBridge.App.Core;

public sealed partial class MainViewModel
{
    public ObservableCollection<SessionSection> Sections { get; } = [];
    readonly Dictionary<string, SessionSection> groupSections = [];
    readonly SessionSection pinnedSection = new(null, "Pinned", _ => { });
    readonly SessionSection sessionsSection = new(null, "Sessions", _ => { });
    readonly HashSet<SessionCardViewModel> needsShown = [];
    string listQuery = "";
    bool needsOnly;
    public event Action<string>? GroupCollapsing;
    public string ListQuery { get => listQuery; set { if (Set(ref listQuery, value)) { SyncSections(); } } }
    public bool NeedsOnly
    {
        get => needsOnly;
        set { if (Set(ref needsOnly, value)) { needsShown.Clear(); SyncSections(); Raise(nameof(HasNeedsYou)); } }
    }

    void SyncSections()
    {
        var filtering = NeedsOnly || !string.IsNullOrWhiteSpace(ListQuery);
        var needs = NeedsYou.Select(n => n.Card).ToHashSet();
        if (NeedsOnly) { needsShown.UnionWith(needs); }
        needsShown.RemoveWhere(c => !owner.ContainsKey(c));
        var hits = string.IsNullOrWhiteSpace(ListQuery) ? null
            : Search(ListQuery, int.MaxValue).Select(h => CardKey(h.Machine ?? Machine.LocalKey, h.SessionId)).ToHashSet();
        var visible = Sessions.Where(c => (hits is null || hits.Contains(KeyOf(c))) && (!NeedsOnly || needsShown.Contains(c))).ToList();
        var wanted = new List<SessionSection>();
        void Add(SessionSection section, string name, List<SessionCardViewModel> members)
        {
            if (members.Count == 0 && (!section.IsGroup || filtering || SelectedMachine?.Key is not null)) { return; }
            section.Update(name, members, !section.IsGroup || filtering || !Order.IsGroupCollapsed(section.Id!), needs);
            wanted.Add(section);
        }
        Add(pinnedSection, "Pinned", visible.Where(c => c.Pinned).ToList());
        var groups = Groups;
        foreach (var gone in groupSections.Keys.Where(id => !groups.Any(g => g.Id == id)).ToList()) { groupSections.Remove(gone); }
        foreach (var group in groups)
        {
            if (!groupSections.TryGetValue(group.Id, out var section))
            {
                section = new(group.Id, group.Name, expanded =>
                {
                    if (!NeedsOnly && string.IsNullOrWhiteSpace(ListQuery))
                    {
                        if (!expanded) { GroupCollapsing?.Invoke(group.Id); }
                        Order.SetGroupCollapsed(group.Id, !expanded);
                    }
                    Rearrange(chosen: true);
                });
                groupSections.Add(group.Id, section);
            }
            Add(section, group.Name, visible.Where(c => !c.Pinned && Order.Groups.GroupOf(KeyOf(c)) == group.Id).ToList());
        }
        Add(sessionsSection, "Sessions", visible.Where(c => !c.Pinned && Order.Groups.GroupOf(KeyOf(c)) is null).ToList());
        var pinsHead = wanted.Contains(pinnedSection) ? pinnedSection.Rows.FirstOrDefault() : null;
        var restHead = wanted.Contains(sessionsSection) && wanted.Any(s => s != sessionsSection) ? sessionsSection.Rows.FirstOrDefault() : null;
        foreach (var card in Sessions) { card.GroupHeader = card == pinsHead ? "Pinned" : card == restHead ? "Sessions" : null; }
        SessionSection.Sync(Sections, wanted);
        Raise(nameof(HasEnded));
    }
}

public sealed class SessionSection(string? id, string name, Action<bool> toggle) : ObservableCollection<SessionCardViewModel>
{
    public string? Id { get; } = id;
    public bool IsGroup => Id is not null;
    public string Name { get; private set; } = name;
    public ObservableCollection<SessionCardViewModel> Rows => this;
    bool expanded = true;
    int count, needs;
    bool unread;
    public bool Expanded { get => expanded; set { if (value != expanded) { toggle(value); } } }
    public string Header => $"{Name} · {count}";
    public string CountText => $" · {count}";
    public double ChevronAngle => Expanded ? 90 : 0;
    public string NeedsText => !Expanded && needs > 0 ? needs == 1 ? "1 needs you" : $"{needs} need you" : "";
    public bool HasNeeds => NeedsText.Length > 0;
    public bool HasUnread => !Expanded && needs == 0 && unread;
    public string EmptyNote => count == 0 ? "Nothing here yet. Open a session's menu and choose Move to group." : "";
    internal void Update(string name, List<SessionCardViewModel> members, bool open, HashSet<SessionCardViewModel> waiting)
    {
        Name = name;
        expanded = open;
        count = members.Count;
        needs = members.Count(waiting.Contains);
        unread = members.Any(c => c.Unread);
        Sync(Rows, members.Where(c => open || c.Session.Question is not null).ToList());
        OnPropertyChanged(new(string.Empty));
    }
    internal static void Sync<T>(ObservableCollection<T> target, IReadOnlyList<T> wanted)
    {
        foreach (var gone in target.Except(wanted).ToList()) { target.Remove(gone); }
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = target.IndexOf(wanted[i]);
            if (at < 0) { target.Insert(i, wanted[i]); }
            else if (at != i) { target.Move(at, i); }
        }
    }
}
