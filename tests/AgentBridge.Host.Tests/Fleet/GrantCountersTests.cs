using AgentBridge.Host;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class GrantCountersTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("ab-counters-").FullName;
    readonly P256KeyPair key = P256KeyPair.Generate();
    readonly FakeTimeProvider clock = new(new(2026, 11, 21, 18, 0, 0, TimeSpan.Zero));
    const string Device = "0123456789abcdef0123456789abcdef";
    static readonly GrantScope Scope = new([Device], ["repo"], ["copilot"], ["small"], ["default"], ["medium"], ["default"], ["ask"]);
    string Folder => Path.Combine(root, "default");
    GrantStore Store(Action<string, byte[]>? write = null) => new(Folder, key, new(Folder, clock), Scope, clock, write);
    GrantCounters Open(Action<string, byte[]>? write = null) => new(Store(write), clock);
    GrantDocument Issue(GrantCaps caps)
    {
        var grant = new GrantDocument(1, Guid.NewGuid(), "123",
            new("123", Device, "AQ", clock.GetUtcNow(), new('a', 64)), new(Device, "default"),
            [new("123", Device, Guid.NewGuid().ToString(), "AAAAAAAAAAAAAAAAAAAAAA", new(42, clock.GetUtcNow()))],
            ["launch"], Scope, caps, clock.GetUtcNow(), 1, clock.GetUtcNow().AddHours(24), [], []);
        Assert.True(Store().Issue(grant).Usable);
        return grant;
    }
    GrantCounterResult Reserve(GrantCounters counters, GrantDocument grant, Guid? request = null, string? binding = null) =>
        counters.Reserve(grant.Id, 1, request ?? Guid.NewGuid(), grant.Subjects[0], binding ?? new('b', 64));
    StopTarget Target() => new(Guid.NewGuid().ToString(), new(99, clock.GetUtcNow()));
    public void Dispose() { key.Dispose(); Directory.Delete(root, true); }

    [Theory]
    [InlineData(1)]
    [InlineData(24)]
    public void Rolling_windows_release_only_at_the_exact_boundary(int hours)
    {
        var grant = Issue(hours == 1 ? new(1, 2, 2) : new(2, 2, 3));
        var counters = Open();
        Assert.True(Reserve(counters, grant).Accepted);
        if (hours == 24)
        {
            clock.Advance(TimeSpan.FromHours(1));
            Assert.True(Reserve(counters, grant).Accepted);
        }
        clock.SetUtcNow(grant.NotBefore.AddHours(hours).AddTicks(-1));
        Assert.Equal(hours == 1 ? GrantCounterProblem.Hourly : GrantCounterProblem.Daily, Reserve(Open(), grant).Problem);
        clock.Advance(TimeSpan.FromTicks(1));
        // The original grant expires at 24h; a snapshot still exposes the exact daily boundary.
        var result = Open().Read(grant.Id, 1);
        Assert.Equal(hours == 1 ? 0 : 1, hours == 1 ? result.Usage!.Hourly : result.Usage!.Daily);
        if (hours == 1) { Assert.True(Reserve(Open(), grant).Accepted); }
        else { Assert.Equal(GrantStoreProblem.Expired, Reserve(Open(), grant).StoreProblem); }
    }

    [Fact]
    public void Pending_and_uncertain_outcomes_occupy_the_last_live_slot_until_proven_not_launched()
    {
        var grant = Issue(new(3, 3, 1));
        var reservation = Reserve(Open(), grant).Reservation!;
        Assert.True(Open().Abandon(reservation.Request, provenNotLaunched: false).Accepted);
        Assert.Equal(GrantCounterProblem.Live, Reserve(Open(), grant).Problem);
        Assert.True(Open().Abandon(reservation.Request, provenNotLaunched: true).Accepted);
        Assert.Equal(new GrantUsage(0, 0, 0), Open().Read(grant.Id, 1).Usage);
        Assert.True(Reserve(Open(), grant).Accepted);
    }

    [Fact]
    public void Restart_reconciles_request_records_and_exact_process_instances_without_refunding_launches()
    {
        var grant = Issue(new(3, 3, 1));
        var reservation = Reserve(Open(), grant).Reservation!;
        var target = Target();
        Assert.True(Open().Reconcile(grant.Id, _ => new(target), pid =>
        {
            Assert.Equal(target.ProcessInstance.Pid, pid);
            return new(GrantProcessStatus.Live, target.ProcessInstance);
        }).Accepted);
        Assert.Equal(GrantReservationState.Committed, Open().Commit(reservation.Request, target).Reservation!.State);
        Assert.Equal(GrantCounterProblem.Live, Reserve(Open(), grant).Problem);
        Assert.True(Open().Reconcile(grant.Id, _ => throw new Exception("bound targets do not need lookup"),
            _ => new(GrantProcessStatus.Exited)).Accepted);
        Assert.Equal(new GrantUsage(1, 1, 0), Open().Read(grant.Id, 1).Usage);
        Assert.Equal(GrantCounterProblem.Binding, Open().Abandon(reservation.Request, true).Problem);
    }

    [Fact]
    public void Unknown_restart_evidence_keeps_reservations_and_pid_reuse_is_not_live()
    {
        var grant = Issue(new(3, 3, 1));
        var reservation = Reserve(Open(), grant).Reservation!;
        Assert.True(Open().Reconcile(grant.Id, _ => new(null), _ => new(GrantProcessStatus.Unknown)).Accepted);
        Assert.Equal(GrantCounterProblem.Live, Reserve(Open(), grant).Problem);
        var target = Target();
        Assert.True(Open().Commit(reservation.Request, target).Accepted);
        Assert.True(Open().Reconcile(grant.Id, _ => throw new Exception("already bound"), _ => new(GrantProcessStatus.Unknown)).Accepted);
        Assert.Equal(GrantCounterProblem.Live, Reserve(Open(), grant).Problem);
        var reused = target.ProcessInstance with { StartedAt = target.ProcessInstance.StartedAt.AddSeconds(1) };
        Assert.True(Open().Reconcile(grant.Id, _ => new(null), _ => new(GrantProcessStatus.Live, reused)).Accepted);
        Assert.Equal(new GrantUsage(1, 1, 0), Open().Read(grant.Id, 1).Usage);
    }

    [Fact]
    public void Backwards_clock_refuses_admission_across_restart_until_the_watermark_catches_up()
    {
        var grant = Issue(new(1, 2, 2));
        Assert.True(Reserve(Open(), grant).Accepted);
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.True(Open().Read(grant.Id, 1).Accepted);
        clock.AdjustTime(clock.GetUtcNow().AddMinutes(-20));
        Assert.Equal(GrantCounterProblem.Clock, Reserve(Open(), grant).Problem);
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.Equal(GrantCounterProblem.Hourly, Reserve(Open(), grant).Problem);
    }

    [Fact]
    public async Task Independent_store_instances_cannot_both_reserve_the_last_slot()
    {
        var grant = Issue(new(1, 1, 1));
        var results = await Task.WhenAll(Task.Run(() => Reserve(Open(), grant)), Task.Run(() => Reserve(Open(), grant)));
        Assert.Single(results, r => r.Accepted);
        Assert.Equal(new GrantUsage(1, 1, 1), Open().Read(grant.Id, 1).Usage);
    }

    [Fact]
    public void Request_bindings_are_durable_and_a_refunded_request_cannot_be_reused()
    {
        var grant = Issue(new(3, 3, 2));
        var request = Guid.NewGuid();
        Assert.True(Reserve(Open(), grant, request).Accepted);
        Assert.Equal(GrantCounterProblem.Duplicate, Reserve(Open(), grant, request).Problem);
        Assert.Equal(GrantCounterProblem.Binding, Reserve(Open(), grant, request, new('c', 64)).Problem);
        Assert.True(Open().Abandon(request, true).Accepted);
        Assert.Equal(GrantCounterProblem.Duplicate, Reserve(Open(), grant, request).Problem);
    }

    [Fact]
    public void Failed_durable_reservation_never_acknowledges_admission_and_old_mac_state_cannot_refund_it()
    {
        var grant = Issue(new(3, 3, 2));
        var path = Path.Combine(Folder, GrantStore.FileName);
        var old = File.ReadAllBytes(path);
        Assert.True(Reserve(Open(), grant).Accepted);
        File.WriteAllBytes(path, old);
        Assert.Equal(GrantStoreProblem.Generation, Reserve(Open(), grant).StoreProblem);
    }

    [Fact]
    public void Failed_reservation_install_refuses_even_after_restart()
    {
        var grant = Issue(new(3, 3, 2));
        Assert.Equal(GrantStoreProblem.WriteFailed, Reserve(Open((_, _) => throw new IOException("fixture")), grant).StoreProblem);
        Assert.Equal(GrantStoreProblem.AuditIncomplete, Reserve(Open(), grant).StoreProblem);
    }

    [Fact]
    public void Usage_transactions_preserve_issue_and_revoke_audit_outcomes()
    {
        var grant = Issue(new(3, 3, 2));
        Assert.True(Reserve(Open(), grant).Accepted);
        Assert.True(Store().Revoke(grant.Id, 1).Usable);
        var rows = GateAuditProjection.Load(Folder);
        Assert.True(rows.Complete);
        Assert.Contains(rows.Rows, r => r.Action == "grant.issue" && r.Gate?.Phase == "terminal" && r.Outcome == "issued");
        Assert.Contains(rows.Rows, r => r.Action == "grant.revoke" && r.Gate?.Phase == "terminal" && r.Outcome == "revoked");
        Assert.Equal(GrantStoreProblem.Revoked, Reserve(Open(), grant).StoreProblem);
    }

    [Fact]
    public void Restart_refunds_only_a_request_record_proving_no_process_was_launched()
    {
        var grant = Issue(new(1, 1, 1));
        Assert.True(Reserve(Open(), grant).Accepted);
        Assert.True(Open().Reconcile(grant.Id, _ => new(null, ProvenNotLaunched: true),
            _ => throw new Exception("no process to inspect")).Accepted);
        Assert.Equal(new GrantUsage(0, 0, 0), Open().Read(grant.Id, 1).Usage);
        Assert.True(Reserve(Open(), grant).Accepted);
    }

    [Fact]
    public void Clock_watermarks_on_refusals_never_create_successful_admission_audit_rows()
    {
        var grant = Issue(new(1, 1, 1));
        Assert.True(Reserve(Open(), grant).Accepted);
        clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(GrantCounterProblem.Hourly, Reserve(Open(), grant).Problem);
        var rows = GateAuditProjection.Load(Folder).Rows;
        Assert.Single(rows, r => r.Action == "grant.reserve" && r.Gate?.Phase == "terminal" && r.Outcome == "admitted");
        Assert.Equal("grant.update", rows.Last().Action);
        clock.AdjustTime(clock.GetUtcNow().AddSeconds(-1));
        Assert.Equal(GrantCounterProblem.Clock, Reserve(Open(), grant).Problem);
    }

    [Fact]
    public void Updates_name_the_changed_grant_and_reconciliation_is_grant_scoped_even_after_revocation()
    {
        var first = Issue(new(2, 2, 2));
        var second = Issue(new(2, 2, 2));
        Assert.True(Reserve(Open(), first).Accepted);
        var reservation = Reserve(Open(), second).Reservation!;
        Assert.True(Open().Commit(reservation.Request, Target()).Accepted);
        Assert.Equal(second.Id.ToString(), GateAuditProjection.Load(Folder).Rows.Last().Target);
        Assert.True(Store().Revoke(second.Id, 1).Usable);
        Assert.True(Open().Reconcile(second.Id, _ => throw new Exception("do not lookup the other grant"),
            _ => new(GrantProcessStatus.Exited)).Accepted);
        Assert.Equal(second.Id.ToString(), GateAuditProjection.Load(Folder).Rows.Last().Target);
        Assert.Equal(new GrantUsage(1, 1, 1), Open().Read(first.Id, 1).Usage);
    }

    [Theory]
    [InlineData(GrantLaunchOutcome.Refused, "scope")]
    [InlineData(GrantLaunchOutcome.Failed, "policy")]
    public void Duplicate_requests_return_the_durable_terminal_outcome_without_spending_again(GrantLaunchOutcome outcome, string reason)
    {
        var grant = Issue(new(3, 3, 2));
        var request = Guid.NewGuid();
        Assert.True(Reserve(Open(), grant, request).Accepted);
        Assert.True(Open().Complete(request, outcome, reason).Accepted);
        var duplicate = Reserve(Open((_, _) => throw new IOException("duplicate must not write")), grant, request);
        Assert.Equal(GrantCounterProblem.Duplicate, duplicate.Problem);
        Assert.Equal(outcome, duplicate.Reservation!.Outcome);
        Assert.Equal(reason, duplicate.Reservation.Reason);
        Assert.Equal(new GrantUsage(1, 1, 0), duplicate.Usage);
        Assert.True(Open().Reconcile(grant.Id, _ => throw new Exception("terminal must not reconcile"),
            _ => throw new Exception("terminal has no process")).Accepted);
        Assert.Equal(GrantCounterProblem.Binding, Reserve(Open(), grant, request, new('c', 64)).Problem);
        Assert.Equal(GrantCounterProblem.Binding, Open().Complete(request,
            outcome == GrantLaunchOutcome.Refused ? GrantLaunchOutcome.Failed : GrantLaunchOutcome.Refused, reason).Problem);
        Assert.Equal(GrantCounterProblem.Binding, Open().Commit(request, Target()).Problem);
    }

    [Fact]
    public void A_started_duplicate_retains_its_exact_process_and_survives_revocation_and_expiry()
    {
        var grant = Issue(new(3, 3, 2));
        var request = Guid.NewGuid();
        var target = Target();
        Assert.True(Reserve(Open(), grant, request).Accepted);
        Assert.True(Open().Commit(request, target).Accepted);
        Assert.True(Store().Revoke(grant.Id, 1).Usable);
        clock.Advance(TimeSpan.FromHours(24));
        var duplicate = Reserve(Open(), grant, request);
        Assert.Equal(GrantCounterProblem.Duplicate, duplicate.Problem);
        Assert.Equal(GrantLaunchOutcome.Started, duplicate.Reservation!.Outcome);
        Assert.Equal(target, duplicate.Reservation.Target);
        Assert.Equal(GrantCounterProblem.Binding, Reserve(Open(), grant, request, new('c', 64)).Problem);
    }

    [Fact]
    public void Only_settled_requests_expire_at_thirty_days_and_uncertain_capacity_is_never_pruned()
    {
        var grant = Issue(new(3, 3, 2));
        var settled = Guid.NewGuid();
        var uncertain = Guid.NewGuid();
        Assert.True(Reserve(Open(), grant, settled).Accepted);
        Assert.True(Reserve(Open(), grant, uncertain).Accepted);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(Open().Complete(settled, GrantLaunchOutcome.Failed, "policy").Accepted);
        clock.Advance(TimeSpan.FromDays(30).Subtract(TimeSpan.FromTicks(1)));
        var next = Issue(new(3, 3, 2));
        Assert.Equal(GrantCounterProblem.Binding, Reserve(Open(), next, settled).Problem);
        clock.Advance(TimeSpan.FromTicks(1));
        Assert.True(Reserve(Open(), next, settled).Accepted);
        Assert.Equal(GrantCounterProblem.Binding, Reserve(Open(), next, uncertain).Problem);
        Assert.Equal(new GrantUsage(0, 0, 1), Open().Read(grant.Id, 1).Usage);
    }

    [Fact]
    public void Terminal_failure_is_persisted_before_acknowledgment_and_never_repaired_on_restart()
    {
        var grant = Issue(new(3, 3, 2));
        var request = Guid.NewGuid();
        Assert.True(Reserve(Open(), grant, request).Accepted);
        Assert.Equal(GrantStoreProblem.WriteFailed, Open((_, _) => throw new IOException("fixture"))
            .Complete(request, GrantLaunchOutcome.Failed, "policy").StoreProblem);
        Assert.Equal(GrantStoreProblem.AuditIncomplete, Reserve(Open(), grant, request).StoreProblem);
    }

    [Fact]
    public void Full_admission_keeps_every_unexpired_binding_but_still_answers_an_exact_duplicate()
    {
        var grant = Issue(new(3, 3, 2));
        var now = clock.GetUtcNow();
        var reservations = Enumerable.Range(0, 4096).Select(_ => new GrantReservation(Guid.NewGuid(), grant.Id, 1,
            grant.Subjects[0], new('b', 64), now, GrantReservationState.Failed, Reason: "policy")).ToArray();
        var state = new GrantCounterState(now, reservations);
        var full = GrantCounters.Admit(state, grant, grant.Id, 1, Guid.NewGuid(), grant.Subjects[0], new('b', 64), now);
        Assert.Equal(GrantCounterProblem.Full, full.Result.Problem);
        Assert.Same(state, full.State);
        var duplicate = GrantCounters.Admit(state, grant, grant.Id, 1, reservations[0].Request, grant.Subjects[0], new('b', 64), now);
        Assert.Equal(GrantCounterProblem.Duplicate, duplicate.Result.Problem);
        Assert.Equal(reservations[0], duplicate.Result.Reservation);
        Assert.Same(state, duplicate.State);
    }

    [Fact]
    public void A_full_persisted_store_refuses_a_new_request_but_returns_the_recorded_terminal_duplicate()
    {
        var grant = Issue(new(3, 3, 2));
        var request = Guid.NewGuid();
        Assert.True(Reserve(Open(), grant, request).Accepted);
        Assert.True(Open().Complete(request, GrantLaunchOutcome.Failed, "policy").Accepted);
        var path = Path.Combine(Folder, GrantStore.FileName);
        var before = File.ReadAllBytes(path);
        var full = new GrantCounters(new GrantStore(Folder, key, new(Folder, clock), Scope, clock, null,
            maxBytes: before.Length + 1), clock);
        Assert.Equal(GrantCounterProblem.Full, Reserve(full, grant).Problem);
        Assert.Equal(before, File.ReadAllBytes(path));
        var duplicate = Reserve(full, grant, request);
        Assert.Equal(GrantCounterProblem.Duplicate, duplicate.Problem);
        Assert.Equal(GrantLaunchOutcome.Failed, duplicate.Reservation!.Outcome);
        Assert.Equal("policy", duplicate.Reservation.Reason);
        Assert.Equal(before, File.ReadAllBytes(path));
    }
}
