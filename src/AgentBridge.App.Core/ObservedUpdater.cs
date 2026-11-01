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
    // One download at a time: Velopack's takes a global update lock and fails, rather than waits,
    // when another holds it, so Settings' and the automatic one's overlapping made the second fail
    // (reported to every device as a failed update).
    readonly SemaphoreSlim downloading = new(1, 1);
    // Which release's download last failed, and when: not tried again for DownloadAgainAfter,
    // whoever asks (Settings, the automatic updates, a device's press, or one waiting above).
    (string? Release, DateTimeOffset At)? failed;
    int checks;
    int downloads;

    public static readonly TimeSpan DownloadAgainAfter = TimeSpan.FromMinutes(10);

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
        Progress ??= 0;
        Raise();
        var held = false;
        try
        {
            await downloading.WaitAsync();
            held = true;
            // The one waited for downloaded the release the last check found: nothing to do.
            if (inner.Ready is not null) { return; }
            var release = Found;
            var now = time.GetUtcNow();
            if (failed is { } f && f.Release == release && now - f.At < DownloadAgainAfter)
            {
                throw new DownloadHeldException($"downloading {release} failed {Words.Ago(f.At, now)}; it is tried again after {(f.At + DownloadAgainAfter).ToLocalTime():t}");
            }
            try
            {
                await inner.DownloadAsync(p =>
                {
                    var at = Math.Clamp(p, 0, 100);
                    if (Progress != at) { Progress = at; Raise(); }
                    progress(p);
                });
            }
            catch (Exception e) when (e is not (OutOfMemoryException or StackOverflowException))
            {
                failed = (release, time.GetUtcNow());
                throw;
            }
            failed = null;
        }
        finally
        {
            if (held) { downloading.Release(); }
            // Settings' and the automatic one's may overlap: the progress goes with the last.
            if (Interlocked.Decrement(ref downloads) == 0) { Progress = null; }
            Raise();
        }
    }

    public void ApplyAndRestart() => inner.ApplyAndRestart();

    // Something the state reads besides the updater changed: Settings' install failing.
    public void Noted() => Raise();

    void Raise() => Changed?.Invoke();
}

// A download not tried, its release's having failed a moment ago (ObservedUpdater.DownloadAgainAfter).
public sealed class DownloadHeldException(string message) : InvalidOperationException(message);
