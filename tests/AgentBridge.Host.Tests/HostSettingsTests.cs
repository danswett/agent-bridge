using AgentBridge.Host;
using Xunit;

public sealed class HostSettingsTests : IDisposable
{
    readonly string folder = Directory.CreateTempSubdirectory("abset-").FullName;

    public void Dispose() => Directory.Delete(folder, true);

    (HostSettings Settings, string? Problem) With(string? text)
    {
        if (text is not null) { File.WriteAllText(Path.Combine(folder, HostSettings.FileName), text); }
        return HostSettings.Load(folder);
    }

    [Fact]
    public void Remote_approval_is_on_without_a_file_or_a_setting_and_off_only_when_set_false()
    {
        Assert.Equal((HostSettings.Default, (string?)null), With(null));
        Assert.True(With("{ \"permissions\": {} }").Settings.RemoteApproval);
        Assert.True(With("{ \"permissions\": { \"remoteApproval\": true } }").Settings.RemoteApproval);
        Assert.Equal((new HostSettings(false), (string?)null), With("{ // mine\n \"permissions\": { \"remoteApproval\": false, } }"));
    }

    [Theory]
    [InlineData("{ \"permissions\": { \"remoteApproval\": \"no\" } }")]
    [InlineData("{ \"permissions\": false }")]
    [InlineData("not json")]
    // usage.publish false beside a value that spoils the file: the opt-out must hold.
    [InlineData("{ \"usage\": { \"publish\": false }, \"push\": { \"enabled\": \"false\" } }")]
    public void A_setting_that_cannot_be_read_as_true_or_false_turns_remote_approval_off_and_says_why(string text)
    {
        var (settings, problem) = With(text);

        Assert.False(settings.RemoteApproval);
        Assert.Contains("remote approval of permission prompts, launching, notifications and agent usage are off", problem);
        Assert.False(settings.Launch.Enabled);
        Assert.False(settings.Push);
        Assert.True(settings.ReadOnly);
        Assert.False(settings.Usage.Publish);
    }

    [Theory]
    [InlineData(null, false, null)]
    [InlineData("{ \"delivery\": {} }", false, null)]
    [InlineData("{ \"delivery\": { \"readOnly\": false } }", false, null)]
    [InlineData("{ \"delivery\": { \"readOnly\": true } }", true, null)]
    [InlineData("{ \"delivery\": { \"readOnly\": 1 } }", true, "delivery.readOnly is not true or false")]
    [InlineData("{ \"delivery\": true }", true, "delivery is not an object")]
    public void A_host_is_read_only_only_when_delivery_read_only_is_true_or_might_have_been_meant(string? text, bool readOnly, string? problem)
    {
        var (settings, said) = With(text);

        Assert.Equal(readOnly, settings.ReadOnly);
        if (problem is null) { Assert.Null(said); } else { Assert.Contains(problem, said); }
    }

    [Theory]
    [InlineData(null, true, null)]
    [InlineData("{ \"push\": {} }", true, null)]
    [InlineData("{ \"push\": { \"enabled\": true } }", true, null)]
    [InlineData("{ \"push\": { \"enabled\": false } }", false, null)]
    [InlineData("{ \"push\": { \"enabled\": \"no\" } }", false, "push.enabled is not true or false")]
    [InlineData("{ \"push\": false }", false, "push is not an object")]
    public void Notifications_are_on_unless_push_enabled_is_false_and_a_value_that_is_not_true_or_false_turns_them_off(string? text, bool on, string? problem)
    {
        var (settings, said) = With(text);

        Assert.Equal(on, settings.Push);
        if (problem is null) { Assert.Null(said); } else { Assert.Contains(problem, said); }
    }

    [Fact]
    public void A_config_from_a_newer_schema_is_not_guessed_at()
    {
        var (settings, problem) = With("{ \"version\": 2, \"permissions\": { \"remoteApproval\": true }, \"launch\": { \"workspaces\": [\"C:\\\\x\"] } }");

        Assert.False(settings.RemoteApproval);
        Assert.False(settings.Launch.Enabled);
        Assert.Contains("written by a newer Agent Bridge", problem);
        Assert.True(With("{ \"version\": 1 }").Settings.RemoteApproval);
    }

    [Fact]
    public void A_wrong_launch_entry_leaves_remote_approval_and_the_other_workspaces_as_they_were()
    {
        // Rooted on whichever platform runs the tests: C:\ is not a full path on Linux.
        string Json(string name) => System.Text.Json.JsonSerializer.Serialize(Path.Combine(folder, name));
        var (settings, problem) = With($$"""{ "permissions": { "remoteApproval": false }, "launch": { "workspaces": [ { "path": {{Json("a")}}, "isolate": 1 }, { "path": {{Json("b")}} } ] } }""");

        Assert.Null(problem);
        Assert.False(settings.RemoteApproval);
        Assert.Equal(["b"], settings.Launch.Workspaces.Select(w => w.Label));
        Assert.Contains(settings.LaunchProblems, p => p.Contains("isolate is not true or false"));
    }

    int Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = ConfigCommands.Run(args, folder, output, error, isSystemDirectory: _ => false);
        Said = output + "" + error;
        return code;
    }

    string Said = "";

    [Fact]
    public void A_workspace_is_approved_at_the_machine_with_the_cli_and_removed_the_same_way()
    {
        var app = Directory.CreateDirectory(Path.Combine(folder, "app")).FullName;
        Assert.Equal(0, Run("workspace", "list"));
        Assert.Contains("No workspace is approved", Said);

        Assert.Equal(0, Run("workspace", "add", app, "--label", "App"));
        Assert.Contains("Approved", Said);
        var (settings, problem) = HostSettings.Load(folder);
        Assert.Null(problem);
        Assert.Equal([("App", app, false)], settings.Launch.Workspaces.Select(w => (w.Label, w.Path, w.Isolate)));
        Assert.Contains("\"version\": 1", File.ReadAllText(Path.Combine(folder, HostSettings.FileName)));

        // Once only, and never a folder that is missing or one without git for worktrees.
        Assert.Equal(1, Run("workspace", "add", app + Path.DirectorySeparatorChar, "--label", "Again"));
        Assert.Contains("already approved, as 'App'", Said);
        Assert.Equal(1, Run("workspace", "add", Path.Combine(folder, "missing")));
        Assert.Equal(1, Run("workspace", "add", Directory.CreateDirectory(Path.Combine(folder, "plain")).FullName, "--isolate"));
        Assert.Contains("is not in a git repository", Said);

        Assert.Equal(0, Run("workspace", "remove", "App"));
        Assert.Empty(HostSettings.Load(folder).Settings.Launch.Workspaces);
    }

    [Fact]
    public void The_cli_refuses_a_setting_it_does_not_know_or_a_value_serve_could_not_read_and_leaves_the_file_as_it_was()
    {
        File.WriteAllText(Path.Combine(folder, HostSettings.FileName), "{ \"permissions\": { \"remoteApproval\": false } }");
        Assert.Equal(2, Run("config", "set", "launch.permision", "allowAll"));
        Assert.Equal(1, Run("config", "set", "launch.permissions", "always"));
        Assert.Contains("Not written", Said);
        Assert.Equal(1, Run("config", "set", "launch.worktrees.limit", "-1"));
        Assert.Equal("{ \"permissions\": { \"remoteApproval\": false } }", File.ReadAllText(Path.Combine(folder, HostSettings.FileName)));

        Assert.Equal(0, Run("config", "set", "launch.permissions", "allowAll"));
        Assert.Equal(0, Run("config", "set", "launch.agents.copilot.models", "[\"gpt-6.1-sol\",\"claude-opus-5.5\"]"));
        Assert.Equal(0, Run("config", "set", "launch.worktrees.limit", "3"));
        var settings = HostSettings.Load(folder).Settings;
        Assert.Equal(("allowAll", 3, false), (settings.Launch.Permissions, settings.Launch.WorktreeLimit, settings.RemoteApproval));
        Assert.Equal(["gpt-6.1-sol", "claude-opus-5.5"], settings.Launch.Agent("copilot").Models);

        Assert.Equal(0, Run("config", "unset", "launch.permissions"));
        Assert.Equal("ask", HostSettings.Load(folder).Settings.Launch.Permissions);

        // Notifications are turned off at the machine the same way, and only with true or false.
        Assert.Equal(1, Run("config", "set", "push.enabled", "no"));
        Assert.True(HostSettings.Load(folder).Settings.Push);
        Assert.Equal(0, Run("config", "set", "push.enabled", "false"));
        Assert.False(HostSettings.Load(folder).Settings.Push);
        Assert.Equal(1, Run("config", "set", "delivery.readOnly", "yes"));
        Assert.Equal(0, Run("config", "set", "delivery.readOnly", "true"));
        Assert.True(HostSettings.Load(folder).Settings.ReadOnly);
    }

    [Fact]
    public void The_home_assistant_connector_is_off_by_default_and_a_wrong_section_turns_off_only_it()
    {
        var off = With(null).Settings;
        Assert.False(off.HomeAssistant.Enabled);

        var (settings, problem) = With("""{ "permissions": { "remoteApproval": false }, "homeAssistant": { "enabled": true, "baseUrl": "not a url" } }""");
        Assert.Null(problem);
        Assert.False(settings.RemoteApproval);
        Assert.False(settings.HomeAssistant.Enabled);
        Assert.Contains(settings.HomeAssistantProblems, p => p.Contains("baseUrl", StringComparison.Ordinal));

        // Set at the machine, checked as serve would read it: on without an address is not written.
        File.Delete(Path.Combine(folder, HostSettings.FileName));
        Assert.Equal(1, Run("config", "set", "homeAssistant.enabled", "true"));
        Assert.Contains("Not written", Said);
        Assert.Equal(0, Run("config", "set", "homeAssistant.baseUrl", "http://ha.test:8123"));
        Assert.Equal(0, Run("config", "set", "homeAssistant.enabled", "true"));
        Assert.Equal(0, Run("config", "set", "homeAssistant.disposable", "true"));
        var ha = HostSettings.Load(folder).Settings.HomeAssistant;
        Assert.True(ha is { Enabled: true, Disposable: true });
        Assert.Equal(new Uri("http://ha.test:8123"), ha.BaseUrl);

        // The accounts whose presses are agents': a list of ids, and nothing else.
        Assert.Equal(0, Run("config", "set", "homeAssistant.agentUserIds", """["agent-user"]"""));
        Assert.Equal(["agent-user"], HostSettings.Load(folder).Settings.HomeAssistant.AgentUserIds);
        Assert.Equal(1, Run("config", "set", "homeAssistant.agentUserIds", "agent-user"));
        Assert.Equal(0, Run("config", "unset", "homeAssistant.agentUserIds"));
        Assert.Empty(HostSettings.Load(folder).Settings.HomeAssistant.AgentUserIds);
    }

    [Fact]
    public void Usage_is_set_at_the_machine_and_a_token_pasted_where_a_login_belongs_is_not_written_or_repeated()
    {
        Assert.Equal(0, Run("config", "set", "usage.copilotAccounts", """["dswett_microsoft"]"""));
        Assert.Equal(0, Run("config", "set", "usage.idleIntervalSeconds", "600"));
        var settings = HostSettings.Load(folder).Settings;
        Assert.Equal([(2, "dswett_microsoft")], settings.Usage.CopilotAccounts.Select(a => (a.Slot, a.Login)));
        Assert.Equal(TimeSpan.FromMinutes(10), settings.Usage.IdleInterval);
        Assert.Empty(settings.UsageProblems);

        var text = File.ReadAllText(Path.Combine(folder, HostSettings.FileName));
        Assert.Equal(1, Run("config", "set", "usage.copilotAccounts", """["gho_0123456789abcdef"]"""));
        Assert.Contains("Not written: usage.copilotAccounts entry 1 looks like a token", Said);
        Assert.DoesNotContain("gho_", Said);
        // Never on disk at all, not even while it is being checked.
        Assert.DoesNotContain(Directory.GetFiles(folder, "*", SearchOption.AllDirectories), f => File.ReadAllText(f).Contains("gho_", StringComparison.Ordinal));
        Assert.Equal(1, Run("config", "set", "usage.publish", "off"));
        Assert.Equal(text, File.ReadAllText(Path.Combine(folder, HostSettings.FileName)));

        Assert.Equal(0, Run("config", "set", "usage.publish", "false"));
        Assert.False(HostSettings.Load(folder).Settings.Usage.Publish);

        // An empty place is kept on purpose (as shadow-week.ps1 copies agent-ha-bridge's list), and
        // written: the login after it keeps its slot.
        Assert.Equal(0, Run("config", "set", "usage.copilotAccounts", "[\"alt-login\",\"\",\"third\"]"));
        Assert.Equal([(2, "alt-login"), (4, "third")], HostSettings.Load(folder).Settings.Usage.CopilotAccounts.Select(a => (a.Slot, a.Login)));
        Assert.Empty(HostSettings.Load(folder).Settings.UsageProblems);
    }

    [Fact]
    public void The_home_assistant_token_is_read_without_an_argument_kept_protected_and_never_shown()
    {
        const string token = "fixture-token-that-must-not-be-printed";
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(0, HaCommands.Run(["ha", "token", "set"], folder, () => token, output, error, devBuild: true, agentHaBridge: () => null));
        Assert.DoesNotContain(token, output.ToString(), StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(folder, HostSettings.FileName)));
        Assert.Equal(token, AgentBridge.HomeAssistant.HaTokenStore.Load(folder));

        File.WriteAllText(Path.Combine(folder, HostSettings.FileName), """{ "homeAssistant": { "enabled": true, "baseUrl": "http://ha.test:8123" } }""");
        output = new StringWriter();
        Assert.Equal(0, HaCommands.Run(["ha", "status"], folder, () => null, output, error, devBuild: true, agentHaBridge: () => null));
        var status = output.ToString();
        Assert.Contains("development build", status, StringComparison.Ordinal);
        Assert.Contains("token: stored, protected", status, StringComparison.Ordinal);
        Assert.DoesNotContain(token, status, StringComparison.Ordinal);

        Assert.Equal(1, HaCommands.Run(["ha", "token", "set"], folder, () => "  ", output, error, devBuild: true, agentHaBridge: () => null));
        Assert.Equal(token, AgentBridge.HomeAssistant.HaTokenStore.Load(folder));
        Assert.Equal(0, HaCommands.Run(["ha", "token", "clear"], folder, () => null, output, error, devBuild: true, agentHaBridge: () => null));
        Assert.Null(AgentBridge.HomeAssistant.HaTokenStore.Load(folder));
    }
}