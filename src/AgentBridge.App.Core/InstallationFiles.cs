namespace AgentBridge.App.Core;

public static class InstallationFiles
{
    public const string Unknown = "Windows file holders could not be checked";
    public static readonly TimeSpan ReleaseWait = TimeSpan.FromMinutes(2);

    public static async Task<string?> AwaitReleaseAsync(Func<string?>? probe, TimeProvider time,
        CancellationToken cancellation, Action<string>? waiting = null, Action<string>? log = null)
    {
        if (probe is null) { return null; }
        using var deadline = new CancellationTokenSource(ReleaseWait, time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        var began = time.GetTimestamp();
        var delay = TimeSpan.FromSeconds(1);
        var last = Unknown;
        while (!deadline.IsCancellationRequested && time.GetElapsedTime(began) < ReleaseWait)
        {
            try
            {
                var held = await Task.Run(probe, stop.Token).WaitAsync(stop.Token);
                if (time.GetElapsedTime(began) >= ReleaseWait) { return last; }
                if (held is null) { return null; }
                last = string.IsNullOrWhiteSpace(held) ? Unknown : held;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested) { return last; }
            catch (OperationCanceledException) { throw; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException or TimeoutException)
            {
                log?.Invoke($"installation file holders could not be checked: {e.Message}");
                last = Unknown;
            }
            waiting?.Invoke(last);
            var left = ReleaseWait - time.GetElapsedTime(began);
            if (left <= TimeSpan.Zero) { break; }
            try { await Task.Delay(delay < left ? delay : left, time, stop.Token); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested) { return last; }
            delay = TimeSpan.FromSeconds(Math.Min(10, delay.TotalSeconds * 2));
        }
        return last;
    }
}
