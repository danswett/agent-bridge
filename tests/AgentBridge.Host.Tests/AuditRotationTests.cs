using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

// One person-signed rotation makes a legacy profile usable for grants (design/fleet-grants.md).
public sealed class AuditRotationTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("ab-rotate-").FullName;
    readonly P256KeyPair device = P256KeyPair.Generate(), person = P256KeyPair.Generate();
    readonly FakeTimeProvider clock = new(new(2026, 11, 21, 18, 0, 0, TimeSpan.Zero));
    string Folder => Path.Combine(root, "default");
    string AuditFolder => Path.Combine(Folder, "audit");
    string Retired => Path.Combine(AuditFolder, "retired");
    AgentAuditLog Log(Action<int>? step = null) => new(Folder, clock) { DeviceKey = device, RotationStep = step };
    bool Pinned(string by) => by == BlobFormat.Base64Url(person.Public);
    SignedCommand Signed(P256KeyPair? by = null, string kind = "user") => Sign(by ?? person, device, Log().Proposal(), clock.GetUtcNow(), kind);
    GrantStoreProblem Grants() => new GrantStore(Folder, device, Log(), new([], [], [], [], [], [], [], []), clock).TryGetUsable(Guid.NewGuid(), 1).Problem;
    Dictionary<string, string> Snapshot() => Directory.GetFiles(Folder, "*", SearchOption.AllDirectories).ToDictionary(f => f, f => Convert.ToHexString(File.ReadAllBytes(f)));
    static readonly AgentAuditEntry Ordinary = new("out", "fixture", "agent:fixture/session", [], "agent.send", "fixture", "sent");
    public void Dispose() { device.Dispose(); person.Dispose(); Directory.Delete(root, true); }

    internal static SignedCommand Sign(P256KeyPair signer, P256KeyPair device, object seal, DateTimeOffset now, string kind = "user", string? request = null)
    {
        var machine = Wire.DeviceId(device.Public);
        return SignedCommands.Sign(signer, device.Public, new CommandBody { V = 1, Action = AgentAuditLog.RotateAction,
            Request = request ?? Guid.NewGuid().ToString(), Principal = new CommandPrincipal { Kind = kind, Device = Wire.DeviceId(signer.Public) },
            Target = new CommandTarget { Machine = machine }, To = machine, IssuedAt = now.ToUnixTimeSeconds(),
            ExpiresAt = now.AddMinutes(5).ToUnixTimeSeconds(), Args = JsonSerializer.SerializeToElement(seal, JsonSerializerOptions.Web) });
    }

    // Tests of other features start from a profile a person has rotated.
    internal static string? Rotate(AgentAuditLog log, P256KeyPair device, DateTimeOffset now)
    {
        using var someone = P256KeyPair.Generate();
        return log.Rotate(Sign(someone, device, log.Proposal(), now), _ => true);
    }

    void Legacy() => Assert.True(Log().Append(Ordinary) && Log().Append(new("local", "fixture", "person:local", [], "session.launch", "fixture", "started")));

    void Pair()
    {
        var op = Guid.NewGuid();
        var gate = new GateAuditMetadata(1, op, Guid.Empty, 0, "intent", "local", "person-client", op, "none");
        var intent = new AgentAuditEntry("local", op.ToString(), "person:local", [], "session.stop", op.ToString(), "reserved", Gate: gate);
        Assert.True(Log().Append(intent) && Log().Append(intent with { Outcome = "ended", Gate = gate with { Phase = "terminal" } }));
    }

    [Fact]
    public void A_legacy_profile_becomes_usable_for_grants_through_one_rotation_and_native_gates_work_throughout()
    {
        Legacy();
        Pair();
        Assert.True(Log().Verify().Intact && Log().Verify(launch: true).Intact && AgentAuditLog.Adopted(AuditFolder));
        Assert.Equal(GrantStoreProblem.RotationRequired, Grants());
        var command = Signed();
        Assert.Null(Log().Rotate(command, Pinned));
        Assert.Equal(GrantStoreProblem.Missing, Grants());
        Assert.False(AgentAuditLog.Adopted(AuditFolder));
        Assert.Equal(2, Directory.GetFiles(Retired).Length);
        Assert.Null(Log().Rotate(command, Pinned));
        Assert.Equal("this profile's audit was already rotated", Log().Rotate(Signed(), Pinned));
        Pair();
        Assert.True(Log().Bootstrap() && Log().Verify().Intact && Log().Verify(launch: true).Intact);
        Assert.Equal(GrantStoreProblem.Missing, Grants());
        // Without the device key nothing can check the genesis, so the stream is not trusted.
        Assert.False(new AgentAuditLog(Folder, clock).Verify().Intact);
    }

    [Theory]
    [InlineData("agent"), InlineData("machine"), InlineData("unpinned"), InlineData("forged"), InlineData("expired"), InlineData("changed"), InlineData("history")]
    public void Only_a_pinned_persons_signature_over_the_current_pre_grant_audit_rotates_and_a_refusal_changes_nothing(string caller)
    {
        Legacy();
        using var other = P256KeyPair.Generate();
        var command = caller switch { "agent" => Signed(kind: "agent"), "machine" => Signed(device), "unpinned" => Signed(other), _ => Signed() };
        if (caller == "forged") { command = new SignedCommand { Body = command.Body, By = command.By, Mac = Signed().Mac }; }
        if (caller == "expired") { clock.Advance(TimeSpan.FromMinutes(6)); }
        if (caller == "changed") { Assert.True(Log().Append(Ordinary)); }
        // Rotating away grant history would reset its tombstones and budgets.
        if (caller == "history") { File.WriteAllBytes(Path.Combine(Folder, GrantStore.FileName), [1]); }
        var before = Snapshot();
        Assert.NotNull(Log().Rotate(command, caller == "machine" ? _ => true : Pinned));
        Assert.Equal(before, Snapshot());
        Assert.Null(AgentAuditLog.Rotation(Folder, device));
    }

    [Theory]
    [InlineData("none"), InlineData("genesis-length"), InlineData("genesis-by-machine"), InlineData("genesis-later"), InlineData("genesis-null"), InlineData("command-null"),
        InlineData("retired-edited"), InlineData("retired-missing"), InlineData("active-rollback")]
    public void Grants_trust_only_a_stream_whose_signed_genesis_and_any_retired_file_still_match(string fault)
    {
        Legacy();
        var legacy = Snapshot();
        var (seal, request) = (Log().Proposal(), Guid.NewGuid().ToString());
        Assert.Null(Log().Rotate(Sign(person, device, seal, clock.GetUtcNow(), request: request), Pinned));
        Assert.True(Log().Append(Ordinary));
        var day = Directory.GetFiles(AuditFolder, "agents-*.jsonl").Single();
        var retired = Directory.GetFiles(Retired, "agents-*").Single();
        var lines = File.ReadAllLines(day);
        var genesis = JsonNode.Parse(lines[0])!;
        if (fault == "genesis-length") { genesis["rotation"]!["length"] = (long)genesis["rotation"]!["length"]! + 1; }
        if (fault == "genesis-by-machine")
        { genesis["rotation"]!["command"] = JsonSerializer.SerializeToNode(Sign(device, device, seal, clock.GetUtcNow(), request: request), JsonSerializerOptions.Web); }
        if (fault is "genesis-null" or "command-null") { genesis["rotation"]!["command"] = null; if (fault == "genesis-null") { genesis["rotation"] = null; } }
        if (fault.StartsWith("genesis-", StringComparison.Ordinal) || fault == "command-null") { lines[0] = genesis.ToJsonString(); }
        if (fault == "genesis-later") { lines = [lines[0], lines[0], .. lines[1..]]; }
        File.WriteAllText(day, string.Join('\n', lines) + "\n");
        if (fault == "retired-edited") { File.AppendAllText(retired, "\n"); }
        if (fault == "retired-missing") { File.Delete(retired); }
        if (fault == "active-rollback") { foreach (var (file, hex) in legacy.Where(f => f.Key.Contains("agents-"))) { File.WriteAllBytes(file, Convert.FromHexString(hex)); } }
        var trusted = fault is "none" or "retired-missing";
        Assert.Equal(fault == "genesis-later" ? GrantStoreProblem.AuditIncomplete : trusted ? GrantStoreProblem.Missing : GrantStoreProblem.RotationRequired, Grants());
        Assert.Equal(trusted, Log().Verify().Intact);
    }

    [Theory]
    [InlineData(0), InlineData(1), InlineData(2), InlineData(3), InlineData(4), InlineData(5), InlineData(6)]
    public void A_crash_between_rotation_steps_finishes_that_rotation_once_at_the_next_write(int step)
    {
        Legacy();
        var command = Signed();
        Assert.NotNull(Log(s => { if (s == step) { throw new IOException("crash"); } }).Rotate(command, Pinned));
        Assert.True(File.Exists(Path.Combine(AuditFolder, "rotation.pending")));
        // A host that cannot verify the pending rotation refuses to write rather than skip it.
        Assert.False(new AgentAuditLog(Folder, clock).Append(Ordinary) || new AgentAuditLog(Folder, clock).Bootstrap());
        Assert.True(step % 2 == 0 ? Log().Append(Ordinary) : Log().Bootstrap());
        Assert.False(File.Exists(Path.Combine(AuditFolder, "rotation.pending")));
        Assert.Equal(2, Directory.GetFiles(Retired).Length);
        Assert.Null(Log().Rotate(command, Pinned));
        Assert.Equal("this profile's audit was already rotated", Log().Rotate(Signed(), Pinned));
        Assert.Equal(GrantStoreProblem.Missing, Grants());
        Assert.True(Log().Verify().Intact && Log().Verify(launch: true).Intact);
    }

    // A revocation retention moved away still binds: with no store and no row left to see, rotating must not let grants
    // start over (a fresh store could reissue the revoked id). Only provably absent history rotates.
    [Fact]
    public void Rotation_refuses_retained_grant_history_even_when_the_store_and_old_rows_are_missing()
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
        Assert.NotNull(Log().Rotate(Signed(), Pinned));
        Assert.Equal(before, Snapshot());
        Assert.Equal(GrantStoreProblem.AuditIncomplete, new GrantStore(Folder, device, Log(), new([], [], [], [], [], [], [], []), clock).TryGetUsable(revoked, 1).Problem);
    }

    [Fact]
    public void Archive_finishes_a_pending_rotation_before_it_moves_bytes_the_rotation_retires()
    {
        Legacy();
        Pair();
        clock.Advance(TimeSpan.FromDays(AgentAuditLog.KeepDays + 1));
        Assert.True(Log().Append(Ordinary));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.NotNull(Log(s => { if (s == 0) { throw new IOException("crash"); } }).Rotate(Signed(), Pinned));
        Log().Archive(clock.GetUtcNow());
        Assert.True(Log().Append(Ordinary));
        Assert.NotNull(AgentAuditLog.Rotation(Folder, device));
        Assert.Equal(GrantStoreProblem.Missing, Grants());
    }
}
