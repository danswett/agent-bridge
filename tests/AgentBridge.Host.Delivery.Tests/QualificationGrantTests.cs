using AgentBridge.Host.Delivery.Copilot;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class QualificationGrantTests
{
    internal sealed class Trust : IQualificationTrust
    {
        public QualificationAccess Access { get; set; } = QualificationAccess.SreCli;
        public QualificationHostKind HostKind { get; set; } = QualificationHostKind.Disposable;
        public QualificationBinding Binding { get; set; } = new(new string('a', 40), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
    }

    internal sealed class Journal : IQualificationGrantJournal
    {
        readonly object gate = new();
        public readonly List<QualificationGrant> Entries = [];
        public bool Available = true;
        public QualificationGrant? Find(Guid id) { lock (gate) { return Entries.LastOrDefault(x => x.Id == id); } }
        public bool TryAppend(QualificationGrant? expected, QualificationGrant next)
        {
            lock (gate)
            {
                if (!Available) { throw new IOException("receipt store unavailable"); }
                if (Find(next.Id) != expected) { return false; }
                Entries.Add(next); return true;
            }
        }
    }

    internal sealed class Run
    {
        public readonly FakeTimeProvider Clock = new();
        public readonly Trust Trust = new();
        public readonly Journal Journal = new();
        public readonly Guid Id = Guid.NewGuid();
        public QualificationGrants Grants() => new(Trust, Journal, Clock);
        public QualificationGrant Issue() => Assert.IsType<QualificationGrant>(Grants().Issue(Id, TimeSpan.FromMinutes(60)).Grant);
    }

    [Fact]
    public void No_trusted_adapters_means_named_refusal()
    {
        var grants = new QualificationGrants();
        Assert.NotNull(grants.Issue(Guid.NewGuid(), TimeSpan.FromMinutes(1)).Refusal);
        Assert.NotNull(grants.Consume(Guid.NewGuid()).Refusal);
    }

    [Theory]
    [InlineData(0, 1)][InlineData(1, 2)][InlineData(1, 0)]
    public void Untrusted_callers_and_non_disposable_hosts_cannot_issue_or_consume(int access, int kind)
    {
        var run = new Run(); run.Issue(); run.Trust.Access = (QualificationAccess)access; run.Trust.HostKind = (QualificationHostKind)kind;
        Assert.NotNull(run.Grants().Issue(Guid.NewGuid(), TimeSpan.FromMinutes(1)).Refusal);
        Assert.NotNull(run.Grants().Consume(run.Id).Refusal);
        Assert.Single(run.Journal.Entries);
    }

    [Fact]
    public void A_person_may_approve_but_only_SRE_CLI_can_start_the_run()
    {
        var run = new Run(); run.Trust.Access = QualificationAccess.OwnerApproval; run.Issue();
        Assert.NotNull(run.Grants().Consume(run.Id).Refusal);
        run.Trust.Access = QualificationAccess.SreCli;
        Assert.Null(run.Grants().Consume(run.Id).Refusal);
    }

    [Theory]
    [InlineData(-1)][InlineData(0)][InlineData(61)]
    public void Nonpositive_or_over_sixty_minute_lifetimes_are_refused(int minutes)
    {
        var run = new Run();
        Assert.NotNull(run.Grants().Issue(run.Id, TimeSpan.FromMinutes(minutes)).Refusal);
        Assert.Empty(run.Journal.Entries);
    }

    [Theory]
    [InlineData("missing")][InlineData("unknown")][InlineData("expired")]
    [InlineData("revoked")][InlineData("consumed")][InlineData("head")]
    [InlineData("vm")][InlineData("run")][InlineData("host")]
    public void Missing_unknown_expired_replayed_and_mismatched_grants_are_refused(string change)
    {
        var run = new Run(); run.Issue(); var id = run.Id;
        switch (change)
        {
            case "missing": id = Guid.Empty; break;
            case "unknown": id = Guid.NewGuid(); break;
            case "expired": run.Clock.Advance(TimeSpan.FromMinutes(60)); break;
            case "revoked": Assert.Null(run.Grants().Revoke(id).Refusal); break;
            case "consumed": Assert.Null(run.Grants().Consume(id).Refusal); break;
            case "head": run.Trust.Binding = run.Trust.Binding with { Head = new string('b', 40) }; break;
            case "vm": run.Trust.Binding = run.Trust.Binding with { Vm = Guid.NewGuid() }; break;
            case "run": run.Trust.Binding = run.Trust.Binding with { Run = Guid.NewGuid() }; break;
            case "host": run.Trust.Binding = run.Trust.Binding with { Host = Guid.NewGuid() }; break;
        }
        Assert.NotNull(run.Grants().Consume(id).Refusal);
    }

    [Fact]
    public async Task One_approval_is_issued_once_and_consumed_by_exactly_one_competing_controller()
    {
        var run = new Run(); run.Issue();
        Assert.NotNull(run.Grants().Issue(run.Id, TimeSpan.FromMinutes(1)).Refusal);
        var attempts = await Task.WhenAll(Task.Run(() => run.Grants().Consume(run.Id)), Task.Run(() => run.Grants().Consume(run.Id)));
        Assert.Single(attempts, x => x.Refusal is null);
        Assert.Equal(new[] { QualificationGrantState.Issued, QualificationGrantState.Consumed }, run.Journal.Entries.Select(x => x.State));
    }

    [Fact]
    public void Revocation_expiry_and_clock_rollback_invalidate_a_consumed_grant()
    {
        var run = new Run(); run.Issue();
        var grant = Assert.IsType<QualificationGrant>(run.Grants().Consume(run.Id).Grant);
        Assert.True(run.Grants().IsCurrent(grant));
        Assert.Null(run.Grants().Revoke(run.Id).Refusal);
        Assert.False(run.Grants().IsCurrent(grant));
        Assert.NotNull(run.Grants().Issue(run.Id, TimeSpan.FromMinutes(1)).Refusal);
        Assert.Equal(QualificationGrantState.Revoked, run.Journal.Entries.Last().State);
        var other = new Run(); other.Issue();
        var current = Assert.IsType<QualificationGrant>(other.Grants().Consume(other.Id).Grant);
        other.Clock.Advance(TimeSpan.FromMinutes(60));
        Assert.False(other.Grants().IsCurrent(current));
        other.Clock.SetUtcNow(current.Issued.AddSeconds(-1));
        Assert.False(other.Grants().IsCurrent(current));
    }

    [Fact]
    public void Failed_durable_audit_never_returns_a_grant_or_consumes_one()
    {
        var run = new Run(); run.Journal.Available = false;
        Assert.Throws<IOException>(() => run.Grants().Issue(run.Id, TimeSpan.FromMinutes(1)));
        run.Journal.Available = true; run.Issue(); run.Journal.Available = false;
        Assert.Throws<IOException>(() => run.Grants().Consume(run.Id));
        Assert.Single(run.Journal.Entries);
    }
}
