using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

internal enum RetryReason { Auto, Owner }
internal enum RetryStop { None, Changed, Ineligible, Expired, Exhausted, Cancelled, Failed }
internal sealed record RetryObservation(JsonElement Error, DateTimeOffset ErrorAt, SessionProcess Process, long ActivityVersion, bool CanReply);
internal sealed record RetryAttempt(int Number, DateTimeOffset At, AnswerOutcome? Outcome);

internal sealed class RetryEpisode
{
    public RetryEpisode(Guid session, Func<RetryObservation?> observe, ISessionReplier replier, TimeProvider time, Action<string> log) { }
    public int NextAttempt => 1;
    public RetryStop Stop => RetryStop.None;
    public IReadOnlyList<RetryAttempt> Attempts => [];
    public void Cancel() { }
    public Task<bool> TryAsync(int expectedAttempt, Guid request, RetryReason reason, CancellationToken cancellation = default) => Task.FromResult(false);
}
