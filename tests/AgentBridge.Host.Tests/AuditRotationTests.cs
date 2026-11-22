using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// The first person-confirmed grant issue rotates a pre-grant audit once (design/fleet-grants.md, owner Nov 21 21:41 PT).
public sealed class AuditRotationTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("ab-rotate-").FullName;
    readonly P256KeyPair device = P256KeyPair.Generate();
    readonly FakeTimeProvider clock = new(new(2026, 11, 21, 18, 0, 0, TimeSpan.Zero));
    const string Device = "0123456789abcdef0123456789abcdef";
    static readonly GrantScope Scope = new([Device], ["repo"], ["copilot"], ["small"], ["default"], ["medium"], ["default"], ["ask"]);
    string Folder => Path.Combine(root, "default");
    string AuditFolder => Path.Combine(Folder, "audit");
    string Retired => Path.Combine(AuditFolder, "retired");
    string Journal => Path.Combine(AuditFolder, "rotation.pending");
    AgentAuditLog Log(Action<int>? step = null) => new(Folder, clock) { DeviceKey = device, RotationStep = step };
    GrantStore Store(Action<int>? step = null) => new(Folder, device, Log(step), Scope, clock);
    GrantStoreProblem Grants() => Store().TryGetUsable(Guid.NewGuid(), 1).Problem;
    GrantDocument Grant() => new(1, Guid.NewGuid(), "123", new("123", Device, "AQ", clock.GetUtcNow(), new('a', 64)), new(Device, "default"),
        [new("123", Device, Guid.NewGuid().ToString(), "AAAAAAAAAAAAAAAAAAAAAA", new(42, clock.GetUtcNow()))], ["launch", "stop"], Scope,
        new(3, 10, 6), clock.GetUtcNow(), 1, Roles: [], StopTargets: []);
    Dictionary<string, string> Snapshot() => Directory.GetFiles(Folder, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".lock", StringComparison.Ordinal))
        .ToDictionary(f => f, f => Convert.ToHexString(File.ReadAllBytes(f)));
    static Action<int> Crash(int step) => s => { if (s == step) { throw new IOException("crash"); } };
    static readonly AgentAuditEntry Ordinary = new("out", "fixture", "agent:fixture/session", [], "agent.send", "fixture", "sent");
    public void Dispose() { device.Dispose(); Directory.Delete(root, true); }

    // Tests of other features start from a profile whose first grant issue rotated it.
    internal static string? Rotate(AgentAuditLog log, P256KeyPair device) => log.RotateForFirstGrant(device, Guid.NewGuid());

    void Legacy() => Assert.True(Log().Append(Ordinary) && Log().Append(new("local", "fixture", "person:local", [], "session.launch", "fixture", "started")));

    void Pair()
    {
        var op = Guid.NewGuid();
        var gate = new GateAuditMetadata(1, op, Guid.Empty, 0, "intent", "local", "person-client", op, "none");
        var intent = new AgentAuditEntry("local", op.ToString(), "person:local", [], "session.stop", op.ToString(), "reserved", Gate: gate);
        Assert.True(Log().Append(intent) && Log().Append(intent with { Outcome = "ended", Gate = gate with { Phase = "terminal" } }));
    }

    [Fact]
    public void The_first_issue_rotates_a_legacy_profile_once_and_native_gates_work_throughout()
    {
        Legacy();
        Pair();
        Assert.True(Log().Verify().Intact && Log().Verify(launch: true).Intact && AgentAuditLog.Adopted(AuditFolder));
        Assert.Equal(GrantStoreProblem.RotationRequired, Grants());
        var legacy = new FileInfo(Directory.GetFiles(AuditFolder, "agents-*.jsonl").Single()).Length;
        var first = Grant();
        var issued = Store().Issue(first);
        Assert.True(issued.Usable && issued.Rotated);
        Assert.False(AgentAuditLog.Adopted(AuditFolder));
        Assert.Equal(2, Directory.GetFiles(Retired).Length);
        var genesis = JsonNode.Parse(File.ReadLines(Directory.GetFiles(AuditFolder, "agents-*.jsonl").Single()).First())!["rotation"]!;
        Assert.Equal(legacy, (long)genesis["length"]!);
        Assert.Equal(AgentAuditLog.FirstGrantReason, (string)genesis["record"]!["reason"]!);
        Assert.Equal(first.Id.ToString(), (string)genesis["record"]!["grant"]!);
        var second = Store().Issue(Grant());
        Assert.True(second.Usable && !second.Rotated);
        Assert.Equal(2, Directory.GetFiles(Retired).Length);
        Assert.Equal("this profile's audit was already rotated", Rotate(Log(), device));
        Pair();
        Assert.True(Log().Bootstrap() && Log().Verify().Intact && Log().Verify(launch: true).Intact);
        Assert.True(Store().TryGetUsable(first.Id, 1).Usable);
        // Without the device key nothing can check the genesis, so the stream is not trusted.
        Assert.False(new AgentAuditLog(Folder, clock).Verify().Intact);
    }

    // Anything but a clean pre-grant log, or a rotation whose journal and retired bytes still verify, stays RotationRequired.
    [Theory]
    [InlineData("torn"), InlineData("mismatch"), InlineData("forged"), InlineData("other-key"), InlineData("invalid-grant")]
    public void A_torn_or_mismatched_legacy_log_or_unverifiable_rotation_refuses_the_first_issue(string fault)
    {
        Legacy();
        if (fault == "torn") { File.AppendAllText(Directory.GetFiles(AuditFolder, "agents-*.jsonl").Single(), "{\"torn\""); }
        if (fault is "mismatch" or "forged") { Assert.NotNull(Rotate(Log(Crash(fault == "mismatch" ? 1 : 0)), device)); }
        if (fault == "other-key") { using var other = P256KeyPair.Generate(); Assert.NotNull(Rotate(Log(Crash(0)), other)); }
        if (fault == "mismatch") { File.AppendAllText(Directory.GetFiles(Retired, "agents-*").Single(), "\n"); }
        if (fault == "forged") { File.WriteAllText(Journal, File.ReadAllText(Journal).Replace("\"mac\":\"", "\"mac\":\"0")); }
        var before = Snapshot();
        var grant = fault == "invalid-grant" ? Grant() with { Caps = new(0, 0, 0) } : Grant();
        Assert.Equal(fault == "invalid-grant" ? GrantStoreProblem.Malformed : GrantStoreProblem.RotationRequired, Store().Issue(grant).Problem);
        Assert.Equal(before, Snapshot());
        Assert.Null(AgentAuditLog.Rotation(Folder, device));
        Assert.False(File.Exists(Path.Combine(Folder, GrantStore.FileName)));
    }

    [Theory]
    [InlineData("none"), InlineData("genesis-length"), InlineData("genesis-reason"), InlineData("genesis-later"), InlineData("genesis-null"), InlineData("record-null"),
        InlineData("genesis-torn"), InlineData("retired-edited"), InlineData("retired-missing"), InlineData("active-rollback")]
    public void Grants_trust_only_a_stream_whose_genesis_and_any_retired_file_still_match(string fault)
    {
        Legacy();
        var legacy = Snapshot();
        Assert.Null(Rotate(Log(), device));
        if (fault != "genesis-torn") { Assert.True(Log().Append(Ordinary)); }
        var day = Directory.GetFiles(AuditFolder, "agents-*.jsonl").Single();
        var retired = Directory.GetFiles(Retired, "agents-*").Single();
        var lines = File.ReadAllLines(day);
        var genesis = JsonNode.Parse(lines[0])!;
        if (fault == "genesis-length") { genesis["rotation"]!["length"] = (long)genesis["rotation"]!["length"]! + 1; }
        if (fault == "genesis-reason") { genesis["rotation"]!["record"]!["reason"] = "person"; }
        if (fault is "genesis-null" or "record-null") { genesis["rotation"]!["record"] = null; if (fault == "genesis-null") { genesis["rotation"] = null; } }
        if (fault.StartsWith("genesis-", StringComparison.Ordinal) || fault == "record-null") { lines[0] = genesis.ToJsonString(); }
        if (fault == "genesis-later") { lines = [lines[0], lines[0], .. lines[1..]]; }
        File.WriteAllText(day, string.Join('\n', lines) + (fault == "genesis-torn" ? "" : "\n"));
        if (fault == "retired-edited") { File.AppendAllText(retired, "\n"); }
        if (fault == "retired-missing") { File.Delete(retired); }
        if (fault == "active-rollback") { foreach (var (file, hex) in legacy.Where(f => f.Key.Contains("agents-"))) { File.WriteAllBytes(file, Convert.FromHexString(hex)); } }
        var trusted = fault is "none" or "retired-missing";
        Assert.Equal(fault == "genesis-later" ? GrantStoreProblem.AuditIncomplete : trusted ? GrantStoreProblem.Missing : GrantStoreProblem.RotationRequired, Grants());
        Assert.Equal(trusted, Log().Verify().Intact);
    }

    // A crash anywhere in the rotate-then-issue sequence leaves the journal; the next write or issue finishes that rotation
    // once, and a refused issue retried later succeeds without a second rotation.
    [Theory]
    [InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(4), InlineData(5), InlineData(6)]
    public void A_crash_between_rotation_steps_is_finished_once_by_the_next_write_or_issue(int step)
    {
        Legacy();
        var grant = Grant();
        Assert.Equal(GrantStoreProblem.RotationRequired, Store(Crash(step)).Issue(grant).Problem);
        Assert.True(File.Exists(Journal));
        // A host that cannot verify the pending rotation refuses to write rather than skip it.
        Assert.False(new AgentAuditLog(Folder, clock).Append(Ordinary) || new AgentAuditLog(Folder, clock).Bootstrap());
        if (step % 2 == 0) { Assert.True(Log().Append(Ordinary)); }
        var issued = Store().Issue(grant);
        Assert.True(issued.Usable);
        Assert.Equal(step % 2 == 1, issued.Rotated);
        Assert.False(File.Exists(Journal));
        Assert.Equal(2, Directory.GetFiles(Retired).Length);
        Assert.Equal("this profile's audit was already rotated", Rotate(Log(), device));
        Assert.True(Log().Verify().Intact && Log().Verify(launch: true).Intact);
    }

    // A revocation retention moved away still binds: with no store and no row left to see, the first issue must not rotate
    // and start grants over (it could reissue the revoked id). Only provably absent history rotates.
    [Fact]
    public void Retained_grant_history_is_never_rotated_away_so_a_revoked_id_cannot_be_reissued()
    {
        Legacy();
        var (revoked, op) = (Guid.NewGuid(), Guid.NewGuid());
        var gate = new GateAuditMetadata(1, op, Guid.Empty, 0, "intent", "local", "person-client", revoked, "none", GrantBinding: new string('a', 64));
        var intent = new AgentAuditEntry("local", op.ToString(), "person:local", [], "grant.revoke", revoked.ToString(), "reserved", Gate: gate) { Grant = revoked.ToString() };
        Assert.True(Log().Append(intent) && Log().Append(intent with { Outcome = "revoked", Gate = gate with { Phase = "terminal" } }));
        clock.Advance(TimeSpan.FromDays(AgentAuditLog.KeepDays + 1));
        Legacy();
        clock.Advance(TimeSpan.FromDays(1));
        Assert.Equal(1, Log().Archive(clock.GetUtcNow()));
        Assert.Equal(GrantStoreProblem.AuditIncomplete, Grants());
        var before = Snapshot();
        Assert.NotNull(Rotate(Log(), device));
        Assert.Equal(before, Snapshot());
        Assert.Equal(GrantStoreProblem.AuditIncomplete, Store().Issue(Grant() with { Id = revoked }).Problem);
        Assert.Null(AgentAuditLog.Rotation(Folder, device));
    }

    [Fact]
    public void Archive_finishes_a_pending_rotation_before_it_moves_bytes_the_rotation_retires()
    {
        Legacy();
        Pair();
        clock.Advance(TimeSpan.FromDays(AgentAuditLog.KeepDays + 1));
        Assert.True(Log().Append(Ordinary));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.NotNull(Rotate(Log(Crash(0)), device));
        Log().Archive(clock.GetUtcNow());
        Assert.True(Log().Append(Ordinary));
        Assert.NotNull(AgentAuditLog.Rotation(Folder, device));
        Assert.Equal(GrantStoreProblem.Missing, Grants());
    }
}
