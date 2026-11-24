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
    public void A_resumed_fleet_worker_replaces_its_old_record_so_only_its_new_process_is_a_fleet_worker()
    {
        var store = Open();
        var first = Guid.NewGuid();
        store.Preparing(first, Session, "fleet");
        store.Bound(first, Session, Process);
        var again = Guid.NewGuid();
        var second = Process with { Id = 701 };
        store.Preparing(again, Session, "fleet", null, supersedes: true);
        Assert.Throws<InvalidOperationException>(() => store.WasFleetLaunched(Session));      // unfinished until bound
        store.Bound(again, Session, second);
        var reopened = Open();
        Assert.True(reopened.IsFleetWorker(Session, second));
        Assert.False(reopened.IsFleetWorker(Session, Process));
        Assert.True(reopened.WasFleetLaunched(Session));
    }

    [Fact]
    public void A_resume_never_supersedes_a_session_any_person_launch_touched()
    {
        var store = Open();
        var person = Guid.NewGuid();
        store.Preparing(person, Session);
        store.Bound(person, Session, Process);
        Assert.Throws<InvalidOperationException>(() => store.Preparing(Guid.NewGuid(), Session, "fleet", null, supersedes: true));
        Assert.False(Open().WasFleetLaunched(Session));
        Assert.True(Open().Contains(Session, Process));
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

    [Fact]
    public void A_role_is_recorded_only_for_a_person_launch_never_for_a_new_launch_by_an_agent()
    {
        var store = Open();
        const string coordinator = "99999999-9999-4999-8999-999999999999", builder = "55555555-5555-4555-8555-555555555555";
        var own = Guid.NewGuid();
        store.Preparing(own, coordinator, null, "quality-lead");
        store.Bound(own, coordinator, Process);
        var plain = Guid.NewGuid();
        store.Preparing(plain, builder);
        store.Bound(plain, builder, Process);
        var reopened = Open();
        Assert.Equal("quality-lead", reopened.RoleFor("quality-lead", null));                      // a person's launch
        Assert.Null(reopened.RoleFor(null, null));
        // Owner decision Nov 23 19:52 PT, item 2: NEW coordinator sessions need the owner's direct approval, so even a coordinator launching a
        // child with its own role records none; a role survives only on the same session resumed in place (RoleKept).
        Assert.Null(reopened.RoleFor("quality-lead", $"agent:m/{coordinator}"));
        Assert.Null(reopened.RoleFor("chief", $"agent:m/{coordinator}"));                          // never a higher or other role
        Assert.Null(reopened.RoleFor("quality-lead", $"agent:m/{builder}"));                       // a builder never records a coordinator role
        Assert.Null(reopened.RoleFor("quality-lead", "agent:m/11111111-1111-4111-8111-111111111111")); // an unknown session has none
        store.Preparing(Guid.NewGuid(), "");                                                       // an unreadable record: none, not a throw
        Assert.Null(Open().RoleFor("quality-lead", $"agent:m/{coordinator}"));
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
