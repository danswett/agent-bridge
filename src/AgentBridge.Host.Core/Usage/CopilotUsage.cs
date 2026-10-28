using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Usage;

// One agent's allowance. Null means there is nothing to say: the agent has never run here
// (it is left off rather than drawn as an empty bar), or a reading would cost something it
// must not (an expired Claude token), in which case the last reading stands and ages.
public interface IUsageCollector
{
    AgentKind Agent { get; }

    Task<AgentUsage?> CollectAsync(CancellationToken cancellationToken = default);

    // A session of this agent has just opened here, so somebody is at the machine. Returns
    // whether that changed anything worth saying.
    bool SessionOpened(DateTimeOffset now) => false;
}

// Copilot's monthly allowance of AI credits, asked of GitHub (api.github.com/copilot_internal/
// user) with the token the CLI itself would spend, and only failing that read from the CLI's
// own cache: the cache is refreshed only when a session starts, and was measured thirteen
// minutes stale in the middle of one, but a stale figure with its age beats nothing.
public sealed class CopilotUsageCollector(UsageOptions options, IUsageHttp http, CopilotCredentials credentials, TimeProvider? time = null) : IUsageCollector
{
    static readonly Uri Quota = new("https://api.github.com/copilot_internal/user");
    readonly TimeProvider clock = time ?? TimeProvider.System;

    public AgentKind Agent => AgentKind.Copilot;

    public bool SessionOpened(DateTimeOffset now) => credentials.ReleaseHold(now);

    public async Task<AgentUsage?> CollectAsync(CancellationToken cancellationToken = default)
    {
        if (Account() is not var (login, host)) { return null; }
        string? failure = null;
        string? token = null;
        try { token = await credentials.ResolveAsync(login, host, cancellationToken); }
        catch (Exception e) when (e is not OperationCanceledException) { failure = $"The Copilot credential could not be read: {UsageFetch.Describe(e)}"; }

        if (token is not null)
        {
            try
            {
                using var body = await UsageFetch.GetJsonAsync(http, Quota,
                    [new("Authorization", $"token {token}"), new("Accept", "application/json"), new("User-Agent", "agent-bridge")], options, cancellationToken);
                if (Convert(body.RootElement, UsageSource.Api, null) is { } read) { return read; }
                failure = "GitHub returned no premium-interaction quota for this account.";
            }
            catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                failure = $"The Copilot quota could not be read: {UsageFetch.Describe(e)}";
            }
        }

        if (Cached(login) is { } cached) { return failure is null ? cached : cached with { Error = failure }; }

        if (failure is null)
        {
            // Says which way to a credential to mend: install gh, sign in to it, or give the
            // host a token.
            var why = credentials.GhReason(login, host);
            failure = why is null ? "No Copilot credential was available and nothing was cached." : $"No Copilot credential was available and nothing was cached ({why}).";
        }
        return UsageRead.Failure(AgentKind.Copilot, failure, clock.GetUtcNow(), account: login);
    }

    // The account whose allowance is being spent here: a login in loggedInUsers, the last one
    // signed in to when there are several. Not every login the cache remembers: a token from
    // the environment leaves an entry behind, and reporting an account nobody is spending
    // from beside the one that is would be wrong.
    internal (string Login, string Host)? Account()
    {
        using var config = UsageRead.Json(Path.Combine(options.CopilotHome, "config.json"), options.MaxStateBytes);
        if (config is null || UsageRead.Property(config.RootElement, "loggedInUsers") is not { ValueKind: JsonValueKind.Array } users) { return null; }
        var logins = new List<(string, string)>();
        foreach (var user in users.EnumerateArray())
        {
            if (UsageRead.String(user, "login") is { Length: > 0 } login) { logins.Add((login, UsageRead.String(user, "host") ?? "https://github.com")); }
        }
        if (logins.Count == 0) { return null; }
        if (logins.Count > 1 && UsageRead.Property(config.RootElement, "lastLoggedInUser") is { } last && UsageRead.String(last, "login") is { } lastLogin)
        {
            foreach (var l in logins) { if (l.Item1 == lastLogin) { return l; } }
        }
        return logins[0];
    }

    AgentUsage? Cached(string login)
    {
        var path = options.CopilotCacheRoots.Select(r => Path.Combine(r, "copilot-user-cache.json")).FirstOrDefault(File.Exists);
        using var cache = UsageRead.Json(path, options.MaxStateBytes);
        if (cache is null || UsageRead.Property(cache.RootElement, "copilotUserCache") is not { ValueKind: JsonValueKind.Object } entries) { return null; }
        JsonElement? newest = null;
        var newestAt = DateTimeOffset.MinValue;
        foreach (var entry in entries.EnumerateObject())
        {
            if (UsageRead.Property(entry.Value, "response") is not { } response || UsageRead.String(response, "login") != login) { continue; }
            if (UsageRead.Instant(entry.Value, "retrievedAt") is { } at && at > newestAt) { (newest, newestAt) = (response, at); }
        }
        return newest is { } n ? Convert(n, UsageSource.Cache, newestAt) : null;
    }

    // Only premium_interactions is drawn. The other snapshots on an enterprise seat report
    // unlimited with a zero entitlement: a bar that is always empty beside the one number
    // that can run out.
    internal AgentUsage? Convert(JsonElement response, UsageSource source, DateTimeOffset? measuredAt)
    {
        if (UsageRead.Property(response, "quota_snapshots") is not { } snapshots || UsageRead.Property(snapshots, "premium_interactions") is not { } premium) { return null; }
        if (UsageRead.Number(premium, "entitlement") is not { } limit || limit <= 0) { return null; }
        var remaining = UsageRead.Number(premium, "remaining") ?? 0;
        var used = Math.Max(0, limit - remaining);
        var resets = UsageRead.Instant(response, "quota_reset_date_utc");
        // Monthly, resetting on the first, so the window began a month before it ends: taken
        // from the reset date rather than from today, so it holds whenever it is read.
        var window = UsageRead.Window("plan", "Plan", 100.0 * used / limit, resets, resets?.AddMonths(-1), used, limit, "AIC");
        return new AgentUsage
        {
            Agent = AgentKind.Copilot,
            Label = UsageRead.Label(AgentKind.Copilot),
            Account = UsageRead.String(response, "login"),
            Plan = UsageRead.String(response, "copilot_plan"),
            Source = source,
            Windows = [window],
            MeasuredAt = measuredAt ?? UsageRead.Instant(premium, "timestamp_utc") ?? clock.GetUtcNow(),
        };
    }
}
