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
    [InlineData("version")]
    [InlineData("generation")]
    [InlineData("hourly")]
    [InlineData("daily")]
    [InlineData("live")]
    [InlineData("pid")]
    public void Quoted_numbers_refuse(string field)
    {
        var json = Json();
        var parent = field is "hourly" or "daily" or "live" ? json["caps"]!.AsObject()
            : field == "pid" ? json["subjects"]![0]!["processInstance"]!.AsObject() : json;
        parent[field] = parent[field]!.ToJsonString();
        Assert.Equal(GrantRefusal.Malformed, Parse(json.ToJsonString()).Reason);
    }

    [Theory]
    [InlineData("issuedAt", false)]
    [InlineData("notBefore", false)]
    [InlineData("expiresAt", false)]
    [InlineData("issuedAt", true)]
    [InlineData("notBefore", true)]
    [InlineData("expiresAt", true)]
    public void Numeric_times_refuse(string field, bool quoted)
    {
        var json = Json();
        var parent = field == "issuedAt" ? json["issuer"]!.AsObject() : json;
        parent[field] = 123;
        if (quoted) { parent[field] = "123"; }
        Assert.Equal(GrantRefusal.Malformed, Parse(json.ToJsonString()).Reason);
    }

    [Fact]
    public void Offset_times_near_maximum_return_lifetime_refusals_on_all_paths()
    {
        var issued = DateTimeOffset.Parse("9999-12-31T23:00:00+14:00");
        var grant = Grant() with { Issuer = Grant().Issuer with { IssuedAt = issued }, NotBefore = issued };
        Assert.Equal(GrantRefusal.Lifetime, Validate(grant).Reason);
        Assert.Equal(GrantRefusal.Lifetime, Parse(JsonSerializer.Serialize(grant, new JsonSerializerOptions(JsonSerializerDefaults.Web))).Reason);
        Assert.Equal(GrantRefusal.Lifetime, GrantEvaluator.Evaluate(grant, Policy(), Request(), issued).Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Omitted_permission_filters_disabled_allow_all_and_prefers_ask(bool allowAll)
    {
        var scope = Scope with { Permissions = ["allowAll", "ask"] };
        var result = GrantEvaluator.Evaluate(Grant() with { Scope = scope },
            Policy() with { Scope = scope, AllowAll = allowAll }, Request(), Now);
        Assert.True(result.Allowed);
        Assert.Equal("ask", result.Launch!.Permission);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Stops_ignore_dropped_launch_choices_but_keep_target_and_policy_checks(bool named)
    {
        var target = new StopTarget(Subject.Session, Subject.ProcessInstance);
        var grant = Grant() with { StopTargets = named ? [target] : [] };
        var policy = Policy() with
        {
            Scope = Scope with { Models = ["other"], Permissions = ["allowAll"] },
            StopTargets = [target], Launches = named ? [] : [new(grant.Id, Subject, target)]
        };
        var request = Request() with { Action = "stop", Target = target };
        Assert.True(GrantEvaluator.Evaluate(grant, policy, request, Now).Allowed);
        Assert.Equal(GrantRefusal.Policy, GrantEvaluator.Evaluate(grant, policy with { StopAllowed = false }, request, Now).Reason);
        Assert.Equal(GrantRefusal.StopTarget, GrantEvaluator.Evaluate(grant, policy with { StopTargets = [] }, request, Now).Reason);
    }

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
        Assert.Equal(GrantRefusal.Lifetime, GrantEvaluator.Evaluate(Grant() with { ExpiresAt = Now.AddHours(24).AddTicks(1) },
            Policy(), Request(), Now).Reason);
    }

    [Theory]
    [InlineData(1001, 5000, 100)]
    [InlineData(1000, 5001, 100)]
    [InlineData(1000, 5000, 101)]
    [InlineData(11, 10, 6)]
    public void Caps_have_documented_upper_bounds_and_hourly_never_exceeds_daily(int hourly, int daily, int live) =>
        Assert.Equal(GrantRefusal.Caps, Validate(Grant() with { Caps = new(hourly, daily, live) }).Reason);

    [Fact]
    public void Maximum_caps_and_launch_tuning_model_names_are_supported()
    {
        var scope = Scope with { Models = ["opus[1m]", "vendor:model"] };
        var grant = Grant() with { Scope = scope, Caps = new(1000, 5000, 100) };
        Assert.True(GrantValidator.Validate(grant, scope).Valid);
        Assert.Equal("opus[1m]", GrantEvaluator.Evaluate(grant, Policy() with { Scope = scope }, Request(), Now).Launch!.Model);
        Assert.False(GrantValidator.Validate(grant with { Scope = scope with { Models = ["-unsafe"] } }, scope).Valid);
        Assert.False(GrantValidator.Validate(grant with { Scope = scope with { Models = ["opus\n"] } }, scope).Valid);
        Assert.True(GrantEvaluator.Evaluate(grant, Policy() with { Scope = scope, Caps = new(1001, 5001, 101) }, Request(), Now).Allowed);
    }

    [Fact]
    public void Normal_offset_times_are_normalized_before_default_expiry_evaluation()
    {
        var grant = Grant() with { Issuer = Grant().Issuer with { IssuedAt = Now.ToOffset(TimeSpan.FromHours(14)) },
            NotBefore = Now.ToOffset(TimeSpan.FromHours(-12)), ExpiresAt = Now.AddHours(8).ToOffset(TimeSpan.FromHours(10)) };
        var validated = Validate(grant).Grant!;
        Assert.Equal(TimeSpan.Zero, validated.Issuer.IssuedAt.Offset);
        Assert.Equal(TimeSpan.Zero, validated.NotBefore.Offset);
        Assert.Equal(TimeSpan.Zero, validated.ExpiresAt!.Value.Offset);
        Assert.True(GrantEvaluator.Evaluate(grant with { ExpiresAt = null }, Policy(), Request(), Now).Allowed);
    }

    [Fact]
    public void Child_sessions_do_not_inherit_the_parent_subject_grant()
    {
        var child = Subject with { Session = Guid.NewGuid().ToString(), ProcessInstance = new(43, Now) };
        Assert.Equal(GrantRefusal.Subject, GrantEvaluator.Evaluate(Grant(), Policy() with { AuthenticatedCaller = child },
            Request() with { Subject = child }, Now).Reason);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void Explicit_allow_all_requires_both_person_flag_and_receiver_policy(bool flag, bool enabled)
    {
        var scope = Scope with { Permissions = ["allowAll"] };
        var result = GrantEvaluator.Evaluate(Grant() with { Scope = scope, AllowAllLaunch = flag },
            Policy() with { Scope = scope, AllowAll = enabled }, Request() with { Permission = "allowAll" }, Now);
        Assert.Equal(flag && enabled, result.Allowed);
    }

    [Fact]
    public void Omitted_permissions_never_implicitly_select_allow_all_even_with_both_flags()
    {
        var scope = Scope with { Permissions = ["allowAll"] };
        Assert.Equal(GrantRefusal.Permission, GrantEvaluator.Evaluate(Grant() with { Scope = scope, AllowAllLaunch = true },
            Policy() with { Scope = scope, AllowAll = true }, Request(), Now).Reason);
    }

    [Fact]
    public void Omitted_tuning_uses_recorded_grant_order_not_receiver_default_order()
    {
        var grant = Grant() with { Scope = Scope with { Models = ["large", "small"] } };
        Assert.Equal("large", GrantEvaluator.Evaluate(grant,
            Policy() with { Scope = Scope with { Models = ["small", "large"] } }, Request(), Now).Launch!.Model);
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
