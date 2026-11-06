using System.Collections.ObjectModel;
using AgentBridge.Local;

namespace AgentBridge.App.Core;

// One machine's "Discovery held" banner over every page (ui.md §7, Windows; #293). Closing it
// only hides it: the host still holds, its sessions stay listed and resumes still wait, and the
// app tells the host nothing.
public sealed class HeldNotice : Observable
{
    string text = "";
    string dismissName = "";

    internal HeldNotice(string machine, string reason)
    {
        Machine = machine;
        Reason = reason;
    }

    // The machine's key (Machine.Key) and the host's reason: together, which hold this is.
    public string Machine { get; }
    public string Reason { get; }
    public string Text { get => text; internal set => Set(ref text, value); }
    // The close button's name: an icon, so named for what it does and to which banner (§9.1 A1).
    public string DismissName { get => dismissName; internal set => Set(ref dismissName, value); }
    // Unique in the window, from the machine's key (§9.1 A2).
    public string AutomationId => $"DiscoveryHeld:{Machine}";
    public string DismissId => $"DismissDiscoveryHeld:{Machine}";
}

public sealed partial class MainViewModel
{
    // Each held machine's banner, in the machines' order, but for those dismissed.
    public ObservableCollection<HeldNotice> HeldNotices { get; } = [];

    // The reason each machine's banner was dismissed on, by its key. Only for the app's run, never
    // saved: a restart shows every hold still in force. A dismissal lasts while its machine's hold
    // is that reason, so a different reason, or a hold that clears and comes back, shows it again.
    readonly Dictionary<string, string> heldDismissed = new(StringComparer.Ordinal);

    // The banner's close button; on the UI thread.
    public void Dismiss(HeldNotice notice)
    {
        if (!HeldNotices.Contains(notice)) { return; }
        heldDismissed[notice.Machine] = notice.Reason;
        SyncHeld();
    }

    void SyncHeld()
    {
        // Kept while the link is down too: the hold is the host's, and it says again what it is on
        // reconnecting, so a dismissal outlives a reconnect to the same hold.
        foreach (var (key, reason) in heldDismissed.ToList())
        {
            if (MachineOf(key)?.Host.Sessions.Held?.Reason != reason) { heldDismissed.Remove(key); }
        }
        var want = new List<HeldNotice>();
        foreach (var m in machines)
        {
            if (m.Host.State != LinkState.Connected || m.Host.Sessions.Held is not { } held || !Shows(m)) { continue; }
            if (heldDismissed.TryGetValue(m.Key, out var dismissed) && dismissed == held.Reason) { continue; }
            var notice = HeldNotices.FirstOrDefault(n => n.Machine == m.Key && n.Reason == held.Reason) ?? new HeldNotice(m.Key, held.Reason);
            notice.Text = $"{(machines.Count > 1 ? $"{m.Name}: " : "")}Discovery held: {held.Reason}. Sessions stay listed, and resuming them waits, until the host can account for every agent process.";
            notice.DismissName = $"Dismiss Discovery held on {m.Name}";
            want.Add(notice);
        }
        foreach (var gone in HeldNotices.Except(want).ToList()) { HeldNotices.Remove(gone); }
        for (var i = 0; i < want.Count; i++)
        {
            var at = HeldNotices.IndexOf(want[i]);
            if (at < 0) { HeldNotices.Insert(i, want[i]); }
            else if (at != i) { HeldNotices.Move(at, i); }
        }
    }
}
