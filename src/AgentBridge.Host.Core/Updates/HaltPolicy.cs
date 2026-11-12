using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentBridge.Host.Core.Updates;

public sealed record VerifiedHaltPolicy(long Release, string Body, string[] Halted);
public sealed record HaltPolicySnapshot(VerifiedHaltPolicy? Policy, DateTimeOffset? At, bool Fresh);

public sealed partial class ReleaseDiscovery
{
    public static readonly TimeSpan PolicyInterval = TimeSpan.FromHours(1);
    public const string HaltPolicyList = "https://api.github.com/repos/danswett/agent-bridge/releases?per_page=100";
    internal sealed record HaltPolicyCache(VerifiedHaltPolicy? Policy, DateTimeOffset? At, DateTimeOffset NotBefore, string? ETag, DateTimeOffset? AttemptedAt = null);

    public HaltPolicySnapshot HaltPolicy => PolicyView(memory.HaltPolicy);

    HaltPolicySnapshot PolicyView(HaltPolicyCache? policy) => new(policy?.Policy, policy?.At,
        !unsaved && policy?.Policy is not null && policy.At is { } at && at <= time.GetUtcNow()
        && time.GetUtcNow() - at < GithubRevalidateEvery);

    // Only verified GitHub assets enter this cache; relay metadata never does.
    public async Task<HaltPolicySnapshot> ReadHaltPolicyAsync(
        Func<string, CancellationToken, Task<VerifiedHaltPolicy>> verify, CancellationToken cancellationToken = default)
    {
        await turn.WaitAsync(cancellationToken);
        try
        {
            using var held = await LockAsync(cancellationToken);
            var now = time.GetUtcNow();
            var state = Sane(Read(), now);
            var policy = state.HaltPolicy ?? new(null, null, DateTimeOffset.MinValue, null);
            if (held is null)
            {
                log?.Invoke("update policy: shared lock unavailable; no policy request made");
                return PolicyView(policy);
            }
            if (policy.AttemptedAt is { } attempted && attempted > now)
            {
                var hold = policy.NotBefore - attempted;
                policy = policy with { At = null, NotBefore = now + (hold > PolicyInterval ? hold : PolicyInterval), AttemptedAt = now };
                state = state with { HaltPolicy = policy };
                Write(state);
            }
            if (now < policy.NotBefore || state.NotBefore is { } until && now < until) { return PolicyView(policy); }
            // Reserve the shared hourly budget before any request.
            policy = policy with { NotBefore = now + PolicyInterval, AttemptedAt = now };
            // Clear freshness durably before a request can discover a new halt.
            state = state with { HaltPolicy = policy with { At = null } };
            Write(state);
            if (unsaved)
            {
                log?.Invoke("update policy: request budget could not be saved; no policy request made");
                return PolicyView(policy);
            }
            var answer = await AskAsync(HaltPolicyList, policy.ETag, cancellationToken);
            state = ObservedQuota(state, answer);
            now = time.GetUtcNow();
            try
            {
                if (answer.Status == HttpStatusCode.NotModified && policy.Policy is not null) { policy = policy with { At = now }; }
                else if (answer.Status == HttpStatusCode.OK && answer.Body is { } body)
                {
                    var verified = await verify(body, cancellationToken);
                    if (policy.Policy is null || verified.Release >= policy.Policy.Release) { policy = policy with { Policy = verified, At = now, ETag = answer.ETag }; }
                    else { log?.Invoke("update policy: older GitHub policy ignored; known halts retained"); }
                }
                else
                {
                    if (answer.Status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                    {
                        state = BackedOff(state, answer, now, HaltPolicyList);
                        if (answer.Wait is { } wait && now + Clamp(wait) > policy.NotBefore) { policy = policy with { NotBefore = now + Clamp(wait) }; }
                    }
                    log?.Invoke("update policy: GitHub policy unavailable; stored policy was not renewed");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or JsonException
                or InvalidOperationException or KeyNotFoundException or FormatException or CryptographicException
                || e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
            {
                log?.Invoke($"update policy: verification failed ({e.GetType().Name}); stored policy was not renewed");
            }
            Write(state with { HaltPolicy = policy });
            return PolicyView(policy);
        }
        finally { turn.Release(); }
    }
}
