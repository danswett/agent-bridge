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
