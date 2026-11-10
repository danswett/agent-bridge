using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery;
using Xunit;

namespace AgentBridge.Host.Delivery.Tests;

// #1518: the account check a Copilot launch makes before anything is created. Tokens are fakes.
public sealed class CopilotLaunchAccountTests
{
    const string Token = "fake-pat-not-a-secret";
    static readonly Uri Who = new("https://api.github.com/user");

    sealed class Http(Func<Uri, UsageHttpResponse> answer) : IUsageHttp
    {
        public readonly List<(Uri Uri, string Authorization)> Calls = [];
        public Task<UsageHttpResponse> GetAsync(Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
        {
            Calls.Add((uri, headers.First(h => h.Key == "Authorization").Value));
            return Task.FromResult(answer(uri));
        }
    }

    static UsageHttpResponse Json(string body) => new(200, body);

    static Http Answers(string login, string quota) =>
        new(uri => uri == Who ? Json($$"""{"login":"{{login}}"}""") : Json(quota));

    static CopilotLaunchAccount Account(Http http, string? token = Token, List<string>? log = null) =>
        new(http, () => token, log is null ? null : log.Add);

    [Fact]
    public async Task A_valid_token_with_quota_is_handed_back_and_both_calls_use_that_same_token()
    {
        var http = Answers("octo-pat", """{"quota_snapshots":{"premium_interactions":{"remaining":40,"entitlement":300}}}""");

        var result = await Account(http).CheckAsync(null, CancellationToken.None);

        Assert.Equal(Token, result.Token);
        Assert.Null(result.Refusal);
        Assert.Equal(2, http.Calls.Count);
        Assert.All(http.Calls, c => Assert.Equal($"token {Token}", c.Authorization));
    }

    [Fact]
    public async Task No_token_in_the_user_environment_refuses_without_calling_GitHub()
    {
        var http = Answers("octo-pat", "{}");

        var result = await Account(http, token: " ").CheckAsync(null, CancellationToken.None);

        Assert.Contains("COPILOT_GITHUB_TOKEN is not set", result.Refusal);
        Assert.Empty(http.Calls);
    }

    [Fact]
    public async Task A_token_for_another_login_than_launch_copilotAccount_refuses_but_any_login_passes_when_it_is_unset()
    {
        var quota = """{"quota_snapshots":{"premium_interactions":{"remaining":5}}}""";

        var wrong = await Account(Answers("danswett", quota)).CheckAsync("octo-pat", CancellationToken.None);
        var unset = await Account(Answers("danswett", quota)).CheckAsync(null, CancellationToken.None);
        var sameCase = await Account(Answers("Octo-Pat", quota)).CheckAsync("octo-pat", CancellationToken.None);

        Assert.Contains("belongs to danswett", wrong.Refusal);
        Assert.Equal(Token, unset.Token);
        Assert.Equal(Token, sameCase.Token);
    }

    [Fact]
    public async Task Zero_premium_interactions_refuses_but_an_absent_figure_is_accepted_and_logged_as_not_reported()
    {
        var log = new List<string>();

        var zero = await Account(Answers("octo-pat", """{"quota_snapshots":{"premium_interactions":{"remaining":0}}}""")).CheckAsync(null, CancellationToken.None);
        var absent = await Account(Answers("octo-pat", """{"quota_snapshots":{}}"""), log: log).CheckAsync(null, CancellationToken.None);

        Assert.Contains("no premium interactions left", zero.Refusal);
        Assert.Equal(Token, absent.Token);
        Assert.Contains(log, l => l.Contains("quota not reported"));
    }

    [Theory]
    [InlineData(401)]
    [InlineData(403)]
    public async Task A_rejected_token_refuses_and_never_falls_back(int status)
    {
        var http = new Http(_ => new UsageHttpResponse(status, ""));

        var result = await Account(http).CheckAsync(null, CancellationToken.None);

        Assert.Contains("GitHub refused COPILOT_GITHUB_TOKEN", result.Refusal);
        Assert.Single(http.Calls);
    }

    [Fact]
    public async Task A_check_that_cannot_complete_refuses_as_could_not_check_the_account()
    {
        var unreachable = new Http(_ => throw new HttpRequestException("name resolution failed for " + Token));
        var serverError = new Http(_ => new UsageHttpResponse(502, ""));

        var a = await Account(unreachable).CheckAsync(null, CancellationToken.None);
        var b = await Account(serverError).CheckAsync(null, CancellationToken.None);

        Assert.StartsWith("could not check the account", a.Refusal);
        Assert.StartsWith("could not check the account", b.Refusal);
    }

    [Fact]
    public async Task Neither_the_log_nor_a_refusal_ever_contains_the_token()
    {
        var log = new List<string>();
        var results = new[]
        {
            await Account(Answers("octo-pat", """{"quota_snapshots":{"premium_interactions":{"remaining":3}}}"""), log: log).CheckAsync(null, CancellationToken.None),
            await Account(Answers("danswett", "{}"), log: log).CheckAsync("octo-pat", CancellationToken.None),
            await Account(new Http(_ => throw new HttpRequestException(Token)), log: log).CheckAsync(null, CancellationToken.None),
        };

        Assert.DoesNotContain(log, l => l.Contains(Token));
        Assert.DoesNotContain(results, r => r.Refusal?.Contains(Token) == true);
        Assert.Contains(log, l => l.Contains("octo-pat") && l.Contains($"{Token.Length} characters"));
    }
}
