using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Cli;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class FleetLaunchGrantTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("ab-fleet-grant-").FullName;
    static readonly DateTimeOffset Now = new(2026, 11, 17, 0, 0, 0, TimeSpan.Zero);
    static readonly ProcessAncestry Outside = new([new PeerProcess(123, Now, "pwsh.exe")], true);
    static AgentCredential Script => AgentCredential.Script("fixture", "machine", "dispatcher", Now) with { FleetLaunch = true };
    static LaunchRequest Request => new() { Request = Guid.NewGuid().ToString("D"), Agent = "claude",
        Workspace = "workers", Permissions = LaunchProtocol.AllowAll };
    void Config(string workspace = "workers", bool isolate = true) => File.WriteAllText(Path.Combine(folder, HostSettings.FileName),
        JsonSerializer.Serialize(new { agents = new { enabled = true, fleet = new { workspaces = new[] { workspace } } },
            launch = new { workspaces = new[] { new { label = "workers", path = folder, isolate } } } }));
    public void Dispose() => Directory.Delete(folder, true);

    [Fact]
    public void allowAll_is_allowed_only_in_a_ticked_isolated_workspace()
    {
        var grant = new FleetLaunchGrant(folder);
        Config();
        Assert.Null(grant.CheckLaunch(Script, Outside, Request));
        Config("elsewhere");
        Assert.Contains("ticked", grant.CheckLaunch(Script, Outside, Request));
        Config(isolate: false);
        Assert.Contains("isolated", grant.CheckLaunch(Script, Outside, Request));
        Config();
        Assert.Contains("listed", grant.CheckLaunch(Script, Outside, Request with { Workspace = "unknown" }));
    }

    [Fact]
    public void A_launch_is_refused_when_the_fleet_grant_is_off_with_no_fallback()
    {
        Config();
        var grant = new FleetLaunchGrant(folder);
        Assert.Contains("off", grant.CheckGrant(Script with { FleetLaunch = false }));
        Assert.Contains("off", grant.CheckLaunch(Script with { FleetLaunch = false }, Outside, Request));
        File.WriteAllText(Path.Combine(folder, HostSettings.FileName), """{"agents":{"enabled":true,"fleet":{"workspaces":true}}}""");
        Assert.NotNull(grant.CheckLaunch(Script, Outside, Request));
    }

    [Fact]
    public void Partial_agent_ancestry_wider_scope_and_unsafe_request_shapes_are_refused()
    {
        Config();
        var grant = new FleetLaunchGrant(folder);
        Assert.NotNull(grant.CheckLaunch(Script, Outside with { Complete = false }, Request));
        Assert.NotNull(grant.CheckLaunch(Script, new([new PeerProcess(123, Now, "copilot.exe")], true), Request));
        Assert.NotNull(grant.CheckLaunch(Script with { Scope = AgentProtocol.ScopeReadReplyLaunch }, Outside, Request));
        foreach (var request in new[] { Request with { Resume = "existing" }, Request with { Profile = "override" },
            Request with { Attachments = [new AttachmentRef { Id = Guid.NewGuid().ToString("D") }] } })
        { Assert.NotNull(grant.CheckLaunch(Script, Outside, request)); }
    }

    [Fact]
    public void Only_the_owner_grants_fleet_and_workers_receive_reply_but_never_launch_scope()
    {
        Config();
        var store = new AgentCredentials(folder, "machine");
        var (script, _) = store.MintScript("dispatcher");
        var channel = new Channel();
        var data = JsonSerializer.SerializeToElement(new { name = "dispatcher" });
        var owner = new FixedCallerGuard(new(CallerKind.HostCli, "fixture owner"));
        Assert.Throws<InvalidOperationException>(() => ScriptCredentialCommands.Handle(channel, "scripts.grant-fleet", data,
            new FixedCallerGuard(new(CallerKind.Agent, "fixture agent")), store, new AgentAuditLog(folder)));
        ScriptCredentialCommands.Handle(channel, "scripts.grant-fleet", data, owner, store, new AgentAuditLog(folder));
        store.Accept(channel, script);
        Assert.True(store.Of(channel)!.FleetLaunch);
        var restarted = new AgentCredentials(folder, "machine");
        restarted.Accept(channel, script);
        Assert.True(restarted.Of(channel)!.FleetLaunch);
        var token = store.PrepareFleet("worker", "launch");
        var worker = store.Check(token, new([new PeerProcess(124, Now, "claude.exe")], true));
        Assert.Null(worker.Accepted); // Unbound tokens must not authenticate.
        store.Bind("launch", 124, Now);
        var accepted = Assert.IsType<AgentCredential>(store.Check(token, new([new PeerProcess(124, Now, "claude.exe")], true)).Accepted);
        Assert.Equal(AgentProtocol.ScopeReadReply, accepted.Scope);
        Assert.Equal(1, accepted.Depth);
        Assert.False(accepted.FleetLaunch);
        ScriptCredentialCommands.Handle(new Channel(), "scripts.revoke-fleet", data, owner, store, new AgentAuditLog(folder));
        Assert.False(store.Of(channel)!.FleetLaunch);
    }

    [Fact]
    public void The_active_dispatch_workspace_must_match_current_approval()
    {
        Config();
        var grant = new FleetLaunchGrant(folder);
        var actual = HostSettings.Load(folder).Settings.Launch;
        Assert.Null(grant.CheckDispatch(actual, Request));
        Assert.NotNull(grant.CheckDispatch(actual with { Workspaces = [new("workers", Path.GetTempPath(), true)] }, Request));
    }

    [Fact]
    public async Task A_granted_script_still_refuses_scope_overrides_and_an_untraceable_pipe()
    {
        Config();
        var store = new AgentCredentials(folder, "machine");
        var (script, _) = store.MintScript("dispatcher");
        var owner = new FixedCallerGuard(new(CallerKind.HostCli, "fixture owner"));
        ScriptCredentialCommands.Handle(new Channel(), "scripts.grant-fleet",
            JsonSerializer.SerializeToElement(new { name = "dispatcher" }), owner, store, new AgentAuditLog(folder));
        var channel = new Channel();
        store.Accept(channel, script);
        var handler = LocalApi.Handler(null!, null!, owner, credentials: store);
        var shape = await Assert.ThrowsAsync<InvalidOperationException>(() => handler(channel, LaunchProtocol.Launch,
            JsonSerializer.SerializeToElement(new { request = Request.Request, agentScope = "read+reply+launch" })));
        Assert.Contains("unsupported fields", shape.Message);
        var ancestry = await Assert.ThrowsAsync<InvalidOperationException>(() => handler(channel, LaunchProtocol.Launch,
            JsonSerializer.SerializeToElement(Request, ProtocolJson.Options)));
        Assert.Contains("ancestry", ancestry.Message);
    }

    sealed class Channel : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.CompletedTask;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
