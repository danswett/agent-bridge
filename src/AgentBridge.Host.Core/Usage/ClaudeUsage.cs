using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Usage;

// Claude's session and weekly windows, asked of Anthropic (api.anthropic.com/api/oauth/usage)
// with the OAuth token Claude Code keeps beside its settings.
//
// The token is never refreshed, and an expired one is never spent. Anthropic issues a new
// refresh token with each use and retires the old one, so a refresh from here would either
// sign Claude Code out or race its own write of the new one. It expires about hourly, so that
// is the common case, and it is handled by saying nothing: the last reading stands and ages.
public sealed class ClaudeUsageCollector(UsageOptions options, IUsageHttp http, TimeProvider? time = null) : IUsageCollector
{
    static readonly Uri Usage = new("https://api.anthropic.com/api/oauth/usage");
    readonly TimeProvider clock = time ?? TimeProvider.System;

    public AgentKind Agent => AgentKind.Claude;

    public async Task<AgentUsage?> CollectAsync(CancellationToken cancellationToken = default)
    {
        string token;
        string? plan;
        using (var credentials = UsageRead.Json(Path.Combine(options.ClaudeHome, ".credentials.json"), options.MaxStateBytes))
        {
            if (credentials is null || UsageRead.Property(credentials.RootElement, "claudeAiOauth") is not { } oauth) { return null; }
            if (UsageRead.String(oauth, "accessToken") is not { } t || !CopilotCredentials.LooksLikeToken(t.Trim())) { return null; }
            token = t.Trim();
            plan = UsageRead.String(oauth, "subscriptionType");
            // Spending an expired token costs a round trip to be told so, and a 401 counts
            // against the account like any failed authentication.
            if (UsageRead.Number(oauth, "expiresAt") is { } expires && DateTimeOffset.FromUnixTimeMilliseconds((long)expires) <= clock.GetUtcNow()) { return null; }
        }
        // Carried by a failure too: one that could not say whose it was would be a second row
        // beside the reading it failed to refresh.
        var accountKey = AccountKey(options.ClaudeAccountPath, options.MaxStateBytes);

        JsonDocument body;
        try
        {
            body = await UsageFetch.GetJsonAsync(http, Usage,
                [new("Authorization", $"Bearer {token}"), new("anthropic-beta", "oauth-2025-04-20"), new("Accept", "application/json"), new("User-Agent", "agent-bridge")],
                options, cancellationToken);
        }
        // A refused token is the expiry above arriving a moment early, not a fault.
        catch (UsageHttpException e) when (e.Status == 401) { return null; }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return UsageRead.Failure(AgentKind.Claude, $"The Claude usage could not be read: {UsageFetch.Describe(e)}", clock.GetUtcNow(), accountKey: accountKey, plan: plan);
        }
        using (body) { return Convert(body.RootElement, accountKey, plan, clock.GetUtcNow()); }
    }

    // limits is the list Claude Code itself draws, every entry of which applies to this plan,
    // the session window included: it reads 0% whenever no five-hour window is open, and
    // dropping it made Claude look as if it had only a weekly cap.
    internal static AgentUsage? Convert(JsonElement response, string? accountKey, string? plan, DateTimeOffset now)
    {
        if (UsageRead.Property(response, "limits") is not { ValueKind: JsonValueKind.Array } limits) { return null; }
        var started = UsageRead.Property(response, "seven_day_breakdown") is { } breakdown ? UsageRead.Instant(breakdown, "window_started_at") : null;
        var windows = new List<UsageWindow>();
        foreach (var limit in limits.EnumerateArray())
        {
            if (UsageRead.Number(limit, "percent") is not { } percent) { continue; }
            var kind = UsageRead.String(limit, "kind") ?? "limit";
            var label = kind switch
            {
                "session" => "Session (5h)",
                "weekly_all" => "Weekly",
                "weekly_opus" => "Weekly (Opus)",
                _ => Capitalise(kind.Replace('_', ' ')),
            };
            windows.Add(UsageRead.Window(kind, label, percent, UsageRead.Instant(limit, "resets_at"), kind.StartsWith("weekly", StringComparison.Ordinal) ? started : null));
        }
        if (windows.Count == 0) { return null; }
        return new AgentUsage
        {
            Agent = AgentKind.Claude,
            Label = UsageRead.Label(AgentKind.Claude),
            AccountKey = accountKey,
            Plan = plan,
            Source = UsageSource.Api,
            Windows = windows,
            MeasuredAt = now,
        };
    }

    // A short digest of the account's and its organisation's UUIDs from ~/.claude.json, never
    // either of them nor anything else there (the email address is beside them). Clients need
    // to know whether two readings are the same allowance and nothing more. The organisation
    // counts because one person in two (a personal plan and a team seat) has two allowances.
    // Anything unreadable is null, and the reading goes ungrouped.
    internal static string? AccountKey(string? path, long maxBytes)
    {
        using var document = UsageRead.Json(path, maxBytes);
        if (document is null || UsageRead.Property(document.RootElement, "oauthAccount") is not { } account) { return null; }
        if (UsageRead.String(account, "accountUuid") is not { } uuid || string.IsNullOrWhiteSpace(uuid)) { return null; }
        var identity = $"claude:{uuid.Trim().ToLowerInvariant()}";
        if (UsageRead.String(account, "organizationUuid") is { } org && !string.IsNullOrWhiteSpace(org)) { identity += $":{org.Trim().ToLowerInvariant()}"; }
        return System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..12];
    }

    static string Capitalise(string text) => text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
}
