using System.Collections.Concurrent;
using System.Text.Json;
using AgentBridge.Host.Core.DevBox;
using AgentBridge.Host.Core.Usage;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// Keeping a Microsoft Dev Box from hibernating the host out from under itself (#47), ported from
// agent-ha-bridge's tests/test-devbox-keepawake.ps1. A Dev Box pool with stop-on-disconnect
// measures idleness by sessions, not load, so a Dev Box busy running agents hibernates anyway; the
// keep-awake skips or delays that pending stop through Dev Center's user-scoped API.
//
// Nothing here reaches Dev Center, the Azure CLI or the machine's Dev Box agent: the identity comes
// from a fixture settings file, the token from a fake runner, and every HTTP call from a fake Dev
// Center that records it. Every name and address is made up.
public sealed class DevBoxTests : IDisposable
{
    static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-30T22:00:00Z");
    static readonly DevBoxIdentity Box = new(new Uri("https://tenant-fake-dc.westus3.devcenter.azure.com"), "FakeProject", "fake-box");
    const string Token = "fake-token-never-logged";

    readonly string root = Path.Combine(Path.GetTempPath(), "abdevbox-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<string> log = [];

    public DevBoxTests() => Directory.CreateDirectory(root);

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); } catch (IOException) { }
    }

    // A Dev Center that answers as the test says and records every call. Its answers are made the
    // way the service sends them: the actions list, an empty 200 for a lever that worked, and a 400
    // whose error envelope carries the code.
    sealed class FakeDevCenter : IDevCenterHttp
    {
        public readonly ConcurrentQueue<(HttpMethod Method, string Uri, string Token)> Calls = new();
        public Func<HttpMethod, string, Task<DevCenterResponse>> Answer = (_, _) => Task.FromResult(new DevCenterResponse(200, "{}"));

        public Task<DevCenterResponse> SendAsync(HttpMethod method, Uri uri, string token, CancellationToken cancellationToken = default)
        {
            Calls.Enqueue((method, uri.ToString(), token));
            return Answer(method, uri.ToString());
        }

        public List<string> Posts => [.. Calls.Where(c => c.Method == HttpMethod.Post).Select(c => c.Uri)];
    }

    sealed class FakeToken(string? token = Token) : IDevCenterToken
    {
        public int Asked;
        public Task<string> GetAsync(CancellationToken cancellationToken = default)
        {
            Asked++;
            return token is null ? throw new DevBoxException("No Dev Center token. Run 'az login' as this user.") : Task.FromResult(token);
        }
    }

    static DevCenterResponse Actions(params object[] actions) => new(200, JsonSerializer.Serialize(new { value = actions }));

    static object Stop(string? at, string name = "idle-stopondisconnect", string type = "Stop") =>
        at is null ? new { name, actionType = type, sourceType = "Pool" } : new { name, actionType = type, sourceType = "Pool", next = new { scheduledTime = at } };

    static DevCenterResponse Refused(string code) =>
        new(400, JsonSerializer.Serialize(new { status = "Failed", error = new { code = "ValidationError", message = "The request is not valid.", details = new[] { new { code, message = "detail" } } } }));

    // The list for a GET, and the given answer for each lever.
    static Func<HttpMethod, string, Task<DevCenterResponse>> Serving(DevCenterResponse list, Func<string, DevCenterResponse>? post = null) =>
        (method, uri) => Task.FromResult(method == HttpMethod.Get ? list : post?.Invoke(uri) ?? new DevCenterResponse(200, ""));

    Task<KeepAwakeResult> Pass(FakeDevCenter dc, DevBoxIdentity? identity = null, bool dryRun = false, IDevCenterToken? token = null) =>
        KeepAwakePass.RunAsync(identity ?? Box, token ?? new FakeToken(), dc, Now, dryRun, log.Add);

    string AgentSettings(string dataplaneId = "fake-tenant:fake-dc:fakeproject:fake-user:fake-box", string url = "https://tenant-fake-dc.westus3.devcenter.azure.com/", string project = "FakeProject")
    {
        var folder = Path.Combine(root, "agent", Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, DevBoxIdentity.SettingsFile);
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            DevBoxAgent = new { disableIdleDetection = false, metadata = new { devCenterUrl = url, projectName = project, devBoxDataplaneId = dataplaneId, poolName = "fake-pool" } },
        }));
        return path;
    }

    // ---- recognising a Dev Box ----

    [Fact]
    public void The_project_endpoint_and_Dev_Box_name_are_read_from_the_Dev_Box_agents_settings()
    {
        var (identity, problem) = DevBoxIdentity.Read(AgentSettings());

        Assert.Null(problem);
        Assert.Equal("FakeProject", identity!.Project);
        Assert.Equal("fake-box", identity.DevBox);
        Assert.Equal("https://tenant-fake-dc.westus3.devcenter.azure.com/", identity.Endpoint.ToString());
        Assert.Equal("some-other-box", DevBoxIdentity.Read(AgentSettings("t:dc:p:u:some-other-box")).Identity!.DevBox);
    }

    [Fact]
    public void The_newest_of_the_agents_settings_files_is_the_one_read()
    {
        var older = AgentSettings("t:dc:p:u:old-box");
        var newer = AgentSettings("t:dc:p:u:new-box");
        File.SetLastWriteTimeUtc(older, DateTime.UtcNow.AddDays(-1));

        Assert.Equal(newer, DevBoxIdentity.FindSettings(Path.Combine(root, "agent")));
        Assert.Null(DevBoxIdentity.FindSettings(Path.Combine(root, "no-agent-here")));
        Assert.Equal("new-box", DevBoxIdentity.Find(Path.Combine(root, "agent")).Identity!.DevBox);
    }

    // Only a machine without the Dev Box agent is not a Dev Box. One whose agent is installed but
    // whose settings are not found there cannot be kept awake, which must be said, not hidden.
    [Fact]
    public void A_Dev_Box_agent_installed_without_settings_to_find_is_a_problem_not_a_machine_that_is_not_a_Dev_Box()
    {
        Assert.Equal((null, null), DevBoxIdentity.Find(Path.Combine(root, "no-agent-here")));

        var installed = Path.Combine(root, "agent-without-settings");
        Directory.CreateDirectory(Path.Combine(installed, "1.0"));
        var (identity, problem) = DevBoxIdentity.Find(installed);

        Assert.Null(identity);
        Assert.Contains("the Dev Box agent is installed", problem);
        Assert.Contains($"no {DevBoxIdentity.SettingsFile} was found", problem);

        // A folder it may not look at is not a folder that is not there.
        var (denied, why) = DevBoxIdentity.Find(installed, _ => throw new UnauthorizedAccessException("Access to the path is denied."));
        Assert.Null(denied);
        Assert.Contains("could not be checked", why);
        Assert.Contains("Access to the path is denied", why);
    }

    [Fact]
    public void A_machine_without_the_Dev_Box_agent_or_whose_settings_say_too_little_is_not_a_Dev_Box()
    {
        Assert.Equal((null, null), DevBoxIdentity.Read(null));
        Assert.Equal((null, null), DevBoxIdentity.Read(Path.Combine(root, "absent.json")));
        Assert.Null(DevBoxIdentity.Read(AgentSettings(dataplaneId: "")).Identity);
        var broken = Path.Combine(root, "broken.json");
        File.WriteAllText(broken, "{ this is not json");
        var (identity, problem) = DevBoxIdentity.Read(broken);
        Assert.Null(identity);
        // Said rather than silent, so a Dev Box whose agent changed its file is not quietly left to sleep.
        Assert.Contains("could not be read", problem);
    }

    [Theory]
    [InlineData("http://tenant-fake-dc.westus3.devcenter.azure.com/")]
    [InlineData("https://devcenter.azure.com.example.net/")]
    [InlineData("https://example.com/")]
    [InlineData("https://tenant-fake-dc.westus3.devcenter.azure.com/somewhere/else")]
    public void The_users_token_is_never_sent_anywhere_but_Dev_Center_itself(string url)
    {
        var (identity, problem) = DevBoxIdentity.Read(AgentSettings(url: url));

        Assert.Null(identity);
        Assert.Contains("no token is sent", problem);
    }

    // ---- clearing a pending stop ----

    [Fact]
    public async Task A_stop_more_than_24_hours_away_is_already_safe_and_is_not_touched()
    {
        var dc = new FakeDevCenter { Answer = Serving(Actions(Stop("2026-10-03T05:00:00Z"))) };

        var result = await Pass(dc);

        // Only the GET: neither lever may be used out there, and trying would log a failure for what
        // is actually the state worth reaching.
        Assert.Equal(KeepAwakeResult.Safe, result.Outcome);
        var call = Assert.Single(dc.Calls);
        Assert.Equal(HttpMethod.Get, call.Method);
        Assert.Equal("https://tenant-fake-dc.westus3.devcenter.azure.com/projects/FakeProject/users/me/devboxes/fake-box/actions?api-version=2024-02-01", call.Uri);
        Assert.Equal(Token, call.Token);

        // Just past 24 hours is past it, though it rounds to 24.00: neither lever would be accepted,
        // and trying both would report a false blocked.
        var edge = new FakeDevCenter { Answer = Serving(Actions(Stop("2026-10-01T22:00:10Z"))) };
        Assert.Equal(KeepAwakeResult.Safe, (await Pass(edge)).Outcome);
        Assert.Empty(edge.Posts);
    }

    [Fact]
    public async Task A_stop_inside_the_window_is_skipped_and_skip_is_tried_before_delay()
    {
        var dc = new FakeDevCenter { Answer = Serving(Actions(Stop("2026-09-30T23:00:00Z"))) };

        var result = await Pass(dc);

        Assert.Equal(KeepAwakeResult.Skipped, result.Outcome);
        Assert.Equal(["https://tenant-fake-dc.westus3.devcenter.azure.com/projects/FakeProject/users/me/devboxes/fake-box/actions/idle-stopondisconnect:skip?api-version=2024-02-01"], dc.Posts);
    }

    [Fact]
    public async Task A_refused_skip_falls_back_to_delay_asking_for_the_full_8_hours_first()
    {
        var dc = new FakeDevCenter
        {
            Answer = Serving(Actions(Stop("2026-09-30T23:00:00Z")), uri => uri.Contains(":skip?")
                ? Refused("OperationNotSupported")
                : new DevCenterResponse(200, """{"next":{"scheduledTime":"2026-10-01T07:00:00Z"}}""")),
        };

        var result = await Pass(dc);

        // 23:00Z + 8h = 07:00Z the next day.
        Assert.Equal(KeepAwakeResult.Delayed, result.Outcome);
        Assert.Equal("delayed 'idle-stopondisconnect' by 8h to 2026-10-01T07:00:00Z", result.Detail);
        Assert.Equal(2, dc.Posts.Count);
        Assert.Contains(":delay?until=2026-10-01T07:00:00Z&api-version=2024-02-01", dc.Posts[1]);
        // The service's own code, not "400", says why skip was refused.
        Assert.Contains(log, l => l.Contains("skip refused for 'idle-stopondisconnect' (OperationNotSupported); trying delay"));
    }

    [Fact]
    public async Task The_delay_ladder_steps_down_when_the_8_hour_limit_is_already_used_up()
    {
        var dc = new FakeDevCenter
        {
            // Everything past +2h is beyond what the service allows this occurrence.
            Answer = Serving(Actions(Stop("2026-09-30T23:00:00Z")), uri =>
                uri.Contains(":skip?") ? Refused("OperationNotSupported")
                : uri.Contains("until=2026-10-01T01:00:00Z") ? new DevCenterResponse(200, "")
                : Refused("DelayUntilTimeExceedLimit")),
        };

        var result = await Pass(dc);

        Assert.Equal(KeepAwakeResult.Delayed, result.Outcome);
        Assert.Contains("by 2h", result.Detail);
        Assert.Equal(["until=2026-10-01T07:00:00Z", "until=2026-10-01T05:00:00Z", "until=2026-10-01T03:00:00Z", "until=2026-10-01T01:00:00Z"],
            dc.Posts.Skip(1).Select(p => p[(p.IndexOf("until=", StringComparison.Ordinal))..p.IndexOf('&')]));
        Assert.Equal(3, log.Count(l => l.Contains("refused (DelayUntilTimeExceedLimit)")));
    }

    [Fact]
    public async Task A_stop_that_cannot_be_moved_at_all_is_blocked_rather_than_success()
    {
        var dc = new FakeDevCenter { Answer = Serving(Actions(Stop("2026-09-30T23:00:00Z")), _ => Refused("DelayUntilTimeExceedLimit")) };

        var result = await Pass(dc);

        Assert.Equal(KeepAwakeResult.Blocked, result.Outcome);
        Assert.True(result.Alarming);
        Assert.Contains("already as far out as the service allows (DelayUntilTimeExceedLimit)", result.Detail);
        Assert.Equal(1 + 1 + KeepAwakePass.DelayLadder.Length, dc.Calls.Count);
    }

    [Fact]
    public async Task Levers_that_never_reach_Dev_Center_are_blocked_without_blaming_the_services_limit()
    {
        var dc = new FakeDevCenter
        {
            Answer = (m, _) => m == HttpMethod.Get ? Task.FromResult(Actions(Stop("2026-09-30T23:00:00Z"))) : throw new HttpRequestException("connection reset"),
        };

        var result = await Pass(dc);

        Assert.Equal(KeepAwakeResult.Blocked, result.Outcome);
        Assert.DoesNotContain("as far out as the service allows", result.Detail);
        Assert.Contains("the last with connection reset", result.Detail);
    }

    [Fact]
    public async Task A_lock_that_cannot_be_taken_at_all_fails_loudly_rather_than_reading_as_busy()
    {
        var notAFolder = Path.Combine(root, "a-file");
        File.WriteAllText(notAFolder, "");
        var dc = new FakeDevCenter { Answer = Serving(Actions(Stop("2026-09-30T23:00:00Z"))) };
        var service = new KeepAwakeService(new KeepAwakeOptions
        {
            Settings = new DevBoxSettings { KeepAwake = true },
            FindIdentity = () => (Box, null),
            Token = new FakeToken(),
            Http = dc,
            Lock = new FileKeepAwakeLock(Path.Combine(notAFolder, "devbox-keepawake.lock")),
        }, log.Add);

        var result = await service.PassAsync();

        Assert.Equal(KeepAwakeResult.Failed, result.Outcome);
        Assert.Contains("lock could not be taken", result.Detail);
        Assert.Empty(dc.Calls);
    }

    [Fact]
    public async Task A_dry_run_says_what_it_would_clear_without_calling_either_lever()
    {
        var dc = new FakeDevCenter { Answer = Serving(Actions(Stop("2026-09-30T23:00:00Z"))) };

        var result = await Pass(dc, dryRun: true);

        Assert.Equal(KeepAwakeResult.DryRun, result.Outcome);
        Assert.Equal("would clear 'idle-stopondisconnect'", result.Detail);
        Assert.Empty(dc.Posts);
    }

    [Fact]
    public async Task A_machine_that_is_not_a_Dev_Box_does_nothing_and_says_so()
    {
        var dc = new FakeDevCenter();
        var token = new FakeToken();

        var result = await KeepAwakePass.RunAsync(null, token, dc, Now, false, log.Add);

        Assert.Equal(KeepAwakeResult.NotADevBox, result.Outcome);
        Assert.Empty(dc.Calls);
        Assert.Equal(0, token.Asked);
    }

    // The case that made agent-ha-bridge's keep-awake look like it worked while the machine kept
    // hibernating: a pool's Stop action is listed permanently, but carries next only once an
    // occurrence exists, which is only after the last session has gone. While connected, nothing
    // is pending, and that is not a failure.
    [Theory]
    [InlineData("""{"value":[]}""")]
    [InlineData("""{"value":[{"name":"idle-stopondisconnect","actionType":"Stop","sourceType":"Pool"}]}""")]
    [InlineData("""{"value":[{"name":"idle-stopondisconnect","actionType":"Stop","next":{"scheduledTime":""}}]}""")]
    [InlineData("""{"value":[{"name":"idle-stopondisconnect","actionType":"Stop","next":{"scheduledTime":"whenever"}}]}""")]
    [InlineData("""{"value":[{"name":"idle-stopondisconnect","actionType":"Stop","next":null}]}""")]
    public async Task A_Dev_Box_with_nothing_pending_reports_no_action_and_reaches_for_neither_lever(string list)
    {
        var dc = new FakeDevCenter { Answer = Serving(new DevCenterResponse(200, list)) };

        var result = await Pass(dc);

        Assert.Equal(KeepAwakeResult.NoAction, result.Outcome);
        Assert.Equal("no pending stop on fake-box", result.Detail);
        Assert.Equal(HttpMethod.Get, Assert.Single(dc.Calls).Method);
    }

    [Fact]
    public async Task An_unscheduled_stop_does_not_hide_a_scheduled_one_later_in_the_list()
    {
        var dc = new FakeDevCenter { Answer = Serving(Actions(Stop(null), Stop("2026-09-30T23:00:00Z", "scheduled-stop"))) };

        var result = await Pass(dc);

        Assert.Equal(KeepAwakeResult.Skipped, result.Outcome);
        Assert.Contains("/actions/scheduled-stop:skip?", Assert.Single(dc.Posts));
    }

    [Fact]
    public async Task Nothing_but_this_Dev_Boxs_own_Stop_actions_is_ever_moved()
    {
        var dc = new FakeDevCenter { Answer = Serving(Actions(Stop("2026-09-30T23:00:00Z", "repair", "Repair"), Stop("2026-09-30T23:30:00Z", "the-stop", "stop"))) };

        await Pass(dc);

        // The Repair action is left alone, a Stop is matched whatever its case (as PowerShell's -eq),
        // and every call is under users/me for this Dev Box.
        Assert.Contains("/actions/the-stop:skip?", Assert.Single(dc.Posts));
        Assert.All(dc.Calls, c => Assert.StartsWith("https://tenant-fake-dc.westus3.devcenter.azure.com/projects/FakeProject/users/me/devboxes/fake-box/actions", c.Uri));
    }

    [Fact]
    public async Task A_missing_az_login_fails_the_pass_loudly_without_calling_Dev_Center()
    {
        var dc = new FakeDevCenter();
        var runner = new FakeRunner(new CommandResult(true, false, 1, ""));
        var service = Service(dc, token: new AzCliToken(runner, () => @"C:\fake\az.cmd"));

        var result = await service.PassAsync();

        Assert.Equal(KeepAwakeResult.Failed, result.Outcome);
        Assert.Contains("Run 'az login' as this user", result.Detail);
        Assert.Empty(dc.Calls);
        Assert.Contains(log, l => l.Contains("keep-awake: failed:") && l.Contains("MAY HIBERNATE"));
        Assert.True(service.State.Last!.Alarming);
    }

    [Fact]
    public async Task The_token_is_asked_of_the_Azure_CLI_for_Dev_Center_and_never_written_to_the_log()
    {
        var dc = new FakeDevCenter { Answer = Serving(Actions(Stop("2026-09-30T23:00:00Z"))) };
        var runner = new FakeRunner(new CommandResult(true, false, 0, Token + "\r\n"));

        var result = await Service(dc, token: new AzCliToken(runner, () => @"C:\fake\az.cmd")).PassAsync();

        Assert.Equal(KeepAwakeResult.Skipped, result.Outcome);
        var (program, arguments) = Assert.Single(runner.Runs);
        Assert.Equal(@"C:\fake\az.cmd", program);
        Assert.Equal(["account", "get-access-token", "--resource", "https://devcenter.azure.com", "--query", "accessToken", "-o", "tsv"], arguments);
        Assert.All(dc.Calls, c => Assert.Equal(Token, c.Token));
        Assert.DoesNotContain(log, l => l.Contains(Token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task No_Azure_CLI_at_all_is_a_failure_that_says_what_to_install()
    {
        var result = await Service(new FakeDevCenter(), token: new AzCliToken(new FakeRunner(new CommandResult(true, false, 0, Token)), () => null)).PassAsync();

        Assert.Equal(KeepAwakeResult.Failed, result.Outcome);
        Assert.Contains("Azure CLI (az) was not found", result.Detail);
    }

    [Fact]
    public async Task Dev_Center_refusing_the_list_is_a_failure_naming_the_services_code()
    {
        var dc = new FakeDevCenter { Answer = Serving(new DevCenterResponse(401, """{"error":{"code":"Unauthorized"}}""")) };

        var result = await Pass(dc);

        Assert.Equal(KeepAwakeResult.Failed, result.Outcome);
        Assert.Contains("(Unauthorized)", result.Detail);
    }

    // ---- one pass at a time, and never beside agent-ha-bridge's own ----

    [Fact]
    public async Task A_second_pass_while_one_is_running_is_busy_and_makes_no_Dev_Center_call()
    {
        var listed = new TaskCompletionSource<DevCenterResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dc = new FakeDevCenter { Answer = (m, _) => m == HttpMethod.Get ? listed.Task : Task.FromResult(new DevCenterResponse(200, "")) };
        // Two hosts of this user (two profiles) share the lock file.
        var first = Service(dc);
        var second = Service(dc);

        var running = first.PassAsync();
        await Until(() => !dc.Calls.IsEmpty, "the first pass's list");
        var result = await second.PassAsync();
        listed.SetResult(Actions(Stop("2026-09-30T23:00:00Z")));

        Assert.Equal(KeepAwakeResult.Busy, result.Outcome);
        Assert.Equal(KeepAwakeResult.Skipped, (await running).Outcome);
        Assert.Single(dc.Calls, c => c.Method == HttpMethod.Get);
        // Once it is done, the next pass runs.
        Assert.Equal(KeepAwakeResult.Skipped, (await second.PassAsync()).Outcome);
    }

    [Fact]
    public async Task A_pass_takes_every_lock_or_none_and_lets_go_of_those_it_took()
    {
        var dc = new FakeDevCenter { Answer = Serving(Actions()) };
        var file = new FileKeepAwakeLock(Path.Combine(root, "devbox-keepawake.lock"));
        IDisposable? legacyPass = null;
        var service = new KeepAwakeService(new KeepAwakeOptions
        {
            Settings = new DevBoxSettings { KeepAwake = true },
            FindIdentity = () => (Box, null),
            Token = new FakeToken(),
            Http = dc,
            // As the host holds agent-ha-bridge's pass mutex: unavailable while a pass of it runs.
            Lock = new KeepAwakeLocks(file, new DelegateKeepAwakeLock(() => legacyPass is null ? new MemoryStream() : null)),
        }, log.Add);

        legacyPass = new MemoryStream();
        Assert.Equal(KeepAwakeResult.Busy, (await service.PassAsync()).Outcome);
        Assert.Empty(dc.Calls);
        // The lock file was let go again, not held by the pass that did not run.
        using (var free = file.TryTake()) { Assert.NotNull(free); }

        legacyPass = null;
        Assert.Equal(KeepAwakeResult.NoAction, (await service.PassAsync()).Outcome);
    }

    [Fact]
    public async Task A_host_stands_back_while_agent_ha_bridges_own_keep_awake_is_on_and_asks_for_nothing()
    {
        var dc = new FakeDevCenter { Answer = Serving(Actions(Stop("2026-09-30T23:00:00Z"))) };
        var token = new FakeToken();
        KeepAwakeOwner? owner = new("agent-ha-bridge's own Dev Box keep-awake is on here (task AgentBridgeDevBoxKeepAwake_0123 (enabled))");
        var service = Service(dc, token: token, agentHaBridge: () => owner);

        var result = await service.PassAsync();

        Assert.Equal(KeepAwakeResult.StoodBack, result.Outcome);
        Assert.Equal(owner.Reason, result.Detail);
        // agent-ha-bridge keeps it awake: nothing to raise.
        Assert.False(result.Alarming);
        Assert.Empty(dc.Calls);
        Assert.Equal(0, token.Asked);
        // Once migrate has disabled that task, the host keeps the Dev Box awake itself.
        owner = null;
        Assert.Equal(KeepAwakeResult.Skipped, (await service.PassAsync()).Outcome);
    }

    // After migrate the task is disabled; if Task Scheduler then cannot be read, standing back is
    // only caution, with nothing known to keep the Dev Box awake: that must not pass silently.
    [Fact]
    public async Task Standing_back_because_Task_Scheduler_could_not_be_read_is_an_alarm_not_a_quiet_stand_back()
    {
        var dc = new FakeDevCenter { Answer = Serving(Actions(Stop("2026-09-30T23:00:00Z"))) };
        var service = Service(dc, agentHaBridge: () => new("Task Scheduler could not be read", Known: false));

        var first = await service.PassAsync();
        var second = await service.PassAsync();

        Assert.Equal(KeepAwakeResult.StoodBack, first.Outcome);
        Assert.True(first.Alarming);
        Assert.True(service.State.Last!.Alarming);
        Assert.Empty(dc.Calls);
        // Said every pass, not once.
        Assert.Equal(2, log.Count(l => l.StartsWith("keep-awake: stood-back:", StringComparison.Ordinal) && l.EndsWith("THIS DEV BOX MAY HIBERNATE WHEN DISCONNECTED", StringComparison.Ordinal)));
        Assert.True(second.Alarming);
    }

    // ---- when passes run ----

    [Fact]
    public async Task A_first_pass_runs_soon_after_the_host_starts_and_then_one_every_interval()
    {
        var time = new FakeTimeProvider(Now);
        var dc = new FakeDevCenter { Answer = Serving(Actions()) };
        await using var service = Service(dc, time: time, settings: new DevBoxSettings { KeepAwake = true, IntervalMinutes = 15 });

        service.Start();
        await Until(() => service.State.NextAt is not null, "the first pass to be scheduled");
        Assert.Equal(Now.AddSeconds(30), service.State.NextAt);
        Assert.Empty(dc.Calls);

        time.Advance(TimeSpan.FromSeconds(30));
        await Until(() => dc.Calls.Count == 1, "the first pass");
        await Until(() => service.State.NextAt == Now.AddSeconds(30).AddMinutes(15), "the next pass to be scheduled");
        Assert.Equal(KeepAwakeResult.NoAction, service.State.Last!.Outcome);

        time.Advance(TimeSpan.FromMinutes(14));
        await Task.Delay(100);
        Assert.Single(dc.Calls);
        time.Advance(TimeSpan.FromMinutes(1));
        await Until(() => dc.Calls.Count == 2, "the second pass");
    }

    [Fact]
    public async Task Keep_awake_turned_off_runs_no_pass_and_turned_on_runs_one_at_once()
    {
        var time = new FakeTimeProvider(Now);
        var dc = new FakeDevCenter { Answer = Serving(Actions()) };
        await using var service = Service(dc, time: time, settings: new DevBoxSettings { KeepAwake = false });
        var changes = 0;
        service.Changed += () => Interlocked.Increment(ref changes);

        service.Start();
        time.Advance(TimeSpan.FromMinutes(31));
        await Task.Delay(100);
        Assert.Empty(dc.Calls);
        Assert.Null(service.State.NextAt);

        service.Enabled = true;

        await Until(() => dc.Calls.Count == 1, "a pass as soon as it is turned on");
        Assert.True(service.State.Enabled);
        Assert.True(changes > 0);
    }

    [Fact]
    public async Task A_Dev_Box_whose_agent_settings_cannot_be_used_fails_loudly_every_pass_and_is_kept_awake_once_they_can_be()
    {
        var time = new FakeTimeProvider(Now);
        var dc = new FakeDevCenter { Answer = Serving(Actions(Stop("2026-09-30T23:00:00Z"))) };
        // The Dev Box agent rewriting its file as the host starts at logon, or a shape not known.
        var readable = false;
        await using var service = new KeepAwakeService(new KeepAwakeOptions
        {
            Settings = new DevBoxSettings { KeepAwake = true },
            FindIdentity = () => readable ? (Box, null) : (null, "appsettings.Production.json could not be read (in use)"),
            Token = new FakeToken(),
            Http = dc,
            Lock = new FileKeepAwakeLock(Path.Combine(root, "devbox-keepawake.lock")),
            Time = time,
        }, m => { lock (log) { log.Add(m); } });

        service.Start();
        // Still a Dev Box for the status, so the app shows the setting and the alarm.
        Assert.True(service.State.IsDevBox);
        Assert.Contains("could not be read", service.State.Problem);
        await Until(() => service.State.NextAt is not null, "the first pass to be scheduled");
        time.Advance(TimeSpan.FromSeconds(30));
        await Until(() => service.State.Last is not null, "the first pass");
        Assert.Equal(KeepAwakeResult.Failed, service.State.Last!.Outcome);
        Assert.True(service.State.Last.Alarming);
        Assert.Empty(dc.Calls);
        // Every pass says it, not once.
        Assert.Equal(KeepAwakeResult.Failed, (await service.PassAsync()).Outcome);
        lock (log) { Assert.Equal(2, log.Count(l => l.StartsWith("keep-awake: failed:", StringComparison.Ordinal) && l.EndsWith("MAY HIBERNATE WHEN DISCONNECTED", StringComparison.Ordinal))); }

        readable = true;
        await Until(() => service.State.NextAt == Now.AddSeconds(30).AddMinutes(15), "the next pass to be scheduled");
        time.Advance(TimeSpan.FromMinutes(15));

        await Until(() => service.State.Last?.Outcome == KeepAwakeResult.Skipped, "the stop skipped");
        Assert.True(service.State.IsDevBox);
        Assert.Null(service.State.Problem);
    }

    [Fact]
    public void An_interval_that_could_outlast_the_shortest_grace_Dev_Box_offers_is_capped_at_30_minutes()
    {
        static (DevBoxSettings, IReadOnlyList<string>) Read(string json) => DevBoxSettings.Read(JsonDocument.Parse(json).RootElement);

        Assert.Equal((false, 15), Of(DevBoxSettings.Read(null).Settings));
        Assert.Equal((true, 5), Of(Read("""{"keepAwake":true,"intervalMinutes":5}""").Item1));
        var (capped, problems) = Read("""{"keepAwake":true,"intervalMinutes":240}""");
        Assert.Equal((true, 30), Of(capped));
        Assert.Contains("more than 30", Assert.Single(problems));
        // Nonsense is the default, said; a keepAwake that is not true or false is off, said.
        Assert.Equal(15, Read("""{"intervalMinutes":"soon"}""").Item1.IntervalMinutes);
        Assert.Equal(15, Read("""{"intervalMinutes":0}""").Item1.IntervalMinutes);
        var (unclear, said) = Read("""{"keepAwake":"yes"}""");
        Assert.False(unclear.KeepAwake);
        Assert.Single(said);
        Assert.False(Read("[]").Item1.KeepAwake);

        static (bool, int) Of(DevBoxSettings s) => (s.KeepAwake, s.IntervalMinutes);
    }

    KeepAwakeService Service(FakeDevCenter dc, IDevCenterToken? token = null, Func<KeepAwakeOwner?>? agentHaBridge = null, TimeProvider? time = null, DevBoxSettings? settings = null) =>
        new(new KeepAwakeOptions
        {
            Settings = settings ?? new DevBoxSettings { KeepAwake = true },
            FindIdentity = () => (Box, null),
            Token = token ?? new FakeToken(),
            Http = dc,
            Lock = new FileKeepAwakeLock(Path.Combine(root, "devbox-keepawake.lock")),
            AgentHaBridgeKeepAwake = agentHaBridge ?? (() => null),
            Time = time ?? new FakeTimeProvider(Now),
        }, m => { lock (log) { log.Add(m); } });

    static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) { throw new TimeoutException($"waited for {what}"); }
            await Task.Delay(10);
        }
    }

    sealed class FakeRunner(CommandResult result) : ICommandRunner
    {
        public readonly List<(string Program, IReadOnlyList<string> Arguments)> Runs = [];
        public Task<CommandResult> RunAsync(string program, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken = default)
        {
            Runs.Add((program, arguments));
            return Task.FromResult(result);
        }
    }
}
