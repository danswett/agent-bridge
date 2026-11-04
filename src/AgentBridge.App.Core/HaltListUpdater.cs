using AgentBridge.Host.Core.Updates;

namespace AgentBridge.App.Core;

// The app's updater behind the release halt list (#580, ReleaseRollout): what Settings, the
// automatic updates and a device's press all go through, so none of them downloads or installs a
// halted release.
//
// A check reads the list whenever it finds a release: a halted one is reported as nothing to
// install (Halted says which), and a list that cannot be read fails the check, so nothing is
// downloaded. Before installing, ConfirmAsync reads it again for the release downloaded, which may
// have waited hours for the sessions to go idle: one halted since is dropped, and ApplyAndRestart
// refuses anything not confirmed moments before, so a caller that forgets to confirm installs
// nothing rather than skipping the list.
//
// Nothing here looks at the version installed: a machine on a halted release stays on it until a
// newer one rolls it forward, by the ordinary rules. Installs never downgrade (#451, and the
// owner's decision on #580).
public sealed class HaltListUpdater(IUpdater inner, Func<CancellationToken, Task<ReleaseRollout>> read, Action<string> log, TimeProvider? time = null) : IUpdater
{
    // Longer than stopping the host takes (HostManager), short enough that the list read is the one
    // that decided.
    public static readonly TimeSpan ConfirmedFor = TimeSpan.FromMinutes(5);

    readonly TimeProvider time = time ?? TimeProvider.System;
    readonly object gate = new();
    // The release the last check found and the list did not halt: the only one downloaded or
    // installed. Null while a check runs, and after one that found nothing, a halted release, or no
    // list.
    string? cleared;
    string? halted;
    (string Version, DateTimeOffset At)? confirmed;

    public string? InstalledVersion => inner.InstalledVersion;
    public string? PendingNotes => Halted is null ? inner.PendingNotes : null;

    public string? Halted { get { lock (gate) { return halted; } } }

    public string? Ready
    {
        get
        {
            var ready = inner.Ready;
            lock (gate) { return ready is not null && ready == cleared ? ready : null; }
        }
    }

    public async Task<string?> CheckAsync()
    {
        lock (gate) { cleared = null; confirmed = null; }
        var found = await inner.CheckAsync();
        if (found is null)
        {
            lock (gate) { halted = null; }
            return null;
        }
        // Throws when the list cannot be read: the check fails, and nothing is downloaded.
        var rollout = await read(CancellationToken.None);
        lock (gate)
        {
            if (rollout.Halts(found))
            {
                if (halted != found) { log($"update: {found} is on the release halt list ({ReleaseRollout.FileName}); it is not downloaded or installed"); }
                halted = found;
                return null;
            }
            halted = null;
            cleared = found;
            return found;
        }
    }

    public Task DownloadAsync(Action<int> progress)
    {
        lock (gate)
        {
            if (halted is { } h) { throw new ReleaseHaltedException(h); }
            if (cleared is null) { throw new InvalidOperationException("no release has been checked against the release halt list; check for updates again"); }
        }
        return inner.DownloadAsync(progress);
    }

    public async Task ConfirmAsync()
    {
        lock (gate) { confirmed = null; }
        var ready = Ready ?? throw new InvalidOperationException("no downloaded update to install; check for updates again");
        var rollout = await read(CancellationToken.None);
        lock (gate)
        {
            if (rollout.Halts(ready))
            {
                log($"update: {ready}, already downloaded, is now on the release halt list ({ReleaseRollout.FileName}); it is not installed");
                cleared = null;
                halted = ready;
                throw new ReleaseHaltedException(ready);
            }
            // A check meanwhile may have found another release, or none.
            if (cleared != ready) { throw new InvalidOperationException("the update found has changed since; check for updates again"); }
            confirmed = (ready, time.GetUtcNow());
        }
    }

    public void ApplyAndRestart()
    {
        var ready = inner.Ready;
        lock (gate)
        {
            if (ready is null || ready != cleared || confirmed is not { } c || c.Version != ready || time.GetUtcNow() - c.At > ConfirmedFor)
            {
                throw new InvalidOperationException($"the release halt list was not read just before installing{(ready is null ? "" : $" {ready}")}, so nothing was installed; check for updates again");
            }
            confirmed = null;
        }
        inner.ApplyAndRestart();
    }
}
