using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class GateRunControlTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "gate-run-" + Guid.NewGuid().ToString("N"));
    readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-11-22T00:00:00Z"));
    static readonly GateControlAuthority Person = new("relay", "person:phone-1");
    string Profile => Path.Combine(root, "default");
    public void Dispose() { if (Directory.Exists(root)) { Directory.Delete(root, true); } }

    AgentAuditLog NewLog() { Directory.CreateDirectory(Profile); return new AgentAuditLog(Profile, clock); }
    List<AuditCommands.Row> Rows() => GateAuditProjection.Load(Profile).Rows.Where(r => r.Gate is not null).OrderBy(r => r.Gate!.Sequence).ToList();

    [Fact]
    public void A_relay_person_start_writes_a_prepared_control_marker_and_assigns_the_run()
    {
        var log = NewLog();
        var run = new GateRunControl(log).Start(Person);
        Assert.NotNull(run);
        Assert.NotEqual(Guid.Empty, run.Run);
        Assert.Equal(run, log.ControlWriterRun);
        var row = Assert.Single(Rows());
        Assert.Equal(("gate.start", "prepared", "profile", "person:phone-1"), (row.Action, row.Outcome, row.Target, row.Principal));
        Assert.Equal((run.Run, "control", "relay", "person-client"), (row.Gate!.Run, row.Gate.Phase, row.Gate.Path, row.Gate.ActorKind));
    }

    [Fact]
    public void The_local_pipe_cannot_start_a_run()
    {
        var log = NewLog();
        Assert.Null(new GateRunControl(log).Start(new("local", "person:local")));
        Assert.Null(log.ControlWriterRun);
        Assert.Empty(Rows());
    }

    [Theory]
    [InlineData("agent:machine/session")]
    [InlineData("ha:connector")]
    [InlineData("")]
    public void A_principal_that_is_not_a_person_cannot_start_a_run(string principal)
    {
        var log = NewLog();
        Assert.Null(new GateRunControl(log).Start(new("relay", principal)));
        Assert.Null(log.ControlWriterRun);
        Assert.Empty(Rows());
    }

    [Fact]
    public void Starting_while_a_run_is_active_is_refused_and_keeps_that_run()
    {
        var log = NewLog();
        var control = new GateRunControl(log);
        var first = control.Start(Person);
        Assert.Null(control.Start(Person));
        Assert.Equal(first, log.ControlWriterRun);
        Assert.Single(Rows());
    }

    [Fact]
    public void Reset_voids_the_current_run_and_issues_a_new_run_with_a_fresh_begin_marker()
    {
        var log = NewLog();
        var control = new GateRunControl(log);
        var old = control.Start(Person)!;
        var fresh = control.Reset(Person);
        Assert.NotNull(fresh);
        Assert.NotEqual(old.Run, fresh.Run);
        Assert.Equal(fresh, log.ControlWriterRun);
        var rows = Rows();
        Assert.Equal(["gate.start", "gate.reset", "gate.start"], rows.Select(r => r.Action));
        Assert.Equal(["prepared", "invalidated", "prepared"], rows.Select(r => r.Outcome));
        Assert.Equal([old.Run, old.Run, fresh.Run], rows.Select(r => r.Gate!.Run));
        Assert.Equal([1L, 2L, 3L], rows.Select(r => r.Gate!.Sequence));
    }

    [Fact]
    public void Reset_without_an_active_run_is_refused()
    {
        var log = NewLog();
        Assert.Null(new GateRunControl(log).Reset(Person));
        Assert.Null(log.ControlWriterRun);
        Assert.Empty(Rows());
    }

    [Fact]
    public void Reset_from_the_local_pipe_is_refused_and_keeps_the_run()
    {
        var log = NewLog();
        var control = new GateRunControl(log);
        var run = control.Start(Person);
        Assert.Null(control.Reset(new("local", "person:local")));
        Assert.Equal(run, log.ControlWriterRun);
        Assert.Single(Rows());
    }

    [Fact]
    public void A_begin_marker_that_cannot_be_written_leaves_no_run()
    {
        var log = NewLog();
        File.WriteAllText(Path.Combine(Profile, "audit"), "not a folder");
        Assert.Null(new GateRunControl(log).Start(Person));
        Assert.Null(log.ControlWriterRun);
    }
}
