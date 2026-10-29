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
    public void A_setting_that_cannot_be_read_as_true_or_false_turns_remote_approval_off_and_says_why(string text)
    {
        var (settings, problem) = With(text);

        Assert.False(settings.RemoteApproval);
        Assert.Contains("remote approval of permission prompts and launching are off", problem);
        Assert.False(settings.Launch.Enabled);
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
    }
}