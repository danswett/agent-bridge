namespace AgentBridge.Relay;

// A reconnect storm writes a connect and a disconnect line per socket and can fill the daily log
// cap (#571), which then drops the lines that matter. Within each one-minute window the first
// `perMinute` lines pass, the rest are dropped, and the next line after the window logs one
// summary with the count. Metrics are counted elsewhere and never sampled. Zero turns sampling off.
// The summary is written by the first line of a later window, so a storm that ends the relay's
// traffic leaves its count unwritten until the next connect or disconnect.
public sealed class LogSampler(ILogger log, int perMinute, TimeProvider time)
{
    static readonly TimeSpan Window = TimeSpan.FromMinutes(1);
    readonly object gate = new();
    DateTimeOffset windowStart;
    int passed;
    long suppressed;

    public void Information(string message, params object?[] args)
    {
        long summary = 0;
        if (perMinute > 0)
        {
            lock (gate)
            {
                var now = time.GetUtcNow();
                if (now - windowStart >= Window)
                {
                    summary = suppressed;
                    windowStart = now;
                    passed = 0;
                    suppressed = 0;
                }
                if (passed >= perMinute)
                {
                    suppressed++;
                    return;
                }
                passed++;
            }
        }
        if (summary > 0)
        {
            log.LogInformation("{Suppressed} connect/disconnect lines suppressed in the last minute (limit {Limit} per minute)", summary, perMinute);
        }
        log.Log(LogLevel.Information, message, args);
    }
}
