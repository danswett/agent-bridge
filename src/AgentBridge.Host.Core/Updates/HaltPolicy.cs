using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentBridge.Host.Core.Updates;

public sealed record VerifiedHaltPolicy(long Release, string Body, string[] Halted);
public sealed record HaltPolicySnapshot(VerifiedHaltPolicy? Policy, DateTimeOffset? At, bool Fresh);

public sealed partial class ReleaseDiscovery
{
    public static readonly TimeSpan PolicyInterval = TimeSpan.FromHours(1);
    // The first ten releases: halts.json is on the newest, and a hundred releases of a dozen assets each ran past PolicyLargest, so
    // the read failed on every machine (VM2, v0.1.7522). A newest release outside the first ten renews nothing (fail closed).
    public const string HaltPolicyList = "https://api.github.com/repos/danswett/agent-bridge/releases?per_page=10";
    // The relay's copy of the same list (the Worker serves per_page 1-100), read only while GitHub's policy read fails and the relay grace is open.
    public const string CdnPolicyList = "https://releases.slipgates.com/repos/danswett/agent-bridge/releases?per_page=10";
    const int PolicyLargest = 4 * 1024 * 1024;
    internal sealed record HaltPolicyCache(VerifiedHaltPolicy? Policy, DateTimeOffset? At, DateTimeOffset NotBefore, string? ETag,
        DateTimeOffset? AttemptedAt = null, long Attempt = 0, long Verified = 0);

    public HaltPolicySnapshot HaltPolicy => PolicyView(memory.HaltPolicy);
    HaltPolicySnapshot PolicyView(HaltPolicyCache? policy) => new(policy?.Policy, policy?.At,
        !unsaved && policy?.Policy?.Body is not null && policy.At is { } at && at <= time.GetUtcNow()
        && time.GetUtcNow() - at < GithubRevalidateEvery);
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
                if (answer.Status == HttpStatusCode.NotModified && policy.Policy is not null && policy.ETag is not null)
                {
                    policy = policy with { At = now, Verified = policy.Attempt };
                    Renewed("GitHub (not modified)", policy);
                }
                else if (answer.Status == HttpStatusCode.OK && answer.Body is { } body)
                {
                    var verified = await verify(body, cancellationToken);
                    if (policy.Policy is null || verified.Release >= policy.Policy.Release)
                    {
                        policy = policy with { Policy = verified, At = now, ETag = answer.ETag, Verified = policy.Attempt };
                        Renewed("GitHub", policy);
                    }
                    else { log?.Invoke("update policy: older GitHub policy ignored; known halts retained"); }
                }
                else
                {
                    if (answer.Status is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
                    {
                        state = BackedOff(state, answer, now, HaltPolicyList);
                        if (answer.Wait is { } wait && now + Clamp(wait) > policy.NotBefore) { policy = policy with { NotBefore = now + Clamp(wait) }; }
                    }
                    // GitHub's policy read failed (a limit, a 5xx or other refusal, no answer at all, a body past the cap or not a
                    // release list), the relay grace of the release list is open, and the relay's copy of the same list is
                    // fresh: its halts.json carrier is still downloaded from github.com and checked against the size and
                    // sha256 the list names, so a relay can hide a new halt for at most the grace, never forge one.
                    // GitHub's own answer wins.
                    if (answer.Status != HttpStatusCode.NotModified && RelayWindow(state, now)
                        && await AskCdnPolicyAsync(cancellationToken) is { } relayed)
                    {
                        var verified = await verify(relayed.Body, cancellationToken);
                        // The relay path never clears a halt: a policy that omits one already known is
                        // refused, whole. Only GitHub's own answer clears (a reviewed edit).
                        var omitted = policy.Policy?.Halted?.Except(verified.Halted ?? [], StringComparer.Ordinal).Any() == true;
                        if (omitted) { log?.Invoke("update policy: a relay policy that omits a known halt was refused; known halts retained"); }
                        else if (policy.Policy is null || verified.Release >= policy.Policy.Release)
                        {
                            // Stale a revalidation period before the relay grace ends, so a halt it hides lasts no
                            // longer than the grace (the 6 hours of freshness never run past its end).
                            var graceEnd = (state.GitHubAnsweredAt ?? state.RelayBootstrapAt)!.Value + GithubRelayAtMost;
                            policy = policy with { Policy = verified, At = Earlier(now - relayed.Age, graceEnd - GithubRevalidateEvery), ETag = null, Verified = policy.Attempt };
                            Renewed($"the relay's list, its copy {Span(relayed.Age)} old; {GitHubSaid(answer)}", policy);
                        }
                        else { log?.Invoke("update policy: older relay policy ignored; known halts retained"); }
                    }
                    else { log?.Invoke($"update policy: GitHub policy unavailable ({GitHubSaid(answer)}); stored policy was not renewed"); }
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

    // The relay's list for the policy carrier: its own 200 for exactly this page, within the wait,
    // saying how old its copy is (at most CdnOldest), and no larger than a GitHub list may be.
    async Task<(string Body, TimeSpan Age)?> AskCdnPolicyAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, CdnPolicyList);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bounded.CancelAfter(CdnWait);
        try
        {
            using var response = await cdnHttp.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token);
            var refused = response.StatusCode != HttpStatusCode.OK ? $"the relay answered {(int)response.StatusCode}"
                : response.RequestMessage?.RequestUri?.AbsoluteUri is { } answered && answered != CdnPolicyList ? "the relay sent the check elsewhere"
                : response.Headers.Age is not { } age ? "the relay didn't say how old its copy is"
                : age < TimeSpan.Zero || age > CdnOldest ? $"its copy was {Span(age)} old"
                : response.Content.Headers.ContentLength > PolicyLargest ? "its list was too large" : null;
            if (refused is not null) { log?.Invoke($"update policy: the relay's list was not used, as {refused}"); return null; }
            await using var stream = await response.Content.ReadAsStreamAsync(bounded.Token);
            using var bytes = new MemoryStream();
            var buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, bounded.Token)) > 0)
            {
                if (bytes.Length + read > PolicyLargest) { log?.Invoke("update policy: the relay's list was not used, as it was too large"); return null; }
                bytes.Write(buffer, 0, read);
            }
            return (System.Text.Encoding.UTF8.GetString(bytes.GetBuffer(), 0, (int)bytes.Length), response.Headers.Age!.Value);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException || e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            log?.Invoke("update policy: the relay could not be reached");
            return null;
        }
    }

    static DateTimeOffset Earlier(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;

    // The fleet's positive signal: ONE info line on every successful renewal, with where it came from, the release the policy
    // names and the time it now counts from (At; it is fresh for six hours from it).
    void Renewed(string from, HaltPolicyCache policy) =>
        log?.Invoke($"update policy: renewed from {from}, release 0.1.{policy.Policy?.Release}, At={policy.At:O}");

    // What GitHub really did, for the log: the status it sent, or that nothing came back (a timeout, a broken connection or a body past the cap).
    static string GitHubSaid(Answer answer) => answer.Status == 0 ? "GitHub gave no answer"
        : answer.Status == HttpStatusCode.OK ? "GitHub answered HTTP 200 with something other than a release list"
        : $"GitHub answered HTTP {(int)answer.Status}";

    static long NextAttempt(HaltPolicyCache policy) =>
        policy.Attempt >= policy.Verified && policy.Attempt is >= 0 and < long.MaxValue ? policy.Attempt + 1 : throw new IOException("update policy: cache generation unavailable");
    // Verification generations, not Saved or clock stamps, order same-release changes.
    static HaltPolicyCache? MergePolicy(HaltPolicyCache? known, HaltPolicyCache? disk)
    {
        if (known is null) { return disk; }
        if (disk is null) { return known with { At = null }; }
        var (before, after) = (known.Policy, disk.Policy);
        var chosen = before is not null && (after is null || (before.Release, known.Verified).CompareTo((after.Release, disk.Verified)) > 0) ? known with { At = null } : disk;
        if (before is not null && after is not null && before.Release == after.Release && known.Verified == disk.Verified
            && (before.Body != after.Body || !(before.Halted ?? []).SequenceEqual(after.Halted ?? [], StringComparer.Ordinal)))
        { chosen = chosen with { Policy = before with { Halted = [.. (before.Halted ?? []).Union(after.Halted ?? [], StringComparer.Ordinal)] }, At = null, ETag = null }; }
        var budget = known.Attempt > disk.Attempt || known.Attempt == disk.Attempt && known.NotBefore > disk.NotBefore ? known : disk;
        return chosen with { Attempt = budget.Attempt, NotBefore = budget.NotBefore, AttemptedAt = budget.AttemptedAt };
    }
}
