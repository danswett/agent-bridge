using System.Runtime.CompilerServices;
using AgentBridge.Local;

namespace AgentBridge.App.Core;

// Who may answer folder-trust questions on this machine (launch.remoteFolderTrust and
// launch.remoteFolderTrustDevices), as the Settings page and the Launch page's "Allow answering
// from this app" both change it: one editor per host connection, whichever page asks first.
// The host replaces the whole device list on each change, so a change made from a list that is
// out of date undoes another. Two ways that happened in review: Allow answering from this app,
// made from a status that still listed a phone just revoked in Settings, approved it again; and
// a second Settings edit made after the first one's reply but before its status event (the two
// can arrive in either order) put back the device the first had revoked. So every change goes
// one at a time, each made from the host's list read over the pipe just before it, and what the
// host acknowledged is what is shown until a status saying the same arrives.
public sealed class TrustEditor
{
    static readonly ConditionalWeakTable<HostConnection, TrustEditor> editors = new();

    public static TrustEditor For(HostConnection host) => editors.GetValue(host, h => new TrustEditor(h));

    readonly HostConnection host;
    readonly SemaphoreSlim slot = new(1, 1);
    HostSettingsView? acked;
    HostStatus? checkedStatus;
    int pending;

    TrustEditor(HostConnection host)
    {
        this.host = host;
        host.Changed += Check;
    }

    // Raised when what Settings says changed other than through a status (an acknowledgement, or
    // the host's word read again).
    public event Action? Changed;

    public bool Pending => Volatile.Read(ref pending) > 0;

    // What the host said last: its acknowledgement of a change until a status agrees, then the status.
    public HostSettingsView? Settings
    {
        get
        {
            var a = Volatile.Read(ref acked);
            var now = host.Status?.Settings;
            if (a is not null && Equals(a, now)) { Interlocked.CompareExchange(ref acked, null, a); return now; }
            return a ?? now;
        }
    }

    // change: from the host's settings now to the request that makes the change, or null when
    // there is none to make. Returns what the host has after it. Throws what the pipe throws.
    public async Task<HostSettingsView> EditAsync(Func<HostSettingsView, SettingsRequest?> change)
    {
        Interlocked.Increment(ref pending);
        await slot.WaitAsync();
        try
        {
            var now = (await host.CallAsync<HostStatus>(HostOps.Status)).Settings;
            var saved = change(now) is { } request ? (await host.CallAsync<SettingsResult>(HostOps.Settings, request)).Settings : now;
            Acknowledge(saved);
            return saved;
        }
        finally
        {
            Interlocked.Decrement(ref pending);
            slot.Release();
        }
    }

    void Acknowledge(HostSettingsView saved)
    {
        Volatile.Write(ref acked, Equals(saved, host.Status?.Settings) ? null : saved);
        Changed?.Invoke();
    }

    // A status that disagrees with an acknowledgement, with nothing on its way: either an older
    // one arriving late, or a change made elsewhere (a person's terminal). Which is the host's to
    // say: it is read again, once per status.
    void Check()
    {
        var s = host.Status;
        if (s is null || Pending || ReferenceEquals(s, checkedStatus) || Volatile.Read(ref acked) is not { } a || Equals(a, s.Settings)) { return; }
        checkedStatus = s;
        _ = RereadAsync();
    }

    async Task RereadAsync()
    {
        await slot.WaitAsync();
        try
        {
            var now = (await host.CallAsync<HostStatus>(HostOps.Status)).Settings;
            Acknowledge(now);
        }
        // Not read: the acknowledgement stands until a status agrees or the next is checked.
        catch (Exception e) when (e is not OutOfMemoryException) { }
        finally { slot.Release(); }
    }
}
