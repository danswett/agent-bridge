internal static class MigrationProcessStartup
{
    internal static Task StartAsync(IEnumerable<string> scripts, Func<string, Task> startReady) =>
        Task.WhenAll(scripts.Select(startReady));
}
