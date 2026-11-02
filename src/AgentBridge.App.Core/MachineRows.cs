using System.Collections.ObjectModel;
using AgentBridge.Local;

namespace AgentBridge.App.Core;

// One row of the Machines page (#180, #213): every machine on the account, this one first, then
// the others connected, then those offline. Its anatomy is ui.md §6.1's: the name on line 1 with
// the status trailing it, at most two facts on line 2 (never an id, never a sentence: a read-only
// host's reason is "View only" with the reason in its tooltip), and the machine's update, if
// any, inside its own row. Its buttons share one style and one column, under the text.
public sealed class MachineRowViewModel : Observable
{
    string name = "";
    string facts = "";
    string status = "";
    string? viewOnlyWhy;
    bool canShowSessions;
    bool canCheckHere;
    MachineUpdateViewModel? update;

    internal MachineRowViewModel(string key, OfflineMachineViewModel? offline = null)
    {
        Key = key;
        Offline = offline;
    }

    // The machine's key (Machine.Key), or "offline:" and the relay's id for one offline.
    public string Key { get; }
    public string Name
    {
        get => name;
        internal set
        {
            if (!Set(ref name, value)) { return; }
            Raise(nameof(ShowSessionsName));
            Raise(nameof(CheckHereName));
        }
    }
    public string Facts { get => facts; internal set => Set(ref facts, value); }
    public string Status { get => status; internal set => Set(ref status, value); }
    public bool ViewOnly => viewOnlyWhy is not null;
    // Why the machine takes no answers, replies or End session from here: the host's words.
    public string? ViewOnlyWhy
    {
        get => viewOnlyWhy;
        internal set { if (Set(ref viewOnlyWhy, value)) { Raise(nameof(ViewOnly)); } }
    }
    public bool CanShowSessions { get => canShowSessions; internal set => Set(ref canShowSessions, value); }
    // This machine's own Check, which Settings' updater does.
    public bool CanCheckHere { get => canCheckHere; internal set => Set(ref canCheckHere, value); }
    public MachineUpdateViewModel? Update
    {
        get => update;
        internal set { if (Set(ref update, value)) { Raise(nameof(HasUpdate)); } }
    }
    public bool HasUpdate => update is not null;
    public OfflineMachineViewModel? Offline { get; }
    public bool IsOffline => Offline is not null;
    public string ShowSessionsName => $"Show the sessions on {name}";
    public string CheckHereName => $"Check {name} for updates";
}

public sealed partial class MainViewModel
{
    public ObservableCollection<MachineRowViewModel> MachineRows { get; } = [];

    // Called whenever a machine, its sessions, its update or the offline list may have changed.
    void SyncMachineRows()
    {
        var want = new List<MachineRowViewModel>();
        foreach (var m in machines.OrderByDescending(m => m.IsLocal))
        {
            var row = MachineRows.FirstOrDefault(r => r.Key == m.Key) ?? new MachineRowViewModel(m.Key);
            var connected = m.Host.State == LinkState.Connected;
            var count = byId.Values.Count(c => owner[c] == m);
            var why = m.IsLocal ? ReadOnlyWhy ?? m.Host.Hello?.Machine.ReadOnly : m.Host.Hello?.Machine.ReadOnly;
            row.Name = m.Name;
            row.ViewOnlyWhy = string.IsNullOrEmpty(why) ? null : why;
            row.Facts = string.Join(" · ", new[] { connected ? Words.Plural(count, "session", "sessions") : null, row.ViewOnly ? "View only" : null }.Where(s => s is not null));
            row.Status = !connected ? "Connecting" : m.IsLocal ? "This machine" : "Online";
            row.CanShowSessions = connected && machines.Count > 1;
            row.CanCheckHere = m.IsLocal && CheckLocal is not null;
            row.Update = MachineUpdates.FirstOrDefault(u => u.Key == m.Key);
            want.Add(row);
        }
        foreach (var o in OfflineMachines)
        {
            var key = $"offline:{o.Id}";
            var row = MachineRows.FirstOrDefault(r => r.Key == key && r.Offline == o) ?? new MachineRowViewModel(key, o);
            row.Name = o.Name;
            row.Status = "Offline";
            want.Add(row);
        }
        foreach (var gone in MachineRows.Except(want).ToList()) { MachineRows.Remove(gone); }
        for (var i = 0; i < want.Count; i++)
        {
            var at = MachineRows.IndexOf(want[i]);
            if (at < 0) { MachineRows.Insert(i, want[i]); }
            else if (at != i) { MachineRows.Move(at, i); }
        }
    }
}
