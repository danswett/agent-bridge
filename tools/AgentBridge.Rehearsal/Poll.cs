using System.Net.WebSockets;
using System.Text.Json;

namespace AgentBridge.Rehearsal;

internal static class Poll
{
    // Asked again until it passes or the deadline does. Something that cannot be reached yet
    // (a host or Home Assistant restarting, as in E2-07 and E18-03) is a reason to ask again,
    // not an answer; it is an error only if it is still so at the deadline.
    public static async Task<Outcome> UntilAsync(string what, Func<CancellationToken, Task<Outcome>> attempt, DateTimeOffset until, TimeSpan every, CancellationToken ct)
    {
        while (true)
        {
            Outcome outcome;
            try { outcome = await attempt(ct); }
            catch (Exception e) when (e is HttpRequestException or IOException or WebSocketException or JsonException or TimeoutException
                || (e is TaskCanceledException && !ct.IsCancellationRequested))
            {
                outcome = Outcome.Error($"{what} could not be read: {e.Message}");
            }
            if (outcome.Verdict == Verdict.Pass || DateTimeOffset.UtcNow >= until) { return outcome; }
            await Task.Delay(every, ct);
        }
    }

    public static DateTimeOffset Deadline(Parsed p) => p.Option("--timeout") is null ? DateTimeOffset.MinValue : DateTimeOffset.UtcNow + p.Timeout(0);
}
