using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Launch;
using Xunit;

public sealed class FleetProvenanceTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("ab-fleet-origin-").FullName;
    const string Session = "77777777-7777-4777-8777-777777777777";
    static readonly SessionProcess Process = new(700, DateTimeOffset.Parse("2026-11-17T00:00:00Z"));
    string Path => System.IO.Path.Combine(folder, "launch-provenance.json");
    LaunchProvenance Open() => new(Path, _ => { });
    public void Dispose() => Directory.Delete(folder, true);

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Only_a_durable_fleet_launch_marks_the_exact_session_and_process(bool fleet)
    {
        var store = Open();
        var request = Guid.NewGuid();
        store.Preparing(request, Session, fleet ? "fleet" : null);
        store.Bound(request, Session, Process);
        var reopened = Open();
        Assert.Equal(fleet, reopened.IsFleetWorker(Session, Process));
        Assert.False(reopened.IsFleetWorker(Guid.NewGuid().ToString(), Process));
        Assert.False(reopened.IsFleetWorker(Session, Process with { Id = 701 }));
        Assert.False(reopened.IsFleetWorker(Session, Process with { StartedAt = Process.StartedAt!.Value.AddTicks(1) }));
        Assert.False(reopened.IsFleetWorker(Session, null));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public void A_session_is_fleet_launched_by_its_record_alone_even_after_its_process_is_gone(bool fleet, bool expected)
    {
        var store = Open();
        var request = Guid.NewGuid();
        store.Preparing(request, Session, fleet ? "fleet" : null);
        store.Bound(request, Session, Process);
        var reopened = Open();
        Assert.Equal(expected, reopened.WasFleetLaunched(Session));
        Assert.False(reopened.WasFleetLaunched(Guid.NewGuid().ToString()));
    }

    [Fact]
    public void A_mixed_or_unfinished_record_never_proves_a_fleet_launch_and_an_unreadable_one_throws()
    {
        var store = Open();
        var fleet = Guid.NewGuid();
        store.Preparing(fleet, Session, "fleet");
        store.Bound(fleet, Session, Process);
        var person = Guid.NewGuid();
        store.Preparing(person, Session);
        store.Bound(person, Session, Process with { Id = 701 });
        Assert.False(Open().WasFleetLaunched(Session));
        store.Preparing(Guid.NewGuid(), "");
        Assert.Throws<InvalidOperationException>(() => Open().WasFleetLaunched(Session));
        File.WriteAllText(Path, "{ not json");
        Assert.Throws<InvalidOperationException>(() => Open().WasFleetLaunched(Session));
    }

    [Fact]
    public void The_launch_role_is_recorded_durably_and_a_conflicting_or_missing_one_reads_as_none()
    {
        var store = Open();
        var request = Guid.NewGuid();
        store.Preparing(request, Session, null, "quality-lead");
        store.Bound(request, Session, Process);
        Assert.Equal("quality-lead", Open().RoleOf(Session));
        Assert.Null(Open().RoleOf(Guid.NewGuid().ToString()));
        var other = Guid.NewGuid();
        store.Preparing(other, Session, null, "builder");
        store.Bound(other, Session, Process);
        Assert.Null(Open().RoleOf(Session));
        var bare = Guid.NewGuid();
        var second = "88888888-8888-4888-8888-888888888888";
        store.Preparing(bare, second);
        store.Bound(bare, second, Process);
        Assert.Null(Open().RoleOf(second));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_conflicting_person_or_process_record_never_inherits_fleet_authority(bool changedProcess)
    {
        var store = Open();
        var fleet = Guid.NewGuid();
        store.Preparing(fleet, Session, "fleet");
        store.Bound(fleet, Session, Process);
        var conflict = Guid.NewGuid();
        store.Preparing(conflict, Session, changedProcess ? "fleet" : null);
        store.Bound(conflict, Session, changedProcess ? Process with { Id = 701 } : Process);
        Assert.False(Open().IsFleetWorker(Session, Process));
    }

    [Theory]
    [InlineData("")]
    [InlineData(Session)]
    public void An_unfinished_unattributed_launch_blocks_fleet_origin_proof(string pendingSession)
    {
        var store = Open();
        var fleet = Guid.NewGuid();
        store.Preparing(fleet, Session, "fleet");
        store.Bound(fleet, Session, Process);
        store.Preparing(Guid.NewGuid(), pendingSession);
        Assert.Throws<InvalidOperationException>(() => Open().IsFleetWorker(Session, Process));
    }

    [Fact]
    public void Missing_or_corrupt_launch_records_never_grant_fleet_authority()
    {
        Assert.False(Open().IsFleetWorker(Session, Process));
        File.WriteAllText(Path, "{");
        Assert.Throws<InvalidOperationException>(() => Open().IsFleetWorker(Session, Process));
    }
}
