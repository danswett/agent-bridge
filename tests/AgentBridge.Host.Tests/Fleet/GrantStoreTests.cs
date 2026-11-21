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
    AgentAuditLog Audit => new(Folder, clock);
    GrantStore Open(P256KeyPair? signingKey = null, Action<string, byte[]>? write = null) =>
        new(Folder, signingKey ?? key, Audit, Scope, clock, write);
    GrantDocument Grant() => new(1, Guid.NewGuid(), "123",
        new("123", Device, "AQ", clock.GetUtcNow(), new('a', 64)), new(Device, "default"),
        [new("123", Device, Guid.NewGuid().ToString(), "AAAAAAAAAAAAAAAAAAAAAA", new(42, clock.GetUtcNow()))],
        ["launch", "stop"], Scope,
        new(3, 10, 6), clock.GetUtcNow(), 1, Roles: [], StopTargets: []);
    public void Dispose() { key.Dispose(); Directory.Delete(root, true); }

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
        var grant = Grant();
        Assert.True(Open().Issue(grant).Usable);
        Assert.True(Open().Revoke(grant.Id, 1).Usable);
        clock.Advance(TimeSpan.FromDays(35));
        Assert.True(Audit.Append(new("local", "fixture", "person:local", [], "host.fixture", "profile", "seen")));
        clock.Advance(TimeSpan.FromDays(1));
        Assert.True(Audit.Append(new("local", "fixture", "person:local", [], "host.fixture", "profile", "seen")));
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
        Assert.True(Open().Issue(grant).Usable);
        Assert.True(Open().Issue(expired).Usable);
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
        var grant = Grant();
        Assert.True(Open().Issue(grant).Usable);
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
        var grant = Grant();
        Assert.True(Open().Issue(grant).Usable);
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
        var grant = Grant();
        Assert.True(Open().Issue(grant).Usable);
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
        var grant = Grant();
        Assert.True(Open().Issue(grant).Usable);
        var json = JsonNode.Parse(File.ReadAllBytes(PathName).AsSpan(32))!;
        json[field] = field == "generation" ? 0 : 2;
        var body = System.Text.Encoding.UTF8.GetBytes(json.ToJsonString());
        DeviceMacFile.Write(PathName, [.. DeviceMacFile.Mac(body, key, "agent-bridge fleet grant store v1"u8), .. body], _ => { });
        Assert.Equal(expected, Open().TryGetUsable(grant.Id, 1).Problem);
    }

    [Fact]
    public void Wrong_generation_and_uninstalled_terminal_refuse()
    {
        var grant = Grant();
        Assert.True(Open().Issue(grant).Usable);
        Assert.Equal(GrantStoreProblem.Generation, Open().TryGetUsable(grant.Id, 2).Problem);
        Assert.Equal(GrantStoreProblem.Generation, Open().Revoke(grant.Id, 2).Problem);
        var file = Directory.GetFiles(Path.Combine(Folder, "audit"), "agents-*.jsonl").Single();
        File.WriteAllLines(file, File.ReadAllLines(file).SkipLast(1));
        Assert.False(Open().TryGetUsable(grant.Id, 1).Usable);
    }
}
