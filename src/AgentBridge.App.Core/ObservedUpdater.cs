using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// The app's one updater, as everything that uses it shares it: Settings' Check, Download and
// Update and restart, and the automatic updates (AutoUpdater), which tell the account's other
// devices what it is doing (#51). Each of them goes through here, so a check or download made from
// Settings is reported as one the AutoUpdater made would be: what the last check found (kept when
// its download fails, so a release that is there is not reported as nothing newer), when, and
// the download's progress.
public sealed class ObservedUpdater(IUpdater inner, TimeProvider? time = null) : IUpdater
{
    readonly TimeProvider time = time ?? TimeProvider.System;
    int checks;
    int downloads;

    // Raised whenever anything below changes, on the thread that changed it.
    public event Action? Changed;

    // The release the last check found, null when it found nothing newer (or none has finished).
    public string? Found { get; private set; }
    public DateTimeOffset? CheckedAt { get; private set; }
    public UpdatePhase Phase => Volatile.Read(ref downloads) > 0 ? UpdatePhase.Downloading : Volatile.Read(ref checks) > 0 ? UpdatePhase.Checking : UpdatePhase.Idle;
    public int? Progress { get; private set; }

    public string? InstalledVersion => inner.InstalledVersion;
    public string? PendingNotes => inner.PendingNotes;
    public string? Ready => inner.Ready;

    public async Task<string?> CheckAsync()
    {
        Interlocked.Increment(ref checks);
        Raise();
        try
        {
            var found = await inner.CheckAsync();
            Found = found;
            CheckedAt = time.GetUtcNow();
            return found;
        }
        finally
        {
            Interlocked.Decrement(ref checks);
            Raise();
        }
    }

    public async Task DownloadAsync(Action<int> progress)
    {
        Interlocked.Increment(ref downloads);
        Progress = 0;
        Raise();
        try
        {
            await inner.DownloadAsync(p =>
            {
                var now = Math.Clamp(p, 0, 100);
                if (Progress != now) { Progress = now; Raise(); }
                progress(p);
            });
        }
        finally
        {
            Interlocked.Decrement(ref downloads);
            Progress = null;
            Raise();
        }
    }

    public void ApplyAndRestart() => inner.ApplyAndRestart();

    void Raise() => Changed?.Invoke();
}
