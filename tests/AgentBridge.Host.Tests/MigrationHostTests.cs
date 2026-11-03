using System.Text.Json;
using AgentBridge.Host.Migration;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The host a migration restarts, over a real pipe to a stand-in that answers status and restart:
// the restart is done once the old host has gone, however long its shutdown, so the migration's
// own wait is for the new host alone (#239's review).
public sealed class MigrationHostTests : IAsyncLifetime
{
    // Short: a Unix socket path must fit about 100 bytes.
    readonly string profile = Path.Combine(Path.GetTempPath(), "abm-" + Guid.NewGuid().ToString("N")[..8]);
    readonly TaskCompletionSource exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly FakeTimeProvider clock = new(new DateTimeOffset(2026, 11, 2, 18, 0, 0, TimeSpan.Zero));
    readonly List<string> asked = [];
    int? watched;
    LocalServer server = null!;

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(profile);
        server = LocalServer.Start(LocalEndpoint.For(profile), (_, op, _) =>
        {
            lock (asked) { asked.Add(op); }
            return Task.FromResult<object?>(op switch
            {
                HostOps.Status => Status(),
                HostOps.Restart => new { restarting = true },
                _ => null,
            });
        });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await server.DisposeAsync();
        try { Directory.Delete(profile, recursive: true); } catch (IOException) { }
    }

    LocalMigrationHost Host() => new(profile)
    {
        Time = clock,
        Watch = pid =>
        {
            watched = pid;
            return ct => exited.Task.WaitAsync(ct);
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

        await UntilAsync(() => watched is not null && Asked(HostOps.Restart));
        // Long past the migration's 45 s for the new host: the old one is still shutting down.
        clock.Advance(TimeSpan.FromMinutes(2));
        Assert.False(restart.IsCompleted);
        exited.SetResult();

        Assert.Null(await restart);
        Assert.Equal(4242, watched);
    }

    [Fact]
    public async Task A_restart_whose_old_host_never_finishes_stopping_is_reported_rather_than_waited_on()
    {
        var restart = Host().RestartAsync(CancellationToken.None);

        await UntilAsync(() => watched is not null && Asked(HostOps.Restart));
        // A step at a time: the timeout's timer may not be set yet when the first is taken.
        while (!restart.IsCompleted)
        {
            clock.Advance(TimeSpan.FromMinutes(1));
            await Task.Delay(10);
        }

        Assert.Equal("it was still stopping after 5 minutes", await restart);
    }

    bool Asked(string op) { lock (asked) { return asked.Contains(op); } }

    static async Task UntilAsync(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!done())
        {
            if (DateTime.UtcNow > deadline) { throw new TimeoutException("timed out"); }
            await Task.Delay(10);
        }
    }
}
