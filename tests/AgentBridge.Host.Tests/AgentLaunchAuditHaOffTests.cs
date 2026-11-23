using System.Collections;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Host;
using AgentBridge.Host.Core.Launch;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

// HA retirement row 19 ("agent started this session" marker and attribution of writes to the agent), HA off (#1396): the functional half of
// HaOffAttributionTests. An agent launches a session through the pipe on a fake host with every Home Assistant variable absent; the launch
// starts, the started process gets no Home Assistant variable, and the audit log's launch row names the launching agent as the actor, never
// the owner or a person. Same collection as the Home Assistant launch-environment tests because it changes process environment variables.
[Collection("Launch HA environment")]
[Trait("Category", "Network")]
public sealed class AgentLaunchAuditHaOffTests : LaunchFlowFixture
{
    const string Principal = "agent:machine/parent";
    static readonly Regex HomeAssistant = new(@"(^|_)HA(_|$)|HOME_?ASSISTANT", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    sealed class Chan : IProtocolChannel
    {
        public string PeerId => "untrusted-wire-id";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.Delay(Timeout.Infinite);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }

    static JsonElement Body(LaunchRequest request) => JsonSerializer.SerializeToElement(request, ProtocolJson.Options);

    [Fact]
    public async Task An_agents_launch_through_the_pipe_with_every_Home_Assistant_variable_absent_starts_and_the_audit_row_names_the_agent()
    {
        var removed = Environment.GetEnvironmentVariables().Cast<DictionaryEntry>().Where(e => HomeAssistant.IsMatch((string)e.Key)).ToList();
        foreach (var e in removed) { Environment.SetEnvironmentVariable((string)e.Key, null); }
        try
        {
            Assert.DoesNotContain(Environment.GetEnvironmentVariables().Keys.Cast<string>(), k => HomeAssistant.IsMatch(k));
            locator.Installed["claude"] = @"C:\fake\claude.exe";
            Git(f.Root, "init", "-q", "-b", "main", app);
            File.WriteAllText(Path.Combine(app, "README.md"), "fixture");
            Git(app, "add", "README.md");
            Git(app, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-qm", "fixture");
            // The host's policy: agent launches are on, and one approved workspace (the repository above) may be launched into by an agent.
            var path = JsonSerializer.Serialize(app);
            File.WriteAllText(Path.Combine(f.Root, HostSettings.FileName), """
                {"agents":{"enabled":true,"launch":{"enabled":true},
                  "limits":{"launchesPerOriginPerHour":3,"launchesPerMachinePerHour":10,"liveAgentLaunched":6,"agentLaunchedIdleHours":2},
                  "inbound":{"local":{"launch":{"workspaces":["App"],"permissions":"ask","agents":["claude"]}}}},
                 "launch":{"workspaces":[{"label":"App","path":__APP__,"isolate":true}]}}
                """.Replace("__APP__", path));
            var launcher = Launcher(Config(c => c with { Workspaces = [new("App", app, true)] }), agentWorktreeCheck: AgentLaunchWorktreeCheck.Check);
            var credentials = new AgentCredentials(f.Root, "machine");
            var channel = new Chan();
            var started = DateTimeOffset.UtcNow;
            var token = credentials.Mint("parent", "launch", 100, started, AgentProtocol.ScopeReadReplyLaunch);
            credentials.Accept(channel, credentials.Check(token,
                new AgentBridge.Local.ProcessAncestry([new AgentBridge.Local.PeerProcess(100, started, "agent.exe")], true)).Accepted!);
            var service = new SessionService(source, new MachineInfo("machine", "Test", "windows", "0.1.0"), launcher: launcher);
            var audit = new AgentAuditLog(f.Root);
            var (requests, problem) = LaunchRequestStore.Open(f.Root, TimeProvider.System);
            var agentLaunch = new AgentPipeLaunch(f.Root, credentials, service, audit, requests, problem);
            var handle = LocalApi.Handler(service, null!, new FixedCallerGuard(new Caller(CallerKind.Agent, "fixture")),
                credentials: credentials, audit: audit, agentLaunch: agentLaunch);
            var request = new LaunchRequest { Request = Guid.NewGuid().ToString("D"), Agent = "claude", Workspace = "App", Permissions = LaunchProtocol.Ask, Prompt = "p" };

            var result = Assert.IsType<LaunchResult>(await handle(channel, LaunchProtocol.Launch, Body(request)));

            Assert.Equal(LaunchOutcome.Started, result.Outcome);
            var spec = Assert.Single(starter.Specs);
            Assert.DoesNotContain(spec.Environment!.Keys, k => HomeAssistant.IsMatch(k));
            var file = Assert.Single(Directory.GetFiles(Path.Combine(f.Root, AgentAuditLog.FolderName), "launches-*.jsonl"));
            var rows = File.ReadAllLines(file).Skip(1).Select(l => JsonSerializer.Deserialize<JsonElement>(l)).ToList();
            // The row's request is the launch id the host derives from the agent's principal and request id, so the row is found by who and what.
            // A launch is audited twice, reserved then started; both rows name the same agent and the same launch.
            var mine = rows.Where(r => r.GetProperty("action").GetString() == "session.launch" && r.GetProperty("principal").GetString() == Principal).ToList();
            Assert.Equal(["reserved", "started"], mine.Select(r => r.GetProperty("outcome").GetString()));
            Assert.Single(mine.Select(r => r.GetProperty("request").GetString()).Distinct());
            Assert.All(mine, r => Assert.Equal("local", r.GetProperty("direction").GetString()));
            Assert.DoesNotContain(rows, r => !r.GetProperty("principal").GetString()!.StartsWith("agent:", StringComparison.Ordinal));
        }
        finally
        {
            foreach (var e in removed) { Environment.SetEnvironmentVariable((string)e.Key, (string?)e.Value); }
        }
    }
}
