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
}
