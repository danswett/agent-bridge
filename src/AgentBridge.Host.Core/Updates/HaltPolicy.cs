using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentBridge.Host.Core.Updates;

// Sequence > 0 marks a signed policy (SignedHaltPolicy); 0 is the legacy GitHub digest path.
public sealed record VerifiedHaltPolicy(long Release, string Body, string[] Halted, long Sequence = 0, DateTimeOffset? Expires = null);
public sealed record HaltPolicySnapshot(VerifiedHaltPolicy? Policy, DateTimeOffset? At, bool Fresh);

public sealed partial class ReleaseDiscovery
{
    public static readonly TimeSpan PolicyInterval = TimeSpan.FromHours(1);
    public const string HaltPolicyList = "https://api.github.com/repos/danswett/agent-bridge/releases?per_page=100";
    const int PolicyLargest = 4 * 1024 * 1024;
    internal sealed record HaltPolicyCache(VerifiedHaltPolicy? Policy, DateTimeOffset? At, DateTimeOffset NotBefore, string? ETag,
        DateTimeOffset? AttemptedAt = null, long Attempt = 0, long Verified = 0);

    public HaltPolicySnapshot HaltPolicy => PolicyView(memory.HaltPolicy);
    HaltPolicySnapshot PolicyView(HaltPolicyCache? policy) => new(policy?.Policy, policy?.At,
        !unsaved && policy?.Policy?.Body is not null && policy.At is { } at && at <= time.GetUtcNow()
        && time.GetUtcNow() - at < GithubRevalidateEvery && (policy.Policy.Sequence == 0 || policy.Policy.Expires > time.GetUtcNow()));
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
            if (held is null) { log?.Invoke("update policy: shared lock unavailable; no policy request made"); return PolicyView(policy); }
            // Legacy path: once a signed policy is held, no unsigned GitHub answer may replace or renew it.
            if (policy.Policy is { Sequence: > 0 }) { return PolicyView(policy); }
            if (policy.AttemptedAt > now || policy.NotBefore > now + PolicyInterval + BackoffJitter)
            {
                policy = policy with { At = null, NotBefore = now + PolicyInterval, AttemptedAt = now, Attempt = NextAttempt(policy) };
                state = state with { HaltPolicy = policy };
                Write(state);
            }
            if (now < policy.NotBefore || state.NotBefore is { } until && now < until) { return PolicyView(policy); }
            // Reserve budget and revoke freshness durably before any request.
            policy = policy with { NotBefore = now + PolicyInterval, AttemptedAt = now, Attempt = NextAttempt(policy) };
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
                if (answer.Status == HttpStatusCode.NotModified && policy.Policy is not null && policy.ETag is not null) { policy = policy with { At = now, Verified = policy.Attempt }; }
                else if (answer.Status == HttpStatusCode.OK && answer.Body is { } body)
                {
                    var verified = await verify(body, cancellationToken);
                    if (policy.Policy is null || verified.Release >= policy.Policy.Release) { policy = policy with { Policy = verified, At = now, ETag = answer.ETag, Verified = policy.Attempt }; }
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

    // The one door for a signed policy from any source (GitHub asset, CDN, relay copy). `verify` throws InvalidDataException
    // naming the refusal; a refusal changes nothing stored, so it never lowers protection. Only a newer sequence lifts a halt.
    public async Task<HaltPolicySnapshot> OfferSignedHaltPolicyAsync(string source, Func<DateTimeOffset, VerifiedHaltPolicy> verify, CancellationToken cancellationToken = default)
    {
        await turn.WaitAsync(cancellationToken);
        try
        {
            using var held = await LockAsync(cancellationToken);
            var now = time.GetUtcNow();
            var state = Sane(Read(), now);
            var policy = state.HaltPolicy ?? new(null, null, DateTimeOffset.MinValue, null);
            var why = "shared lock unavailable";
            try
            {
                if (held is not null && verify(now) is var offered)
                {
                    var known = policy.Policy is { Sequence: > 0 } signed ? signed : null;
                    why = offered.Sequence < 1 ? "unsigned" : offered.Sequence < known?.Sequence ? "older sequence"
                        : offered.Sequence == known?.Sequence && (offered.Body != known.Body || offered.Expires != known.Expires) ? "sequence reused" : "";
                    if (why.Length == 0)
                    {
                        var generation = NextAttempt(policy);
                        Write(state with { HaltPolicy = policy = policy with { Policy = offered, At = now, ETag = null, Attempt = generation, Verified = generation } });
                        return PolicyView(policy);
                    }
                }
            }
            catch (InvalidDataException e) { why = e.Message; }
            catch (Exception e) when (e is JsonException or FormatException or CryptographicException or ArgumentException or InvalidOperationException or KeyNotFoundException) { why = "malformed"; }
            catch (IOException) { why = "cache unavailable"; }
            log?.Invoke($"update policy: signed policy from {source} refused ({why}); stored policy kept");
            return PolicyView(policy);
        }
        finally { turn.Release(); }
    }
    static long NextAttempt(HaltPolicyCache policy) =>
        policy.Attempt >= policy.Verified && policy.Attempt is >= 0 and < long.MaxValue ? policy.Attempt + 1 : throw new IOException("update policy: cache generation unavailable");
    // Verification generations, not Saved or clock stamps, order same-release changes.
    static HaltPolicyCache? MergePolicy(HaltPolicyCache? known, HaltPolicyCache? disk)
    {
        if (known is null) { return disk; }
        if (disk is null) { return known with { At = null }; }
        var (before, after) = (known.Policy, disk.Policy);
        var chosen = before is not null && (after is null || (before.Sequence, before.Release, known.Verified).CompareTo((after.Sequence, after.Release, disk.Verified)) > 0) ? known with { At = null } : disk;
        if (before is not null && after is not null && before.Sequence == after.Sequence && before.Release == after.Release && known.Verified == disk.Verified
            && (before.Body != after.Body || !(before.Halted ?? []).SequenceEqual(after.Halted ?? [], StringComparer.Ordinal)))
        { chosen = chosen with { Policy = before with { Halted = [.. (before.Halted ?? []).Union(after.Halted ?? [], StringComparer.Ordinal)] }, At = null, ETag = null }; }
        var budget = known.Attempt > disk.Attempt || known.Attempt == disk.Attempt && known.NotBefore > disk.NotBefore ? known : disk;
        return chosen with { Attempt = budget.Attempt, NotBefore = budget.NotBefore, AttemptedAt = budget.AttemptedAt };
    }
}
