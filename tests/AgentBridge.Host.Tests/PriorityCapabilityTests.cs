using System.Text.Json;
using System.Text.Json.Nodes;
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
            DeliveryPriorityRequest? request = null, bool admit = true, string body = "body", DateTimeOffset? now = null) =>
            Journal.DecidePriority(id ?? Guid.NewGuid(), "target", sender ?? Agent, body,
                request ?? new("urgent", "stop wrong work"), receiver ?? Receiver, now ?? Clock.GetUtcNow(), _ => admit);
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
        using var broken = new Fake(f.Records.Where(r => r.Reason != "requested"));
        Assert.Equal("normal", broken.Decide().Priority);
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
    [InlineData("revoked", false)]
    [InlineData("expired", false)]
    [InlineData("consumed", false)]
    [InlineData("revoked", true)]
    [InlineData("expired", true)]
    [InlineData("consumed", true)]
    public void Final_priority_evidence_cannot_be_hidden_in_the_legacy_replay_path(string action, bool stripped)
    {
        using var f = new Fake(); var cap = f.Cap(); f.Issue(cap); var id = Guid.NewGuid(); f.Decide(id);
        if (action == "revoked") { f.Journal.RevokePriority(cap.Id, Person, Receiver, () => true, f.Clock.GetUtcNow()); }
        if (action == "expired")
        { f.Clock.Advance(TimeSpan.FromHours(2)); Assert.False(f.Journal.PriorityCurrent(id, "target", Agent, Receiver, f.Clock.GetUtcNow())); }
        var records = f.Records; Assert.Equal(action, records[^1].Reason);
        var disguised = records[^1] with { State = "dropped" };
        if (stripped) { disguised = disguised with { Capability = null, PriorityReason = null, PriorityPrevious = null }; }
        using var replay = new Fake(records[..^1].Append(disguised));
        replay.Clock.SetUtcNow(action == "expired" ? cap.NotBefore.AddHours(1) : f.Clock.GetUtcNow());
        Assert.Equal("normal", replay.Decide().Priority);
        Assert.False(replay.Journal.PriorityCurrent(id, "target", Agent, Receiver, replay.Clock.GetUtcNow()));
        Assert.Throws<InvalidOperationException>(() => replay.Issue(replay.Cap()));
    }

    [Fact]
    public void A_final_revoke_with_a_missing_MAC_quarantines_instead_of_throwing()
    {
        using var f = new Fake(); var cap = f.Cap(); f.Issue(cap); var id = Guid.NewGuid(); f.Decide(id);
        f.Journal.RevokePriority(cap.Id, Person, Receiver, () => true, f.Clock.GetUtcNow());
        var records = f.Records;
        using var replay = new Fake(records[..^1].Append(records[^1] with { Hmac = null! }));
        Assert.Equal("normal", replay.Decide().Priority);
        Assert.False(replay.Journal.PriorityCurrent(id, "target", Agent, Receiver, replay.Clock.GetUtcNow()));
        Assert.Throws<InvalidOperationException>(() => replay.Issue(replay.Cap()));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_MAC_valid_zero_body_dropped_record_sharing_priority_evidence_stays_unconfirmed(bool capabilityId)
    {
        using var f = new Fake(); var cap = f.Cap(); f.Issue(cap); var request = Guid.NewGuid(); f.Decide(request);
        var header = new DeliveryHeader(Agent.From, (capabilityId ? cap.Id : request).ToString("D"), null, f.Clock.GetUtcNow(), "normal", 0);
        f.Journal.Write("legacy", header, "", DeliveryState.Dropped);
        using var replay = new Fake(f.Records);
        Assert.Equal(DeliveryState.Unconfirmed, replay.Journal.State("legacy", header.Id));
        Assert.Equal("normal", replay.Decide().Priority);
        Assert.False(replay.Journal.PriorityCurrent(request, "target", Agent, Receiver, replay.Clock.GetUtcNow()));
    }

    [Fact]
    public void A_refused_retry_must_keep_the_canonical_body_and_exact_receiver_and_an_unchanged_retry_spends_once()
    {
        using var f = new Fake(); f.Issue(f.Cap());
        var other = Receiver with { Profile = "other" };
        f.Journal.IssuePriority(f.Cap() with { Audience = other }, Person, other, () => true, f.Clock.GetUtcNow());
        var id = Guid.NewGuid(); Assert.False(f.Decide(id, admit: false).Accepted);
        Assert.False(f.Decide(id, body: "different body").Accepted);
        Assert.False(f.Decide(id, receiver: other).Accepted);
        Assert.DoesNotContain(f.Records, r => r.Reason == "consumed");
        using var replay = new Fake(f.Records);
        Assert.False(replay.Decide(id, body: "different body").Accepted);
        Assert.False(replay.Decide(id, receiver: other).Accepted);
        Assert.True(replay.Decide(id, body: " body ").Accepted);
        Assert.Single(replay.Records, r => r.Reason == "consumed");
        Assert.Equal(1, replay.Records.Count(r => r.Reason == "granted" && r.Header.Id == id.ToString("D")));
    }

    [Fact]
    public void A_successful_current_check_durably_advances_the_clock_so_a_rolled_back_clock_is_refused_after_replay()
    {
        using var f = new Fake(); f.Issue(f.Cap()); var id = Guid.NewGuid(); f.Decide(id);
        var start = f.Clock.GetUtcNow(); f.Clock.Advance(TimeSpan.FromHours(1));
        Assert.True(f.Journal.PriorityCurrent(id, "target", Agent, Receiver, f.Clock.GetUtcNow()));
        Assert.Throws<InvalidOperationException>(() => f.Decide(now: start));
        using var replay = new Fake(f.Records);
        Assert.Equal(start, replay.Clock.GetUtcNow());
        Assert.Throws<InvalidOperationException>(() => replay.Decide());
        Assert.False(replay.Journal.PriorityCurrent(id, "target", Agent, Receiver, start));
        replay.Clock.Advance(TimeSpan.FromHours(1));
        Assert.True(replay.Journal.PriorityCurrent(id, "target", Agent, Receiver, replay.Clock.GetUtcNow()));
        Assert.Contains(f.Records, r => r.Reason == "checked");
    }

    [Fact]
    public void Successful_current_checks_coalesce_to_one_durable_append_per_second_per_receiver()
    {
        using var f = new Fake(); f.Issue(f.Cap()); var id = Guid.NewGuid(); f.Decide(id);
        int Checked() => f.Records.Count(r => r.Reason == "checked");
        foreach (var step in new[] { 100, 100, 700 })
        {
            f.Clock.Advance(TimeSpan.FromMilliseconds(step));
            Assert.True(f.Journal.PriorityCurrent(id, "target", Agent, Receiver, f.Clock.GetUtcNow()));
            Assert.Equal(1, Checked());
        }
        f.Clock.Advance(TimeSpan.FromMilliseconds(200));
        Assert.True(f.Journal.PriorityCurrent(id, "target", Agent, Receiver, f.Clock.GetUtcNow()));
        Assert.Equal(2, Checked());
        using var replay = new Fake(f.Records); replay.Clock.Advance(TimeSpan.FromMilliseconds(1200));
        Assert.True(replay.Journal.PriorityCurrent(id, "target", Agent, Receiver, replay.Clock.GetUtcNow()));
        Assert.DoesNotContain(replay.Records, r => r.Reason == "checked");
        replay.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True(replay.Journal.PriorityCurrent(id, "target", Agent, Receiver, replay.Clock.GetUtcNow()));
        Assert.Single(replay.Records, r => r.Reason == "checked");
    }

    [Fact]
    public void Requested_priority_and_the_accepted_effective_priority_are_stored_in_the_record_headers()
    {
        using var f = new Fake(); f.Issue(f.Cap());
        var urgent = Guid.NewGuid(); f.Decide(urgent);
        var fyi = Guid.NewGuid(); f.Decide(fyi, request: new("fyi"));
        var invalid = Guid.NewGuid(); f.Decide(invalid, request: new("bogus"));
        var demoted = Guid.NewGuid(); f.Decide(demoted, sender: new("stranger"));
        string[] Priorities(Guid id, string action) => f.Records.Where(r => r.Header.Id == id.ToString("D") && r.Reason == action)
            .Select(r => r.Header.Priority).ToArray();
        Assert.Equal(["urgent"], Priorities(urgent, "requested"));
        Assert.Equal(["urgent"], Priorities(urgent, "granted"));
        Assert.Equal(["urgent"], Priorities(urgent, "consumed"));
        Assert.Equal(["fyi"], Priorities(fyi, "requested"));
        Assert.Equal(["normal"], Priorities(invalid, "requested"));
        Assert.Equal(["urgent"], Priorities(demoted, "requested"));
        Assert.Equal(["normal"], Priorities(demoted, "demoted"));
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

    [Fact]
    public void Global_priority_quarantine_keeps_unrelated_legacy_handoff_on_restart()
    {
        using var f = new Fake(); var cap = f.Cap(); f.Issue(cap); f.Decide();
        f.Journal.RevokePriority(cap.Id, Person, Receiver, () => true, f.Clock.GetUtcNow());
        var other = Receiver with { Profile = "other" };
        f.Journal.IssuePriority(f.Cap() with { Audience = other }, Person, other, () => true, f.Clock.GetUtcNow());
        var otherId = Guid.NewGuid(); Assert.Equal("urgent", f.Decide(otherId, receiver: other).Priority);
        var header = DeliveryEnvelope.Prepare(new(Agent.From, Guid.NewGuid().ToString("D"), null,
            f.Clock.GetUtcNow(), "normal", 0), "legacy body").Header;
        f.Journal.Write("legacy", header, "legacy body", DeliveryState.Enqueued);
        var handoff = f.Journal.ReserveHandoff("legacy", header, "legacy body", 'q');
        var records = f.Records.Select(r => r.Reason == "revoked" ? r with
            { State = "dropped", Capability = null, PriorityReason = null, PriorityPrevious = null } : r).ToArray();
        using var replay = new Fake(records);
        Assert.Equal(DeliveryState.Unconfirmed, replay.Journal.State("legacy", handoff.Id));
        Assert.True(replay.Journal.VerifyHandoff("legacy", handoff, "legacy body"));
        Assert.Equal("normal", replay.Decide().Priority);
        Assert.False(replay.Journal.PriorityCurrent(otherId, "target", Agent, other, replay.Clock.GetUtcNow()));
        using var again = new Fake(records.Concat(replay.Records));
        Assert.Equal("normal", again.Decide().Priority);
        Assert.Throws<InvalidOperationException>(() => again.Issue(again.Cap()));
    }

    [Fact]
    public void A_body_record_that_never_reached_handoff_still_quarantines_priority_bindings()
    {
        using var f = new Fake(); f.Issue(f.Cap()); f.Decide();
        var header = DeliveryEnvelope.Prepare(new(Agent.From, Guid.NewGuid().ToString("D"), null, f.Clock.GetUtcNow(), "normal", 0), "body").Header;
        f.Journal.Write("legacy", header, "body", DeliveryState.Enqueued);
        using var replay = new Fake(f.Records);
        Assert.Equal("normal", replay.Decide().Priority);
        Assert.Throws<InvalidOperationException>(() => replay.Issue(replay.Cap()));
    }

    [Fact]
    public void A_handed_off_body_record_reopens_as_unconfirmed()
    {
        using var f = new Fake();
        var header = DeliveryEnvelope.Prepare(new(Agent.From, Guid.NewGuid().ToString("D"), null, f.Clock.GetUtcNow(), "normal", 0), "body").Header;
        f.Journal.Write("legacy", header, "body", DeliveryState.Enqueued);
        var handoff = f.Journal.ReserveHandoff("legacy", header, "body", 'q');
        using var replay = new Fake(f.Records);
        Assert.Equal(DeliveryState.Unconfirmed, replay.Journal.State("legacy", handoff.Id));
    }

    [Fact]
    public void Linked_legacy_handoff_is_unconfirmed_but_refuses_capability_authority()
    {
        using var f = new Fake(); f.Issue(f.Cap()); var id = Guid.NewGuid(); f.Decide(id);
        var header = DeliveryEnvelope.Prepare(new(Agent.From, id.ToString("D"), null, f.Clock.GetUtcNow(), "urgent", 0), "body").Header;
        f.Journal.Write("target", header, "body", DeliveryState.Enqueued);
        var handoff = f.Journal.ReserveHandoff("target", header, "body", 'u');
        using var replay = new Fake(f.Records);
        Assert.Equal(DeliveryState.Unconfirmed, replay.Journal.State("target", handoff.Id));
        Assert.True(replay.Journal.VerifyHandoff("target", handoff, "body"));
        Assert.False(replay.Journal.PriorityCurrent(id, "target", Agent, Receiver, replay.Clock.GetUtcNow()));
        Assert.Equal("normal", replay.Decide().Priority);
    }

    [Fact]
    public void Empty_legacy_requires_its_MAC_and_nonempty_dropped_history_is_only_unconfirmed()
    {
        using var f = new Fake();
        var header = new DeliveryHeader(Agent.From, Guid.NewGuid().ToString("D"), null, f.Clock.GetUtcNow(), "normal", 0);
        f.Journal.Write("legacy", header, "", DeliveryState.Dropped);
        using var valid = new Fake(f.Records);
        Assert.Equal(DeliveryState.Dropped, valid.Journal.State("legacy", header.Id));
        Assert.Throws<InvalidDataException>(() => new Fake(f.Records.Select(r => r with { Reason = "spoof" })));
        f.Journal.Write("legacy", header, "body", DeliveryState.Dropped);
        using var replay = new Fake(f.Records);
        Assert.Equal(DeliveryState.Unconfirmed, replay.Journal.State("legacy", header.Id));
        Assert.Equal("normal", replay.Decide().Priority);
    }

    [Theory]
    [InlineData("revoked")]
    [InlineData("expired")]
    [InlineData("consumed")]
    public void Every_strip_subset_and_legacy_spoof_is_inert_or_monotonically_spent(string action)
    {
        using var f = new Fake(); var cap = f.Cap(1); f.Issue(cap); var id = Guid.NewGuid();
        var other = Receiver with { Profile = "other" };
        var b = f.Cap() with { Principal = "other", Audience = other, ExpiresAt = cap.ExpiresAt.AddHours(1) };
        f.Journal.IssuePriority(b, Person, other, () => true, f.Clock.GetUtcNow());
        f.Decide(id);
        if (action == "revoked") { f.Journal.RevokePriority(cap.Id, Person, Receiver, () => true, f.Clock.GetUtcNow()); }
        if (action == "expired") { f.Clock.Advance(TimeSpan.FromHours(2)); f.Journal.PriorityCurrent(id, "target", Agent, Receiver, f.Clock.GetUtcNow()); }
        var records = f.Records; Assert.Equal(action, records[^1].Reason);
        var bRequest = Guid.NewGuid(); f.Decide(bRequest, new("other"), other);
        var suffix = f.Records.Skip(records.Length).ToArray();
        for (var location = 0; location < (action == "consumed" ? 1 : 3); location++)
        for (var changedId = 1; changedId >= 0; changedId--)
        for (var withSuffix = 0; withSuffix < 2; withSuffix++)
        for (var mask = 0; mask < 64; mask++)
        foreach (var spoof in new[] { 1, 0, 2, 3 })
        {
            var node = JsonSerializer.SerializeToNode(records[^1], ProtocolJson.Options)!.AsObject();
            var header = node["header"]!.AsObject();
            if (spoof != 0)
            {
                node["state"] = spoof == 3 ? "handoff" : "dropped";
                node["capability"] = null; node["priorityReason"] = null; node["priorityPrevious"] = null; node["reason"] = null;
                header["seq"] = spoof == 2 ? null : JsonValue.Create("q1"); header["n"] = 1;
            }
            if ((mask & 1) != 0) { node.Remove("capability"); }
            if ((mask & 2) != 0) { node.Remove("reason"); node.Remove("priorityReason"); }
            if ((mask & 4) != 0) { node.Remove("priorityPrevious"); }
            if ((mask & 8) != 0) { header.Remove("seq"); }
            if ((mask & 16) != 0) { header.Remove("n"); }
            if ((mask & 32) != 0) { node.Remove("state"); }
            if (changedId != 0) { header["id"] = Guid.NewGuid().ToString("D"); }
            var candidate = node.Deserialize<DeliveryJournalRecord>(ProtocolJson.Options)!;
            var prefix = records[..^1];
            var ordered = location == 0 ? prefix.Append(candidate) : location == 1 ? prefix.Prepend(candidate) :
                prefix.Take(1).Append(candidate).Concat(prefix.Skip(1));
            using var replay = new Fake(ordered.Concat(withSuffix == 0 ? [] : suffix));
            replay.Clock.SetUtcNow(action == "expired" && withSuffix == 0 && (spoof != 0 || changedId != 0 || (mask & 39) != 0)
                ? cap.NotBefore.AddHours(1) : f.Clock.GetUtcNow());
            Assert.NotEqual("urgent", replay.Decide().Priority);
            if (candidate != records[^1])
            {
                Assert.False(replay.Journal.PriorityCurrent(id, "target", Agent, Receiver, replay.Clock.GetUtcNow()));
                Assert.NotEqual("urgent", replay.Decide(sender: new("other"), receiver: other).Priority);
                Assert.False(replay.Journal.PriorityCurrent(bRequest, "target", new("other"), other, replay.Clock.GetUtcNow()));
                Assert.Equal(DeliveryState.Unconfirmed, replay.Journal.State(candidate.Session, candidate.Header.Id));
            }
        }
        if (action == "consumed")
        {
            using var replay = new Fake(records[..^1]);
            Assert.Equal("quota", replay.Decide().Reason);
            using var identity = new Fake(records.Select(r => r.Reason == "granted" ? r with
                { Capability = b, Header = r.Header with { Id = Guid.NewGuid().ToString("D") } } : r).Concat(suffix));
            Assert.Equal("normal", identity.Decide().Priority);
            Assert.Throws<InvalidOperationException>(() => identity.Issue(identity.Cap() with { Principal = "fresh" }));
            Assert.Equal("normal", identity.Decide(request: new("normal", "reason")).Priority);
            Assert.Equal("urgent", identity.Decide(sender: Person).Priority);
            foreach (var priority in new[] { "normal", "fyi", "urgent" })
            {
                using var ordinary = new Fake(); var uuid = Guid.NewGuid(); var sender = priority == "urgent" ? Person : Agent;
                ordinary.Decide(uuid, sender, request: new(priority, "reason"));
                var h = new DeliveryHeader(sender.From, uuid.ToString("D"), null, ordinary.Clock.GetUtcNow(), priority, 0);
                ordinary.Journal.Write("target", h, "body", DeliveryState.Enqueued);
                using var reopened = new Fake(ordinary.Records);
                Assert.Equal(DeliveryState.Unconfirmed, reopened.Journal.State("target", h.Id));
                var handoff = ordinary.Journal.ReserveHandoff("target", h, "body", priority == "urgent" ? 'u' : 'q');
                using var handed = new Fake(ordinary.Records);
                Assert.Equal(DeliveryState.Unconfirmed, handed.Journal.State("target", h.Id));
                Assert.True(handed.Journal.VerifyHandoff("target", handoff, "body"));
            }
        }
        if (action == "expired")
        {
            using var before = new Fake(); var h = new DeliveryHeader(Agent.From, Guid.NewGuid().ToString("D"), null,
                before.Clock.GetUtcNow(), "normal", 0);
            before.Journal.Write("legacy", h, "body", DeliveryState.Enqueued);
            before.Issue(before.Cap());
            using var reopened = new Fake(before.Records);
            Assert.Equal(DeliveryState.Unconfirmed, reopened.Journal.State("legacy", h.Id));
            Assert.Equal("normal", reopened.Decide().Priority);
            Assert.Throws<InvalidOperationException>(() => reopened.Issue(reopened.Cap() with { Principal = "fresh" }));
        }
    }
}
