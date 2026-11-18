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
    public void A_registered_script_authenticates_without_a_session_process_and_advertises_read_and_reply()
    {
        var store = Store((_, _) => false);
        var minted = Mint(store);
        var credential = Assert.IsType<AgentCredential>(store.Check(minted.Token, Outside).Accepted);
        var channel = new Chan();
        var hello = store.Accept(channel, credential);
        Assert.Equal("script:machine1/dispatcher", hello.Principal);
        Assert.Equal(AgentProtocol.ScopeReadReply, hello.Scope);
        Assert.Equal(credential, store.Of(channel));
        Assert.True(store.IsLive(credential));
        Assert.NotNull(Store((_, _) => false).Check(minted.Token, Outside).Accepted);
        clock.Now = Started.AddHours(1);
        Assert.Null(store.Of(channel));
        Assert.False(store.IsLive(credential));
        Assert.Null(store.Check(minted.Token, Outside).Accepted);
    }

    [Fact]
    public async Task Every_script_hello_and_operation_writes_exactly_one_private_audit_record()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var store = Store();
        var minted = Mint(store);
        var audit = new AgentAuditLog(root);
        var roster = new Roster();
        var handle = LocalApi.Handler(null!, null!, new FixedCallerGuard(new(CallerKind.App, "fixture app")),
            credentials: store, fleet: new FleetList(store, roster, audit: audit), audit: audit);
        var accepted = Local(Outside);
        await handle(accepted, AgentProtocol.Hello, JsonSerializer.SerializeToElement(new
        {
            token = minted.Token,
            message = "private hello text",
        }));
        Assert.Single(Lines(root));

        var inside = Local(new ProcessAncestry([new PeerProcess(300, Started, "copilot.exe")], true));
        await Assert.ThrowsAsync<InvalidOperationException>(() => handle(inside, AgentProtocol.Hello,
            JsonSerializer.SerializeToElement(new { token = minted.Token })));
        Assert.Equal(2, Lines(root).Length);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handle(accepted, "session.stop",
            JsonSerializer.SerializeToElement(new { text = "private operation text" })));
        Assert.Equal(3, Lines(root).Length);

        await handle(accepted, FleetList.Op, Empty);
        Assert.Equal(4, Lines(root).Length);
        Assert.All(Lines(root), row =>
        {
            Assert.Equal("script:machine1/dispatcher", row.GetProperty("principal").GetString());
            Assert.DoesNotContain(minted.Token, row.GetRawText(), StringComparison.Ordinal);
            Assert.DoesNotContain("private", row.GetRawText(), StringComparison.Ordinal);
        });
    }

    [Theory]
    [InlineData("expired")]
    [InlineData("revoked")]
    [InlineData("untraced")]
    [InlineData("agent")]
    public async Task Refused_script_presentations_are_audited_once_without_request_content(string reason)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var store = Store();
        var minted = Mint(store);
        if (reason == "expired") { clock.Now = Started.AddHours(1); }
        if (reason == "revoked") { store.RevokeScript("dispatcher"); }
        var handler = LocalApi.Handler(null!, null!, new FixedCallerGuard(new(
            reason == "agent" ? CallerKind.Agent : CallerKind.App, "fixture")), credentials: store, audit: new(root));

        await Assert.ThrowsAsync<InvalidOperationException>(() => handler(
            Local(reason == "untraced" ? ProcessAncestry.Unknown : Outside), AgentProtocol.Hello,
            JsonSerializer.SerializeToElement(new { token = minted.Token, message = "private content" })));

        var row = Assert.Single(Lines(root));
        Assert.Equal(minted.Credential.Principal, row.GetProperty("principal").GetString());
        Assert.Equal("refused", row.GetProperty("outcome").GetString());
        Assert.DoesNotContain(minted.Token, row.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain("private content", row.GetRawText(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fleet.find")]
    [InlineData("agent.send")]
    [InlineData("host.restart")]
    [InlineData("private operation text")]
    public async Task Invalid_script_operations_are_audited_once_by_name_without_content(string op)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var store = Store();
        var channel = new Chan();
        var minted = Mint(store);
        store.Accept(channel, minted.Credential);
        var handler = LocalApi.Handler(null!, null!, new FixedCallerGuard(new(CallerKind.App, "fixture")),
            credentials: store, fleet: new FleetList(store, new Roster()), audit: new(root));

        await Assert.ThrowsAnyAsync<Exception>(() => handler(channel, op,
            JsonSerializer.SerializeToElement(new { text = "private operation text", token = minted.Token })));

        var row = Assert.Single(Lines(root));
        Assert.Equal(minted.Credential.Principal, row.GetProperty("principal").GetString());
        Assert.DoesNotContain("private operation text", row.GetRawText(), StringComparison.Ordinal);
        Assert.DoesNotContain(minted.Token, row.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_script_hello_cannot_authenticate_when_the_audit_is_unwritable()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var store = Store();
        var minted = Mint(store);
        var channel = Local(Outside);
        var handler = LocalApi.Handler(null!, null!, new FixedCallerGuard(new(CallerKind.App, "fixture")),
            credentials: store, audit: new(root, maxBytes: 1));

        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() => handler(channel, AgentProtocol.Hello,
            JsonSerializer.SerializeToElement(new { token = minted.Token })));

        Assert.Contains("auditUnwritable", refusal.Message);
        Assert.Null(store.Of(channel));
    }

    [Fact]
    public async Task A_script_record_and_token_copied_to_another_machine_are_refused_and_audited_by_name()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var source = Store();
        var minted = Mint(source);
        var otherRoot = Path.Combine(root, "machine2");
        Directory.CreateDirectory(otherRoot);
        File.Copy(Path.Combine(root, AgentCredentials.FileName), Path.Combine(otherRoot, AgentCredentials.FileName));
        var other = new AgentCredentials(otherRoot, "machine2", clock);
        Assert.NotNull(other.Problem);
        var audit = new AgentAuditLog(otherRoot);
        var handle = LocalApi.Handler(null!, null!, new FixedCallerGuard(new(CallerKind.App, "fixture app")),
            credentials: other, audit: audit);

        await Assert.ThrowsAsync<InvalidOperationException>(() => handle(Local(Outside), AgentProtocol.Hello,
            JsonSerializer.SerializeToElement(new { token = minted.Token })));

        var row = Assert.Single(Lines(otherRoot));
        Assert.Equal("script:machine1/dispatcher", row.GetProperty("principal").GetString());
        Assert.Equal("refused", row.GetProperty("outcome").GetString());
        Assert.DoesNotContain(minted.Token, row.GetRawText(), StringComparison.Ordinal);
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
            Assert.Contains("fleet.list, fleet.find and agent.send", refusal.Message);
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
        Assert.Contains("fleet.list, fleet.find and agent.send", (await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler(oldChannel, "session.launch", Empty))).Message);
        Assert.Null(Store().Check(old.Token, Outside).Accepted);
        Assert.NotNull(Store().Check(newer.Token, Outside).Accepted);
        Assert.Contains("script:machine1/dispatcher", string.Join("\n",
            Directory.GetFiles(Path.Combine(root, AgentAuditLog.FolderName), "agents-*.jsonl").Select(File.ReadAllText)));
    }

    [Fact]
    public async Task A_revoked_script_cannot_send_through_the_direct_entry_point()
    {
        var store = Store();
        var channel = new Chan();
        store.Accept(channel, Mint(store).Credential);
        store.RevokeScript("dispatcher");
        var refusal = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentBridge.Host.Lite.LiteAgentSend.HandleAsync(store, null, channel, Empty, windows: true));
        Assert.Contains("no longer live", refusal.Message);
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

    static JsonElement[] Lines(string root) =>
        (Directory.Exists(Path.Combine(root, AgentAuditLog.FolderName))
            ? Directory.GetFiles(Path.Combine(root, AgentAuditLog.FolderName), "agents-*.jsonl") : [])
            .SelectMany(File.ReadAllLines).Skip(1)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToArray();

    static IProtocolChannel Local(ProcessAncestry ancestry)
    {
        var constructor = typeof(LocalChannel).GetConstructors(System.Reflection.BindingFlags.Instance |
            System.Reflection.BindingFlags.NonPublic).Single(c => c.GetParameters().Length is 6 or 7);
        var local = (LocalChannel)constructor.Invoke([Stream.Null, "fixture", "fixture", "local", null,
            ProtocolJson.Options, 8]);
        typeof(LocalChannel).GetProperty(nameof(LocalChannel.Peer))!.SetValue(local,
            new PipePeer(300, "fixture", false, "pwsh.exe") { Ancestry = ancestry });
        return local;
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
