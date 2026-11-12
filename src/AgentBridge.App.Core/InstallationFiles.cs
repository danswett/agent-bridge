namespace AgentBridge.App.Core;

public static class InstallationFiles
{
    public const string Unknown = "Windows file holders could not be checked";
    public static readonly TimeSpan ReleaseWait = TimeSpan.FromMinutes(2);

    public sealed record Budget(TimeProvider Time)
    {
        public long Began { get; } = Time.GetTimestamp();
        public TimeSpan Remaining => ReleaseWait - Time.GetElapsedTime(Began);
        public async Task<string?> ProbeOnceAsync(Func<string?>? probe, CancellationToken cancellation)
        {
            if (probe is null) { return null; }
            var left = Remaining;
            if (left <= TimeSpan.Zero) { return Unknown; }
            using var deadline = new CancellationTokenSource(left, Time);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
            try { return await Task.Run(probe, stop.Token).WaitAsync(stop.Token); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellation.IsCancellationRequested) { return Unknown; }
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
        using var deadline = new CancellationTokenSource(leftAtStart, time);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellation, deadline.Token);
        var began = budget.Began;
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
