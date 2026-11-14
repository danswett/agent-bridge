using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Fleet;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class ScriptCallerTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abscript-" + Guid.NewGuid().ToString("N")[..8]);
    readonly Clock clock = new();
    static readonly DateTimeOffset Started = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);
    static readonly ProcessAncestry Outside = new([new PeerProcess(300, Started, "pwsh.exe")], true);
    static readonly JsonElement Empty = JsonSerializer.SerializeToElement(new { });

    public ScriptCallerTests() => Directory.CreateDirectory(root);
    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    AgentCredentials Store(Func<int, DateTimeOffset, bool>? alive = null) => new(root, "machine1", clock, rootAlive: alive);

    (string Token, AgentCredential Credential) Mint(AgentCredentials store, string name = "dispatcher")
    {
        var minted = store.MintScript(name, TimeSpan.FromHours(1));
        var protectedBytes = File.ReadAllBytes(minted.Path);
        var clear = OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(protectedBytes, null, DataProtectionScope.CurrentUser) : protectedBytes;
        try { return (Encoding.ASCII.GetString(clear), minted.Credential); }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    [Fact]
    public void A_registered_script_authenticates_without_a_session_process_and_advertises_read_only()
    {
        var store = Store((_, _) => false);
        var minted = Mint(store);
        var credential = Assert.IsType<AgentCredential>(store.Check(minted.Token, Outside).Accepted);
        var channel = new Chan();
        var hello = store.Accept(channel, credential);
        Assert.Equal("script:machine1/dispatcher", hello.Principal);
        Assert.Equal(AgentProtocol.ScopeRead, hello.Scope);
        Assert.Equal(credential, store.Of(channel));
        Assert.True(store.IsLive(credential));
        Assert.NotNull(Store((_, _) => false).Check(minted.Token, Outside).Accepted);
        clock.Now = Started.AddHours(1);
        Assert.Null(store.Of(channel));
        Assert.False(store.IsLive(credential));
        Assert.Null(store.Check(minted.Token, Outside).Accepted);
    }

    [Theory]
    [InlineData(false, "pwsh.exe", false)]
    [InlineData(true, null, false)]
    [InlineData(true, "", false)]
    [InlineData(true, "copilot.exe", false)]
    [InlineData(true, "claude.exe", false)]
    [InlineData(true, "codex.exe", false)]
    [InlineData(true, "node.exe", true)]
    public void Partial_untraceable_and_agent_ancestries_never_authenticate_as_scripts(bool complete, string? image, bool registered)
    {
        var store = Store();
        if (registered) { store.Mint("session1", "launch1", 100, Started, AgentProtocol.ScopeRead); }
        var minted = Mint(store);
        var ancestry = new ProcessAncestry(image is null ? [] :
            [new PeerProcess(300, Started, "pwsh.exe"), new PeerProcess(100, Started, image)], complete);
        Assert.Null(store.Check(minted.Token, ancestry).Accepted);
        Assert.Null(store.Check(minted.Token, Outside, agentCaller: true).Accepted);
        Assert.Null(store.Check(minted.Token, new([new PeerProcess(300, null, "pwsh.exe")], true)).Accepted);
        Assert.NotNull(store.Check(minted.Token, Outside).Accepted);
    }

    [Theory]
    [InlineData("agent.send")]
    [InlineData("session.launch")]
    [InlineData("session.answer")]
    [InlineData("session.reply")]
    [InlineData("session.stop")]
    [InlineData("session.rename")]
    [InlineData("session.history")]
    [InlineData("sessions.list")]
    [InlineData("host.restart")]
    [InlineData("hello")]
    [InlineData("agent.hello")]
    [InlineData("future.operation")]
    public async Task A_script_connection_never_falls_back_to_person_authority_even_after_expiry(string op)
    {
        var store = Store();
        var channel = new Chan();
        store.Accept(channel, Mint(store).Credential);
        var lines = new List<string>();
        var handler = LocalApi.Handler(null!, null!, new FixedCallerGuard(new(CallerKind.App, "fixture app")), credentials: store, log: lines.Add);
        foreach (var now in new[] { Started, Started.AddHours(1) })
        {
            clock.Now = now;
            var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => handler(channel, op, Empty));
            Assert.Contains("fleet.list and fleet.find", refusal.Message);
        }
        Assert.All(lines, line => Assert.Contains("script:machine1/dispatcher", line));
    }

    [Fact]
    public void Empty_session_and_launch_identifiers_cannot_revoke_or_bind_script_records()
    {
        var store = Store();
        var minted = Mint(store);
        store.Revoke("", "not a session");
        store.RevokeSession("", "not a session");
        store.Abandon("");
        store.Bind("", 100, Started);
        Assert.NotNull(Store().Check(minted.Token, Outside).Accepted);
    }

    [Fact]
    public async Task Only_a_successful_new_token_fleet_read_retires_the_old_rotation_and_persists_it()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var store = Store();
        var old = Mint(store);
        var newer = Mint(store);
        var channel = new Chan();
        store.Accept(channel, newer.Credential);
        var roster = new Roster();
        var audit = new AgentAuditLog(root);
        var op = new FleetList(store, roster, audit: audit);
        Assert.Equal(2, Store().Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new FleetList(store, roster).HandleAsync(channel, Empty));
        await Assert.ThrowsAsync<InvalidOperationException>(() => op.FindAsync(channel, Empty));
        roster.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => op.HandleAsync(channel, Empty));
        roster.Fail = false;
        Assert.Equal(2, Store().Count);
        var oldChannel = new Chan();
        store.Accept(oldChannel, old.Credential);
        await op.HandleAsync(oldChannel, Empty);
        Assert.Equal(2, Store().Count);
        var reply = JsonSerializer.SerializeToElement(await op.FindAsync(channel,
            JsonSerializer.SerializeToElement(new { name = "SG Quality", fleetScope = "spoof" })), ProtocolJson.Options);
        Assert.Equal("unverified", reply.GetProperty("result").GetString());
        Assert.Equal("account-a", reply.GetProperty("fleetScope").GetString());
        Assert.DoesNotContain("node", reply.GetRawText());
        Assert.Equal(1, Store().Count);
        Assert.Null(store.Of(oldChannel));
        var handler = LocalApi.Handler(null!, null!, new FixedCallerGuard(new(CallerKind.App, "fixture app")), credentials: store);
        Assert.Contains("fleet.list and fleet.find", (await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler(oldChannel, "session.launch", Empty))).Message);
        Assert.Null(Store().Check(old.Token, Outside).Accepted);
        Assert.NotNull(Store().Check(newer.Token, Outside).Accepted);
        Assert.Contains("script:machine1/dispatcher", string.Join("\n",
            Directory.GetFiles(Path.Combine(root, AgentAuditLog.FolderName), "agents-*.jsonl").Select(File.ReadAllText)));
    }

    [Fact]
    public async Task A_script_cannot_send_through_the_direct_entry_point()
    {
        var store = Store();
        var channel = new Chan();
        store.Accept(channel, Mint(store).Credential);
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentBridge.Host.Lite.LiteAgentSend.HandleAsync(store, null, channel, Empty, windows: true));
        Assert.Contains("fleet.list and fleet.find", refusal.Message);
    }

    [Fact]
    public async Task Expiry_during_a_fleet_read_refuses_the_result_without_retiring_the_old_hash()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var store = Store();
        Mint(store);
        var newer = Mint(store);
        var channel = new Chan();
        store.Accept(channel, newer.Credential);
        var roster = new Roster { OnRead = () => clock.Now = Started.AddHours(1) };
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FleetList(store, roster, audit: new AgentAuditLog(root)).HandleAsync(channel, Empty));
        Assert.Equal(2, Store().Count);
    }

    [Fact]
    public async Task A_rotation_save_failure_refuses_the_read_and_invalidates_open_grants()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var store = Store();
        Mint(store);
        var newer = Mint(store);
        var channel = new Chan();
        store.Accept(channel, newer.Credential);
        Directory.CreateDirectory(Path.Combine(root, AgentCredentials.FileName + ".tmp"));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new FleetList(store, new Roster(), audit: new AgentAuditLog(root)).HandleAsync(channel, Empty));
        Assert.NotNull(store.Problem);
        Assert.False(store.IsLive(newer.Credential));
        Assert.Equal(2, Store().Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_oversized_fleet_reply_never_retires_the_old_hash(bool find)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var store = Store();
        Mint(store);
        var newer = Mint(store);
        var channel = new Chan();
        store.Accept(channel, newer.Credential);
        var roster = new Roster { Machines = Enumerable.Range(0, 1024).Select(i =>
            new FleetMachine($"machine{i}", true, [new("fixture", "SG Quality", "working", Started)], Name: new string('m', 64))).ToArray() };
        var op = new FleetList(store, roster, audit: new AgentAuditLog(root));
        await Assert.ThrowsAsync<InvalidOperationException>(() => find
            ? op.FindAsync(channel, JsonSerializer.SerializeToElement(new { name = "SG Quality" })) : op.HandleAsync(channel, Empty));
        Assert.Equal(2, Store().Count);
    }

    [Fact]
    public void A_faulted_store_invalidates_already_authenticated_scripts()
    {
        var store = Store();
        var minted = Mint(store);
        var channel = new Chan();
        store.Accept(channel, minted.Credential);
        store.Mint("session1", "launch1", 100, Started, AgentProtocol.ScopeRead);
        Directory.CreateDirectory(Path.Combine(root, AgentCredentials.FileName + ".tmp"));
        store.RevokeSession("session1", "fixture");
        Assert.NotNull(store.Problem);
        Assert.Null(store.Of(channel));
        Assert.False(store.IsLive(minted.Credential));
    }

    sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = Started;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    sealed class Roster : IScopedFleetRoster
    {
        public bool Fail { get; set; }
        public Action? OnRead { get; init; }
        public IReadOnlyList<FleetMachine>? Machines { get; init; }
        public Task<IReadOnlyList<FleetMachine>> ReadAsync(CancellationToken ct)
        {
            OnRead?.Invoke();
            return Fail ? Task.FromException<IReadOnlyList<FleetMachine>>(new IOException("fixture roster failure"))
                : Task.FromResult<IReadOnlyList<FleetMachine>>(Machines ?? [new("machine2", true, [new("fixture", "SG Quality", "working", Started)])]);
        }
        public async Task<FleetRosterSnapshot> ReadScopedAsync(CancellationToken ct) => new("account-a", await ReadAsync(ct));
    }

    sealed class Chan : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.Delay(Timeout.Infinite);
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
