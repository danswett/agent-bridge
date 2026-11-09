using AgentBridge.Host.Migration;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The host a migration restarts, over a real pipe to a stand-in that answers status and restart:
// the restart is done once the old host has gone, however long its shutdown, so the migration's
// own wait is for the new host alone (#239's review).
[Trait("Category", "Network")]
public sealed class MigrationHostTests : IAsyncLifetime
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    // Short: a Unix socket path must fit about 100 bytes.
    readonly string profile = Path.Combine(Path.GetTempPath(), "abm-" + Guid.NewGuid().ToString("N")[..8]);
    readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.Zero));
    // Signals rather than polling: the old host watched, and the wait for it begun (the timeout
    // already set by then, so one advance of the clock is enough).
    readonly TaskCompletionSource<int> watched = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly TaskCompletionSource waiting = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Whether the wait was torn down when it was given up on.
    CancellationToken waitedWith;
    LocalServer server = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(profile);
        server = LocalServer.Start(LocalEndpoint.For(profile), (_, op, _) => Task.FromResult<object?>(op switch
        {
            HostOps.Status => Status(),
            HostOps.Restart => new { restarting = true },
            _ => null,
        }));
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();
        try { Directory.Delete(profile, recursive: true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    // A cold pipe on a loaded windows-11-arm runner took past the host's 2 s to connect (#1478):
    // RestartAsync then returned its error, `waiting` was never signalled and the test sat out its
    // 10 s. A passing run doesn't wait these out, so they can be long.
    LocalMigrationHost Host() => new(profile)
    {
        Time = clock,
        ConnectTimeout = TimeSpan.FromSeconds(60),
        CallTimeout = TimeSpan.FromSeconds(60),
        Watch = (pid, _) =>
        {
            watched.TrySetResult(pid);
            return ct =>
            {
                waitedWith = ct;
                waiting.TrySetResult();
                return exited.Task.WaitAsync(ct);
            };
        },
    };

    static HostStatus Status() => new()
    {
        Version = "0.1.0",
        Profile = "test",
        Folder = "x",
        ProcessId = 4242,
        StartedAt = DateTimeOffset.UtcNow,
        Machine = new MachineInfo("host-id", "Test PC", "windows", "0.1.0"),
        Relay = new RelayStatus(RelayState.SignedOut),
        SignIn = new SignInStatus(SignInState.Idle),
        Settings = new HostSettingsView(true),
    };

    [Fact]
    public async Task A_restart_for_a_migration_is_done_only_once_the_old_host_has_gone()
    {
        var restart = Host().RestartAsync(CancellationToken.None);

        await waiting.Task.WaitAsync(Patience);
        // Long past the migration's 45 s for the new host: the old one is still shutting down.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(restart.IsCompleted);
        exited.SetResult();

        Assert.Null(await restart);
        Assert.Equal(4242, await watched.Task);
    }

    [Fact]
    public async Task A_restart_whose_old_host_never_finishes_stopping_is_reported_and_its_wait_given_up()
    {
        var restart = Host().RestartAsync(CancellationToken.None);

        await waiting.Task.WaitAsync(Patience);
        clock.Advance(TimeSpan.FromMinutes(5));

        Assert.Equal("it was still stopping after 5 minutes", await restart.WaitAsync(Patience));
        Assert.True(waitedWith.IsCancellationRequested);
    }

    [Fact]
    public async Task A_process_with_the_old_hosts_pid_that_started_after_it_is_not_waited_on()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var started = new DateTimeOffset(self.StartTime.ToUniversalTime(), TimeSpan.Zero);
        using var cancel = new CancellationTokenSource();

        // The host said it started before this process did: this one only reuses its pid.
        var reused = LocalMigrationHost.WatchProcess(self.Id, started.AddHours(-1))(cancel.Token);
        // One that did start before the host said so is waited on.
        var same = LocalMigrationHost.WatchProcess(self.Id, started.AddSeconds(1))(cancel.Token);

        Assert.True(reused.IsCompletedSuccessfully);
        Assert.False(same.IsCompleted);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => same);
    }
}
