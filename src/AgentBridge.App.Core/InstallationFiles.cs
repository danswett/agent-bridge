namespace AgentBridge.App.Core;

public static class InstallationFiles
{
    public const string Unknown = "Windows file holders could not be checked";
    public static readonly TimeSpan ReleaseWait = TimeSpan.FromMinutes(2);
    public static readonly string TimedOut = $"the Windows installation file check (Restart Manager) timed out after {ReleaseWait.TotalSeconds:0} s; no file holder was confirmed";

    public static IReadOnlyList<string> LockedImages(string folder)
    {
        var locked = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => Path.GetExtension(f).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || Path.GetExtension(f).Equals(".dll", StringComparison.OrdinalIgnoreCase)))
        {
            try { using var check = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) when (IsSharingViolation(e)) { locked.Add(file); }
            catch (UnauthorizedAccessException) { locked.Add(file); }
        }
        return locked;
    }

    // Windows reports a held file as ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION. .NET on Unix
    // emulates FileShare.None with flock and reports a conflict as the raw errno EWOULDBLOCK
    // (11 on Linux, 35 on macOS); missing it threw instead of reporting the lock (#2884 CI).
    static bool IsSharingViolation(IOException e) => OperatingSystem.IsWindows()
        ? (e.HResult & 0xffff) is 32 or 33
        : e.HResult == (OperatingSystem.IsLinux() ? 11 : 35);

    public sealed class Budget(TimeProvider time)
    {
        public TimeProvider Time { get; } = time;
        public long Began { get; } = time.GetTimestamp();
        TimeSpan excluded;
        public TimeSpan Elapsed => Time.GetElapsedTime(Began) - excluded;
        public TimeSpan Remaining => ReleaseWait - Elapsed;
        public void ExcludeSince(long timestamp) => excluded += Time.GetElapsedTime(timestamp);
        internal Func<IReadOnlyList<string>>? LockedFiles { get; set; }
        Func<string?>? clearProbe;
        HashSet<string>? clearFiles;
        public async Task<string?> ProbeOnceAsync(Func<string?>? probe, CancellationToken cancellation, bool fresh = false)
        {
            if (probe is null) { return null; }
            // A clear scan serves the duplicate check right after the wait, not a deadline; a changed
            // lock footprint invalidates it. The last gate before stop and handoff scans fresh: an
            // outside holder of an image the app already holds changes no file name (#2884 review).
            using var deadline = new CancellationTokenSource(ReleaseWait, Time);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
            var token = stop.Token;
            try
            {
                return await Task.Run(() =>
                {
                    var files = LockedFiles?.Invoke();
                    if (!fresh && clearProbe == probe && (files is null || clearFiles?.SetEquals(files) == true)) { return null; }
                    var held = probe();
                    token.ThrowIfCancellationRequested();
                    clearProbe = held is null ? probe : null;
                    clearFiles = held is null && files is not null ? new(files, StringComparer.OrdinalIgnoreCase) : null;
                    return held;
                }, token).WaitAsync(token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested) { return TimedOut; }
        }
    }

    public static async Task<string?> AwaitReleaseAsync(Func<string?>? probe, TimeProvider time,
        CancellationToken cancellation, Action<string>? waiting = null, Action<string>? log = null, Budget? budget = null)
    {
        if (probe is null) { return null; }
        budget ??= new Budget(time);
        time = budget.Time;
        var leftAtStart = budget.Remaining;
        if (leftAtStart <= TimeSpan.Zero) { return Unknown; }
        var delay = TimeSpan.FromSeconds(1);
        var last = Unknown;
        while (budget.Elapsed < ReleaseWait)
        {
            try
            {
                var held = await budget.ProbeOnceAsync(probe, cancellation);
                if (held == TimedOut) { log?.Invoke(held); return held; }
                if (held is null) { return null; }
                last = string.IsNullOrWhiteSpace(held) ? Unknown : held;
            }
            catch (OperationCanceledException) { throw; }
            catch (TimeoutException) { return TimedOut; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                log?.Invoke($"installation file holders could not be checked: {e.Message}");
                last = Unknown;
            }
            waiting?.Invoke(last);
            var left = budget.Remaining;
            if (left <= TimeSpan.Zero) { break; }
            await Task.Delay(delay < left ? delay : left, time, cancellation);
            delay = TimeSpan.FromSeconds(Math.Min(10, delay.TotalSeconds * 2));
        }
        return last;
    }
}
