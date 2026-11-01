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

    const int MaxAttempts = 3;

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
            // Each list sent says which it was made from: one changed meanwhile by another app or a
            // person's terminal is refused, and the change is made again from the list as it is,
            // so a device revoked there is never approved again from here.
            for (var attempt = 1; ; attempt++)
            {
                var now = (await host.CallAsync<HostStatus>(HostOps.Status)).Settings;
                if (change(now) is not { } request) { Volatile.Write(ref acked, Equals(now, host.Status?.Settings) ? null : now); return now; }
                if (request.RemoteFolderTrustDevices is not null) { request = request with { RemoteFolderTrustDevicesWas = now.RemoteFolderTrustDevices ?? [] }; }
                var result = await host.CallAsync<SettingsResult>(HostOps.Settings, request);
                if (result.Conflict && attempt < MaxAttempts) { continue; }
                if (result.Conflict) { throw new InvalidOperationException("the devices approved for folder trust kept changing at the machine; try again"); }
                Volatile.Write(ref acked, Equals(result.Settings, host.Status?.Settings) ? null : result.Settings);
                return result.Settings;
            }
        }
        finally
        {
            var last = Interlocked.Decrement(ref pending) == 0;
            slot.Release();
            // Told once nothing is on its way, failed or not: what was shown while edits were
            // pending is held back until then (Settings' boxes show what was asked for), so a
            // change from the Launch page whose status came before its reply was never redrawn.
            if (last) { Changed?.Invoke(); }
            // A status that came while this was on its way was not checked then; the reply may be
            // older than it (a change at the terminal in between). Failed or not: an earlier
            // acknowledgement may still be held.
            Check();
        }
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
            Volatile.Write(ref acked, Equals(now, host.Status?.Settings) ? null : now);
            Changed?.Invoke();
        }
        // Not read: the acknowledgement stands until a status agrees or the next is checked.
        catch (Exception e) when (e is not OutOfMemoryException) { }
        finally { slot.Release(); }
        // A status that came during the read and agreed with what was held then was let pass,
        // and the read, older than it, may just have replaced that: it is checked now.
        Check();
    }
}
