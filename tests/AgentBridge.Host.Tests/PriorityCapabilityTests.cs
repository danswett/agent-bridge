using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Protocol;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class PriorityCapabilityTests
{
    static readonly DeliverySender Person = new("person", Owner: true);
    static readonly DeliverySender Agent = new("principal");
    static readonly GrantAudience Receiver = new(new string('a', 32), "profile");
    sealed class Fake : IDisposable
    {
        public readonly FakeTimeProvider Clock = new();
        public readonly List<byte[]> Disk = [];
        public readonly DeliveryJournal Journal;
        public bool FailWrite;
        public Fake(IEnumerable<DeliveryJournalRecord>? replay = null) =>
            Journal = new(new byte[32], b => { if (FailWrite) { throw new IOException("full"); } Disk.Add(b); }, _ => { }, replay);
        public PriorityCapability Cap(int hourly = 3) => new(Guid.NewGuid(), Agent.Id,
            new(Person.Id, Receiver.Device, "verified-person-proof", Clock.GetUtcNow(), new string('b', 64)),
            Receiver, Clock.GetUtcNow(), Clock.GetUtcNow().AddHours(2), hourly);
        public void Issue(PriorityCapability cap) => Journal.IssuePriority(cap, Person, Receiver, () => true, Clock.GetUtcNow());
        public PriorityDecision Decide(Guid? id = null, DeliverySender? sender = null, GrantAudience? receiver = null,
            DeliveryPriorityRequest? request = null, bool admit = true) =>
            Journal.DecidePriority(id ?? Guid.NewGuid(), "target", sender ?? Agent, "body",
                request ?? new("urgent", "stop wrong work"), receiver ?? Receiver, Clock.GetUtcNow(), _ => admit);
        public DeliveryJournalRecord[] Records => Disk.Select(b => JsonSerializer.Deserialize<DeliveryJournalRecord>(b, ProtocolJson.Options)!).ToArray();
        public void Dispose() => Journal.Dispose();
    }

    [Fact]
    public void Only_the_authenticated_current_person_can_issue_a_bounded_receiver_capability()
    {
        using var f = new Fake(); var cap = f.Cap();
        Assert.Throws<InvalidOperationException>(() => f.Journal.IssuePriority(cap, Agent, Receiver, () => true, f.Clock.GetUtcNow()));
        Assert.Throws<InvalidOperationException>(() => f.Journal.IssuePriority(cap, Person, Receiver with { Profile = "other" }, () => true, f.Clock.GetUtcNow()));
        Assert.Throws<InvalidOperationException>(() => f.Journal.IssuePriority(cap, Person, Receiver, () => false, f.Clock.GetUtcNow()));
        Assert.Throws<InvalidOperationException>(() => f.Issue(cap with { Hourly = 4 }));
        Assert.Throws<InvalidOperationException>(() => f.Issue(cap with { ExpiresAt = cap.NotBefore.AddDays(2) }));
        f.Issue(cap);
        Assert.Throws<InvalidOperationException>(() => f.Issue(cap));
    }

    [Fact]
    public void Urgent_reason_is_required_and_no_capability_never_grants_agent_priority()
    {
        using var f = new Fake();
        Assert.False(f.Decide(request: new("urgent", " ")).Accepted);
        Assert.False(f.Decide(request: new("invalid", "reason")).Accepted);
        Assert.Equal("normal", f.Decide().Priority);
        Assert.Contains(f.Records, r => r.Reason == "demoted");
        Assert.DoesNotContain(f.Records, r => r.Reason == "consumed");
        Assert.Equal("urgent", f.Decide(sender: Person).Priority);
    }

    [Fact]
    public void Person_grants_are_exact_principal_receiver_bound_and_never_renew_consumed_UUIDs()
    {
        using var f = new Fake(); var cap = f.Cap(); f.Issue(cap);
        Assert.Equal("normal", f.Decide(sender: new("other")).Priority);
        Assert.Equal("normal", f.Decide(receiver: Receiver with { Profile = "other" }).Priority);
        var id = Guid.NewGuid(); Assert.Equal("urgent", f.Decide(id).Priority);
        Assert.False(f.Decide(id).Accepted);
        Assert.True(f.Journal.PriorityCurrent(id, "target", Agent, Receiver, f.Clock.GetUtcNow()));
        Assert.False(f.Journal.PriorityCurrent(id, "other", Agent, Receiver, f.Clock.GetUtcNow()));
        Assert.False(f.Journal.PriorityCurrent(id, "target", new("other"), Receiver, f.Clock.GetUtcNow()));
        Assert.Single(f.Records, r => r.Reason == "consumed");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void Quota_spans_capabilities_and_survives_restart_until_the_rolling_hour(int hourly)
    {
        using var f = new Fake(); f.Issue(f.Cap(hourly));
        for (var i = 0; i < hourly; i++) { Assert.Equal("urgent", f.Decide().Priority); }
        f.Issue(f.Cap(hourly));
        using var replay = new Fake(f.Records);
        Assert.Equal("quota", replay.Decide().Reason);
        replay.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal("urgent", replay.Decide().Priority);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Expiry_and_person_revocation_survive_replay_and_remove_consumed_authority(bool revoke)
    {
        using var f = new Fake(); var cap = f.Cap(); f.Issue(cap); var id = Guid.NewGuid(); f.Decide(id);
        if (revoke)
        {
            Assert.Throws<InvalidOperationException>(() => f.Journal.RevokePriority(cap.Id, Agent, Receiver, () => true, f.Clock.GetUtcNow()));
            f.Journal.RevokePriority(cap.Id, Person, Receiver, () => true, f.Clock.GetUtcNow());
        }
        else { f.Clock.Advance(TimeSpan.FromHours(2)); }
        Assert.False(f.Journal.PriorityCurrent(id, "target", Agent, Receiver, f.Clock.GetUtcNow()));
        using var replay = new Fake(f.Records); replay.Clock.SetUtcNow(f.Clock.GetUtcNow());
        Assert.Equal("no-capability", replay.Decide().Reason);
        Assert.False(replay.Journal.PriorityCurrent(id, "target", Agent, Receiver, replay.Clock.GetUtcNow()));
    }

    [Fact]
    public void Separately_person_issued_receiver_allocations_have_separate_quota()
    {
        using var f = new Fake(); f.Issue(f.Cap());
        var other = Receiver with { Profile = "other" };
        f.Journal.IssuePriority(f.Cap() with { Audience = other }, Person, other, () => true, f.Clock.GetUtcNow());
        for (var i = 0; i < 3; i++) { Assert.Equal("urgent", f.Decide(receiver: other).Priority); }
        Assert.Equal("quota", f.Decide(receiver: other).Reason);
        Assert.Equal("urgent", f.Decide().Priority);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("urgent")]
    public void Pre_admission_refusal_burns_no_quota_and_the_same_UUID_can_retry(string priority)
    {
        using var f = new Fake(); f.Issue(f.Cap()); var id = Guid.NewGuid(); var request = new DeliveryPriorityRequest(priority, "reason");
        Assert.False(f.Decide(id, request: request, admit: false).Accepted);
        Assert.DoesNotContain(f.Records, r => r.Reason == "consumed");
        Assert.True(f.Decide(id, request: request).Accepted);
        Assert.Equal(priority == "urgent" ? 1 : 0, f.Records.Count(r => r.Reason == "consumed"));
    }

    [Fact]
    public void Broken_MAC_chain_or_failed_append_never_grants_and_clock_refusal_is_not_uncertain()
    {
        using var f = new Fake(); f.Issue(f.Cap()); f.Decide();
        Assert.Throws<InvalidDataException>(() => new Fake(f.Records.Where(r => r.Reason != "requested")));
        var issued = f.Records[0];
        Assert.Throws<InvalidDataException>(() => new Fake([issued with { Reason = "revoked" }]));
        f.Clock.Advance(TimeSpan.FromHours(2)); f.Decide();
        using var rollback = new Fake(f.Records);
        Assert.Throws<InvalidOperationException>(() => rollback.Decide());
        Assert.False(rollback.Journal.Uncertain);
        using var full = new Fake { FailWrite = true };
        Assert.Throws<IOException>(() => full.Issue(full.Cap()));
        Assert.True(full.Journal.Uncertain);
        Assert.Throws<InvalidOperationException>(() => full.Decide());
        Assert.Empty(full.Records);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("consumed")]
    public void Final_priority_evidence_cannot_be_hidden_in_the_legacy_replay_path(string action)
    {
        using var f = new Fake(); var cap = f.Cap(); f.Issue(cap); var id = Guid.NewGuid(); f.Decide(id);
        if (action == "revoked") { f.Journal.RevokePriority(cap.Id, Person, Receiver, () => true, f.Clock.GetUtcNow()); }
        if (action == "expired")
        { f.Clock.Advance(TimeSpan.FromHours(2)); Assert.False(f.Journal.PriorityCurrent(id, "target", Agent, Receiver, f.Clock.GetUtcNow())); }
        var records = f.Records; Assert.Equal(action, records[^1].Reason);
        Assert.Throws<InvalidDataException>(() => new Fake(records[..^1].Append(records[^1] with { State = "dropped" })));
    }

    [Theory]
    [InlineData("capability")]
    [InlineData("reason")]
    [InlineData("previous")]
    public void Any_priority_field_refuses_a_non_priority_state_even_when_other_fields_are_absent(string field)
    {
        using var f = new Fake(); f.Issue(f.Cap());
        var issued = Assert.Single(f.Records);
        var disguised = issued with { State = "enqueued", Capability = field == "capability" ? issued.Capability : null,
            PriorityReason = field == "reason" ? "reason" : null, PriorityPrevious = field == "previous" ? "" : null };
        Assert.Throws<InvalidDataException>(() => new Fake([disguised]));
    }
}
