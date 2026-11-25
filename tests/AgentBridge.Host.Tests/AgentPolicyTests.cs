using AgentBridge.Host;
using Xunit;

public sealed class AgentPolicyTests
{
    static HostSettings Parse(string json) => HostSettings.Parse(json, "config.json").Settings;

    [Fact]
    public void An_absent_agents_section_turns_agents_off_with_the_default_limits()
    {
        var s = Parse("""{ "version": 1 }""");
        Assert.False(s.Agents.Enabled);
        Assert.Equal(AgentInboundPolicy.Off, s.Agents.InboundLocal);
        Assert.Equal(AgentInboundPolicy.Off, s.Agents.InboundRemote);
        Assert.False(s.Agents.OutboundRemote);
        Assert.Equal("none", s.Agents.InboundRemote.Read);
        Assert.Null(s.Agents.InboundLocal.Launch);
        Assert.Equal(AgentPolicyLimits.Default, s.Agents.Limits);
        Assert.Equal((3, 20, 60, 6, 12, 3, 10), (s.Agents.Limits.MaxDepth, s.Agents.Limits.ConversationMessages, s.Agents.Limits.ConversationMinutes, s.Agents.Limits.PerPairPerMinute, s.Agents.Limits.PerTargetPerMinute, s.Agents.Limits.LaunchesPerOriginPerHour, s.Agents.Limits.LaunchesPerMachinePerHour));
        Assert.Empty(s.AgentsProblems);
    }

    [Fact]
    public void A_full_valid_section_is_read_as_written()
    {
        var s = Parse("""
        { "agents": { "enabled": true, "credentialDays": 3,
          "issue": { "launched": "read+reply+launch", "adopted": "read+reply" },
          "outbound": { "remote": true, "allowAgentLaunchedToLaunch": true },
          "inbound": {
            "local": { "read": "any", "reply": "forced-ask", "gate": "quiet", "allowAllTargets": { "workspaces": "same" },
                       "launch": { "workspaces": "approved", "permissions": "ask", "agents": ["copilot"], "profiles": [] } },
            "remote": { "fromMachines": "any", "read": "agent-launched", "coordinationGroups": { "maxHours": 8, "perMemberPerDay": 100, "perGroupPerMinute": 5 } },
            "exclude": { "sessions": ["s1"], "workspaces": ["private"] } },
          "limits": { "maxDepth": 2, "perPairPerMinute": 4 }, "label": "none" } }
        """);
        var a = s.Agents;
        Assert.Empty(s.AgentsProblems);
        Assert.True(a.Enabled);
        Assert.Equal(3, a.CredentialDays);
        Assert.Equal("read+reply+launch", a.IssueLaunched);
        Assert.True(a.OutboundAllowAgentLaunchedToLaunch);
        Assert.Equal("forced-ask", a.InboundLocal.Reply);
        Assert.Equal("same", a.InboundLocal.AllowAllTargets);
        Assert.Equal(["copilot"], a.InboundLocal.Launch!.Agents);
        Assert.Equal(["any"], a.InboundRemote.FromMachines);
        Assert.Equal(new AgentCoordinationGroups(8, 100, 5), a.InboundRemote.CoordinationGroups);
        Assert.Equal(["private"], a.ExcludedWorkspaces);
        Assert.Equal((2, 4, 20), (a.Limits.MaxDepth, a.Limits.PerPairPerMinute, a.Limits.ConversationMessages));
        Assert.Equal("none", a.Label);
    }

    [Fact]
    public void Resume_roles_are_off_unless_the_person_lists_valid_role_names()
    {
        Assert.Empty(Parse("""{ "agents": { "enabled": true } }""").Agents.ResumeRoles);
        var listed = Parse("""{ "agents": { "enabled": true, "resume": { "roles": ["quality-lead", "sre"] } } }""");
        Assert.Empty(listed.AgentsProblems);
        Assert.Equal(["quality-lead", "sre"], listed.Agents.ResumeRoles);
        foreach (var bad in new[] { "\"roles\": [\"a b\"]", "\"roles\": \"sre\"", "\"roles\": [1]", "\"who\": []" })
        {
            var s = Parse("{ \"agents\": { \"enabled\": true, \"resume\": { " + bad + " } } }");
            Assert.NotEmpty(s.AgentsProblems);
            Assert.Empty(s.Agents.ResumeRoles);
        }
    }

    [Theory]
    [InlineData("""{ "agents": "yes" }""")]
    [InlineData("""{ "agents": { "enabled": true, "inbound": [] } }""")]
    [InlineData("""{ "agents": { "enabled": true, "limits": 5 } }""")]
    [InlineData("""{ "agents": { "enabled": true, "inbound": { "local": { "read": "any" }, "remote": 1 } } }""")]
    public void A_section_or_nested_object_of_the_wrong_type_denies_everything_and_is_said(string json)
    {
        var s = Parse(json);
        Assert.Equal(AgentPolicy.Off, s.Agents);
        Assert.NotEmpty(s.AgentsProblems);
        Assert.Equal(s.AgentsProblems, s.SectionProblems.Where(p => p.StartsWith("agents")));
    }

    [Fact]
    public void A_broken_agents_section_leaves_valid_siblings_loaded()
    {
        var s = Parse("""{ "agents": 7, "launch": { "defaultAgent": "copilot" }, "push": { "enabled": false } }""");
        Assert.Equal(AgentPolicy.Off, s.Agents);
        Assert.False(s.Push);
        Assert.Equal("copilot", s.Launch.DefaultAgent);
    }

    [Fact]
    public void A_file_that_cannot_be_read_leaves_agents_off()
    {
        var folder = Path.Combine(Path.GetTempPath(), "ab-agents-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(folder, HostSettings.FileName));
        try
        {
            var (settings, problem) = HostSettings.Load(folder);
            Assert.NotNull(problem);
            Assert.Equal(AgentPolicy.Off, settings.Agents);
        }
        finally { Directory.Delete(folder, true); }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("4")]
    [InlineData("\"3\"")]
    [InlineData("null")]
    public void A_limit_out_of_range_falls_back_to_the_default_never_a_higher_number(string value)
    {
        var s = Parse($$"""{ "agents": { "enabled": true, "limits": { "maxDepth": {{value}} } } }""");
        Assert.Equal(3, s.Agents.Limits.MaxDepth);
        Assert.Equal(value == "null", s.AgentsProblems.Count == 0);
    }

    [Fact]
    public void An_unknown_key_or_bad_value_is_said_and_takes_the_narrowest_value()
    {
        var s = Parse("""
        { "agents": { "enabled": "true", "bogus": 1, "credentialDays": 30,
          "inbound": { "local": { "read": "everything", "gate": "loud", "allowAllTargets": true, "coordinationGroups": { "maxHours": 99 },
                                  "launch": { "workspaces": "approved", "permissions": "allowAll" }, "extra": 1 } } } }
        """);
        var a = s.Agents;
        Assert.False(a.Enabled);
        Assert.Equal(7, a.CredentialDays);
        Assert.Equal("none", a.InboundLocal.Read);
        Assert.Equal("off", a.InboundLocal.Gate);
        Assert.Null(a.InboundLocal.AllowAllTargets);
        Assert.Null(a.InboundLocal.CoordinationGroups);
        Assert.Null(a.InboundLocal.Launch);
        Assert.Contains(s.AgentsProblems, p => p.Contains("agents.bogus"));
        Assert.Contains(s.AgentsProblems, p => p.Contains("agents.inbound.local.extra"));
        Assert.Equal(9, s.AgentsProblems.Count);
    }

    [Fact]
    public void A_launch_grant_must_state_ask_permissions()
    {
        var s = Parse("""{ "agents": { "inbound": { "local": { "launch": { "workspaces": "approved" } } } } }""");
        Assert.Null(s.Agents.InboundLocal.Launch);
        Assert.Single(s.AgentsProblems);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("\"true\"", false)]
    public void Person_grant_all_extension_is_explicit_boolean_without_changing_standing_ask(string value, bool expected)
    {
        var s = Parse("{\"agents\":{\"inbound\":{\"local\":{\"launch\":{\"workspaces\":\"approved\",\"permissions\":\"ask\",\"allowAllLaunch\":" + value + "}}}}}");
        Assert.Equal(expected, s.Agents.InboundLocal.Launch?.AllowAllLaunch ?? false);
    }
    // #2770: fleet launch limits (the dispatcher's route) are owner-set in config.json only, default 8 an hour and 6 live, raisable to a hard ceiling of 60.
    [Fact]
    public void The_fleet_limits_default_to_8_an_hour_and_6_live_and_the_route_is_off()
    {
        var s = Parse("""{ "agents": { "enabled": true } }""");
        Assert.Equal((8, 6), (s.Agents.Limits.Launch.FleetPerHour, s.Agents.Limits.Launch.LiveFleetWorkers));
        Assert.False(s.Agents.FleetLaunchEnabled);
        Assert.Empty(s.AgentsProblems);
    }

    [Fact]
    public void The_owner_can_raise_the_fleet_limits_and_a_value_above_60_is_clamped_to_60()
    {
        var s = Parse("""{ "agents": { "enabled": true, "fleetLaunch": { "enabled": true }, "limits": { "fleetPerHour": 20, "liveFleetWorkers": 70 } } }""");
        Assert.True(s.Agents.FleetLaunchEnabled);
        Assert.Equal((20, 60), (s.Agents.Limits.Launch.FleetPerHour, s.Agents.Limits.Launch.LiveFleetWorkers));
        // A clamp is not a problem: a problem turns the whole agents policy, and so the route, off.
        Assert.Empty(s.AgentsProblems);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("2.5")]
    [InlineData("\"many\"")]
    [InlineData("null")]
    public void A_fleet_limit_that_is_not_a_whole_number_from_1_is_the_default_never_unlimited(string value)
    {
        var s = Parse("{ \"agents\": { \"limits\": { \"fleetPerHour\": " + value + ", \"liveFleetWorkers\": " + value + " } } }");
        Assert.Equal((8, 6), (s.Agents.Limits.Launch.FleetPerHour, s.Agents.Limits.Launch.LiveFleetWorkers));
        Assert.Empty(s.AgentsProblems);
    }

    [Fact]
    public void An_unknown_fleetLaunch_key_is_said_and_ignored_and_the_route_stays_off()
    {
        var s = Parse("""{ "agents": { "fleetLaunch": { "enabled": "yes", "maxInFlight": 60 } } }""");
        Assert.False(s.Agents.FleetLaunchEnabled);
        Assert.Contains(s.AgentsProblems, p => p.Contains("fleetLaunch"));
    }
}
