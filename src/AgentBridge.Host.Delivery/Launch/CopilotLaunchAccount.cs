using System.Text.Json;
using AgentBridge.Host.Core.Usage;

namespace AgentBridge.Host.Delivery;

// The check behind ILaunchAccount: reads COPILOT_GITHUB_TOKEN from the user-level environment
// (the host's own process environment may predate it: Windows Terminal tabs started weeks ago
// have none), then asks GitHub with that same token who it is and whether it has quota. The
// decision is on the data returned; the only clock is the HTTP client's own last-resort timeout,
// reported as a fault, never as an answer (#1511). The token is never logged or put in a reason:
// only its length and the account's login are.
public sealed class CopilotLaunchAccount(IUsageHttp http, Func<string?>? readToken = null, Action<string>? log = null) : ILaunchAccount
{
    public const string Variable = "COPILOT_GITHUB_TOKEN";

    static readonly Uri Who = new("https://api.github.com/user");
    static readonly Uri Quota = new("https://api.github.com/copilot_internal/user");

    readonly Func<string?> read = readToken ?? UserToken;
    readonly Action<string> say = log ?? (_ => { });

    // The user-level value on Windows, where it is stored; elsewhere the environment is all there is.
    public static string? UserToken() =>
        OperatingSystem.IsWindows()
            ? Environment.GetEnvironmentVariable(Variable, EnvironmentVariableTarget.User)
            : Environment.GetEnvironmentVariable(Variable);

    public async Task<LaunchAccount> CheckAsync(string? expectedLogin, CancellationToken cancellationToken)
    {
        var token = read()?.Trim();
        if (string.IsNullOrEmpty(token))
        {
            return LaunchAccount.Refuse($"{Variable} is not set in this user's environment, so no Copilot session was started; a stored login is never used instead");
        }
        if (!CopilotCredentials.LooksLikeToken(token))
        {
            return LaunchAccount.Refuse($"{Variable} in this user's environment is not a token ({token.Length} characters), so no Copilot session was started");
        }
        var headers = new KeyValuePair<string, string>[]
        {
            new("Authorization", $"token {token}"), new("Accept", "application/json"), new("User-Agent", "agent-bridge"),
        };

        var (user, problem) = await Get(Who, headers, cancellationToken);
        if (problem is not null) { return LaunchAccount.Refuse(problem); }
        string? login;
        try { login = user!.RootElement.ValueKind == JsonValueKind.Object && user.RootElement.TryGetProperty("login", out var l) && l.ValueKind == JsonValueKind.String ? l.GetString() : null; }
        finally { user!.Dispose(); }
        if (string.IsNullOrEmpty(login)) { return LaunchAccount.Refuse("could not check the account: GitHub did not say whose token this is; no Copilot session was started"); }
        if (expectedLogin is { Length: > 0 } && !string.Equals(login, expectedLogin, StringComparison.OrdinalIgnoreCase))
        {
            return LaunchAccount.Refuse($"{Variable} belongs to {login}, not the account this host is set to launch with ({expectedLogin}); no Copilot session was started");
        }

        var (quota, quotaProblem) = await Get(Quota, headers, cancellationToken);
        if (quotaProblem is not null) { return LaunchAccount.Refuse(quotaProblem); }
        using (quota)
        {
            var remaining = Remaining(quota!.RootElement, out var unlimited);
            if (!unlimited && remaining is <= 0)
            {
                return LaunchAccount.Refuse($"{login} has no premium interactions left this month, so no Copilot session was started");
            }
            say($"launch: Copilot account {login} (token {token.Length} characters){(unlimited ? ", premium interactions unlimited" : remaining is null ? ", quota not reported" : "")}");
        }
        return LaunchAccount.Allowed(token);
    }

    // null when GitHub reported no figure: unknown is not zero.
    static double? Remaining(JsonElement response, out bool unlimited)
    {
        unlimited = false;
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("quota_snapshots", out var snapshots) || snapshots.ValueKind != JsonValueKind.Object
            || !snapshots.TryGetProperty("premium_interactions", out var premium) || premium.ValueKind != JsonValueKind.Object) { return null; }
        unlimited = premium.TryGetProperty("unlimited", out var u) && u.ValueKind == JsonValueKind.True;
        return premium.TryGetProperty("remaining", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetDouble(out var value) && double.IsFinite(value) ? value : null;
    }

    async Task<(JsonDocument? Body, string? Problem)> Get(Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
    {
        UsageHttpResponse response;
        try { response = await http.GetAsync(uri, headers, cancellationToken); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException && !cancellationToken.IsCancellationRequested)
        {
            // Never the exception's text: a client's message can name what was sent.
            return (null, $"could not check the account: {(e is TaskCanceledException ? "the request to GitHub timed out" : "GitHub could not be reached")}; no Copilot session was started");
        }
        if (response.Status is 401 or 403)
        {
            return (null, $"GitHub refused {Variable} ({response.Status}): it is expired or revoked, or the account has no Copilot access; no Copilot session was started");
        }
        if (response.Status == 404 && uri == Quota)
        {
            return (null, "the account behind this token has no Copilot subscription (404); no Copilot session was started");
        }
        if (response.Status is < 200 or >= 300)
        {
            return (null, $"could not check the account: GitHub answered {response.Status}; no Copilot session was started");
        }
        try { return (JsonDocument.Parse(response.Body), null); }
        catch (JsonException) { return (null, "could not check the account: GitHub's answer could not be read; no Copilot session was started"); }
    }
}
