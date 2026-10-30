using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Agent usage: each collector against synthetic agent homes and a fake network, and the
// monitor's pacing against a fake clock. Nothing here reaches a vendor, gh or a credential
// store.
public sealed class UsageTests : IDisposable
{
    const string Login = "octo-cat";
    const string Token = "gho_fixturetoken000000000000000000000000";
    static readonly DateTimeOffset Now = new(2026, 10, 28, 20, 0, 0, TimeSpan.Zero);

    readonly Fixture f = new();
    readonly FakeHttp http = new();
    readonly FakeCommands commands = new();
    readonly FakeTimeProvider time = new(Now);
    readonly List<string> log = [];

    public void Dispose() => f.Dispose();

    string CacheRoot => Path.Combine(f.Root, "home", "AppData", "copilot");

    UsageOptions Options() => new()
    {
        CopilotHome = f.CopilotHome,
        CopilotCacheRoots = [CacheRoot, f.CopilotHome],
        ClaudeHome = f.ClaudeHome,
        ClaudeAccountPath = Path.Combine(f.Root, "home", ".claude.json"),
        CodexHome = f.CodexHome,
        RetryDelay = TimeSpan.Zero,
    };

    CopilotCredentials Credentials(Dictionary<string, string>? env = null, ICredentialStore? store = null, string? gh = "/bin/gh") =>
        new(Options(), commands, store, name => env is not null && env.TryGetValue(name, out var v) ? v : null, () => gh, time, log.Add);

    CopilotUsageCollector CopilotCollector(Dictionary<string, string>? env = null, ICredentialStore? store = null, string? gh = "/bin/gh") =>
        new(Options(), http, Credentials(env, store, gh), time);

    void CopilotConfig(params string[] logins)
    {
        Directory.CreateDirectory(f.CopilotHome);
        var users = string.Join(",", logins.Select(l => $"{{\"host\":\"https://github.com\",\"login\":\"{l}\"}}"));
        File.WriteAllText(Path.Combine(f.CopilotHome, "config.json"),
            $"// User settings belong in settings.json.\n// This file is managed automatically.\n{{\"lastLoggedInUser\":{{\"host\":\"https://github.com\",\"login\":\"{logins[^1]}\"}},\"loggedInUsers\":[{users}]}}");
    }

    static string Quota(string login = Login, double remaining = 316687, double entitlement = 1500000) => $$"""
        {"login":"{{login}}","copilot_plan":"enterprise","quota_reset_date_utc":"2026-11-01T00:00:00.000Z",
         "quota_snapshots":{"chat":{"entitlement":0,"unlimited":true},
           "premium_interactions":{"entitlement":{{entitlement}},"remaining":{{remaining}},"timestamp_utc":"2026-10-28T19:59:00.000Z"} } }
        """;

    // --- Copilot ---

    [Fact]
    public async Task Copilot_reads_the_signed_in_logins_allowance_from_GitHub_with_the_token_the_CLI_would_spend()
    {
        CopilotConfig(Login);
        http.Respond(200, Quota());

        var usage = await CopilotCollector(env: new() { ["GH_TOKEN"] = Token }).CollectAsync();

        var request = Assert.Single(http.Requests);
        Assert.Equal("https://api.github.com/copilot_internal/user", request.Uri.ToString());
        Assert.Contains(new KeyValuePair<string, string>("Authorization", $"token {Token}"), request.Headers);
        Assert.NotNull(usage);
        Assert.Equal((AgentKind.Copilot, "GitHub Copilot", Login, "enterprise", UsageSource.Api), (usage.Agent, usage.Label, usage.Account, usage.Plan, usage.Source));
        var plan = Assert.Single(usage.Windows);
        Assert.Equal(("plan", "Plan", 78.9, 1183313d, 1500000d, "AIC"), (plan.Key, plan.Label, plan.Percent, plan.Used, plan.Limit, plan.Unit));
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), plan.ResetsAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), plan.StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 19, 59, 0, TimeSpan.Zero), usage.MeasuredAt);
        Assert.Null(usage.Error);
    }

    [Fact]
    public async Task Copilot_with_several_logins_reads_the_one_last_signed_in_and_asks_gh_for_exactly_that_login()
    {
        CopilotConfig("someone-else", Login);
        commands.Next = new CommandResult(true, false, 0, Token + "\n");
        http.Respond(200, Quota());

        var usage = await CopilotCollector().CollectAsync();

        Assert.Equal(Login, usage!.Account);
        var run = Assert.Single(commands.Runs);
        Assert.Equal(["auth", "token", "--hostname", "github.com", "--user", Login], run.Arguments);
    }

    [Fact]
    public async Task Copilot_reads_its_own_stored_login_before_asking_gh()
    {
        CopilotConfig(Login);
        var store = new FakeStore { [$"https://github.com:{Login}.copilot-cli"] = Token };
        http.Respond(200, Quota());

        await CopilotCollector(store: store).CollectAsync();

        Assert.Empty(commands.Runs);
        Assert.Contains(new KeyValuePair<string, string>("Authorization", $"token {Token}"), Assert.Single(http.Requests).Headers);
    }

    [Fact]
    public async Task Copilot_that_has_never_run_here_is_left_off_rather_than_reported_empty()
    {
        Assert.Null(await CopilotCollector(env: new() { ["GH_TOKEN"] = Token }).CollectAsync());
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Copilot_falls_back_to_the_newest_cached_answer_for_the_same_login_and_says_why()
    {
        CopilotConfig(Login);
        Directory.CreateDirectory(CacheRoot);
        File.WriteAllText(Path.Combine(CacheRoot, "copilot-user-cache.json"), $$"""
            {"copilotUserCache":{
              "a":{"retrievedAt":"2026-10-28T18:00:00.000Z","response":{{Quota(remaining: 600000)}}},
              "b":{"retrievedAt":"2026-10-28T19:00:00.000Z","response":{{Quota(remaining: 450000)}}},
              "c":{"retrievedAt":"2026-10-28T19:30:00.000Z","response":{{Quota(login: "someone-else", remaining: 1)}} } } }
            """);
        http.Respond(503, "{}");

        var usage = await CopilotCollector(env: new() { ["GH_TOKEN"] = Token }).CollectAsync();

        Assert.Equal(3, http.Requests.Count);
        Assert.Equal(UsageSource.Cache, usage!.Source);
        Assert.Equal(70.0, Assert.Single(usage.Windows).Percent);
        Assert.Equal(new DateTimeOffset(2026, 10, 28, 19, 0, 0, TimeSpan.Zero), usage.MeasuredAt);
        Assert.Equal("The Copilot quota could not be read: the server answered 503 (ServiceUnavailable)", usage.Error);
    }

    [Fact]
    public async Task Copilot_takes_a_refused_token_as_an_answer_and_does_not_ask_again_in_the_same_read()
    {
        CopilotConfig(Login);
        http.Respond(401, "{\"message\":\"Bad credentials\"}");

        var usage = await CopilotCollector(env: new() { ["GH_TOKEN"] = Token }).CollectAsync();

        Assert.Single(http.Requests);
        Assert.Empty(usage!.Windows);
        Assert.Equal(UsageSource.None, usage.Source);
        Assert.StartsWith("The Copilot quota could not be read: the server answered 401", usage.Error);
    }

    [Fact]
    public async Task Copilot_with_no_credential_and_nothing_cached_says_which_way_to_one_is_missing_without_naming_the_account_in_the_log()
    {
        CopilotConfig(Login);
        commands.Next = new CommandResult(true, false, 1, "");

        var collector = CopilotCollector();
        var usage = await collector.CollectAsync();

        Assert.Empty(http.Requests);
        Assert.Equal("No Copilot credential was available and nothing was cached (gh has no token for this account; run gh auth login).", usage!.Error);
        Assert.Equal(Login, usage.Account);
        Assert.Single(log);
        Assert.DoesNotContain(log, l => l.Contains(Login, StringComparison.OrdinalIgnoreCase));

        // Said once, not every poll.
        await collector.CollectAsync();
        Assert.Single(log);
    }

    [Fact]
    public async Task Copilot_never_sends_a_token_for_another_host_to_api_github_com()
    {
        Directory.CreateDirectory(f.CopilotHome);
        File.WriteAllText(Path.Combine(f.CopilotHome, "config.json"), $"{{\"loggedInUsers\":[{{\"host\":\"https://ghe.example\",\"login\":\"{Login}\"}}]}}");

        var usage = await CopilotCollector(env: new() { ["GH_TOKEN"] = Token }).CollectAsync();

        Assert.Empty(http.Requests);
        Assert.Empty(commands.Runs);
        Assert.Equal(UsageSource.None, usage!.Source);
    }

    [Fact]
    public async Task Gh_printing_more_than_one_line_is_not_taken_as_a_token()
    {
        CopilotConfig(Login);
        commands.Next = new CommandResult(true, false, 0, "warning: something\n" + Token + "\n");

        var usage = await CopilotCollector().CollectAsync();

        Assert.Empty(http.Requests);
        Assert.Contains("gh answered with something that is not a token", usage!.Error);
        Assert.DoesNotContain(log, l => l.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_gh_that_did_not_answer_is_left_alone_for_hours_unless_a_session_opens_a_while_later()
    {
        CopilotConfig(Login);
        var credentials = Credentials();
        commands.Next = new CommandResult(true, true, -1, "");

        Assert.Null(await credentials.ResolveAsync(Login, "https://github.com"));
        Assert.Null(await credentials.ResolveAsync("another-login", "https://github.com"));
        Assert.Single(commands.Runs);
        Assert.Equal("gh did not answer in time", credentials.GhReason(Login, "https://github.com"));

        // A session opening straight after must not become a prompt storm.
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.False(credentials.ReleaseHold(time.GetUtcNow()));
        Assert.Null(await credentials.ResolveAsync(Login, "https://github.com"));
        Assert.Single(commands.Runs);

        time.Advance(TimeSpan.FromMinutes(11));
        Assert.True(credentials.ReleaseHold(time.GetUtcNow()));
        commands.Next = new CommandResult(true, false, 0, Token);
        Assert.Equal(Token, await credentials.ResolveAsync(Login, "https://github.com"));
        Assert.Equal(2, commands.Runs.Count);
    }

    [Fact]
    public async Task A_gh_hold_ends_on_its_own_after_six_hours()
    {
        var credentials = Credentials();
        commands.Next = new CommandResult(true, true, -1, "");
        await credentials.ResolveAsync(Login, "https://github.com");

        time.Advance(TimeSpan.FromHours(6));
        commands.Next = new CommandResult(true, false, 0, Token);
        Assert.Equal(Token, await credentials.ResolveAsync(Login, "https://github.com"));
    }

    // --- Further Copilot accounts (usage.copilotAccounts) ---

    const string Further = "dswett_microsoft";
    const string FurtherToken = "gho_further-fixture";

    CopilotAccountCollector AccountCollector(CopilotCredentials credentials, int slot = 2, string login = Further) =>
        new(Options(), http, credentials, new CopilotAccount(slot, login), time);

    static (UsageSettings Settings, IReadOnlyList<string> Problems) ReadUsage(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return UsageSettings.Read(doc.RootElement.Clone());
    }

    [Fact]
    public void Further_accounts_keep_the_place_they_were_listed_in_and_an_entry_that_is_not_a_login_is_named_by_its_place_alone()
    {
        var (settings, problems) = ReadUsage("""{"copilotAccounts":["ghp_pasted0123456789", " DanSwett ", "danswett", "not a login", "hubot"]}""");

        Assert.Equal([new CopilotAccount(3, "DanSwett")], settings.CopilotAccounts);
        Assert.Equal(
        [
            "usage.copilotAccounts entry 1 looks like a token, not a login, so it is not used",
            "usage.copilotAccounts entry 3 repeats an earlier login",
            "usage.copilotAccounts entry 4 is not a GitHub login",
            "usage.copilotAccounts entry 5 is past the 3 further logins there are rows for",
        ], problems);
        Assert.DoesNotContain(problems, p => p.Contains("ghp_", StringComparison.Ordinal) || p.Contains("hubot", StringComparison.Ordinal));
    }

    [Fact]
    public void A_usage_setting_meant_as_off_is_never_read_as_on_and_a_pace_out_of_range_keeps_the_default()
    {
        Assert.False(ReadUsage("""{"publish":"false"}""").Settings.Publish);
        Assert.False(ReadUsage("false").Settings.Publish);
        Assert.False(ReadUsage("""{"publish":false}""").Settings.Publish);
        Assert.True(ReadUsage("{}").Settings.Publish);

        var (settings, problems) = ReadUsage("""{"intervalSeconds":5,"idleIntervalSeconds":60,"copilotAccounts":"hubot"}""");
        Assert.Equal((TimeSpan.FromSeconds(120), TimeSpan.FromSeconds(120)), (settings.Interval, settings.IdleInterval));
        Assert.Empty(settings.CopilotAccounts);
        Assert.Equal(2, problems.Count);
        Assert.Equal(TimeSpan.FromSeconds(300), ReadUsage("""{"idleIntervalSeconds":300}""").Settings.IdleInterval);
    }

    [Fact]
    public async Task A_further_account_is_read_only_with_the_token_gh_holds_for_exactly_that_login()
    {
        // The signed-in account's token in the environment and its stored login are not this one's.
        var store = new FakeStore { [$"https://github.com:{Further}.copilot-cli"] = Token };
        var credentials = Credentials(env: new() { ["GH_TOKEN"] = Token }, store: store);
        commands.Next = new CommandResult(true, false, 0, FurtherToken + "\n");
        http.Respond(200, Quota(login: "DSwett_Microsoft", remaining: 1200000));

        var usage = await AccountCollector(credentials).CollectAsync();

        Assert.Equal(["auth", "token", "--hostname", "github.com", "--user", Further], Assert.Single(commands.Runs).Arguments);
        Assert.Contains(new KeyValuePair<string, string>("Authorization", $"token {FurtherToken}"), Assert.Single(http.Requests).Headers);
        Assert.Equal((AgentKind.Copilot, "DSwett_Microsoft", 2, UsageSource.Api, 20.0), (usage!.Agent, usage.Account, usage.Slot, usage.Source, usage.Windows[0].Percent));
        Assert.Null(usage.Error);
    }

    [Fact]
    public async Task A_further_account_whose_token_reads_another_login_is_refused_rather_than_shown_under_its_name()
    {
        commands.Next = new CommandResult(true, false, 0, FurtherToken);
        http.Respond(200, Quota(login: "someone-else"));

        var usage = await AccountCollector(Credentials()).CollectAsync();

        Assert.Empty(usage!.Windows);
        Assert.Equal((Further, 2), (usage.Account, usage.Slot));
        Assert.Equal($"The token gh holds for {Further} reads the allowance of someone-else instead.", usage.Error);
    }

    [Fact]
    public async Task A_further_accounts_answer_that_does_not_say_whose_it_is_is_not_shown_under_its_name()
    {
        commands.Next = new CommandResult(true, false, 0, FurtherToken);
        http.Respond(200, Quota().Replace($"\"login\":\"{Login}\",", "", StringComparison.Ordinal));

        var usage = await AccountCollector(Credentials()).CollectAsync();

        Assert.Empty(usage!.Windows);
        Assert.Equal((Further, 2), (usage.Account, usage.Slot));
        Assert.Equal($"GitHub's answer for {Further} did not say whose allowance it is.", usage.Error);
    }

    [Fact]
    public async Task A_further_account_gh_holds_no_token_for_keeps_its_row_and_says_why_with_the_spelling_github_last_gave()
    {
        var credentials = Credentials();
        var collector = AccountCollector(credentials);
        commands.Next = new CommandResult(true, false, 0, FurtherToken);
        http.Respond(200, Quota(login: "DSwett_Microsoft"));
        await collector.CollectAsync();

        commands.Next = new CommandResult(true, false, 1, "");
        var usage = await collector.CollectAsync();

        Assert.Equal(UsageSource.None, usage!.Source);
        Assert.Equal(("DSwett_Microsoft", 2), (usage.Account, usage.Slot));
        Assert.Equal($"No Copilot credential was available for {Further} (gh has no token for this account; run gh auth login).", usage.Error);
        Assert.Equal("no Copilot credential was available (gh has no token for this account; run gh auth login)", collector.Plain);
        Assert.DoesNotContain(log, l => l.Contains(Further, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_rate_limited_further_account_is_tried_again_then_reported_as_its_own_failure()
    {
        commands.Next = new CommandResult(true, false, 0, FurtherToken);
        http.Respond(429, "{}");

        var usage = await AccountCollector(Credentials()).CollectAsync();

        Assert.Equal(3, http.Requests.Count);
        Assert.Empty(usage!.Windows);
        Assert.Equal($"The Copilot quota for {Further} could not be read: the server answered 429 (TooManyRequests)", usage.Error);
    }

    // --- Claude ---

    void ClaudeCredentials(string token = "sk-ant-oat01-fixture", DateTimeOffset? expires = null)
    {
        Directory.CreateDirectory(f.ClaudeHome);
        File.WriteAllText(Path.Combine(f.ClaudeHome, ".credentials.json"),
            $"{{\"claudeAiOauth\":{{\"accessToken\":\"{token}\",\"refreshToken\":\"sk-ant-ort01-never\",\"expiresAt\":{(expires ?? Now.AddHours(1)).ToUnixTimeMilliseconds()},\"subscriptionType\":\"max\"}}}}");
        File.WriteAllText(Path.Combine(f.Root, "home", ".claude.json"),
            "{\"projects\":{},\"oauthAccount\":{\"accountUuid\":\"AAAA-1111\",\"organizationUuid\":\"BBBB-2222\",\"emailAddress\":\"octo@example.com\"}}");
    }

    const string ClaudeBody = """
        {"limits":[
          {"kind":"session","percent":12,"resets_at":"2026-10-28T22:00:00Z"},
          {"kind":"weekly_all","percent":41.26,"resets_at":"2026-11-02T00:00:00Z"},
          {"kind":"weekly_opus","percent":null},
          {"kind":"monthly_extra","percent":3}],
         "seven_day_breakdown":{"window_started_at":"2026-10-26T00:00:00Z"}}
        """;

    [Fact]
    public async Task Claude_reads_every_window_its_plan_has_with_the_week_start_for_pace()
    {
        ClaudeCredentials();
        http.Respond(200, ClaudeBody);

        var usage = await new ClaudeUsageCollector(Options(), http, time).CollectAsync();

        var request = Assert.Single(http.Requests);
        Assert.Equal("https://api.anthropic.com/api/oauth/usage", request.Uri.ToString());
        Assert.Contains(new KeyValuePair<string, string>("Authorization", "Bearer sk-ant-oat01-fixture"), request.Headers);
        Assert.Contains(new KeyValuePair<string, string>("anthropic-beta", "oauth-2025-04-20"), request.Headers);
        Assert.Equal(("max", UsageSource.Api, Now), (usage!.Plan, usage.Source, usage.MeasuredAt));
        Assert.Equal(["Session (5h)", "Weekly", "Monthly extra"], usage.Windows.Select(w => w.Label));
        Assert.Equal([12, 41.3, 3], usage.Windows.Select(w => w.Percent));
        Assert.Null(usage.Windows[0].StartsAt);
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero), usage.Windows[1].StartsAt);
    }

    [Fact]
    public async Task Claude_is_told_apart_by_a_digest_of_its_account_and_organisation_never_by_either()
    {
        ClaudeCredentials();
        http.Respond(200, ClaudeBody);

        var usage = await new ClaudeUsageCollector(Options(), http, time).CollectAsync();

        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("claude:aaaa-1111:bbbb-2222")))[..12];
        Assert.Equal(expected, usage!.AccountKey);
        Assert.Null(usage.Account);
        var json = JsonSerializer.Serialize(usage, ProtocolJson.Options);
        Assert.DoesNotContain("AAAA", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("octo@example.com", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Claude_profile_names_its_own_account_whichever_layout_it_has()
    {
        // ~/.claude: the account is beside it, in that profile's own home.
        Assert.Equal(Path.Combine(f.Root, "home", ".claude.json"), UsageOptions.ClaudeAccountFor(f.ClaudeHome));
        // A CLAUDE_CONFIG_DIR keeps it inside.
        var config = Path.Combine(f.Root, "elsewhere", "claude-config");
        Directory.CreateDirectory(config);
        File.WriteAllText(Path.Combine(config, ".claude.json"), "{}");
        Assert.Equal(Path.Combine(config, ".claude.json"), UsageOptions.ClaudeAccountFor(config));
    }

    [Fact]
    public async Task Claude_never_spends_an_expired_token_and_leaves_the_last_reading_to_age()
    {
        ClaudeCredentials(expires: Now.AddMinutes(-1));

        Assert.Null(await new ClaudeUsageCollector(Options(), http, time).CollectAsync());
        Assert.Empty(http.Requests);
    }

    [Fact]
    public async Task Claude_takes_a_refused_token_as_expiry_rather_than_a_fault()
    {
        ClaudeCredentials();
        http.Respond(401, "{}");

        Assert.Null(await new ClaudeUsageCollector(Options(), http, time).CollectAsync());
        Assert.Single(http.Requests);
    }

    [Fact]
    public async Task Claude_failing_for_another_reason_says_so_and_still_says_whose_it_was()
    {
        ClaudeCredentials();
        http.Fail(new HttpRequestException("The SSL connection could not be established, see inner exception.", new IOException("An existing connection was forcibly closed by the remote host.")));

        var usage = await new ClaudeUsageCollector(Options(), http, time).CollectAsync();

        Assert.Equal(3, http.Requests.Count);
        Assert.Empty(usage!.Windows);
        Assert.NotNull(usage.AccountKey);
        Assert.Equal("The Claude usage could not be read: The SSL connection could not be established - An existing connection was forcibly closed by the remote host.", usage.Error);
    }

    [Fact]
    public async Task Claude_never_writes_to_its_credentials()
    {
        ClaudeCredentials();
        http.Respond(200, ClaudeBody);
        var before = f.Listing();

        await new ClaudeUsageCollector(Options(), http, time).CollectAsync();

        Assert.Equal(before, f.Listing());
    }

    // --- Codex ---

    [Fact]
    public async Task Codex_reports_the_newest_rate_limits_its_transcripts_recorded()
    {
        var older = f.CodexTranscript("older");
        Fixture.Append(older, Codex.Event(new { type = "token_count", rate_limits = new { primary = new { used_percent = 99.0 } } }, 1));
        File.SetLastWriteTimeUtc(older, Now.AddDays(-1).UtcDateTime);
        var newer = Path.Combine(f.CodexHome, "sessions", "2026", "10", "28", "rollout-newer.jsonl");
        Fixture.Append(newer,
            Codex.Event(new { type = "token_count", rate_limits = new { primary = new { used_percent = 5.0 } } }, 2),
            Codex.Event(new
            {
                type = "token_count",
                rate_limits = new
                {
                    primary = new { used_percent = 23.5, window_minutes = 300, resets_at = Now.AddHours(2).ToUnixTimeSeconds() },
                    secondary = new { used_percent = 61.0, window_minutes = 10080, resets_at = Now.AddDays(3).ToUnixTimeSeconds() },
                    plan_type = "plus",
                },
            }, 3),
            Codex.Event(new { type = "agent_message", message = "done" }, 4));

        var usage = await new CodexUsageCollector(Options()).CollectAsync();

        Assert.Equal((UsageSource.Transcript, "plus", Fixture.T0.AddSeconds(3)), (usage!.Source, usage.Plan, usage.MeasuredAt));
        Assert.Equal([("primary", "5h limit", 23.5), ("secondary", "Weekly", 61.0)], usage.Windows.Select(w => (w.Key, w.Label, w.Percent)));
        Assert.Equal(Now.AddHours(2), usage.Windows[0].ResetsAt);
        // Codex's windows roll: a start guessed from the length would be wrong.
        Assert.All(usage.Windows, w => Assert.Null(w.StartsAt));
    }

    static string Limits(double percent, int second) =>
        Codex.Event(new { type = "token_count", rate_limits = new { primary = new { used_percent = percent } } }, second);

    [Fact]
    public async Task Codex_takes_the_newest_reading_across_its_transcripts_not_the_file_written_last()
    {
        var heardLast = f.CodexTranscript("heard-last");
        Fixture.Append(heardLast, Limits(30, 100));
        File.SetLastWriteTimeUtc(heardLast, Now.AddMinutes(-5).UtcDateTime);
        // Written to more recently, by a tool result, but its own limits are older.
        var writtenLast = f.CodexTranscript("written-last");
        Fixture.Append(writtenLast, Limits(20, 50), Codex.Event(new { type = "exec_command_end", stdout = "ok" }, 200));
        File.SetLastWriteTimeUtc(writtenLast, Now.UtcDateTime);

        var usage = await new CodexUsageCollector(Options()).CollectAsync();

        Assert.Equal(30, Assert.Single(usage!.Windows).Percent);
        Assert.Equal(Fixture.T0.AddSeconds(100), usage.MeasuredAt);
    }

    [Fact]
    public async Task Codex_reads_the_latest_limits_from_the_end_of_a_transcript_too_long_to_read_whole()
    {
        var transcript = f.CodexTranscript("long");
        var output = new string('x', 4000);
        Fixture.Append(transcript, [Limits(10, 1), .. Enumerable.Range(0, 100).Select(i => Codex.Event(new { type = "exec_command_end", stdout = output }, 2 + i)), Limits(47, 500)]);

        var usage = await new CodexUsageCollector(new UsageOptions { CopilotHome = f.CopilotHome, ClaudeHome = f.ClaudeHome, CodexHome = f.CodexHome, MaxStateBytes = 64 * 1024 }).CollectAsync();

        Assert.True(new FileInfo(transcript).Length > 64 * 1024);
        Assert.Equal(47, Assert.Single(usage!.Windows).Percent);
    }

    [Fact]
    public async Task Codex_that_has_never_run_here_is_left_off()
    {
        Assert.Null(await new CodexUsageCollector(Options()).CollectAsync());
        Fixture.Append(f.CodexTranscript("no-limits"), Codex.Event(new { type = "agent_message", message = "hi" }, 1));
        Assert.Null(await new CodexUsageCollector(Options()).CollectAsync());
    }

    // --- The monitor ---

    static AgentUsage Reading(AgentKind agent, double percent, DateTimeOffset at, string? error = null) => new()
    {
        Agent = agent,
        Label = agent.ToString(),
        Source = UsageSource.Api,
        Windows = [new UsageWindow { Key = "plan", Label = "Plan", Percent = percent }],
        Error = error,
        MeasuredAt = at,
    };

    UsageMonitor Monitor(FakeSessions sessions, Func<string?>? agentHaBridge = null, params FakeCollector[] collectors) =>
        new(collectors, sessions, Options(), time, agentHaBridge, log.Add);

    [Fact]
    public async Task The_first_pass_asks_every_agent_and_announces_what_they_said()
    {
        var copilot = new FakeCollector(AgentKind.Copilot, () => Reading(AgentKind.Copilot, 40, time.GetUtcNow()));
        var codex = new FakeCollector(AgentKind.Codex, () => null);
        await using var monitor = Monitor(new FakeSessions(), null, copilot, codex);
        var announced = new List<(UsageReport, long)>();
        monitor.Changed += (r, v) => announced.Add((r, v));

        Assert.True(await monitor.PassAsync());

        Assert.Equal((1, 1), (copilot.Calls, codex.Calls));
        var (report, version) = Assert.Single(announced);
        Assert.Equal(1, version);
        Assert.Equal(AgentKind.Copilot, Assert.Single(report.Agents).Agent);
        Assert.Equal(report, monitor.Current.Report);
    }

    [Fact]
    public async Task An_agent_with_no_session_here_is_asked_every_quarter_hour_and_one_with_a_session_every_two_minutes()
    {
        var sessions = new FakeSessions();
        var copilot = new FakeCollector(AgentKind.Copilot, () => Reading(AgentKind.Copilot, 40, time.GetUtcNow()));
        var claude = new FakeCollector(AgentKind.Claude, () => Reading(AgentKind.Claude, 10, time.GetUtcNow()));
        await using var monitor = Monitor(sessions, null, copilot, claude);
        sessions.Open(AgentKind.Copilot);
        await monitor.PassAsync();

        time.Advance(TimeSpan.FromSeconds(119));
        await monitor.PassAsync();
        Assert.Equal((1, 1), (copilot.Calls, claude.Calls));

        time.Advance(TimeSpan.FromSeconds(1));
        await monitor.PassAsync();
        Assert.Equal((2, 1), (copilot.Calls, claude.Calls));

        time.Advance(TimeSpan.FromSeconds(780));
        await monitor.PassAsync();
        Assert.Equal(2, claude.Calls);
    }

    [Fact]
    public async Task A_session_opening_brings_the_next_ask_forward_but_never_within_half_a_minute_of_the_last()
    {
        var sessions = new FakeSessions();
        var claude = new FakeCollector(AgentKind.Claude, () => Reading(AgentKind.Claude, 10, time.GetUtcNow()));
        await using var monitor = Monitor(sessions, null, claude);
        await monitor.PassAsync();

        sessions.Open(AgentKind.Claude);
        time.Advance(TimeSpan.FromSeconds(20));
        await monitor.PassAsync();
        Assert.Equal(1, claude.Calls);

        time.Advance(TimeSpan.FromSeconds(10));
        await monitor.PassAsync();
        Assert.Equal(2, claude.Calls);

        // And closing: the final tally of the session that just finished.
        sessions.Close(AgentKind.Claude);
        time.Advance(TimeSpan.FromSeconds(30));
        await monitor.PassAsync();
        Assert.Equal(3, claude.Calls);
    }

    [Fact]
    public async Task A_clock_that_went_backwards_makes_an_ask_due_rather_than_holding_every_ask_off()
    {
        var clock = new SettableTime { Now = Now };
        var claude = new FakeCollector(AgentKind.Claude, () => Reading(AgentKind.Claude, 10, clock.Now));
        await using var monitor = new UsageMonitor([claude], new FakeSessions(), Options(), clock);
        await monitor.PassAsync();

        clock.Now = Now.AddHours(-3);
        await monitor.PassAsync();

        Assert.Equal(2, claude.Calls);
    }

    [Fact]
    public async Task While_agent_ha_bridge_runs_for_this_user_a_session_does_not_speed_up_the_asking()
    {
        var sessions = new FakeSessions();
        sessions.Open(AgentKind.Copilot);
        var copilot = new FakeCollector(AgentKind.Copilot, () => Reading(AgentKind.Copilot, 40, time.GetUtcNow()));
        var probes = 0;
        await using var monitor = Monitor(sessions, () => { probes++; return "agent-ha-bridge is running"; }, copilot);

        await monitor.PassAsync();
        for (var i = 0; i < 6; i++) { time.Advance(TimeSpan.FromMinutes(2)); await monitor.PassAsync(); }

        Assert.Equal(1, copilot.Calls);
        Assert.Contains(log, l => l.Contains("agent-ha-bridge is asking", StringComparison.Ordinal));
        Assert.True(probes <= 7);

        time.Advance(TimeSpan.FromMinutes(3));
        await monitor.PassAsync();
        Assert.Equal(2, copilot.Calls);
    }

    [Fact]
    public async Task An_unchanged_reading_is_announced_again_only_after_half_an_hour()
    {
        var claude = new FakeCollector(AgentKind.Claude, () => Reading(AgentKind.Claude, 10, time.GetUtcNow()));
        await using var monitor = Monitor(new FakeSessions(), null, claude);
        var versions = new List<long>();
        monitor.Changed += (_, v) => versions.Add(v);

        await monitor.PassAsync();
        time.Advance(TimeSpan.FromMinutes(15));
        await monitor.PassAsync();
        Assert.Equal([1L], versions);
        // The newest figure is still what a client asking now gets.
        Assert.Equal(time.GetUtcNow(), monitor.Current.Report.Agents[0].MeasuredAt);

        time.Advance(TimeSpan.FromMinutes(15));
        await monitor.PassAsync();
        Assert.Equal([1L, 2L], versions);
    }

    [Fact]
    public async Task A_changed_figure_is_announced_at_once()
    {
        var percent = 10.0;
        var claude = new FakeCollector(AgentKind.Claude, () => Reading(AgentKind.Claude, percent, time.GetUtcNow()));
        await using var monitor = Monitor(new FakeSessions(), null, claude);
        var versions = new List<long>();
        monitor.Changed += (_, v) => versions.Add(v);
        await monitor.PassAsync();

        percent = 11;
        time.Advance(TimeSpan.FromMinutes(15));
        Assert.True(await monitor.PassAsync());

        Assert.Equal([1L, 2L], versions);
        Assert.Equal(11, monitor.Current.Report.Agents[0].Windows[0].Percent);
    }

    [Fact]
    public async Task Credits_spent_without_moving_the_percent_by_a_tenth_are_announced_too()
    {
        var used = 1000.0;
        var copilot = new FakeCollector(AgentKind.Copilot, () => new AgentUsage
        {
            Agent = AgentKind.Copilot,
            Label = "GitHub Copilot",
            Source = UsageSource.Api,
            Windows = [new UsageWindow { Key = "plan", Label = "Plan", Percent = 40, Used = used, Limit = 1500000 }],
            MeasuredAt = time.GetUtcNow(),
        });
        await using var monitor = Monitor(new FakeSessions(), null, copilot);
        var versions = new List<long>();
        monitor.Changed += (_, v) => versions.Add(v);
        await monitor.PassAsync();

        used = 1001;
        time.Advance(TimeSpan.FromMinutes(15));
        await monitor.PassAsync();

        Assert.Equal([1L, 2L], versions);
    }

    [Fact]
    public async Task Nothing_to_say_keeps_the_last_reading_rather_than_dropping_it()
    {
        AgentUsage? next = Reading(AgentKind.Claude, 10, Now);
        var claude = new FakeCollector(AgentKind.Claude, () => next);
        await using var monitor = Monitor(new FakeSessions(), null, claude);
        await monitor.PassAsync();

        next = null;
        time.Advance(TimeSpan.FromMinutes(15));
        Assert.False(await monitor.PassAsync());

        Assert.Equal(10, Assert.Single(monitor.Current.Report.Agents).Windows[0].Percent);
    }

    [Fact]
    public async Task A_collector_that_throws_becomes_that_agents_failure_and_the_others_are_still_read()
    {
        var copilot = new FakeCollector(AgentKind.Copilot, () => throw new InvalidOperationException("broken"));
        var claude = new FakeCollector(AgentKind.Claude, () => Reading(AgentKind.Claude, 10, time.GetUtcNow()));
        await using var monitor = Monitor(new FakeSessions(), null, copilot, claude);

        await monitor.PassAsync();

        var agents = monitor.Current.Report.Agents;
        Assert.Equal("Usage for copilot could not be collected: broken", agents.Single(a => a.Agent == AgentKind.Copilot).Error);
        Assert.Single(agents.Single(a => a.Agent == AgentKind.Claude).Windows);
        Assert.Contains("usage: copilot could not be read: Usage for copilot could not be collected: broken", log);
    }

    sealed class FakeAccount(int slot, Func<AgentUsage?> read) : IUsageCollector
    {
        public int Calls { get; private set; }
        public AgentKind Agent => AgentKind.Copilot;
        public string Key => $"copilot_{slot}";
        public int? Slot => slot;

        public Task<AgentUsage?> CollectAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(read() is { } r ? r with { Slot = slot } : null);
        }
    }

    [Fact]
    public async Task Further_accounts_are_read_one_a_pass_longest_waiting_first_at_the_idle_pace_whatever_is_open()
    {
        var sessions = new FakeSessions();
        sessions.Open(AgentKind.Copilot);
        var copilot = new FakeCollector(AgentKind.Copilot, () => Reading(AgentKind.Copilot, 40, time.GetUtcNow()));
        var second = new FakeAccount(2, () => Reading(AgentKind.Copilot, 10, time.GetUtcNow()));
        var third = new FakeAccount(3, () => throw new InvalidOperationException("broken"));
        await using var monitor = new UsageMonitor([copilot, second, third], sessions, Options(), time, null, log.Add);

        await monitor.PassAsync();
        Assert.Equal((1, 1, 0), (copilot.Calls, second.Calls, third.Calls));
        time.Advance(TimeSpan.FromSeconds(15));
        await monitor.PassAsync();
        Assert.Equal((1, 1, 1), (copilot.Calls, second.Calls, third.Calls));

        // Every row, in order, each in its place; one that failed does not take the others with it.
        var agents = monitor.Current.Report.Agents;
        Assert.Equal([null, 2, 3], agents.Select(a => a.Slot));
        Assert.Equal(10, agents[1].Windows[0].Percent);
        Assert.Equal("Usage for copilot could not be collected: broken", agents[2].Error);

        // The session open here sets the signed-in account's pace, never a further one's.
        time.Advance(TimeSpan.FromMinutes(4));
        await monitor.PassAsync();
        Assert.Equal((2, 1, 1), (copilot.Calls, second.Calls, third.Calls));
        time.Advance(TimeSpan.FromSeconds(885));
        await monitor.PassAsync();
        time.Advance(TimeSpan.FromSeconds(15));
        await monitor.PassAsync();
        Assert.Equal((2, 2), (second.Calls, third.Calls));
        Assert.Contains("usage: further Copilot account 3 could not be read: Usage for copilot could not be collected: broken", log);
    }

    [Fact]
    public async Task The_log_says_when_an_agents_state_changes_and_not_on_every_read()
    {
        var claude = new FakeCollector(AgentKind.Claude, () => Reading(AgentKind.Claude, 10, time.GetUtcNow()));
        await using var monitor = Monitor(new FakeSessions(), null, claude);

        await monitor.PassAsync();
        time.Advance(TimeSpan.FromMinutes(15));
        await monitor.PassAsync();

        Assert.Equal(["usage: claude read from api"], log);
    }

    sealed class FakeHttp : IUsageHttp
    {
        Func<UsageHttpResponse>? always;
        public List<(Uri Uri, IReadOnlyList<KeyValuePair<string, string>> Headers)> Requests { get; } = [];

        public void Respond(int status, string body) => always = () => new UsageHttpResponse(status, body);
        public void Fail(Exception e) => always = () => throw e;

        public Task<UsageHttpResponse> GetAsync(Uri uri, IReadOnlyList<KeyValuePair<string, string>> headers, CancellationToken cancellationToken)
        {
            Requests.Add((uri, headers));
            var next = always ?? throw new InvalidOperationException("no response scripted");
            return Task.FromResult(next());
        }
    }

    sealed class FakeCommands : ICommandRunner
    {
        public CommandResult Next { get; set; } = new(false, false, -1, "");
        public List<(string Program, IReadOnlyList<string> Arguments)> Runs { get; } = [];

        public Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Runs.Add((program, arguments));
            return Task.FromResult(Next);
        }
    }

    // FakeTimeProvider refuses to go backwards, which is the case under test.
    sealed class SettableTime : TimeProvider
    {
        public DateTimeOffset Now { get; set; }
        public override DateTimeOffset GetUtcNow() => Now;
    }

    sealed class FakeStore : Dictionary<string, string>, ICredentialStore
    {
        public string? Read(string target) => TryGetValue(target, out var v) ? v : null;
    }

    sealed class FakeCollector(AgentKind agent, Func<AgentUsage?> read) : IUsageCollector
    {
        public int Calls { get; private set; }
        public AgentKind Agent => agent;

        public Task<AgentUsage?> CollectAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(read());
        }
    }
}

// Just enough of a session source for the monitor: which agents have a session open.
internal sealed class FakeSessions : ISessionSource
{
    readonly List<Session> open = [];

    public void Open(AgentKind agent) => open.Add(new Session
    {
        Id = Guid.NewGuid().ToString(),
        Agent = agent,
        Status = SessionStatus.Idle,
        StatusSince = Fixture.T0,
        UpdatedAt = Fixture.T0,
        Activity = new Activity(),
    });

    public void Close(AgentKind agent) => open.RemoveAll(s => s.Agent == agent);

    public long Revision => 0;
    public event EventHandler<SessionChange>? Changed { add { } remove { } }
    public IReadOnlyList<Session> Snapshot() => [.. open];
    public SessionList List(string? cursor = null) => new([.. open], 0);
    public Session? Get(string id) => open.FirstOrDefault(s => s.Id == id);
    public SessionSubscription Subscribe() => throw new NotSupportedException();
    public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
