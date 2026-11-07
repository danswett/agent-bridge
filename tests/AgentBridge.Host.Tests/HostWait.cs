using System.Diagnostics;

// The Host suite's version of the Transport test stack's polling convention.
// Keep its condition, deadline and polling interval; append diagnostics only on failure.
static class HostWait
{
    public static async Task UntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var sw = Stopwatch.StartNew();
        var limit = timeout ?? TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (sw.Elapsed > limit)
                throw new TimeoutException($"timed out waiting for {what} ({TestTimeoutDiagnostics.Describe()})");
            await Task.Delay(10);
        }
    }
}
