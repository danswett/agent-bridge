using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

// #3644: a Codex launch (it chooses its own session id) is given an agent credential before it starts, bound to its process, and
// told its session's id only once the host has proved by the process tree which session the launch started. Fakes only: no real
// token, no host process.
public sealed partial class LaunchFlowTests
{
    static LaunchRequest CodexAsk() => new() { Request = Guid.NewGuid().ToString(), Agent = "codex", Workspace = "App", Prompt = "add pagination" };

    string LaunchIdOf(RecordedCredentials credentials) => credentials.Calls.Single(c => c.StartsWith("prepare ", StringComparison.Ordinal)).Split(' ')[^1];

    [Fact]
    public async Task A_Codex_launch_is_given_a_credential_before_it_starts_and_told_its_session_once_its_process_tree_proves_it()
    {
        locator.Installed["codex"] = @"C:\fake\codex.cmd";
        starter.Registers = false;
        var credentials = new RecordedCredentials();
        var launching = Launcher(Config(), credentials: credentials).LaunchAsync(CodexAsk(), "phone");
        await WaitAsync(() => Task.FromResult(starter.Pids.Count == 1));
        starter.RegisterCodex(starter.Specs[0], starter.Pids[0], 7001, starter.CodexId);
        var result = await launching;

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        var launchId = LaunchIdOf(credentials);
        Assert.Equal("tok-" + launchId, Assert.Single(starter.Specs).Environment![Launchers.TokenVariable]);
        Assert.Equal([$"prepare  {launchId}", $"bind {launchId} {starter.Pids[0]}", $"resolve {launchId} {starter.CodexId}"], credentials.Calls);
    }

    [Fact]
    public async Task A_Codex_session_bound_to_its_launch_by_timing_alone_gets_no_credential_and_the_launch_gives_it_up()
    {
        locator.Installed["codex"] = @"C:\fake\codex.cmd";
        starter.Registers = false;
        var credentials = new RecordedCredentials();
        var launching = Launcher(Config(), credentials: credentials).LaunchAsync(CodexAsk(), "phone");
        await WaitAsync(() => Task.FromResult(starter.Pids.Count == 1));
        // The launcher has gone and the session's process is no child of it: only its timing says whose it is.
        starter.Exit(starter.Pids[0]);
        starter.RegisterCodex(starter.Specs[0], 1, 7001, starter.CodexId);
        var result = await launching;

        Assert.Equal(LaunchOutcome.Started, result.Outcome);
        var launchId = LaunchIdOf(credentials);
        await WaitAsync(() => Task.FromResult(credentials.Calls.Contains($"abandon {launchId}")));
        Assert.DoesNotContain(credentials.Calls, c => c.StartsWith("resolve ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Codex_launch_that_never_registers_gives_up_its_credential_when_it_stops_being_followed()
    {
        locator.Installed["codex"] = @"C:\fake\codex.cmd";
        starter.Registers = false;
        starter.ExitsAtOnce = true;
        var credentials = new RecordedCredentials();
        var result = await Launcher(Config(), credentials: credentials).LaunchAsync(CodexAsk(), "phone");

        Assert.Equal(LaunchOutcome.Failed, result.Outcome);
        var launchId = LaunchIdOf(credentials);
        await WaitAsync(() => Task.FromResult(credentials.Calls.Contains($"abandon {launchId}")));
        Assert.DoesNotContain(credentials.Calls, c => c.StartsWith("resolve ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_Codex_launch_that_could_not_start_abandons_its_credential_and_one_without_a_service_inherits_no_token()
    {
        locator.Installed["codex"] = @"C:\fake\codex.cmd";
        starter.Throws = new IOException("no such file");
        var credentials = new RecordedCredentials();
        var result = await Launcher(Config(), credentials: credentials).LaunchAsync(CodexAsk(), "phone");

        Assert.Equal(LaunchOutcome.Failed, result.Outcome);
        Assert.Equal([$"prepare  {LaunchIdOf(credentials)}", $"abandon {LaunchIdOf(credentials)}"], credentials.Calls);

        starter.Throws = null;
        starter.Registers = false;
        starter.Specs.Clear();
        _ = Launcher(Config()).LaunchAsync(CodexAsk(), "phone");
        await WaitAsync(() => Task.FromResult(starter.Specs.Count == 1));
        Assert.Null(starter.Specs[0].Environment![Launchers.TokenVariable]);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_Codex_fleet_launch_starts_only_with_a_read_reply_credential_that_takes_its_session_when_it_registers(bool issues)
    {
        locator.Installed["codex"] = @"C:\fake\codex.cmd";
        Git(f.Root, "init", "-q", "-b", "main", app);
        File.WriteAllText(Path.Combine(app, "README.md"), "fixture\n");
        Git(app, "add", ".");
        Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
        starter.Registers = false;
        var profile = Path.Combine(f.Root, "fleet-profile");
        var credentials = new AgentCredentials(profile, "machine");
        if (!issues) { Directory.CreateDirectory(Path.Combine(profile, AgentCredentials.FileName) + ".tmp"); }
        var launcher = Launcher(Config(c => c with { Workspaces = [new("App", app, true)] }), credentials: credentials,
            agentWorktreeCheck: (workspace, made, id) => AgentLaunchWorktreeCheck.Check(workspace, made, id, f.Root), agentWorktreeRefusalAudit: (_, _, _) => true);
        const string principal = "script:machine/dispatcher";
        var request = new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "codex", Workspace = "App", Permissions = LaunchProtocol.AllowAll, Prompt = "fixture" };
        var launching = GateLaunchAudit.LaunchAsync(launcher, request, principal, new AgentAuditLog(profile), new("local", "agent", principal, null), fleet: true, refusal: () => null);
        await WaitAsync(() => Task.FromResult(starter.Pids.Count == 1 || launching.IsCompleted));
        if (!issues)
        {
            Assert.Equal(LaunchOutcome.Refused, (await launching).Outcome);
            Assert.Empty(starter.Specs);
            return;
        }
        Assert.False(string.IsNullOrEmpty(Assert.Single(starter.Specs).Environment![Launchers.TokenVariable]));
        starter.RegisterCodex(starter.Specs[0], starter.Pids[0], 7001, starter.CodexId);
        var result = await launching;

        Assert.True(result.Outcome == LaunchOutcome.Started, result.Reason);
        using var saved = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, AgentCredentials.FileName)));
        var credential = Assert.Single(saved.RootElement.GetProperty("records").EnumerateArray()).GetProperty("credential");
        Assert.Equal(starter.CodexId, credential.GetProperty("session").GetString());
        Assert.Equal(AgentProtocol.ScopeReadReply, credential.GetProperty("scope").GetString());
        Assert.Equal(1, credential.GetProperty("depth").GetInt32());
    }
}
