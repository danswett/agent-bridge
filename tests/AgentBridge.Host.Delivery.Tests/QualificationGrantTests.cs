using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Host.Delivery;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class QualificationGrantTests
{
    sealed class WallClock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now = now; public override DateTimeOffset GetUtcNow() => Now; }
    sealed class SteppingClock(params DateTimeOffset[] times) : TimeProvider
    { int next; public override DateTimeOffset GetUtcNow() => times[Math.Min(next++, times.Length - 1)]; }
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
        public DateTimeOffset HighWater = DateTimeOffset.MinValue;
        public bool Available = true;
        public Action? Racing; // Runs once inside the next nonce lookup or append, before it reads.
        public Action? TimeRacing; // Competes before the next time checkpoint is flushed.
        void Race() { var race = Racing; Racing = null; race?.Invoke(); }
        public QualificationGrant? Find(Guid id) { lock (gate) { return Entries.LastOrDefault(x => x.Id == id); } }
        public QualificationGrant? FindChallenge(string nonce) { Race(); lock (gate) { return Entries.LastOrDefault(x => x.Binding.Challenge == nonce); } }
        public DateTimeOffset ObserveTime(DateTimeOffset now)
        { lock (gate) { var race = TimeRacing; TimeRacing = null; race?.Invoke(); if (!Available) { throw new IOException("receipt store unavailable"); } return HighWater = now > HighWater ? now : HighWater; } }
        sealed record Snapshot(List<QualificationGrant> Entries, DateTimeOffset HighWater);
        public Journal Restart()
        {
            var data = JsonSerializer.Deserialize<Snapshot>(JsonSerializer.Serialize(new Snapshot(Entries, HighWater)))!;
            var copy = new Journal { HighWater = data.HighWater }; copy.Entries.AddRange(data.Entries); return copy;
        }
        public bool TryAppend(QualificationGrant? expected, QualificationGrant next, DateTimeOffset observedAt)
        {
            lock (gate)
            {
                if (!Available) { throw new IOException("receipt store unavailable"); }
                Race();
                if (observedAt < HighWater || Find(next.Id) != expected || FindChallenge(next.Binding.Challenge) != expected) { return false; }
                Entries.Add(next); HighWater = observedAt; return true;
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
        var challenge = changeChallenge ? new QueueQualification.Challenge("agent-bridge-qualify:" + new string('b', 64)) : run.Challenge;
        var target = changeChallenge ? run.Trust.Binding.Target : run.Trust.Binding.Target with { StartedAt = 101 };
        Assert.NotNull(run.Grants().Consume(run.Id, target, challenge).Refusal);
        Assert.Single(run.Journal.Entries);
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public void A_consumed_grant_is_not_current_for_another_target_or_challenge(bool changeChallenge)
    {
        var run = new Run(); run.Issue();
        var grant = Assert.IsType<QualificationGrant>(run.Consume().Grant);
        var challenge = changeChallenge ? new QueueQualification.Challenge("agent-bridge-qualify:" + new string('b', 64)) : run.Challenge;
        var target = changeChallenge ? run.Trust.Binding.Target : run.Trust.Binding.Target with { ProcessId = 43 };
        Assert.False(run.Grants().IsCurrent(grant, target, challenge));
        Assert.NotNull(run.Consume().Refusal);
        Assert.NotNull(run.Grants().Consume(run.Id, target, challenge).Refusal);
    }

    [Fact]
    public void Journal_restart_preserves_target_nonce_and_consumption_without_reference_identity()
    {
        var run = new Run(); var issued = run.Issue();
        var grant = Assert.IsType<QualificationGrant>(run.Consume().Grant);
        var restored = run.Journal.Restart();
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
        var journal = history == "restart" ? run.Journal.Restart() : run.Journal;
        var count = journal.Entries.Count;
        run.Trust.Binding = run.Trust.Binding with { Target = run.Trust.Binding.Target with { ProcessId = 43 } };
        Assert.NotNull(new QualificationGrants(run.Trust, journal, run.Clock).Issue(Guid.NewGuid(), TimeSpan.FromMinutes(1)).Refusal);
        Assert.Equal(count, journal.Entries.Count);
    }

    [Theory]
    [InlineData("pid")][InlineData("started")][InlineData("console")][InlineData("consoleZero")][InlineData("mode")][InlineData("cliPath")]
    [InlineData("cliVersion")][InlineData("cliHash")][InlineData("injectorPath")][InlineData("injectorVersion")][InlineData("injectorHash")]
    [InlineData("protocol")][InlineData("allDefault")][InlineData("nullTarget")][InlineData("head")][InlineData("vm")][InlineData("run")]
    [InlineData("host")][InlineData("nonce")][InlineData("shortNonce")][InlineData("approval")][InlineData("consoleOverflow")]
    public void Every_empty_zero_or_default_binding_field_refuses_issue(string field)
    {
        var run = new Run(); var b = run.Trust.Binding; var t = b.Target; var approval = field == "approval" ? Guid.Empty : run.Id;
        run.Trust.Binding = field switch
        {
            "pid" => b with { Target = t with { ProcessId = 0 } },
            "started" => b with { Target = t with { StartedAt = 0 } },
            "console" => b with { Target = t with { ConsoleId = "" } },
            "consoleZero" => b with { Target = t with { ConsoleId = "0:0:0" } },
            "consoleOverflow" => b with { Target = t with { ConsoleId = "200:4294967296:101" } },
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
        Assert.All(new[] { run.Journal, consumed.Journal }, j => Assert.Equal(QualificationGrantState.Expired, j.Entries.Last().State));
    }

    [Fact]
    public void An_expiry_reached_mid_check_is_either_unseen_or_journaled()
    {
        var run = new Run(); run.Issue(); var grant = Assert.IsType<QualificationGrant>(run.Consume().Grant);
        var clock = new SteppingClock(grant.Expires.AddTicks(-1), grant.Expires);
        var current = new QualificationGrants(run.Trust, run.Journal, clock).IsCurrent(grant, run.Trust.Binding.Target, run.Challenge);
        Assert.True(current || run.Journal.Entries.Last().State == QualificationGrantState.Expired);
    }

    [Fact]
    public void An_observed_expiry_is_journaled_even_when_another_controller_consumes_first()
    {
        var run = new Run(); run.Issue(); run.Journal.TimeRacing = () => Assert.Null(run.Consume().Refusal);
        var late = new QualificationGrants(run.Trust, run.Journal, new WallClock(run.Clock.GetUtcNow().AddMinutes(61)));
        Assert.NotNull(late.Consume(run.Id, run.Trust.Binding.Target, run.Challenge).Refusal);
        Assert.Equal(QualificationGrantState.Expired, run.Journal.Entries.Last().State);
    }

    [Theory]
    [InlineData("owner")][InlineData("fleet")][InlineData("foreign")]
    public void Revocation_requires_SRE_CLI_with_the_grants_own_disposable_binding(string caller)
    {
        var run = new Run(); run.Issue();
        if (caller == "owner") { run.Trust.Access = QualificationAccess.OwnerApproval; }
        if (caller == "fleet") { run.Trust.HostKind = QualificationHostKind.Fleet; }
        if (caller == "foreign") { run.Trust.Binding = run.Trust.Binding with { Run = Guid.NewGuid() }; }
        Assert.NotNull(run.Grants().Revoke(run.Id).Refusal);
        Assert.Single(run.Journal.Entries);
    }

    public static IEnumerable<object[]> StateClockRestartCases()
    {
        foreach (var state in Enum.GetValues<QualificationGrantState>())
        foreach (var minutes in new[] { -1, 29, 30, 59, 60, 61 })
        foreach (var restart in new[] { false, true })
        foreach (var current in new[] { false, true })
        foreach (var mismatch in new[] { false, true })
        { yield return new object[] { (int)state, minutes, restart, current, mismatch }; }
    }

    [Theory]
    [MemberData(nameof(StateClockRestartCases))]
    public void Every_state_clock_restart_combination_refuses_stale_or_mismatched_authority(
        int state, int minutes, bool restart, bool current, bool mismatch)
    {
        var run = new Run(); run.Issue(); var grant = Assert.IsType<QualificationGrant>(run.Consume().Grant);
        var clock = new WallClock(grant.Issued.AddMinutes(30)); var controller = new QualificationGrants(run.Trust, run.Journal, clock);
        Assert.True(controller.IsCurrent(grant, run.Trust.Binding.Target, run.Challenge));
        var old = grant with { State = (QualificationGrantState)state }; run.Journal.Entries.Add(old);
        var journal = restart ? run.Journal.Restart() : run.Journal;
        if (restart) { controller = new(run.Trust, journal, clock); }
        clock.Now = grant.Issued.AddMinutes(minutes);
        var target = mismatch ? run.Trust.Binding.Target with { ProcessId = 43 } : run.Trust.Binding.Target;
        var accepted = current ? controller.IsCurrent(old, target, run.Challenge)
            : controller.Consume(run.Id, target, run.Challenge).Refusal is null;
        Assert.Equal(!mismatch && minutes is >= 30 and < 60
            && old.State == (current ? QualificationGrantState.Consumed : QualificationGrantState.Issued), accepted);
        if (minutes >= 60 && old.State is QualificationGrantState.Issued or QualificationGrantState.Consumed)
        {
            Assert.Equal(QualificationGrantState.Expired, journal.Entries.Last().State);
            clock.Now = grant.Issued.AddMinutes(30);
            var restored = journal.Restart();
            var after = new QualificationGrants(run.Trust, restored, clock);
            Assert.False(after.IsCurrent(grant, run.Trust.Binding.Target, run.Challenge));
            Assert.NotNull(after.Consume(run.Id, run.Trust.Binding.Target, run.Challenge).Refusal);
        }
    }

    [Theory]
    [InlineData("issue")][InlineData("consume")][InlineData("revoke")]
    public void Restart_refuses_issue_consume_and_revoke_below_the_journal_clock_high_water(string operation)
    {
        var run = new Run(); run.Issue(); var grant = Assert.IsType<QualificationGrant>(run.Consume().Grant);
        var clock = new WallClock(grant.Issued.AddMinutes(30));
        Assert.True(new QualificationGrants(run.Trust, run.Journal, clock).IsCurrent(grant, run.Trust.Binding.Target, run.Challenge));
        if (operation == "consume") { run.Journal.Entries.Add(grant with { State = QualificationGrantState.Issued }); }
        var restored = run.Journal.Restart();
        clock.Now = grant.Issued.AddMinutes(29); var controller = new QualificationGrants(run.Trust, restored, clock);
        run.Trust.Binding = operation == "issue" ? run.Trust.Binding with { Challenge = "agent-bridge-qualify:" + new string('b', 64) } : run.Trust.Binding;
        var result = operation switch { "issue" => controller.Issue(Guid.NewGuid(), TimeSpan.FromMinutes(1)),
            "consume" => controller.Consume(run.Id, run.Trust.Binding.Target, run.Challenge), _ => controller.Revoke(run.Id) };
        Assert.NotNull(result.Refusal); Assert.Equal(run.Journal.Entries.Count, restored.Entries.Count);
    }

    [Fact]
    public void Revocation_retries_when_a_competing_consume_wins_the_append()
    {
        var run = new Run(); run.Issue(); run.Journal.Racing = () => Assert.Null(run.Consume().Refusal);
        Assert.Null(run.Grants().Revoke(run.Id).Refusal);
        Assert.Equal(QualificationGrantState.Revoked, run.Journal.Entries.Last().State);
    }

    [Theory]
    [InlineData("issue")][InlineData("consume")][InlineData("revoke")]
    public void A_clock_checkpoint_winning_before_append_fences_the_older_operation(string operation)
    {
        var run = new Run(); run.Issue(); var count = run.Journal.Entries.Count;
        if (operation == "issue") { run.Trust.Binding = run.Trust.Binding with { Challenge = "agent-bridge-qualify:" + new string('b', 64) }; }
        run.Journal.Racing = () => run.Journal.ObserveTime(run.Clock.GetUtcNow().AddMinutes(30));
        var result = operation switch { "issue" => run.Grants().Issue(Guid.NewGuid(), TimeSpan.FromMinutes(1)),
            "consume" => run.Consume(), _ => run.Grants().Revoke(run.Id) };
        Assert.NotNull(result.Refusal); Assert.Equal(count, run.Journal.Entries.Count);
    }
}
