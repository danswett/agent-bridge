using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery.Launch;
using Xunit;

public sealed class AgencyCatalogScanTests
{
    sealed class SlowRunner : ICommandRunner
    {
        public int Scans;
        public readonly SemaphoreSlim Started = new(0);
        public readonly SemaphoreSlim Finish = new(0);

        public async Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Scans);
            Started.Release();
            await Finish.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
            return new CommandResult(true, false, 0, $$"""{"sessions":[{"session_id":"{{Guid.NewGuid():D}}","can_resume":true,"summary":"s"}]}""");
        }
    }

    [Fact]
    public async Task A_never_scanned_catalog_answers_at_once_as_not_ready_and_the_finished_scan_is_there_on_the_next_ask()
    {
        var runner = new SlowRunner();
        var catalog = new AgencyCatalog(runner);

        var first = catalog.CachedSessions("agency");

        Assert.False(first.Ready);
        Assert.Empty(first.Entries);
        await runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        runner.Finish.Release();
        for (var i = 0; i < 100 && !catalog.CachedSessions("agency").Ready; i++) { await Task.Delay(50); }
        var next = catalog.CachedSessions("agency");
        Assert.True(next.Ready);
        Assert.Single(next.Entries);
        Assert.Equal(1, runner.Scans);
    }

    [Fact]
    public async Task An_event_during_a_scan_makes_one_more_scan_after_it_and_a_held_list_is_served_meanwhile()
    {
        var runner = new SlowRunner();
        var catalog = new AgencyCatalog(runner);
        catalog.Refresh("agency");
        await runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        runner.Finish.Release();
        for (var i = 0; i < 100 && !catalog.CachedSessions("agency").Ready; i++) { await Task.Delay(50); }
        var held = catalog.CachedSessions("agency").Entries.Single().Id;

        catalog.Refresh("agency");
        catalog.Refresh("agency");
        await runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(held, catalog.CachedSessions("agency").Entries.Single().Id);
        runner.Finish.Release();
        await runner.Started.WaitAsync(TimeSpan.FromSeconds(10));
        runner.Finish.Release();
        for (var i = 0; i < 100 && catalog.CachedSessions("agency").Entries.Single().Id == held; i++) { await Task.Delay(50); }

        Assert.Equal(3, runner.Scans);
        Assert.NotEqual(held, catalog.CachedSessions("agency").Entries.Single().Id);
    }
}
