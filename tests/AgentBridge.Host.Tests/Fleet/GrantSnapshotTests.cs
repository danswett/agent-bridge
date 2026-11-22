using AgentBridge.Host;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class GrantSnapshotTests : IDisposable
{
    readonly string root = Path.Combine(Environment.CurrentDirectory, "ab-grant-snapshot-" + Guid.NewGuid().ToString("N"));
    readonly P256KeyPair key = P256KeyPair.Generate();
    readonly FakeTimeProvider clock = new(new(2026, 11, 21, 18, 0, 0, TimeSpan.Zero));
    const string Device = "0123456789abcdef0123456789abcdef";
    static readonly GrantScope Scope = new([Device], ["repo"], ["copilot"], ["small"], ["default"], ["medium"], ["default"], ["ask"]);
    string Folder => Path.Combine(root, "default");
    AgentAuditLog Audit => new(Folder, clock) { DeviceKey = key };
    GrantStore Open(Action<string, byte[]>? write = null) => new(Folder, key, Audit, Scope, clock, write);
    GrantSubject Subject() => new("123", Device, Guid.NewGuid().ToString(), "AAAAAAAAAAAAAAAAAAAAAA", new(42, clock.GetUtcNow()));
    GrantDocument Issue(GrantCaps caps, bool twoCallers = false)
    {
        Assert.Null(AuditRotationTests.Rotate(Audit, key));
        var grant = new GrantDocument(1, Guid.NewGuid(), "123",
            new("123", Device, "AQ", clock.GetUtcNow(), new('a', 64)), new(Device, "default"),
            twoCallers ? [Subject(), Subject()] : [Subject()], ["launch"], Scope, caps,
            clock.GetUtcNow(), 1, clock.GetUtcNow().AddHours(24), [], []);
        Assert.True(Open().Issue(grant).Usable);
        return grant;
    }
    Dictionary<string, byte[]> Files() => Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
    void Unchanged(Dictionary<string, byte[]> before)
    {
        var after = Files();
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var file in before.Keys) { Assert.Equal(before[file], after[file]); }
    }
    public void Dispose()
    {
        key.Dispose();
        if (Directory.Exists(root)) { Directory.Delete(root, true); }
    }

    [Fact]
    public void Snapshot_projects_pending_and_live_usage_without_writes_or_advancing_the_receiver_watermark()
    {
        var grant = Issue(new(3, 10, 6));
        var counters = new GrantCounters(Open(), clock);
        var live = Guid.NewGuid();
        var pending = Guid.NewGuid();
        Assert.True(counters.Reserve(grant.Id, 1, live, grant.Subjects[0], new('b', 64)).Accepted);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(counters.Reserve(grant.Id, 1, pending, grant.Subjects[0], new('c', 64)).Accepted);
        var target = new StopTarget(Guid.NewGuid().ToString(), new(99, clock.GetUtcNow()));
        Assert.True(counters.Commit(live, target).Accepted);
        var watermark = clock.GetUtcNow();
        clock.Advance(TimeSpan.FromHours(1));
        var before = Files();
        var snapshot = Open((_, _) => throw new IOException("snapshot must not write")).Snapshot();
        Assert.Equal(GrantStoreProblem.None, snapshot.Problem);
        Assert.Equal(4L, snapshot.Generation);
        Assert.Equal(watermark, snapshot.ObservedAt);
        var entry = Assert.Single(snapshot.Grants);
        Assert.Equal(grant.Id, entry.Grant.Id);
        Assert.Equal(grant.Generation, entry.Grant.Generation);
        Assert.Equal(grant.Issuer.Digest, entry.Grant.Issuer.Digest);
        Assert.Equal(grant.Audience, entry.Grant.Audience);
        Assert.Equal(new GrantUsage(0, 2, 2), entry.Usage);
        Assert.Equal(pending, Assert.Single(entry.PendingRequestIds));
        Assert.Equal(target.Session, Assert.Single(entry.LiveSessionIds));
        Assert.Equal(2, entry.RequestCount);
        Assert.Equal(live, entry.LastOperation!.Request);
        Assert.Equal(grant.Subjects[0].Session, entry.LastOperation.Caller);
        Assert.Equal(GrantCounterProblem.None, entry.LastOperation.Outcome);
        Unchanged(before);
    }

    [Fact]
    public void Snapshot_reports_the_actual_later_refused_caller_and_request_after_restart_not_the_last_admission()
    {
        var grant = Issue(new(1, 3, 2), twoCallers: true);
        var counters = new GrantCounters(Open(), clock);
        var admitted = Guid.NewGuid();
        var refused = Guid.NewGuid();
        Assert.True(counters.Reserve(grant.Id, 1, admitted, grant.Subjects[0], new('b', 64)).Accepted);
        Assert.Equal(GrantCounterProblem.Hourly, counters.Reserve(grant.Id, 1, refused, grant.Subjects[1], new('c', 64)).Problem);
        var before = Files();
        var snapshot = Open().Snapshot();
        Assert.Equal(GrantStoreProblem.None, snapshot.Problem);
        Assert.Equal(3L, snapshot.Generation);
        var entry = Assert.Single(snapshot.Grants);
        Assert.Equal(new GrantUsage(1, 1, 1), entry.Usage);
        Assert.Equal(admitted, Assert.Single(entry.PendingRequestIds));
        Assert.Empty(entry.LiveSessionIds);
        Assert.Equal(2, entry.RequestCount);
        Assert.Equal(refused, entry.LastOperation!.Request);
        Assert.Equal(grant.Subjects[1].Session, entry.LastOperation.Caller);
        Assert.Equal(GrantCounterProblem.Hourly, entry.LastOperation.Outcome);
        Unchanged(before);
    }

    [Fact]
    public void Snapshot_records_the_latest_malformed_refusal_with_no_caller_or_raw_text_in_audit()
    {
        var grant = Issue(new(3, 10, 6));
        var counters = new GrantCounters(Open(), clock);
        Assert.True(counters.Reserve(grant.Id, 1, Guid.NewGuid(), grant.Subjects[0], new('b', 64)).Accepted);
        const string rawCaller = "untrusted-session-prompt-marker-not-an-identifier";
        var refused = Guid.NewGuid();
        Assert.Equal(GrantCounterProblem.Malformed, counters.Reserve(grant.Id, 1, refused,
            grant.Subjects[0] with { Session = rawCaller }, new('c', 64)).Problem);
        foreach (var bytes in Files().Values)
        { Assert.DoesNotContain(rawCaller, System.Text.Encoding.UTF8.GetString(bytes)); }
        var snapshot = Open().Snapshot();
        Assert.Equal(GrantStoreProblem.None, snapshot.Problem);
        var entry = Assert.Single(snapshot.Grants);
        Assert.NotNull(entry.LastOperation);
        Assert.Equal(refused, entry.LastOperation.Request);
        Assert.Null(entry.LastOperation.Caller);
        Assert.Equal(GrantCounterProblem.Malformed, entry.LastOperation.Outcome);
        Assert.Equal(2, entry.RequestCount);
        Assert.Equal(new GrantUsage(1, 1, 1), entry.Usage);
    }

    [Fact]
    public void Snapshot_keeps_rotation_required_and_never_creates_state_or_repairs_legacy_audit()
    {
        Assert.True(Audit.Append(new("out", "fixture", "agent:fixture/session", [], "agent.send", "fixture", "sent")));
        var before = Files();
        var snapshot = Open().Snapshot();
        Assert.Equal(GrantStoreProblem.RotationRequired, snapshot.Problem);
        Assert.Empty(snapshot.Grants);
        Assert.False(File.Exists(Path.Combine(Folder, GrantStore.FileName)));
        Unchanged(before);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Snapshot_refuses_damaged_evidence_without_repair_or_latching_an_authority_failure(bool lostHead)
    {
        var grant = Issue(new(1, 3, 2));
        var path = Path.Combine(Folder, lostHead ? Path.Combine("audit", "agents.head") : GrantStore.FileName);
        var good = File.ReadAllBytes(path);
        if (lostHead) { File.Delete(path); }
        else { var corrupt = good.ToArray(); corrupt[^1] ^= 1; File.WriteAllBytes(path, corrupt); }
        var before = Files();
        var store = Open();
        Assert.Equal(lostHead ? GrantStoreProblem.AuditIncomplete : GrantStoreProblem.BadMac, store.Snapshot().Problem);
        Unchanged(before);
        File.WriteAllBytes(path, good);
        Assert.Equal(GrantStoreProblem.None, store.Snapshot().Problem);
        Assert.True(store.TryGetUsable(grant.Id, grant.Generation).Usable);
    }
}
