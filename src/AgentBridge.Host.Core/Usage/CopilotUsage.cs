using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Usage;

// One agent's allowance. Null means there is nothing to say: the agent has never run here
// (it is left off rather than drawn as an empty bar), or a reading would cost something it
// must not (an expired Claude token), in which case the last reading stands and ages.
public interface IUsageCollector
{
    AgentKind Agent { get; }

    // What the monitor keeps this collector's reading under: the agent's name, or for a further
    // Copilot account, copilot_<slot>, as agent-ha-bridge names its sensors.
    string Key => Agent.ToString().ToLowerInvariant();

    // A further account's place (usage.copilotAccounts), or null for the agent's signed-in one.
    // Its reading is never what the sessions here spend, so it is asked at the idle pace.
    int? Slot => null;

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

    // One /copilot_internal/user read with the token given: the request both Copilot readers
    // make, written once so the two cannot drift apart.
    internal static Task<JsonDocument> FetchAsync(IUsageHttp http, string token, UsageOptions options, CancellationToken cancellationToken) =>
        UsageFetch.GetJsonAsync(http, Quota,
            [new("Authorization", $"token {token}"), new("Accept", "application/json"), new("User-Agent", "agent-bridge")], options, cancellationToken);

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
                using var body = await FetchAsync(http, token, options, cancellationToken);
                if (Convert(body.RootElement, UsageSource.Api, null, clock) is { } read) { return read; }
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
        return newest is { } n ? Convert(n, UsageSource.Cache, newestAt, clock) : null;
    }

    // Only premium_interactions is drawn. The other snapshots on an enterprise seat report
    // unlimited with a zero entitlement: a bar that is always empty beside the one number
    // that can run out.
    internal static AgentUsage? Convert(JsonElement response, UsageSource source, DateTimeOffset? measuredAt, TimeProvider clock)
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

// A further Copilot login that usage.copilotAccounts names, in its own row (agent-ha-bridge's
// Get-BridgeCopilotLoginAllowance, #200).
//
// Its token comes from gh, asked for exactly that login, and from nothing else: the environment's
// token and the CLI's stored login belong to whoever Copilot is signed in as, and spent for
// another login they would publish the wrong account's figure under this one's name. That is also
// why an answer is checked against the login before it is believed. Nor is the CLI's cache a
// fallback: only the sessions Copilot runs refresh it, and those spend the signed-in account. A
// login gh holds no token for is reported as having no credential, with gh's reason, rather than
// disappearing.
public sealed class CopilotAccountCollector(UsageOptions options, IUsageHttp http, CopilotCredentials credentials, CopilotAccount account, TimeProvider? time = null) : IUsageCollector
{
    readonly TimeProvider clock = time ?? TimeProvider.System;
    // How GitHub spells the login, as this host last read it. A failure only has the spelling the
    // list gave, and a client grouping rows by account would otherwise draw one account twice.
    string spelling = account.Login;

    public AgentKind Agent => AgentKind.Copilot;
    public string Key => $"copilot_{account.Slot}";
    public int? Slot => account.Slot;

    // The last failure as the log may say it, with no account in it: the reading's error names the
    // login, for the account's own devices, and the host's log names none. Null after a reading.
    public string? Plain { get; private set; }

    public async Task<AgentUsage?> CollectAsync(CancellationToken cancellationToken = default)
    {
        var login = account.Login;
        string? token;
        try { token = await credentials.FromGhOnlyAsync(login, $"further account {account.Slot}", cancellationToken); }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            var why = UsageFetch.Describe(e);
            return Failed($"The Copilot credential for {login} could not be read: {why}", $"its credential could not be read: {why}");
        }
        if (token is null)
        {
            var why = credentials.GhReason(login, CopilotCredentials.GitHub);
            return Failed(why is null ? $"No Copilot credential was available for {login}." : $"No Copilot credential was available for {login} ({why}).",
                why is null ? "no Copilot credential was available" : $"no Copilot credential was available ({why})");
        }
        try
        {
            using var body = await CopilotUsageCollector.FetchAsync(http, token, options, cancellationToken);
            if (CopilotUsageCollector.Convert(body.RootElement, UsageSource.Api, null, clock) is not { } read)
            {
                return Failed($"GitHub returned no premium-interaction quota for {login}.", "GitHub returned no premium-interaction quota for it");
            }
            // Logins are not case-sensitive, so neither is this.
            if (read.Account is { Length: > 0 } answered && !string.Equals(answered, login, StringComparison.OrdinalIgnoreCase))
            {
                return Failed($"The token gh holds for {login} reads the allowance of {answered} instead.", "the token gh holds for it reads another account's allowance");
            }
            spelling = read.Account is { Length: > 0 } a ? a : spelling;
            Plain = null;
            return read with { Account = spelling, Slot = account.Slot };
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var why = UsageFetch.Describe(e);
            return Failed($"The Copilot quota for {login} could not be read: {why}", $"its quota could not be read: {why}");
        }
    }

    AgentUsage Failed(string error, string plain)
    {
        Plain = plain;
        return UsageRead.Failure(AgentKind.Copilot, error, clock.GetUtcNow(), account: spelling) with { Slot = account.Slot };
    }
}
