using AgentBridge.Host;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using System.Text.Json.Nodes;
using System.Security.AccessControl;
using Xunit;

public sealed class GrantStoreTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("ab-grants-").FullName;
    readonly P256KeyPair key = P256KeyPair.Generate();
    readonly FakeTimeProvider clock = new(new(2026, 11, 21, 18, 0, 0, TimeSpan.Zero));
    const string Device = "0123456789abcdef0123456789abcdef";
    static readonly GrantScope Scope = new([Device], ["repo"], ["copilot"], ["small"], ["default"], ["medium"], ["default"], ["ask"]);
    string Folder => Path.Combine(root, "default");
    string PathName => Path.Combine(Folder, GrantStore.FileName);
    AgentAuditLog Audit => new(Folder, clock) { DeviceKey = key };
    bool rotated;
    GrantStore Open(P256KeyPair? signingKey = null, Action<string, byte[]>? write = null)
    {
        if (!rotated) { rotated = true; Assert.Null(AuditRotationTests.Rotate(Audit, key)); }
        return new(Folder, signingKey ?? key, Audit, Scope, clock, write);
    }
    GrantDocument Grant() => new(1, Guid.NewGuid(), "123",
        new("123", Device, "AQ", clock.GetUtcNow(), new('a', 64)), new(Device, "default"),
        [new("123", Device, Guid.NewGuid().ToString(), "AAAAAAAAAAAAAAAAAAAAAA", new(42, clock.GetUtcNow()))],
        ["launch", "stop"], Scope,
        new(3, 10, 6), clock.GetUtcNow(), 1, Roles: [], StopTargets: []);
    GrantDocument Issued() { var grant = Grant(); Assert.True(Open().Issue(grant).Usable); return grant; }
    // Rotation anchors both streams, so re-adoption needs both heads gone.
    void DropHeads() { foreach (var name in new[] { "agents.head", "launches.head" }) { File.Delete(Path.Combine(Folder, "audit", name)); } }
    public void Dispose() { key.Dispose(); Directory.Delete(root, true); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Only_the_first_issue_rotates_a_legacy_profile_and_other_store_paths_refuse(bool bothStreams)
    {
        rotated = true;
        Assert.True(Audit.Append(new("out", "fixture", "agent:fixture/session", [], "agent.send", "fixture", "sent")));
        if (bothStreams) { Assert.True(Audit.Append(new("local", "fixture", "person:local", [], "session.launch", "fixture", "started"))); }
        var grant = Grant();
        Assert.True(Audit.Bootstrap() && AgentAuditLog.Adopted(Path.Combine(Folder, "audit")));
        Assert.Equal(GrantStoreProblem.RotationRequired, Open().TryGetUsable(grant.Id, 1).Problem);
        Assert.Equal(GrantStoreProblem.RotationRequired, Open().Revoke(grant.Id, 1).Problem);
        Assert.Equal(GrantStoreProblem.RotationRequired, Open().List().Problem);
        Assert.False(File.Exists(PathName) || Directory.Exists(Path.Combine(Folder, "audit", "retired")));
        Assert.True(Open().Issue(grant).Rotated);
        Assert.True(Open().TryGetUsable(grant.Id, 1).Usable);
    }

    [Fact]
    public void Malformed_adopted_gate_metadata_refuses_without_bootstrap_throwing()
    {
        var grant = Grant();
        Assert.True(Open().Issue(grant).Usable);
        var auditFolder = Path.Combine(Folder, "audit");
        var file = Directory.GetFiles(auditFolder, "agents-*.jsonl").Single();
        var lines = File.ReadAllLines(file);
        var row = JsonNode.Parse(lines[^1])!;
        row["gate"]!["sequence"] = 0;
        lines[^1] = row.ToJsonString();
        File.WriteAllLines(file, lines);
        DropHeads();
        Assert.True(Audit.Bootstrap());
        Assert.Equal(GrantStoreProblem.AuditIncomplete, Open().TryGetUsable(grant.Id, 1).Problem);
    }

    [Theory]
    [InlineData(false), InlineData(true)]
    public void Retiring_an_adopted_prefix_refuses_grants_but_preserves_non_grant_gates(bool legacy)
    {
        if (legacy) { Assert.True(Audit.Append(new("out", "fixture", "agent:fixture/session", [], "agent.send", "fixture", "sent"))); }
        var grant = Grant();
        Assert.True(Open().Issue(grant).Usable);
        var head = Path.Combine(Folder, "audit", "agents.head");
        if (!legacy) { DropHeads(); Assert.True(Audit.Bootstrap()); }
        clock.Advance(TimeSpan.FromDays(35));
        Assert.True(Audit.Append(new("out", "fixture", "agent:fixture/session", [], "agent.send", "fixture", "sent")));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.True(Audit.Append(new("out", "fixture", "agent:fixture/session", [], "agent.send", "fixture", "sent")));
        var op = Guid.NewGuid();
        Assert.True(Audit.Append(new("local", op.ToString(), "person:local", [], "session.stop", op.ToString(), "reserved", Gate: new(1, op, Guid.Empty, 0, "intent", "local", "person-client", op, "none"))));
        Assert.True(Audit.Bootstrap());
        Assert.Equal(GrantStoreProblem.AuditIncomplete, Open().TryGetUsable(grant.Id, 1).Problem);
    }

    [Fact]
    public void Deleted_heads_rebootstrap_over_gated_history_refuses_even_without_a_store()
    {
        var grant = Grant();
        Assert.True(Open().Issue(grant).Usable);
        var auditFolder = Path.Combine(Folder, "audit");
        DropHeads();
        Assert.True(Audit.Bootstrap());
        Assert.Equal(GrantStoreProblem.AuditIncomplete, Open().TryGetUsable(grant.Id, 1).Problem);
        File.Delete(PathName);
        Assert.Equal(GrantStoreProblem.AuditIncomplete, Open().Issue(Grant()).Problem);
    }

    [Fact]
    public void Issue_refuses_a_grant_the_G1_known_options_validator_refuses()
    {
        var grant = Grant() with { Scope = Scope with { Models = ["future-model"] } };
        Assert.True(GrantValidator.ValidateShape(grant).Valid);
        Assert.Equal(GrantRefusal.Identifier, GrantValidator.Validate(grant, Scope).Reason);
        Assert.Equal(GrantStoreProblem.Malformed, Open().Issue(grant).Problem);
        Assert.False(File.Exists(PathName));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Retired_audit_coverage_refuses_even_when_the_store_is_deleted(bool deleted)
    {
        var grant = Issued();
        Assert.True(Open().Revoke(grant.Id, 1).Usable);
        clock.Advance(TimeSpan.FromDays(35)); Assert.True(Audit.Append(new("local", "fixture", "person:local", [], "host.fixture", "profile", "seen")));
        clock.Advance(TimeSpan.FromDays(1)); Assert.True(Audit.Append(new("local", "fixture", "person:local", [], "host.fixture", "profile", "seen")));
        Assert.True(AgentAuditLog.RetainedThrough(Path.Combine(Folder, "audit")) > 0);
        Assert.True(GateAuditProjection.Load(Folder).Complete);
        if (deleted) { File.Delete(PathName); }
        Assert.Equal(GrantStoreProblem.AuditIncomplete, Open().Issue(grant).Problem);
        Assert.Equal(GrantStoreProblem.AuditIncomplete, Open().TryGetUsable(grant.Id, 1).Problem);
    }

    [Fact]
    public void Restart_preserves_expiry_and_tombstones_and_never_reissues_an_id()
    {
        var grant = Grant();
        var expired = Grant() with { ExpiresAt = clock.GetUtcNow().AddHours(1) };
        Assert.True(Open().Issue(grant).Usable && Open().Issue(expired).Usable);
        Assert.True(Open().Revoke(grant.Id, grant.Generation).Usable);
        Assert.Equal(GrantStoreProblem.Revoked, Open().TryGetUsable(grant.Id, 1).Problem);
        Assert.Equal(GrantStoreProblem.Revoked, Open().Issue(grant).Problem);
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal(GrantStoreProblem.Expired, Open().TryGetUsable(expired.Id, 1).Problem);
    }

    [Theory]
    [InlineData("corrupt", GrantStoreProblem.BadMac)]
    [InlineData("foreign", GrantStoreProblem.BadMac)]
    [InlineData("oversize", GrantStoreProblem.Oversize)]
    [InlineData("unreadable", GrantStoreProblem.Unreadable)]
    [InlineData("public", GrantStoreProblem.Unreadable)]
    [InlineData("missing", GrantStoreProblem.Missing)]
    public void Invalid_storage_never_becomes_an_empty_writable_store(string fault, GrantStoreProblem expected)
    {
        var grant = Issued();
        using var foreign = P256KeyPair.Generate();
        if (fault == "public" && OperatingSystem.IsWindows())
        { var file = new FileInfo(PathName); var acl = file.GetAccessControl(); acl.SetAccessRuleProtection(false, true); file.SetAccessControl(acl); }
        else if (fault == "public")
        { File.SetUnixFileMode(PathName, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead); }
        if (fault == "corrupt") { var bytes = File.ReadAllBytes(PathName); bytes[^1] ^= 1; File.WriteAllBytes(PathName, bytes); }
        if (fault == "oversize") { using var stream = File.Create(PathName); stream.SetLength(GrantStore.MaxBytes + 1); }
        if (fault is "missing" or "unreadable") { File.Delete(PathName); }
        if (fault == "unreadable") { Directory.CreateDirectory(PathName); }
        var store = Open(fault == "foreign" ? foreign : key);
        Assert.Equal(expected, store.TryGetUsable(grant.Id, 1).Problem);
        Assert.Equal(expected, store.Issue(Grant()).Problem);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Restoring_the_pre_revocation_store_and_agents_audit_refuses_when_adopted_or_gapped(bool adopted)
    {
        var grant = Issued();
        var auditFolder = Path.Combine(Folder, "audit");
        var head = Path.Combine(auditFolder, "agents.head");
        var old = new[] { PathName, head, Directory.GetFiles(auditFolder, "agents-*.jsonl").Single() }.ToDictionary(f => f, File.ReadAllBytes);
        Assert.True(Open().Revoke(grant.Id, 1).Usable);
        var op = Guid.NewGuid();
        var gate = new GateAuditMetadata(1, op, Guid.Empty, 0, "intent", "local", "person-client", op, "none", LaunchBinding: new('a', 64));
        var launch = new AgentAuditEntry("local", op.ToString(), "person:local", [], "session.launch", op.ToString(), "reserved", Gate: gate);
        Assert.True(adopted || Audit.Append(launch) && Audit.Append(launch with { Outcome = "started", Gate = gate with { Phase = "terminal" } }));
        foreach (var (file, bytes) in old) { File.WriteAllBytes(file, bytes); }
        if (adopted) { DropHeads(); Assert.True(Audit.Bootstrap() && AgentAuditLog.Adopted(auditFolder)); }
        Assert.True(Audit.Verify().Intact && Audit.Verify(launch: true).Intact);
        Assert.Equal(GrantStoreProblem.AuditIncomplete, Open().TryGetUsable(grant.Id, 1).Problem);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("header")]
    [InlineData("short-legacy")]
    [InlineData("sequence")]
    [InlineData("gate-case")]
    public void An_adoption_cutoff_cannot_resurrect_a_rolled_back_revoked_grant(string proof)
    {
        if (proof == "short-legacy") { Assert.True(Audit.Append(new("out", "fixture", "agent:fixture/session", [], "agent.send", "fixture", "sent"))); }
        var grant = Issued();
        var auditFolder = Path.Combine(Folder, "audit");
        var head = Path.Combine(auditFolder, "agents.head");
        var file = Directory.GetFiles(auditFolder, "agents-*.jsonl").Single();
        var old = new[] { PathName, head, file }.ToDictionary(f => f, File.ReadAllBytes);
        // Legacy history was rotated away before the first grant; the rollback below must refuse either way.
        Assert.True(Open().Revoke(grant.Id, 1).Usable);
        foreach (var (savedFile, bytes) in old) { File.WriteAllBytes(savedFile, bytes); }
        if (proof == "gate-case")
        {
            var lines = File.ReadAllLines(file);
            var previous = new string('0', 64);
            for (var i = 0; i < lines.Length; i++)
            {
                var row = JsonNode.Parse(lines[i])!;
                if (row["gate"] is { } gate) { row.AsObject().Remove("gate"); row["Gate"] = gate; }
                row["previousSha256"] = previous;
                lines[i] = row.ToJsonString();
                previous = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(lines[i]))).ToLowerInvariant();
            }
            File.WriteAllText(file, string.Join('\n', lines) + "\n");
        }
        DropHeads();
        Assert.True(Audit.Bootstrap());
        var json = JsonNode.Parse(File.ReadAllText(head))!;
        json["adoptedGatedRows"] = 0;
        if (proof == "empty") { json["adoptedPrefix"] = JsonNode.Parse("""{"root":"","rootSha256":"","tail":"","tailSha256":"","length":0,"sequence":0,"empty":true}"""); }
        else if (proof != "gate-case")
        {
            var header = File.ReadLines(file).First();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(header))).ToLowerInvariant();
            json["adoptedPrefix"] = new JsonObject { ["root"] = Path.GetFileName(file), ["rootSha256"] = hash,
                ["tail"] = Path.GetFileName(file), ["tailSha256"] = hash, ["length"] = System.Text.Encoding.UTF8.GetByteCount(header) + 1L,
                ["sequence"] = proof == "sequence" ? 1 : 0, ["empty"] = false };
        }
        File.WriteAllText(head, json.ToJsonString());
        Assert.True(Audit.Verify().Intact);
        Assert.Equal(GrantStoreProblem.AuditIncomplete, Open().TryGetUsable(grant.Id, 1).Problem);
        // Grants refuse; the native launch preflight stays available.
        Assert.True(Audit.Bootstrap());
    }

    [Fact]
    public void A_forged_maximal_legacy_cutoff_cannot_resurrect_a_rolled_back_revoked_grant()
    {
        Assert.True(Audit.Append(new("out", "fixture", "agent:fixture/session", [], "agent.send", "fixture", "sent")));
        var grant = Issued();
        var auditFolder = Path.Combine(Folder, "audit");
        var head = Path.Combine(auditFolder, "agents.head");
        var file = Directory.GetFiles(auditFolder, "agents-*.jsonl").Single();
        var honest = JsonNode.Parse(File.ReadAllText(head))!;
        var old = new[] { PathName, file }.ToDictionary(f => f, File.ReadAllBytes);
        // The rotated profile revokes; were a re-adopted cutoff trusted, the rollback below would resurrect the grant.
        Assert.True(Open().Revoke(grant.Id, 1).Usable);
        foreach (var (saved, bytes) in old) { File.WriteAllBytes(saved, bytes); }
        DropHeads();
        Assert.True(Audit.Bootstrap());
        // Copy the honest cutoff (header and agent.send, zero gates) onto the re-adopted head; only those fields change.
        var json = JsonNode.Parse(File.ReadAllText(head))!;
        foreach (var field in new[] { "adoptedGatedRows", "adoptedPrefix" }) { json[field] = honest[field]?.DeepClone(); }
        File.WriteAllText(head, json.ToJsonString());
        Assert.True(Audit.Verify().Intact);
        Assert.Equal(GrantStoreProblem.AuditIncomplete, Open().TryGetUsable(grant.Id, 1).Problem);
    }

    [Fact]
    public void Replaying_an_old_valid_mac_file_cannot_resurrect_revocation_in_a_running_or_restarted_receiver()
    {
        var grant = Grant();
        var store = Open();
        Assert.True(store.Issue(grant).Usable);
        var old = File.ReadAllBytes(PathName);
        Assert.True(store.Revoke(grant.Id, 1).Usable);
        File.WriteAllBytes(PathName, old);
        Assert.Equal(GrantStoreProblem.Generation, store.TryGetUsable(grant.Id, 1).Problem);
        Assert.Equal(GrantStoreProblem.Generation, Open().TryGetUsable(grant.Id, 1).Problem);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_install_leaves_a_durable_intent_and_refuses_even_after_the_disk_recovers(bool issue)
    {
        var grant = Issued();
        var failing = Open(write: (_, _) => throw new IOException("fixture"));
        Assert.Equal(GrantStoreProblem.WriteFailed, (issue ? failing.Issue(Grant()) : failing.Revoke(grant.Id, 1)).Problem);
        Assert.False(failing.TryGetUsable(grant.Id, 1).Usable);
        Assert.Equal(GrantStoreProblem.AuditIncomplete, Open().TryGetUsable(grant.Id, 1).Problem);
        Assert.False(Open().Issue(Grant()).Usable);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("incomplete")]
    [InlineData("all")]
    public void Missing_or_incomplete_audit_refuses_a_mac_valid_store(string fault)
    {
        var grant = Issued();
        var file = Directory.GetFiles(Path.Combine(Folder, "audit"), "agents-*.jsonl").Single();
        if (fault == "all") { Directory.Move(Path.Combine(Folder, "audit"), Path.Combine(root, "lost-audit")); }
        else if (fault == "incomplete") { File.AppendAllText(file, "{"); }
        else { File.Delete(file); }
        Assert.Equal(GrantStoreProblem.AuditIncomplete, Open().TryGetUsable(grant.Id, 1).Problem);
    }

    [Theory]
    [InlineData("version", GrantStoreProblem.Version)]
    [InlineData("generation", GrantStoreProblem.Malformed)]
    [InlineData("unknown", GrantStoreProblem.Malformed)]
    public void Mac_valid_unsupported_or_malformed_formats_refuse(string field, GrantStoreProblem expected)
    {
        var grant = Issued();
        var json = JsonNode.Parse(File.ReadAllBytes(PathName).AsSpan(32))!;
        json[field] = field == "generation" ? 0 : 2;
        var body = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
        DeviceMacFile.Write(PathName, [.. DeviceMacFile.Mac(body, key, "agent-bridge fleet grant store v1"u8), .. body], _ => { });
        Assert.Equal(expected, Open().TryGetUsable(grant.Id, 1).Problem);
    }

    [Fact]
    public void Revoke_past_the_lifetime_intent_cap_always_succeeds_is_idempotent_and_survives_restart()
    {
        var grants = Enumerable.Range(0, 256).Select(_ => Grant()).ToArray();
        Assert.All(grants, g => Assert.True(Open().Issue(g).Usable));
        Assert.True(Open().Revoke(grants[0].Id, 1).Usable && Open().Revoke(grants[0].Id, 1).Usable && Open().Revoke(grants[1].Id, 1).Usable);
        Assert.Equal(GrantStoreProblem.Oversize, Open().Issue(Grant()).Problem);
        Assert.Equal([GrantStoreProblem.Revoked, GrantStoreProblem.Revoked, GrantStoreProblem.None], grants[..3].Select(g => Open().TryGetUsable(g.Id, 1).Problem));
    }

    [Fact]
    public void Wrong_generation_and_uninstalled_terminal_refuse()
    {
        var grant = Issued();
        Assert.Equal(GrantStoreProblem.Generation, Open().TryGetUsable(grant.Id, 2).Problem);
        Assert.Equal(GrantStoreProblem.Generation, Open().Revoke(grant.Id, 2).Problem);
        var file = Directory.GetFiles(Path.Combine(Folder, "audit"), "agents-*.jsonl").Single();
        File.WriteAllLines(file, File.ReadAllLines(file).SkipLast(1));
        Assert.False(Open().TryGetUsable(grant.Id, 1).Usable);
    }
}
