using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host.Fleet.Grants;
using Xunit;

public sealed class GrantAuthorityTests
{
    static readonly DateTimeOffset Now = new(2026, 11, 21, 18, 0, 0, TimeSpan.Zero);
    const string Device = "0123456789abcdef0123456789abcdef";
    static readonly GrantSubject Subject = new("123", Device, "e3d83a44-3d03-4e72-9f61-17d6b1cc897a",
        "AAAAAAAAAAAAAAAAAAAAAA", new(42, Now.AddHours(-1)));
    static readonly GrantScope Scope = new([Device], ["repo"], ["copilot"], ["small"], ["default"], ["medium"], ["default"], ["ask"]);
    static GrantDocument Grant() => new(1, Guid.Parse("82ca773f-3e02-426c-9fb1-5bdd87e68125"), "123",
        new("123", Device, "AQ", Now, new('a', 64)), new(Device, "default"), [Subject], ["launch", "stop"],
        Scope, new(3, 10, 6), Now, 1, Roles: [], StopTargets: []);
    static HostPolicySnapshot Policy() => new("123", Grant().Audience, Subject, Scope, new(3, 10, 6), true, true);
    static GrantRequest Request() => new(Subject, "launch", Device, "repo", "copilot");
    static JsonObject Json() => JsonNode.Parse(JsonSerializer.Serialize(Grant(), new JsonSerializerOptions(JsonSerializerDefaults.Web)))!.AsObject();
    static GrantValidation Validate(GrantDocument grant) => GrantValidator.Validate(grant, Scope);
    static GrantValidation Parse(string json) => GrantValidator.Parse(json, Scope);

    [Theory]
    [InlineData("caps")]
    [InlineData("hourly")]
    [InlineData("daily")]
    [InlineData("live")]
    public void Every_cap_is_required(string field)
    {
        var json = Json();
        if (field == "caps") { json.Remove(field); } else { json["caps"]!.AsObject().Remove(field); }
        Assert.False(Parse(json.ToJsonString()).Valid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Caps_are_positive_not_unlimited(int cap) =>
        Assert.Equal(GrantRefusal.Caps, Validate(Grant() with { Caps = new(cap, 10, 6) }).Reason);

    [Fact]
    public void Lifetime_defaults_to_eight_hours_and_never_exceeds_twenty_four()
    {
        Assert.False(GrantEvaluator.Evaluate(Grant(), Policy(), Request(), Now.AddHours(8)).Allowed);
        Assert.False(Validate(Grant() with { ExpiresAt = Now.AddHours(24).AddTicks(1) }).Valid);
        Assert.True(Validate(Grant() with { ExpiresAt = Now.AddHours(24) }).Valid);
    }

    [Fact]
    public void Names_only_subjects_and_unknown_fields_refuse()
    {
        var json = Json();
        json["subjects"] = JsonNode.Parse("""[{"name":"SG Builder 247"}]""");
        Assert.False(Parse(json.ToJsonString()).Valid);
        json = Json();
        json["unknown"] = true;
        Assert.False(Parse(json.ToJsonString()).Valid);
        json = Json();
        json["subjects"]![0]!["name"] = "SG Builder 247";
        Assert.False(Parse(json.ToJsonString()).Valid);
    }

    [Theory]
    [InlineData("resume")]
    [InlineData("restore")]
    [InlineData("transfer")]
    [InlineData("answer")]
    public void Undelegated_actions_refuse(string action) =>
        Assert.False(Validate(Grant() with { Actions = [action] }).Valid);

    [Fact]
    public void Wider_than_policy_refuses_but_an_explicit_narrower_choice_passes()
    {
        var grant = Grant() with { Scope = Scope with { Models = ["small", "large"] } };
        Assert.Equal(GrantRefusal.Scope, GrantEvaluator.Evaluate(grant, Policy(), Request() with { Model = "large" }, Now).Reason);
        var result = GrantEvaluator.Evaluate(grant, Policy(), Request() with { Model = "small" }, Now);
        Assert.True(result.Allowed);
        Assert.Equal("small", result.Launch!.Model);
        Assert.Equal(new(3, 10, 6), result.Launch.Caps);
        Assert.Equal(new(1, 2, 1), GrantEvaluator.Evaluate(grant with { Caps = new(1, 2, 1) }, Policy(), Request(), Now).Launch!.Caps);
        Assert.False(GrantEvaluator.Evaluate(Grant(), Policy() with { Scope = grant.Scope }, Request() with { Model = "large" }, Now).Allowed);
    }

    [Fact]
    public void Missing_choices_resolve_only_inside_both_snapshots()
    {
        var policy = Policy() with { Scope = Scope with { Models = ["large", "small"] } };
        Assert.Equal("small", GrantEvaluator.Evaluate(Grant(), policy, Request(), Now).Launch!.Model);
        Assert.False(GrantEvaluator.Evaluate(Grant(), policy with { Scope = Scope with { Models = ["large"] } }, Request(), Now).Allowed);
    }

    [Theory]
    [InlineData("workspace")]
    [InlineData("agent")]
    [InlineData("profile")]
    [InlineData("effort")]
    [InlineData("context")]
    [InlineData("permission")]
    public void Every_requested_choice_must_be_inside_grant_and_policy(string field)
    {
        var wide = Scope with
        {
            Workspaces = ["repo", "other"],
            Agents = ["copilot", "claude"],
            Profiles = ["default", "other"],
            Efforts = ["medium", "high"],
            Contexts = ["default", "long_context"],
            Permissions = ["ask", "allowAll"]
        };
        var request = field switch
        {
            "workspace" => Request() with { Workspace = "other" },
            "agent" => Request() with { Agent = "claude" },
            "profile" => Request() with { Profile = "other" },
            "effort" => Request() with { Effort = "high" },
            "context" => Request() with { Context = "long_context" },
            _ => Request() with { Permission = "allowAll" },
        };
        Assert.False(GrantEvaluator.Evaluate(Grant() with { Scope = wide }, Policy(), request, Now).Allowed);
        Assert.False(GrantEvaluator.Evaluate(Grant(), Policy() with { Scope = wide, AllowAll = true }, request, Now).Allowed);
    }

    [Fact]
    public void Allow_all_requires_explicit_current_policy()
    {
        var scope = Scope with { Permissions = ["allowAll"] };
        var grant = Grant() with { Scope = scope };
        var policy = Policy() with { Scope = scope };
        Assert.Equal(GrantRefusal.Permission, GrantEvaluator.Evaluate(grant, policy, Request(), Now).Reason);
        Assert.True(GrantEvaluator.Evaluate(grant, policy with { AllowAll = true }, Request(), Now).Allowed);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(287999999999, true)]
    [InlineData(288000000000, false)]
    public void Validity_includes_not_before_but_excludes_expiry(long ticks, bool allowed) =>
        Assert.Equal(allowed, GrantEvaluator.Evaluate(Grant(), Policy(), Request(), Now.AddTicks(ticks)).Allowed);

    [Fact]
    public void Stops_require_exact_person_exception_or_same_subject_same_grant_launch()
    {
        var target = new StopTarget(Subject.Session, Subject.ProcessInstance);
        var request = Request() with { Action = "stop", Target = target };
        var policy = Policy() with { StopTargets = [target] };
        Assert.Equal(GrantRefusal.StopTarget, GrantEvaluator.Evaluate(Grant(), policy, request, Now).Reason);
        Assert.True(GrantEvaluator.Evaluate(Grant() with { StopTargets = [target] }, policy, request, Now).Allowed);
        var launch = new GrantLaunch(Grant().Id, Subject, target);
        Assert.True(GrantEvaluator.Evaluate(Grant(), policy with { Launches = [launch] }, request, Now).Allowed);
        Assert.False(GrantEvaluator.Evaluate(Grant(), policy with { Launches = [launch with { GrantId = Guid.Empty }] }, request, Now).Allowed);
        Assert.False(GrantEvaluator.Evaluate(Grant() with { StopTargets = [target] }, policy,
            request with { Target = target with { ProcessInstance = new(42, Now) } }, Now).Allowed);
    }

    [Fact]
    public void Caller_audience_account_actions_and_host_off_cannot_be_widened()
    {
        Assert.False(GrantEvaluator.Evaluate(Grant(), Policy(), Request() with { Subject = Subject with { Credential = "AQ" } }, Now).Allowed);
        Assert.False(GrantEvaluator.Evaluate(Grant(), Policy() with { Audience = new(Device, "other") }, Request(), Now).Allowed);
        Assert.False(GrantEvaluator.Evaluate(Grant(), Policy() with { Account = "456" }, Request(), Now).Allowed);
        Assert.False(GrantEvaluator.Evaluate(Grant(), Policy() with { AuthenticatedCaller = Subject with { ProcessInstance = new(42, Now) } }, Request(), Now).Allowed);
        Assert.False(GrantEvaluator.Evaluate(Grant() with { Actions = ["stop"] }, Policy(), Request(), Now).Allowed);
        Assert.False(GrantEvaluator.Evaluate(Grant(), Policy() with { LaunchAllowed = false }, Request(), Now).Allowed);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("""{"hourly":null,"daily":10,"live":6}""")]
    [InlineData("""{"hourly":1.5,"daily":10,"live":6}""")]
    [InlineData("""{"hourly":3,"hourly":4,"daily":10,"live":6}""")]
    public void Malformed_or_duplicate_caps_never_default(string caps)
    {
        var json = Json();
        json["caps"] = JsonNode.Parse(caps);
        var text = json.ToJsonString();
        if (caps.Contains("\"hourly\":4")) { text = text.Replace("\"hourly\":4", "\"hourly\":3,\"hourly\":4"); }
        Assert.False(Parse(text).Valid);
    }

    [Fact]
    public void Unknown_identifiers_versions_paths_and_unbound_roles_refuse()
    {
        Assert.Equal(GrantRefusal.Version, Validate(Grant() with { Version = 2 }).Reason);
        Assert.False(Validate(Grant() with { Audience = new("machine-name", "default") }).Valid);
        Assert.False(Validate(Grant() with { Subjects = [Subject with { Session = "SG-Builder-247" }] }).Valid);
        Assert.False(Validate(Grant() with { Subjects = [Subject with { SourceHost = "*" }] }).Valid);
        Assert.False(Validate(Grant() with { Scope = Scope with { Agents = [@"C:\agent.exe"] } }).Valid);
        Assert.False(Validate(Grant() with { Scope = Scope with { Workspaces = ["repo/child"] } }).Valid);
        Assert.False(Validate(Grant() with { Roles = [new("Builder", Subject with { Credential = "AQ" })] }).Valid);
        Assert.False(Validate(Grant() with { Scope = Scope with { Models = ["unknown-model"] } }).Valid);
        Assert.True(Parse(Json().ToJsonString()).Valid);
        Assert.False(Validate(Grant() with { NotBefore = Now.AddHours(24), ExpiresAt = Now.AddHours(25) }).Valid);
    }
}
