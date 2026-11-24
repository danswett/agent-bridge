namespace AgentBridge.Host.Delivery.Launch;

internal sealed class LaunchedConsoleSize(IConsoleInjector injector, Started started, Action<string> log)
{
    int firstRead;

    public Task AtLaunchAsync() => ApplyAsync();

    public Task FirstReadAsync() => Interlocked.Exchange(ref firstRead, 1) == 0 ? ApplyAsync() : Task.CompletedTask;

    async Task ApplyAsync()
    {
        try
        {
            var result = await injector.SizeForLaunchAsync(started.ProcessId, started.StartedAt, 100);
            if (result.Problem is not null || result.Columns is not >= 100)
            { log($"launch console sizing: {result.Problem ?? "minimum width was not reached"}"); }
        }
        catch (Exception e)
        {
            // This worker never types. Sizing failure cannot authorize any reply.
            log($"launch console sizing failed: {e.GetType().Name}");
        }
    }
}
