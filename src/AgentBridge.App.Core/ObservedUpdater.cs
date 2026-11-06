using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// The app's one updater, as everything that uses it shares it: Settings' Check and Restart to
// update, and the background updates (AutoUpdater), which tell the account's other
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
    // Which release's download last failed, when, and how many times in a row: not tried again
    // until RetryAt, whoever asks (Settings, the automatic updates, a device's press, or one
    // waiting above).
    (string? Release, DateTimeOffset At, int Times)? failed;
    int checks;
    int downloads;

    // The wait after a release's first failed download, doubled after each further one in a row
    // up to DownloadAgainAtMost (#516): a download that keeps failing (a feed out of reach, a disk
    // full) is retried in the background, more and more rarely, and never ends the app.
    public static readonly TimeSpan DownloadAgainAfter = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DownloadAgainAtMost = TimeSpan.FromHours(4);

    public static TimeSpan HoldAfter(int times)
    {
        var hold = DownloadAgainAfter * Math.Pow(2, Math.Clamp(times - 1, 0, 16));
        return hold < DownloadAgainAtMost ? hold : DownloadAgainAtMost;
    }

    // When the release the last check found may be downloaded again after its download failed;
    // null when it has not failed (or another release has been found since).
    public DateTimeOffset? RetryAt => failed is { } f && f.Release == Found ? f.At + HoldAfter(f.Times) : null;

    // Raised whenever anything below changes, on the thread that changed it.
    public event Action? Changed;

    // The release the last check found, null when it found nothing newer (or none has finished).
    public string? Found { get; private set; }
    public DateTimeOffset? CheckedAt { get; private set; }
    // As of when Found is known: the feed's own answer may be older than the check that read it,
    // from the machine's shared copy (#530), and the devices are told how old it really is.
    public DateTimeOffset? FoundAsOf { get; private set; }
    public DateTimeOffset? DiscoveredAt => inner.DiscoveredAt;
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
            var from = Bound();
            var found = await inner.CheckAsync();
            // A check that finished after its source changed answers for the old one.
            if (Bound() != from) { return found; }
            Found = found;
            CheckedAt = time.GetUtcNow();
            FoundAsOf = inner.DiscoveredAt is { } at && at < CheckedAt ? at : CheckedAt;
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
            var from = Bound();
            var release = Found;
            var now = time.GetUtcNow();
            if (failed is { } f && f.Release == release && now - f.At < HoldAfter(f.Times))
            {
                throw new DownloadHeldException($"downloading {release} failed {Words.Ago(f.At, now)}; it is tried again after {(f.At + HoldAfter(f.Times)).ToLocalTime():t}");
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
                // Held against this source only: one that changed meanwhile owes it nothing.
                if (Bound() == from) { failed = (release, time.GetUtcNow(), failed is { } before && before.Release == release ? before.Times + 1 : 1); }
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

    public void ApplyAndRestart(string version) => inner.ApplyAndRestart(version);

    // Something the state reads besides the updater changed: Settings' install failing.
    public void Noted() => Raise();

    // The source changed (Include pre-releases): what the last check found was the old source's,
    // so it is no longer reported (UpdateState.available) until a check on the new one, nor is a
    // failed download of the old source's held against the new. Source names it, with the changes
    // counted here, so what others cached from a check (AutoUpdater) can be told to be the old
    // source's too, whether the app changed it or feed.txt did.
    public void SourceChanged()
    {
        changes++;
        Found = null;
        CheckedAt = null;
        failed = null;
        Raise();
    }
    int changes;
    public string Source => $"{changes}|{Bound()}";

    // The inner updater's source as this last saw it (the feed, and Include pre-releases): when
    // feed.txt changes it, with no SourceChanged from the app, what the last check found and a
    // failed download's hold are the old feed's, so they go too. Before, a failure on feed A held
    // the same version on feed B for up to four hours.
    string? bound;
    string Bound()
    {
        var now = inner.Source;
        if (bound is not null && bound != now) { (Found, CheckedAt, failed) = (null, null, null); }
        bound = now;
        return now;
    }

    void Raise() => Changed?.Invoke();
}

// A download not tried, its release's having failed a moment ago (ObservedUpdater.DownloadAgainAfter).
public sealed class DownloadHeldException(string message) : InvalidOperationException(message);
