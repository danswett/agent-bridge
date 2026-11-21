using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Host.Delivery;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class QualificationGrantTests
{
    sealed class WallClock(DateTimeOffset now) : TimeProvider
    { public DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }
    internal sealed class Trust : IQualificationTrust
    {
        public QualificationAccess Access { get; set; } = QualificationAccess.SreCli;
        public QualificationHostKind HostKind { get; set; } = QualificationHostKind.Disposable;
        public QualificationBinding Binding { get; set; } = new(new string('a', 40), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            new(42, 100, "200:44:101", 7, @"C:\agent\cli.exe", "1.2", new string('a', 64),
                @"C:\agent\injector.exe", "2.3", new string('b', 64), KeyPlan.Header),
            "agent-bridge-qualify:" + new string('a', 64));
    }

    internal sealed class Journal : IQualificationGrantJournal
    {
        readonly object gate = new();
        public readonly List<QualificationGrant> Entries = [];
        public bool Available = true;
        public QualificationGrant? Find(Guid id) { lock (gate) { return Entries.LastOrDefault(x => x.Id == id); } }
        public QualificationGrant? FindChallenge(string nonce)
        { lock (gate) { return Entries.LastOrDefault(x => x.Binding.Challenge == nonce); } }
        public bool TryAppend(QualificationGrant? expected, QualificationGrant next)
        {
            lock (gate)
            {
                if (!Available) { throw new IOException("receipt store unavailable"); }
                if (Find(next.Id) != expected || FindChallenge(next.Binding.Challenge) != expected) { return false; }
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
        public QueueQualification.Challenge Challenge => new(Trust.Binding.Challenge);
        public QualificationGrantDecision Consume(Guid? id = null) => Grants().Consume(id ?? Id, Trust.Binding.Target, Challenge);
        public bool Current(QualificationGrant grant) => Grants().IsCurrent(grant, Trust.Binding.Target, Challenge);
    }

    [Fact]
    public void No_trusted_adapters_means_named_refusal()
    {
        var grants = new QualificationGrants();
        Assert.NotNull(grants.Issue(Guid.NewGuid(), TimeSpan.FromMinutes(1)).Refusal);
        var run = new Run();
        Assert.NotNull(grants.Consume(Guid.NewGuid(), run.Trust.Binding.Target, run.Challenge).Refusal);
    }

    [Theory]
    [InlineData(0, 1)][InlineData(1, 2)][InlineData(1, 0)][InlineData(7, 1)][InlineData(1, 9)]
    public void Untrusted_callers_and_non_disposable_hosts_cannot_issue_or_consume(int access, int kind)
    {
        var run = new Run(); run.Issue(); run.Trust.Access = (QualificationAccess)access; run.Trust.HostKind = (QualificationHostKind)kind;
        Assert.NotNull(run.Grants().Issue(Guid.NewGuid(), TimeSpan.FromMinutes(1)).Refusal);
        Assert.NotNull(run.Consume().Refusal);
        Assert.Single(run.Journal.Entries);
    }

    [Fact]
    public void A_person_may_approve_but_only_SRE_CLI_can_start_the_run()
    {
        var run = new Run(); run.Trust.Access = QualificationAccess.OwnerApproval; run.Issue();
        Assert.NotNull(run.Consume().Refusal);
        run.Trust.Access = QualificationAccess.SreCli;
        Assert.Null(run.Consume().Refusal);
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
    [InlineData("vm")][InlineData("run")][InlineData("host")][InlineData("target")][InlineData("challenge")]
    public void Missing_unknown_expired_replayed_and_mismatched_grants_are_refused(string change)
    {
        var run = new Run(); run.Issue(); var id = run.Id;
        switch (change)
        {
            case "missing": id = Guid.Empty; break;
            case "unknown": id = Guid.NewGuid(); break;
            case "expired": run.Clock.Advance(TimeSpan.FromMinutes(60)); break;
            case "revoked": Assert.Null(run.Grants().Revoke(id).Refusal); break;
            case "consumed": Assert.Null(run.Consume(id).Refusal); break;
            case "head": run.Trust.Binding = run.Trust.Binding with { Head = new string('b', 40) }; break;
            case "vm": run.Trust.Binding = run.Trust.Binding with { Vm = Guid.NewGuid() }; break;
            case "run": run.Trust.Binding = run.Trust.Binding with { Run = Guid.NewGuid() }; break;
            case "host": run.Trust.Binding = run.Trust.Binding with { Host = Guid.NewGuid() }; break;
            case "target": run.Trust.Binding = run.Trust.Binding with { Target = run.Trust.Binding.Target with { InputMode = 8 } }; break;
            case "challenge": run.Trust.Binding = run.Trust.Binding with { Challenge = "agent-bridge-qualify:" + new string('b', 64) }; break;
        }
        Assert.NotNull(run.Consume(id).Refusal);
    }

    [Fact]
    public async Task One_approval_is_issued_once_and_consumed_by_exactly_one_competing_controller()
    {
        var run = new Run(); run.Issue();
        Assert.NotNull(run.Grants().Issue(run.Id, TimeSpan.FromMinutes(1)).Refusal);
        var attempts = await Task.WhenAll(Task.Run(() => run.Consume()), Task.Run(() => run.Consume()));
        Assert.Single(attempts, x => x.Refusal is null);
        Assert.Equal(new[] { QualificationGrantState.Issued, QualificationGrantState.Consumed }, run.Journal.Entries.Select(x => x.State));
    }

    [Fact]
    public void Revocation_expiry_and_clock_rollback_invalidate_a_consumed_grant()
    {
        var run = new Run(); run.Issue();
        var grant = Assert.IsType<QualificationGrant>(run.Consume().Grant);
        Assert.True(run.Current(grant));
        Assert.Null(run.Grants().Revoke(run.Id).Refusal);
        Assert.False(run.Current(grant));
        Assert.NotNull(run.Grants().Issue(run.Id, TimeSpan.FromMinutes(1)).Refusal);
        Assert.Equal(QualificationGrantState.Revoked, run.Journal.Entries.Last().State);
        var other = new Run(); other.Issue();
        var current = Assert.IsType<QualificationGrant>(other.Consume().Grant);
        other.Clock.Advance(TimeSpan.FromMinutes(60));
        Assert.False(other.Current(current));
        Assert.False(new QualificationGrants(other.Trust, other.Journal, new WallClock(current.Issued.AddSeconds(-1)))
            .IsCurrent(current, other.Trust.Binding.Target, other.Challenge));
    }

    [Fact]
    public void Empty_approval_or_incomplete_binding_is_refused_and_only_SRE_can_revoke()
    {
        var run = new Run();
        Assert.NotNull(run.Grants().Issue(Guid.Empty, TimeSpan.FromMinutes(1)).Refusal);
        run.Trust.Binding = run.Trust.Binding with { Vm = Guid.Empty };
        Assert.NotNull(run.Grants().Issue(run.Id, TimeSpan.FromMinutes(1)).Refusal);
        run.Trust.Binding = run.Trust.Binding with { Vm = Guid.NewGuid(), Head = "not-a-sha" };
        Assert.NotNull(run.Grants().Issue(run.Id, TimeSpan.FromMinutes(1)).Refusal);
        run.Trust.Binding = run.Trust.Binding with { Head = new string('a', 40) }; run.Issue();
        run.Trust.Access = QualificationAccess.OwnerApproval;
        Assert.NotNull(run.Grants().Revoke(run.Id).Refusal);
        Assert.Single(run.Journal.Entries);
    }

    [Fact]
    public void Failed_durable_audit_never_returns_a_grant_or_consumes_one()
    {
        var run = new Run(); run.Journal.Available = false;
        Assert.Throws<IOException>(() => run.Grants().Issue(run.Id, TimeSpan.FromMinutes(1)));
        run.Journal.Available = true; run.Issue(); run.Journal.Available = false;
        Assert.Throws<IOException>(() => run.Consume());
        Assert.Single(run.Journal.Entries);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void A_grant_cannot_be_consumed_for_another_target_or_challenge(bool changeChallenge)
    {
        var run = new Run(); run.Issue();
        var target = run.Trust.Binding.Target;
        var challenge = run.Challenge;
        if (changeChallenge) { challenge = new("agent-bridge-qualify:" + new string('b', 64)); }
        else { target = target with { StartedAt = target.StartedAt + 1 }; }
        Assert.NotNull(run.Grants().Consume(run.Id, target, challenge).Refusal);
        Assert.Single(run.Journal.Entries);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void A_consumed_grant_is_not_current_for_another_target_or_challenge(bool changeChallenge)
    {
        var run = new Run(); run.Issue();
        var grant = Assert.IsType<QualificationGrant>(run.Consume().Grant);
        var target = run.Trust.Binding.Target;
        var challenge = run.Challenge;
        if (changeChallenge) { challenge = new("agent-bridge-qualify:" + new string('b', 64)); }
        else { target = target with { ProcessId = target.ProcessId + 1 }; }
        Assert.False(run.Grants().IsCurrent(grant, target, challenge));
        Assert.NotNull(run.Consume().Refusal);
        Assert.NotNull(run.Grants().Consume(run.Id, target, challenge).Refusal);
    }

    [Fact]
    public void Journal_restart_preserves_target_nonce_and_consumption_without_reference_identity()
    {
        var run = new Run(); var issued = run.Issue();
        var grant = Assert.IsType<QualificationGrant>(run.Consume().Grant);
        var restored = new Journal();
        restored.Entries.AddRange(JsonSerializer.Deserialize<List<QualificationGrant>>(JsonSerializer.Serialize(run.Journal.Entries))!);
        var controller = new QualificationGrants(run.Trust, restored, run.Clock);
        Assert.Equal(issued.Binding, restored.Entries[0].Binding);
        Assert.True(controller.IsCurrent(grant, run.Trust.Binding.Target with { }, new(run.Challenge.Text)));
        Assert.NotNull(controller.Consume(run.Id, run.Trust.Binding.Target, run.Challenge).Refusal);
        Assert.False(controller.IsCurrent(grant, run.Trust.Binding.Target, new("agent-bridge-qualify:" + new string('b', 64))));
        Assert.False(controller.IsCurrent(grant, run.Trust.Binding.Target with { InputMode = 8 }, run.Challenge));
        Assert.NotNull(controller.Consume(run.Id, run.Trust.Binding.Target with { InputMode = 8 }, run.Challenge).Refusal);
        Assert.Equal(2, restored.Entries.Count);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Missing_target_or_challenge_refuses_without_consumption(bool missingChallenge)
    {
        var run = new Run(); run.Issue();
        Assert.NotNull(run.Grants().Consume(run.Id, missingChallenge ? run.Trust.Binding.Target : null,
            missingChallenge ? null : run.Challenge).Refusal);
        Assert.Single(run.Journal.Entries);
        var grant = Assert.IsType<QualificationGrant>(run.Consume().Grant);
        Assert.False(run.Grants().IsCurrent(grant, missingChallenge ? run.Trust.Binding.Target : null,
            missingChallenge ? null : run.Challenge));
    }

    [Theory]
    [InlineData("issued")][InlineData("consumed")][InlineData("expired")][InlineData("revoked")][InlineData("restart")]
    public void A_challenge_nonce_any_earlier_grant_holds_is_never_issued_again(string history)
    {
        var run = new Run(); run.Issue();
        if (history is "consumed" or "restart") { Assert.Null(run.Consume().Refusal); }
        if (history == "revoked") { Assert.Null(run.Grants().Revoke(run.Id).Refusal); }
        if (history == "expired") { run.Clock.Advance(TimeSpan.FromMinutes(60)); Assert.NotNull(run.Consume().Refusal); }
        var journal = run.Journal;
        if (history == "restart")
        {
            journal = new Journal();
            journal.Entries.AddRange(JsonSerializer.Deserialize<List<QualificationGrant>>(JsonSerializer.Serialize(run.Journal.Entries))!);
        }
        var count = journal.Entries.Count;
        run.Trust.Binding = run.Trust.Binding with { Target = run.Trust.Binding.Target with { ProcessId = 43 } };
        Assert.NotNull(new QualificationGrants(run.Trust, journal, run.Clock).Issue(Guid.NewGuid(), TimeSpan.FromMinutes(1)).Refusal);
        Assert.Equal(count, journal.Entries.Count);
    }

    [Theory]
    [InlineData("pid")][InlineData("started")][InlineData("console")][InlineData("consoleZero")][InlineData("mode")][InlineData("cliPath")]
    [InlineData("cliVersion")][InlineData("cliHash")][InlineData("injectorPath")][InlineData("injectorVersion")][InlineData("injectorHash")]
    [InlineData("protocol")][InlineData("allDefault")][InlineData("nullTarget")][InlineData("head")][InlineData("vm")][InlineData("run")]
    [InlineData("host")][InlineData("nonce")][InlineData("shortNonce")][InlineData("approval")]
    public void Every_empty_zero_or_default_binding_field_refuses_issue(string field)
    {
        var run = new Run(); var b = run.Trust.Binding; var t = b.Target; var approval = run.Id;
        run.Trust.Binding = field switch
        {
            "pid" => b with { Target = t with { ProcessId = 0 } },
            "started" => b with { Target = t with { StartedAt = 0 } },
            "console" => b with { Target = t with { ConsoleId = "" } },
            "consoleZero" => b with { Target = t with { ConsoleId = "0:0:0" } },
            "mode" => b with { Target = t with { InputMode = 0 } },
            "cliPath" => b with { Target = t with { CliPath = " " } },
            "cliVersion" => b with { Target = t with { CliVersion = "" } },
            "cliHash" => b with { Target = t with { CliHash = "" } },
            "injectorPath" => b with { Target = t with { InjectorPath = "" } },
            "injectorVersion" => b with { Target = t with { InjectorVersion = "" } },
            "injectorHash" => b with { Target = t with { InjectorHash = new string('g', 64) } },
            "protocol" => b with { Target = t with { Protocol = "" } },
            "allDefault" => b with { Target = new(0, 0, "", 0, "", "", "", "", "", "", "") },
            "nullTarget" => b with { Target = null! },
            "head" => b with { Head = "" },
            "vm" => b with { Vm = Guid.Empty },
            "run" => b with { Run = Guid.Empty },
            "host" => b with { Host = Guid.Empty },
            "nonce" => b with { Challenge = "" },
            "shortNonce" => b with { Challenge = "agent-bridge-qualify:" + new string('a', 8) },
            _ => b,
        };
        if (field == "approval") { approval = Guid.Empty; }
        Assert.NotNull(run.Grants().Issue(approval, TimeSpan.FromMinutes(1)).Refusal);
        Assert.Empty(run.Journal.Entries);
    }

    [Fact]
    public void Expiry_is_journaled_so_a_wall_clock_rollback_cannot_revive_a_grant()
    {
        var run = new Run(); run.Issue(); var consumed = new Run(); consumed.Issue();
        var grant = Assert.IsType<QualificationGrant>(consumed.Consume().Grant);
        var clock = new WallClock(grant.Issued.AddMinutes(61));
        Assert.NotNull(new QualificationGrants(run.Trust, run.Journal, clock).Consume(run.Id, run.Trust.Binding.Target, run.Challenge).Refusal);
        Assert.False(new QualificationGrants(consumed.Trust, consumed.Journal, clock).IsCurrent(grant, consumed.Trust.Binding.Target, consumed.Challenge));
        clock.Now = grant.Issued.AddMinutes(30);
        Assert.NotNull(run.Grants().Consume(run.Id, run.Trust.Binding.Target, run.Challenge).Refusal);
        Assert.False(consumed.Current(grant));
        Assert.Equal(QualificationGrantState.Expired, run.Journal.Entries.Last().State);
        Assert.Equal(QualificationGrantState.Expired, consumed.Journal.Entries.Last().State);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void Revocation_requires_the_SRE_callers_own_disposable_binding(bool fleetHost)
    {
        var run = new Run(); run.Issue();
        if (fleetHost) { run.Trust.HostKind = QualificationHostKind.Fleet; }
        else { run.Trust.Binding = run.Trust.Binding with { Run = Guid.NewGuid() }; }
        Assert.NotNull(run.Grants().Revoke(run.Id).Refusal);
        Assert.Single(run.Journal.Entries);
    }
}
