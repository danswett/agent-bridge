using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class AgentCredentialsTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abcred-" + Guid.NewGuid().ToString("N")[..8]);
    static readonly DateTimeOffset Started = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    public AgentCredentialsTests() => Directory.CreateDirectory(root);

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    AgentCredentials Store(TimeProvider? time = null) => new(root, "machine1", time);

    static ProcessAncestry Inside(int rootPid = 100, DateTimeOffset? started = null, bool complete = true) =>
        new([new PeerProcess(300, Started.AddMinutes(5), "cli.exe"), new PeerProcess(rootPid, started ?? Started, "agent.exe")], complete);

    [Fact]
    public void A_token_is_accepted_from_inside_its_session_and_the_connection_becomes_the_agent()
    {
        var store = Store();
        var token = store.Mint("sess-1", "launch-1", 100, Started, AgentProtocol.ScopeRead);
        var decision = store.Check(token, Inside());
        Assert.NotNull(decision.Accepted);
        Assert.Equal("sess-1", decision.Accepted!.Session);
        Assert.Equal(AgentProtocol.TokenLength, token.Length);
    }

    [Fact]
    public void A_token_presented_from_outside_its_session_is_refused_and_logged()
    {
        var lines = new List<string>();
        var store = new AgentCredentials(root, "machine1", log: lines.Add);
        var token = store.Mint("sess-1", "launch-1", 100, Started, AgentProtocol.ScopeRead);
        var decision = store.Check(token, new ProcessAncestry([new PeerProcess(300, Started, "other.exe")], true));
        Assert.Null(decision.Accepted);
        Assert.Contains(lines, l => l.Contains("credential presented from outside its session"));
    }

    [Fact]
    public void A_partial_ancestry_a_recycled_pid_and_a_missing_start_time_are_refused()
    {
        var store = Store();
        var token = store.Mint("sess-1", "launch-1", 100, Started, AgentProtocol.ScopeRead);
        Assert.Null(store.Check(token, Inside(complete: false)).Accepted);
        Assert.Null(store.Check(token, Inside(started: Started.AddHours(3))).Accepted);
        Assert.Null(store.Check(token, new ProcessAncestry([new PeerProcess(100, null, "agent.exe")], true)).Accepted);
    }

    [Fact]
    public void A_root_that_exited_after_the_pipe_connected_is_refused_and_an_open_connection_loses_its_agent()
    {
        var alive = true;
        var store = new AgentCredentials(root, "machine1", rootAlive: (_, _) => alive);
        var token = store.Mint("sess-1", "launch-1", 100, Started, AgentProtocol.ScopeRead);
        var channel = new FakeChannel();
        store.Accept(channel, store.Check(token, Inside()).Accepted!);
        Assert.NotNull(store.Of(channel));
        alive = false;
        Assert.Null(store.Of(channel));
        Assert.Null(store.Check(token, Inside()).Accepted);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void A_launch_token_answers_to_nobody_until_the_started_process_is_bound()
    {
        var store = Store();
        var token = store.Prepare("sess-1", "launch-1")!;
        Assert.Null(store.Check(token, Inside()).Accepted);
        store.Bind("launch-1", 100, Started);
        Assert.Equal("sess-1", store.Check(token, Inside()).Accepted!.Session);
        Assert.Null(store.Check(token, Inside(rootPid: 101)).Accepted);
    }

    [Fact]
    public void A_launch_that_did_not_start_loses_its_token_and_a_resume_replaces_the_old_one()
    {
        var store = Store();
        var failed = store.Prepare("sess-1", "launch-1")!;
        store.Abandon("launch-1");
        store.Bind("launch-1", 100, Started);
        Assert.Null(store.Check(failed, Inside()).Accepted);
        Assert.Equal(0, store.Count);

        var first = store.Prepare("sess-2", "l2")!;
        store.Bind("l2", 100, Started);
        var second = store.Prepare("sess-2", "l3")!;
        store.Bind("l3", 100, Started);
        Assert.Null(store.Check(first, Inside()).Accepted);
        Assert.NotNull(store.Check(second, Inside()).Accepted);
    }

    [Fact]
    public void An_unknown_or_malformed_token_is_refused()
    {
        var store = Store();
        store.Mint("sess-1", "launch-1", 100, Started, AgentProtocol.ScopeRead);
        Assert.Null(store.Check(AgentProtocol.TokenPrefix + new string('A', 43), Inside()).Accepted);
        Assert.Null(store.Check(null, Inside()).Accepted);
        Assert.Null(store.Check("nope", Inside()).Accepted);
    }

    [Fact]
    public void A_new_credential_for_a_session_kills_the_old_one_so_a_resumed_sessions_old_token_is_dead()
    {
        var store = Store();
        var old = store.Mint("sess-1", "launch-1", 100, Started, AgentProtocol.ScopeRead);
        var resumed = store.Mint("sess-1", "launch-2", 200, Started, AgentProtocol.ScopeRead);
        Assert.Null(store.Check(old, Inside()).Accepted);
        Assert.NotNull(store.Check(resumed, Inside(200)).Accepted);
    }

    [Fact]
    public void Only_the_hash_is_stored_and_credentials_survive_a_restart()
    {
        var token = Store().Mint("sess-1", "launch-1", 100, Started, AgentProtocol.ScopeRead);
        Assert.DoesNotContain(token, File.ReadAllText(Path.Combine(root, AgentCredentials.FileName)));
        Assert.NotNull(Store().Check(token, Inside()).Accepted);
    }

    [Fact]
    public void An_unreadable_store_fails_closed_and_is_not_treated_as_empty()
    {
        var token = Store().Mint("sess-1", "launch-1", 100, Started, AgentProtocol.ScopeRead);
        File.WriteAllText(Path.Combine(root, AgentCredentials.FileName), "{ not json");
        var store = Store();
        Assert.NotNull(store.Problem);
        Assert.Null(store.Check(token, Inside()).Accepted);
        Assert.Throws<InvalidOperationException>(() => store.Mint("sess-2", "l", 1, Started, AgentProtocol.ScopeRead));
    }

    [Fact]
    public void An_expired_or_revoked_credential_is_refused()
    {
        var time = new Clock(Started);
        var store = Store(time);
        var a = store.Mint("sess-1", "l1", 100, Started, AgentProtocol.ScopeRead, lifetime: TimeSpan.FromHours(1));
        var b = store.Mint("sess-2", "l2", 100, Started, AgentProtocol.ScopeRead);
        store.RevokeSession("sess-2", "test");
        Assert.Null(store.Check(b, Inside()).Accepted);
        time.Now = Started.AddHours(2);
        Assert.Null(store.Check(a, Inside()).Accepted);
    }

    [Fact]
    public void A_script_record_defaults_to_thirty_days_without_minting_or_storing_a_token()
    {
        var record = AgentCredential.Script("credential1", "machine1", "pump-wake", Started);
        Assert.Equal(Started.AddDays(30), record.ExpiresAt);
        Assert.Equal("script:machine1/pump-wake", Store().PrincipalOf(record));
        Assert.False(File.Exists(Path.Combine(root, AgentCredentials.FileName)));
        Assert.Throws<ArgumentException>(() => AgentCredential.Script("credential1", "machine1", "../bad", Started));
        Assert.Throws<ArgumentException>(() => AgentCredential.Script("credential1", "machine1", "pump", Started, TimeSpan.FromDays(31)));
    }

    [Fact]
    public void Script_rotation_keeps_both_hashes_across_restart_and_refuses_a_third_without_replacing_the_file()
    {
        var clock = new Clock(Started);
        var first = Store(clock).MintScript("pump-wake", TimeSpan.FromDays(1));
        var before = File.ReadAllBytes(first.Path);
        var second = Store(clock).MintScript("pump-wake");
        Assert.False(before.SequenceEqual(File.ReadAllBytes(second.Path)));
        var rotated = File.ReadAllBytes(second.Path);
        var store = Store(clock);
        Assert.Equal(2, store.Count);
        Assert.True(File.ReadAllText(Path.Combine(root, AgentCredentials.FileName)).Contains(first.Credential.Id, StringComparison.Ordinal));
        Assert.Throws<InvalidOperationException>(() => store.MintScript("pump-wake"));
        Assert.True(rotated.SequenceEqual(File.ReadAllBytes(second.Path)));
        Assert.Equal(2, Store(clock).Count);
        clock.Now = Started.AddDays(2);
        store = Store(clock);
        store.MintScript("pump-wake");
        Assert.Equal(2, Store(clock).Count);
        Assert.Equal(first.Path, second.Path);
        Assert.Equal(AgentProtocol.ScopeReadReply, second.Credential.Scope);
    }

    [Fact]
    public void A_script_file_is_owner_only_user_protected_and_no_clear_token_reaches_the_store_or_logs()
    {
        var lines = new List<string>();
        var store = new AgentCredentials(root, "machine1", new Clock(Started), lines.Add);
        var minted = store.MintScript("pump-wake");
        var bytes = File.ReadAllBytes(minted.Path);
        var clear = OperatingSystem.IsWindows()
            ? ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser) : bytes;
        try
        {
            var token = Encoding.ASCII.GetString(clear);
            Assert.True(token.Length == AgentProtocol.TokenLength && token.StartsWith(AgentProtocol.TokenPrefix, StringComparison.Ordinal));
            Assert.Null(store.Check(token, Inside()).Accepted);
            var saved = File.ReadAllText(Path.Combine(root, AgentCredentials.FileName));
            Assert.True(saved.Contains(Convert.ToHexString(SHA256.HashData(clear)), StringComparison.Ordinal));
            Assert.False(saved.Contains(token, StringComparison.Ordinal));
            Assert.False(string.Join("\n", lines).Contains(token, StringComparison.Ordinal));
            Assert.False(minted.ToString().Contains(token, StringComparison.Ordinal));
            if (OperatingSystem.IsWindows()) { Assert.False(bytes.SequenceEqual(clear)); }
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
        Assert.True(PrivateFolder.IsPrivate(Path.GetDirectoryName(minted.Path)!));
        if (OperatingSystem.IsWindows())
        {
            var me = WindowsIdentity.GetCurrent().User!;
            var rules = new FileInfo(minted.Path).GetAccessControl().GetAccessRules(true, true, typeof(SecurityIdentifier));
            Assert.NotEmpty(rules.Cast<FileSystemAccessRule>());
            foreach (FileSystemAccessRule rule in rules) { Assert.True(me.Equals(rule.IdentityReference)); }
        }
        else { Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(minted.Path)); }
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(minted.Path)!));
    }

    [Fact]
    public void A_failed_script_file_replacement_preserves_the_old_hash_and_does_not_consume_a_rotation_slot()
    {
        var store = Store(new Clock(Started));
        var minted = store.MintScript("pump-wake");
        File.Delete(minted.Path);
        Directory.CreateDirectory(minted.Path);
        Assert.Throws<InvalidOperationException>(() => store.MintScript("pump-wake"));
        Assert.Equal(1, Store(new Clock(Started)).Count);
        Directory.Delete(minted.Path);
        Assert.Empty(Directory.GetFileSystemEntries(Path.GetDirectoryName(minted.Path)!));
    }

    [Fact]
    public void Invalid_script_names_or_a_broken_store_create_no_secret_files()
    {
        var store = Store(new Clock(Started));
        Assert.Throws<ArgumentException>(() => store.MintScript("../escape"));
        Assert.Throws<ArgumentException>(() => store.MintScript("pump", TimeSpan.FromDays(31)));
        Assert.Empty(Directory.GetFileSystemEntries(root));
        File.WriteAllText(Path.Combine(root, AgentCredentials.FileName), "{ broken");
        Assert.Throws<InvalidOperationException>(() => Store().MintScript("pump"));
        Assert.Single(Directory.GetFileSystemEntries(root));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void A_logging_failure_issues_nothing_and_leaves_the_rotation_slot_available(bool rotating, bool expired)
    {
        var clock = new Clock(Started);
        var existing = rotating ? Store(clock).MintScript("pump").Path : null;
        if (expired)
        {
            Store(clock).Mint("expired", "launch", 100, Started, AgentProtocol.ScopeRead, lifetime: TimeSpan.FromHours(1));
            clock.Now = Started.AddHours(2);
        }
        var count = Store(clock).Count;
        var before = existing is null ? null : File.ReadAllBytes(existing);
        var verifier = Path.Combine(root, AgentCredentials.FileName);
        var hashes = File.Exists(verifier) ? File.ReadAllBytes(verifier) : null;
        var store = new AgentCredentials(root, "machine1", clock, line =>
        {
            if (line.Contains("issuing script credential", StringComparison.Ordinal)) { throw new IOException("synthetic log failure"); }
        });
        Assert.Throws<IOException>(() => store.MintScript("pump"));
        Assert.Equal(count, store.Count);
        Assert.Equal(count, Store(clock).Count);
        if (existing is not null)
        {
            Assert.True(before!.SequenceEqual(File.ReadAllBytes(existing)));
            Assert.True(hashes!.SequenceEqual(File.ReadAllBytes(verifier)));
        }
        else { Assert.Empty(Directory.GetFiles(root, "*", SearchOption.AllDirectories)); }
        Store(clock).MintScript("pump");
        Assert.Equal(rotating ? 2 : 1, Store(clock).Count);
    }

    [Fact]
    public void Concurrent_script_mints_cannot_exceed_two_and_case_distinct_names_have_separate_files()
    {
        var store = Store(new Clock(Started));
        Parallel.For(0, 8, _ =>
        {
            try { store.MintScript("pump"); }
            catch (InvalidOperationException) { }
        });
        Assert.Equal(2, Store(new Clock(Started)).Count);
        var upper = store.MintScript("PUMP");
        Assert.Equal(2, Directory.GetFiles(Path.GetDirectoryName(upper.Path)!).Length);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    public void A_script_record_survives_reload_with_its_own_principal_but_cannot_authenticate_yet(int days)
    {
        var (token, credential) = ScriptRecord();
        credential["expiresAt"] = Started.AddDays(days);
        WriteCredential(credential);
        var store = Store(new Clock(Started));
        Assert.Null(store.Problem);
        Assert.Equal(1, store.Count);
        var record = System.Text.Json.JsonSerializer.Deserialize<AgentCredential>(credential.ToJsonString(),
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web))!;
        Assert.Equal("script:machine1/pump-wake", store.PrincipalOf(record));
        Assert.Null(store.Check(token, Inside()).Accepted);
    }

    [Theory]
    [InlineData("scope", "none")]
    [InlineData("scope", "read")]
    [InlineData("scope", "read+reply+launch")]
    [InlineData("kind", "Script")]
    [InlineData("kind", "unknown")]
    [InlineData("kind", null)]
    [InlineData("principal", "agent:machine1/pump-wake")]
    [InlineData("principal", "script:machine2/pump-wake")]
    [InlineData("principal", "script:machine1/")]
    [InlineData("principal", "script:machine1/../pump-wake")]
    [InlineData("principal", "script:machine1/..")]
    [InlineData("principal", "script:machine1/pump/wake")]
    [InlineData("principal", "script:machine1/pump wake")]
    [InlineData("principal", "script:machine1/pump\nwake")]
    [InlineData("principal", null)]
    [InlineData("expiresAt", null)]
    [InlineData("issuedAt", null)]
    [InlineData("issuedAt", "0001-01-01T00:00:00+00:00")]
    [InlineData("session", "sess-1")]
    [InlineData("launchId", "launch-1")]
    [InlineData("rootPid", "100")]
    [InlineData("rootStartUtc", "2026-11-07T12:00:00+00:00")]
    [InlineData("depth", "1")]
    public void An_invalid_script_record_fails_the_whole_store_closed(string field, string? value)
    {
        var (token, credential) = ScriptRecord();
        if (value is null) { credential.Remove(field); }
        else if (field is "rootPid" or "depth") { credential[field] = int.Parse(value, System.Globalization.CultureInfo.InvariantCulture); }
        else { credential[field] = value; }
        WriteCredential(credential);
        var store = Store(new Clock(Started));
        Assert.NotNull(store.Problem);
        Assert.Equal(0, store.Count);
        Assert.Null(store.Check(token, Inside()).Accepted);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(31)]
    public void A_script_record_requires_a_positive_life_of_at_most_thirty_days(int days)
    {
        var (_, credential) = ScriptRecord();
        credential["expiresAt"] = Started.AddDays(days);
        WriteCredential(credential);
        Assert.NotNull(Store().Problem);
    }

    [Fact]
    public void A_legacy_session_record_keeps_its_binding_and_cannot_carry_a_script_principal()
    {
        var token = Store().Mint("sess-1", "launch-1", 100, Started, AgentProtocol.ScopeRead);
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(root, AgentCredentials.FileName)))!;
        var credential = saved["records"]![0]!["credential"]!.AsObject();
        credential.Remove("kind");
        credential.Remove("principal");
        File.WriteAllText(Path.Combine(root, AgentCredentials.FileName), saved.ToJsonString());
        var store = Store();
        Assert.Equal("agent:machine1/sess-1", store.PrincipalOf(store.Check(token, Inside()).Accepted!));
        credential["kind"] = "session";
        credential["principal"] = "script:machine1/pump-wake";
        File.WriteAllText(Path.Combine(root, AgentCredentials.FileName), saved.ToJsonString());
        Assert.NotNull(Store().Problem);
    }

    (string Token, JsonObject Credential) ScriptRecord()
    {
        var token = Store(new Clock(Started)).Mint("sess-1", "launch-1", 100, Started, AgentProtocol.ScopeRead);
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(root, AgentCredentials.FileName)))!;
        var credential = saved["records"]![0]!["credential"]!.AsObject();
        credential["kind"] = "script";
        credential["principal"] = "script:machine1/pump-wake";
        credential["session"] = "";
        credential["launchId"] = "";
        credential["rootPid"] = 0;
        credential["rootStartUtc"] = default(DateTimeOffset);
        credential["scope"] = AgentProtocol.ScopeReadReply;
        credential["issuedAt"] = Started;
        credential["expiresAt"] = Started.AddDays(30);
        return (token, credential);
    }

    void WriteCredential(JsonObject credential) =>
        File.WriteAllText(Path.Combine(root, AgentCredentials.FileName), credential.Root.ToJsonString());

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    sealed class FakeChannel : IProtocolChannel
    {
        public string PeerId => "fixture";
        public string PeerName => "fixture";
        public string PeerKind => "fixture";
        public bool IsOpen => true;
        public Task Closed => Task.Delay(Timeout.Infinite);
        public void Close() { }
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }
}
