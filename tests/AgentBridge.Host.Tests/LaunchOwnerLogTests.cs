using AgentBridge.Transport;
using Xunit;

namespace AgentBridge.Host.Tests;

// Session watch S1b (docs/design/session-watch.md, #4262): the durable owner record the host writes at launch from the
// AUTHENTICATED principal, so R2 and R3 can still name the launching agent after the 15 minute launch ledger has forgotten it.
public sealed class LaunchOwnerLogTests : IDisposable
{
    const string Session = "22222222-2222-4222-8222-222222222222";
    const string Agent = "agent:0123456789abcdef0123456789abcdef/agent_bridge_1111111111111111";
    static readonly DateTimeOffset T = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);
    readonly string root = Path.Combine(Path.GetTempPath(), "launch-owner-" + Guid.NewGuid().ToString("N"));
    readonly P256KeyPair key = P256KeyPair.Generate();
    readonly List<string> log = [];
    public LaunchOwnerLogTests() => Directory.CreateDirectory(root);
    public void Dispose() { key.Dispose(); Directory.Delete(root, true); }
    LaunchOwnerLog Open(P256KeyPair? k = null) => new(root, k ?? key, log.Add);

    [Fact]
    public void A_launching_agent_is_still_the_owner_after_a_host_restart()
    {
        Open().Record(Session, Agent, T);
        Assert.Equal(Agent, Open().OwnerOf(Session));
    }

    [Theory]
    [InlineData("phone")]
    [InlineData("script:0123456789abcdef0123456789abcdef/watch")]
    [InlineData("agent:not-a-device/agent_bridge_1111111111111111")]
    [InlineData("agent:0123456789abcdef0123456789abcdef/")]
    public void A_person_a_script_or_a_malformed_launcher_leaves_no_agent_owner(string principal)
    {
        var owners = Open();
        owners.Record(Session, principal, T);
        Assert.Null(owners.OwnerOf(Session));
        Assert.Null(Open().OwnerOf(Session));
    }

    [Fact]
    public void An_edited_file_or_a_file_from_another_host_key_means_no_owner()
    {
        Open().Record(Session, Agent, T);
        using (var other = P256KeyPair.Generate()) { Assert.Null(Open(other).OwnerOf(Session)); }
        var file = Directory.GetFiles(root).Single();
        var bytes = File.ReadAllBytes(file);
        bytes[^3] ^= 1;
        File.WriteAllBytes(file, bytes);
        Assert.Null(Open().OwnerOf(Session));
        Assert.Contains(log, l => l.Contains("owner record could not be verified"));
    }

    [Fact]
    public void A_record_is_removed_seven_days_after_its_session_was_last_live_and_kept_while_it_is_live()
    {
        var owners = Open();
        owners.Record(Session, Agent, T);
        owners.Prune(_ => true, T.AddDays(6));
        owners.Prune(_ => false, T.AddDays(12));
        Assert.Equal(Agent, owners.OwnerOf(Session));
        owners.Prune(_ => false, T.AddDays(14));
        Assert.Null(owners.OwnerOf(Session));
        Assert.Null(Open().OwnerOf(Session));
    }

    [Fact]
    public void A_session_that_is_not_recorded_has_no_owner() => Assert.Null(Open().OwnerOf(Session));
}
