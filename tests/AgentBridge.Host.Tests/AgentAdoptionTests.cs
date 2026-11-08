using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class AgentAdoptionTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abadopt-" + Guid.NewGuid().ToString("N")[..8]);
    readonly List<string> lines = [];
    static readonly DateTimeOffset Started = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    public AgentAdoptionTests() => Directory.CreateDirectory(root);

    public void Dispose() { try { Directory.Delete(root, true); } catch (IOException) { } }

    AgentCredentials Store(TimeProvider? time = null) => new(root, "machine1", time, lines.Add, (_, _) => true);

    static ProcessAncestry Inside(int rootPid = 100, DateTimeOffset? started = null, bool complete = true) =>
        new([new PeerProcess(300, Started.AddMinutes(5), "cli.exe"), new PeerProcess(rootPid, started ?? Started, "agent.exe")], complete);

    [Fact]
    public void Nothing_is_issued_until_a_person_adopts_the_session()
    {
        var store = Store();
        var claim = store.Claim(Inside());
        Assert.Null(claim.Token);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void An_adopted_session_gets_a_working_credential_when_it_asks_from_inside_itself()
    {
        var store = Store();
        store.Adopt("sess-1", 100, Started, AgentProtocol.ScopeRead);
        var claim = store.Claim(Inside());
        Assert.NotNull(claim.Token);
        Assert.NotNull(store.Check(claim.Token, Inside()).Accepted);
        Assert.Equal("sess-1", claim.Credential!.Session);
    }

    [Fact]
    public void A_claim_works_once_and_a_second_ask_gets_nothing()
    {
        var store = Store();
        store.Adopt("sess-1", 100, Started, AgentProtocol.ScopeRead);
        Assert.NotNull(store.Claim(Inside()).Token);
        Assert.Null(store.Claim(Inside()).Token);
    }

    [Fact]
    public void An_adoption_lapses_after_ten_minutes()
    {
        var time = new Clock(Started);
        var store = Store(time);
        store.Adopt("sess-1", 100, Started, AgentProtocol.ScopeRead);
        time.Now = Started.AddMinutes(AgentProtocol.AdoptionMinutes + 1);
        Assert.Null(store.Claim(Inside()).Token);
    }

    [Fact]
    public void A_partial_ancestry_an_outside_caller_and_a_recycled_pid_are_all_refused()
    {
        var store = Store();
        store.Adopt("sess-1", 100, Started, AgentProtocol.ScopeRead);
        Assert.Null(store.Claim(Inside(complete: false)).Token);
        Assert.Null(store.Claim(new ProcessAncestry([new PeerProcess(300, Started, "other.exe")], true)).Token);
        Assert.Null(store.Claim(Inside(started: Started.AddHours(3))).Token);
        Assert.Null(store.Claim(ProcessAncestry.Unknown).Token);
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void A_session_id_in_the_environment_does_not_count_so_only_the_process_tree_decides()
    {
        var store = Store();
        store.Adopt("sess-1", 100, Started, AgentProtocol.ScopeRead);
        Environment.SetEnvironmentVariable("AGENT_BRIDGE_SESSION", "sess-1");
        try { Assert.Null(store.Claim(new ProcessAncestry([new PeerProcess(300, Started, "other.exe")], true)).Token); }
        finally { Environment.SetEnvironmentVariable("AGENT_BRIDGE_SESSION", null); }
    }

    [Fact]
    public void A_claim_covered_by_two_adoptions_is_refused()
    {
        var store = Store();
        store.Adopt("sess-1", 100, Started, AgentProtocol.ScopeRead);
        store.Adopt("sess-2", 100, Started, AgentProtocol.ScopeRead);
        Assert.Null(store.Claim(Inside()).Token);
    }

    [Fact]
    public void Adopting_again_replaces_the_first_adoption_instead_of_adding_a_second()
    {
        var store = Store();
        store.Adopt("sess-1", 100, Started, AgentProtocol.ScopeRead);
        store.Adopt("sess-1", 100, Started, AgentProtocol.ScopeReadReply);
        Assert.Equal(AgentProtocol.ScopeReadReply, store.Claim(Inside()).Credential!.Scope);
    }

    [Fact]
    public void Scopes_beyond_read_and_reply_cannot_be_adopted()
    {
        var store = Store();
        Assert.Throws<ArgumentException>(() => store.Adopt("sess-1", 100, Started, AgentProtocol.ScopeReadReplyLaunch));
        Assert.Throws<ArgumentException>(() => store.Adopt("sess-1", 100, Started, "admin"));
    }

    [Fact]
    public void A_session_the_host_launched_cannot_be_adopted()
    {
        var store = Store();
        store.Mint("sess-1", "launch-1", 100, Started, AgentProtocol.ScopeRead);
        Assert.Throws<InvalidOperationException>(() => store.Adopt("sess-1", 100, Started, AgentProtocol.ScopeRead));
    }

    [Fact]
    public void An_unreadable_store_refuses_adoption_and_claims()
    {
        File.WriteAllText(Path.Combine(root, AgentCredentials.FileName), "{ not json");
        var store = Store();
        Assert.ThrowsAny<Exception>(() => store.Adopt("sess-1", 100, Started, AgentProtocol.ScopeRead));
        Assert.Null(store.Claim(Inside()).Token);
    }

    [Fact]
    public void Revoking_kills_the_credential_and_any_adoption_still_waiting()
    {
        var store = Store();
        store.Adopt("sess-1", 100, Started, AgentProtocol.ScopeRead);
        var token = store.Claim(Inside()).Token!;
        store.Adopt("sess-1", 100, Started, AgentProtocol.ScopeRead);
        Assert.True(store.Revoke("sess-1", "test"));
        Assert.Null(store.Check(token, Inside()).Accepted);
        Assert.Null(store.Claim(Inside()).Token);
    }

    [Fact]
    public void Adopting_again_after_a_resume_gives_a_new_credential_and_the_old_one_is_dead()
    {
        var store = Store();
        store.Adopt("sess-1", 100, Started, AgentProtocol.ScopeRead);
        var old = store.Claim(Inside()).Token!;
        store.Adopt("sess-1", 200, Started, AgentProtocol.ScopeRead);
        var fresh = store.Claim(Inside(200)).Token!;
        Assert.Null(store.Check(old, Inside()).Accepted);
        Assert.NotNull(store.Check(fresh, Inside(200)).Accepted);
    }

    [Fact]
    public void No_token_ever_reaches_the_log()
    {
        var store = Store();
        store.Adopt("sess-1", 100, Started, AgentProtocol.ScopeRead);
        var token = store.Claim(Inside()).Token!;
        store.Revoke("sess-1", "test");
        Assert.NotEmpty(lines);
        Assert.DoesNotContain(lines, l => l.Contains(token) || l.Contains(AgentProtocol.TokenPrefix));
    }

    [Fact]
    public void Adopting_again_ends_the_earlier_credential_and_its_open_connection_at_once()
    {
        var store = Store();
        store.Adopt("sess-1", 100, Started, AgentProtocol.ScopeReadReply);
        var claim = store.Claim(Inside());
        var channel = new FakeChannel();
        store.Accept(channel, claim.Credential!);
        Assert.NotNull(store.Of(channel));

        store.Adopt("sess-1", 100, Started, AgentProtocol.ScopeRead);

        Assert.Null(store.Check(claim.Token, Inside()).Accepted);
        Assert.Null(store.Of(channel));
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

    sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
