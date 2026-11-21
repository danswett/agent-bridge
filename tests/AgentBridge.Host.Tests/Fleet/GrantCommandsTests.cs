using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Host.Fleet.Grants;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

public sealed class GrantCommandsTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("ab-grant-cli-").FullName;
    readonly P256KeyPair key = P256KeyPair.Generate();
    readonly FakeTimeProvider clock = new(new(2026, 11, 21, 20, 0, 0, TimeSpan.Zero));
    const string Device = "0123456789abcdef0123456789abcdef";
    static readonly GrantScope Scope = new([Device], ["repo"], ["copilot"], ["small"], ["default"], ["medium"], ["default"], ["ask"]);
    string Folder => Path.Combine(root, "default");
    AgentAuditLog Audit => new(Folder, clock);
    GrantStore Store => new(Folder, key, Audit, Scope, clock);
    HostPolicySnapshot Policy => new("123", new(Device, "default"), Grant().Subjects[0], Scope, new(3, 10, 6), true, true);
    GrantDocument Grant() => new(1, Guid.NewGuid(), "123", new("123", Device, "AQ", clock.GetUtcNow(), new('a', 64)),
        new(Device, "default"), [new("123", Device, Guid.NewGuid().ToString(), "AAAAAAAAAAAAAAAAAAAAAA", new(42, clock.GetUtcNow()))],
        ["launch", "stop"], Scope, new(2, 8, 4), clock.GetUtcNow(), 1, clock.GetUtcNow().AddHours(1), Roles: [], StopTargets: []);
    public void Dispose() { key.Dispose(); Directory.Delete(root, true); }
    int Run(string[] args, string json, Func<bool>? refused = null, Func<string, string?>? confirm = null,
        StringWriter? output = null) => GrantCommands.Run(args, refused ?? (() => false), Audit,
        () => Store, () => Policy, _ => json, confirm ?? (digest => digest), key, output ?? new(), new StringWriter(), clock);

    [Fact]
    public void Production_caller_guard_refuses_a_script_parent_before_revoke_opens_the_store()
    {
        var opened = false;
        var guard = new CallerGuard(() => [], null, hostImage: @"C:\host.exe");
        var ancestry = new ProcessAncestry([new PeerProcess(42, clock.GetUtcNow(), @"C:\host.exe"),
            new PeerProcess(43, clock.GetUtcNow(), @"C:\pwsh.exe")], true);
        Assert.Equal(4, GrantCommands.Run(["grants", "revoke", Guid.NewGuid().ToString(), "1"],
            () => guard.Classify(ancestry).Kind != CallerKind.HostCli, Audit, () => { opened = true; return Store; },
            () => throw new Exception("policy read"), _ => throw new Exception("input read"),
            _ => throw new Exception("confirmed"), key, new StringWriter(), new StringWriter(), clock));
        Assert.False(opened);
    }

    [Fact]
    public void A_refused_attempt_before_the_first_grant_does_not_poison_issue_and_revoke()
    {
        var grant = Grant();
        var json = JsonSerializer.Serialize(grant, ProtocolJson.Options);
        Assert.Equal(4, Run(["grants", "issue", "document.json"], json, refused: () => true));
        Assert.Equal(0, Run(["grants", "issue", "document.json"], json));
        Assert.Equal(0, Run(["grants", "revoke", grant.Id.ToString(), "1"], ""));
    }

    [Fact]
    public void Confirmation_displays_every_normalized_field_that_the_digest_authorizes()
    {
        var grant = Grant(); grant = grant with { NotBefore = clock.GetUtcNow().AddMinutes(5), Roles = [new("Quality Lead", grant.Subjects[0])] };
        var output = new StringWriter();
        Assert.Equal(0, Run(["grants", "issue", "document.json"], JsonSerializer.Serialize(grant, ProtocolJson.Options), output: output));
        var summary = JsonNode.Parse(output.ToString().Trim().Split('\n')[0])!;
        foreach (var field in new[] { "version", "account", "issuer", "audience", "notBefore", "roles", "stopTargets" })
        { Assert.NotNull(summary[field]); }
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("path")]
    [InlineData("isolate")]
    [InlineData("unchanged")]
    public void Current_workspace_approval_bounds_offered_scope_before_persisting(string change)
    {
        var settings = HostSettings.Default with { Launch = new() { Workspaces = change == "removed" ? [] :
            [new("repo", change == "path" ? root : Folder, change != "isolate")] },
            Agents = new() { InboundLocal = new() { Launch = new(["approved"], ["copilot"], []) } } };
        var workspaces = GrantCommands.ApprovedWorkspaces(settings, [new() { Label = "repo", Path = Folder, Isolate = true }]);
        var policy = Policy with { Scope = Scope with { Workspaces = workspaces } };
        var grant = Grant();
        Assert.Equal(change == "unchanged" ? 0 : 2, GrantCommands.Run(["grants", "issue", "document.json"], () => false,
            Audit, () => Store, () => policy, _ => JsonSerializer.Serialize(grant, ProtocolJson.Options), d => d,
            key, new StringWriter(), new StringWriter(), clock));
        Assert.Equal(change == "unchanged", File.Exists(Path.Combine(Folder, GrantStore.FileName)));
    }

    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("invalid")]
    [InlineData("unchanged")]
    public void Current_account_is_reloaded_after_confirmation_before_persisting(string change)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(Path.Combine(Folder, HostSettings.FileName), "{}");
        var accepted = HostSettings.Load(Folder).Settings;
        var account = Path.Combine(Folder, "account.json"); File.WriteAllText(account, JsonSerializer.Serialize(new SignInResult("fixture", "123", "person")));
        var grant = Grant();
        Assert.Equal(change == "unchanged" ? 0 : 4, GrantCommands.Run(["grants", "issue", "document.json"], () => false,
            Audit, () => Store, () => GrantCommands.RecheckPolicy(Folder, accepted, Policy),
            _ => JsonSerializer.Serialize(grant, ProtocolJson.Options), d =>
            {
                if (change == "missing") { File.Delete(account); }
                else { File.WriteAllText(account, change == "invalid" ? "{" :
                    JsonSerializer.Serialize(new SignInResult("fixture", change == "changed" ? "456" : "123", "person"))); }
                return d;
            }, key, new StringWriter(), new StringWriter(), clock));
        Assert.Equal(change == "unchanged", File.Exists(Path.Combine(Folder, GrantStore.FileName)));
    }

    [Theory]
    [InlineData("agent")]
    [InlineData("script")]
    [InlineData("unknown")]
    public void Nonperson_ancestry_is_audited_before_opening_the_store_or_reading_input(string kind)
    {
        var guard = new CallerGuard(() => [], null, hostImage: @"C:\host.exe");
        var ancestry = kind == "unknown" ? ProcessAncestry.Unknown :
            new ProcessAncestry([new PeerProcess(42, clock.GetUtcNow(), @"C:\host.exe"),
                new PeerProcess(43, clock.GetUtcNow(), kind == "agent" ? @"C:\copilot.exe" : @"C:\pwsh.exe")], true);
        foreach (var action in new[] { "issue", "revoke" })
        {
            Assert.Equal(4, GrantCommands.Run(["grants", action], () => kind == "script" || guard.Classify(ancestry).Kind != CallerKind.HostCli,
                Audit, () => throw new Exception("store opened"), () => throw new Exception("policy read"),
                _ => throw new Exception("input read"), _ => throw new Exception("confirmed"), key, new StringWriter(), new StringWriter(), clock));
        }
        Assert.False(File.Exists(Path.Combine(Folder, GrantStore.FileName)));
        Assert.Equal(2, GateAuditProjection.Load(Folder).Rows.Count(r => r.Outcome == "refused"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Receipt_binds_the_exact_person_confirmed_digest_and_allow_all_choice(bool allowAll)
    {
        var grant = Grant() with { AllowAllLaunch = allowAll };
        string? confirmed = null;
        var output = new StringWriter();
        Assert.Equal(0, Run(["grants", "issue", "document.json"], JsonSerializer.Serialize(grant, ProtocolJson.Options),
            confirm: digest => { confirmed = digest; return digest; }, output: output));
        var stored = Store.TryGetUsable(grant.Id, 1).Grant!;
        Assert.Equal(confirmed, stored.Issuer.Digest);
        var receipt = JsonNode.Parse(output.ToString().Trim().Split('\n')[^1])!;
        Assert.Equal(confirmed, receipt["digest"]!.GetValue<string>());
        Assert.Equal(allowAll, receipt["allowAllLaunch"]!.GetValue<bool>());
        Assert.NotEqual(grant.Issuer.PersonProof, stored.Issuer.PersonProof);
        var body = JsonSerializer.SerializeToUtf8Bytes(stored with
        { Issuer = stored.Issuer with { PersonProof = "", Digest = "" } }, ProtocolJson.Options);
        Assert.Equal(confirmed, Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(body)));
        Assert.Equal(AgentBridge.Roster.Codec.Base64Url(CommandMac.Of(key, key.Public, body)), stored.Issuer.PersonProof);
    }

    [Fact]
    public void Revoke_acknowledges_a_durable_tombstone_and_list_changes_no_files()
    {
        var grant = Grant();
        Assert.True(Store.Issue(grant).Usable);
        var output = new StringWriter();
        Assert.Equal(0, Run(["grants", "revoke", grant.Id.ToString(), "1"], "", output: output));
        Assert.True(JsonNode.Parse(output.ToString())!["tombstone"]!.GetValue<bool>());
        Assert.Equal(GrantStoreProblem.Revoked, Store.TryGetUsable(grant.Id, 1).Problem);
        var before = Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(p => p, File.ReadAllBytes);
        Assert.Equal(0, Run(["grants", "list"], "", refused: () => throw new Exception("list guarded")));
        Assert.Equal(before.Keys.Order(), Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order());
        foreach (var (path, bytes) in before) { Assert.Equal(bytes, File.ReadAllBytes(path)); }
    }

    [Theory]
    [InlineData("caps")]
    [InlineData("hourly")]
    [InlineData("expiresAt")]
    [InlineData("wide")]
    [InlineData("digest")]
    [InlineData("scope")]
    [InlineData("subject")]
    [InlineData("lifetime")]
    public void Missing_caps_expiry_wider_policy_or_wrong_confirmation_never_installs_a_grant(string fault)
    {
        var json = JsonNode.Parse(JsonSerializer.Serialize(Grant(), ProtocolJson.Options))!;
        if (fault is "caps" or "expiresAt") { json.AsObject().Remove(fault); }
        if (fault == "hourly") { json["caps"]!.AsObject().Remove("hourly"); }
        if (fault == "wide") { json["caps"]!["hourly"] = 4; }
        if (fault == "scope") { json["scope"]!["models"] = new JsonArray("unknown"); }
        if (fault == "subject") { json["subjects"]![0]!["session"] = "SG Builder"; }
        if (fault == "lifetime") { json["expiresAt"] = clock.GetUtcNow().AddHours(25); }
        Assert.NotEqual(0, Run(["grants", "issue", "document.json"], json.ToJsonString(), confirm: d => fault == "digest" ? new('b', 64) : d));
        Assert.False(File.Exists(Path.Combine(Folder, GrantStore.FileName)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Changed_ancestry_or_policy_at_confirmation_refuses_before_installation(bool ancestry)
    {
        var changed = false;
        Assert.Equal(4, GrantCommands.Run(["grants", "issue", "document.json"], () => ancestry && changed,
            Audit, () => Store, () => Policy with { LaunchAllowed = ancestry || !changed },
            _ => JsonSerializer.Serialize(Grant(), ProtocolJson.Options), digest => { changed = true; return digest; },
            key, new StringWriter(), new StringWriter(), clock));
        Assert.False(File.Exists(Path.Combine(Folder, GrantStore.FileName)));
        if (ancestry) { Assert.Contains(GateAuditProjection.Load(Folder).Rows, r => r.Outcome == "refused"); }
    }
}
