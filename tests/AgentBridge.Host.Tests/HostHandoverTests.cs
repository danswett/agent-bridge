using AgentBridge.Host;
using Xunit;

// host.restart's hand-over to the host that replaces it (#239), with the profile's real lock:
// a restart ends with exactly one host serving the profile, however long the old one takes to
// stop.
public sealed class HostHandoverTests : IDisposable
{
    readonly string profile = Path.Combine(Path.GetTempPath(), "abh-" + Guid.NewGuid().ToString("N")[..8]);
    // The replacements that took the profile's lock, as serve does before anything else.
    readonly List<HostLock> serving = [];

    public HostHandoverTests() => Directory.CreateDirectory(profile);

    public void Dispose()
    {
        foreach (var l in serving) { l.Dispose(); }
        Directory.Delete(profile, recursive: true);
    }

    // A replacement takes the lock or stands back (serve's exit 3). It looks once: by the time it
    // does, a lock wait of any length has run out under a shutdown that outlasts it.
    void StartReplacement()
    {
        if (HostLock.TryAcquire(profile, TimeSpan.Zero) is { } taken) { serving.Add(taken); }
    }

    [Fact]
    public async Task A_restart_whose_old_host_takes_longer_than_the_lock_wait_to_stop_still_ends_with_exactly_one_host_running()
    {
        var disposed = new TaskCompletionSource();
        var old = HostHandover.RunAsync(async handover =>
        {
            using var hostLock = HostLock.TryAcquire(profile, TimeSpan.Zero) ?? throw new InvalidOperationException("the lock was taken");
            handover.Request();
            // Still disposing (a usage probe it waits for, say) long after the restart was asked for.
            await disposed.Task;
            return 0;
        }, StartReplacement);

        disposed.SetResult();

        Assert.Equal(0, await old);
        Assert.Single(serving);
        Assert.Null(HostLock.TryAcquire(profile, TimeSpan.Zero));
    }

    [Fact]
    public async Task A_restart_whose_old_host_fails_on_its_way_out_still_starts_its_replacement()
    {
        var old = HostHandover.RunAsync(async handover =>
        {
            using var hostLock = HostLock.TryAcquire(profile, TimeSpan.Zero) ?? throw new InvalidOperationException("the lock was taken");
            handover.Request();
            await Task.Yield();
            throw new IOException("a disposal failed");
        }, StartReplacement);

        await Assert.ThrowsAsync<IOException>(() => old);
        Assert.Single(serving);
    }
}
