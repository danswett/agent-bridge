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
        Assert.False(Store().TryGetUsable(grant.Id, 1).Usable);
        Assert.Equal(GrantCounterProblem.Clock, Reserve(Open(), grant).Problem);
        clock.Advance(TimeSpan.FromMinutes(20));
        Assert.True(Store().TryGetUsable(grant.Id, 1).Usable);
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
    [InlineData(false)]
    [InlineData(true)]
    public void Expiry_before_any_reservation_is_durable_even_if_install_fails(bool failWrite)
    {
        var grant = Issue(new(3, 3, 3));
        clock.Advance(TimeSpan.FromHours(25));
        var counters = failWrite ? Open((_, _) => throw new IOException("fixture")) : Open();
        Assert.Equal(failWrite ? GrantStoreProblem.WriteFailed : GrantStoreProblem.Expired,
            Reserve(counters, grant).StoreProblem);
        clock.AdjustTime(grant.NotBefore.AddHours(1));
        var retried = Reserve(Open(), grant);
        if (failWrite) { Assert.Equal(GrantStoreProblem.AuditIncomplete, retried.StoreProblem); }
        else { Assert.Equal(GrantCounterProblem.Clock, retried.Problem); }
    }

    [Fact]
    public void Expiry_and_watermark_use_the_same_transaction_clock_snapshot()
    {
        var grant = Issue(new(3, 3, 3));
        clock.Advance(TimeSpan.FromHours(23));
        clock.AutoAdvanceAmount = TimeSpan.FromHours(2);
        Assert.True(Reserve(Open(), grant).Accepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Expiry_refusal_prevents_timed_lookup_after_restart_and_clock_rollback(bool priorReservation)
    {
        var grant = Issue(new(3, 3, 3));
        if (priorReservation) { Assert.True(Reserve(Open(), grant).Accepted); }
        clock.Advance(TimeSpan.FromHours(25));
        Assert.Equal(GrantStoreProblem.Expired, Reserve(Open(), grant).StoreProblem);
        Assert.True(Open().Read(grant.Id, 1).Accepted);
        clock.AdjustTime(grant.NotBefore.AddHours(1));
        Assert.Equal(GrantCounterProblem.Clock, Reserve(Open(), grant).Problem);
        Assert.False(Store().TryGetUsable(grant.Id, 1).Usable);
        var store = new GrantStore(Folder, key, new(Folder, new FakeTimeProvider(grant.NotBefore.AddHours(25))), Scope, clock);
        Assert.True(store.Revoke(grant.Id, 1).Usable);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R4rl_rejected_reservation_change_preserves_the_observed_clock(bool abandon)
    {
        var grant = Issue(new(3, 3, 3));
        var reservation = Reserve(Open(), grant).Reservation!;
        Assert.True(Open().Commit(reservation.Request, Target()).Accepted);
        clock.Advance(TimeSpan.FromHours(2));
        var refused = abandon ? Open().Abandon(reservation.Request, true)
            : Open().Commit(reservation.Request, Target());
        Assert.Equal(GrantCounterProblem.Binding, refused.Problem);
        clock.AdjustTime(grant.NotBefore.AddHours(1));
        Assert.Equal(GrantCounterProblem.Clock, Reserve(Open(), grant).Problem);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void R4rl_unchanged_commit_or_reconcile_does_not_add_a_durable_intent(bool reconcile)
    {
        var grant = Issue(new(3, 3, 3));
        var reservation = Reserve(Open(), grant).Reservation!;
        var target = Target();
        Assert.True(Open().Commit(reservation.Request, target).Accepted);
        var before = GateAuditProjection.Load(Folder).Rows.Count;
        var result = reconcile
            ? Open().Reconcile(grant.Id, _ => throw new Exception("already bound"),
                _ => new(GrantProcessStatus.Live, target.ProcessInstance))
            : Open().Commit(reservation.Request, target);
        Assert.True(result.Accepted);
        Assert.Equal(before, GateAuditProjection.Load(Folder).Rows.Count);
    }

    [Fact]
    public void R4rl_empty_grant_id_returns_a_typed_refusal()
    {
        var grant = Issue(new(3, 3, 3));
        Assert.Equal(GrantCounterProblem.Malformed,
            Open().Reserve(Guid.Empty, 1, Guid.NewGuid(), grant.Subjects[0], new('b', 64)).Problem);
    }
}
