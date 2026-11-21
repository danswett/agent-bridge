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
