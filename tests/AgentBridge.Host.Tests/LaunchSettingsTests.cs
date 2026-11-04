using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Host;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

public sealed class LaunchSettingsTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "ab-launch-settings-" + Guid.NewGuid().ToString("N"));
    string PathOfConfig => Path.Combine(folder, HostSettings.FileName);
    string? readOnly;

    public LaunchSettingsTests() => Directory.CreateDirectory(folder);
    public void Dispose() => Directory.Delete(folder, recursive: true);

    HostControl Control() => new(new HostControlOptions
    {
        Profile = "fixture",
        Folder = folder,
        Version = "0.1.0",
        Machine = new MachineInfo("fixture", "Fixture", "windows", "0.1.0"),
        Running = HostControl.Settings(HostSettings.Load(folder).Settings, HostConfigFile.ReadName(folder)),
        SignIn = (_, _, _) => throw new InvalidOperationException("No sign-in in these tests."),
        SignedIn = (_, _) => { },
        Stop = () => { },
        Restart = () => throw new InvalidOperationException("Saving must not restart the host."),
        ReadOnlyNow = () => readOnly,
    });

    static async Task<SettingsResult> Save(HostControl control, SettingsRequest request) =>
        Assert.IsType<SettingsResult>(await control.HandleAsync(null!, HostOps.Settings, JsonSerializer.SerializeToElement(request, ProtocolJson.Options)));

    [Fact]
    public async Task Saving_launch_defaults_preserves_other_choices_and_unavailable_approved_folders_without_restarting()
    {
        var unavailable = Path.Combine(folder, "offline-repository");
        var root = new JsonObject
        {
            ["name"] = "Original",
            ["future"] = new JsonObject { ["keep"] = true },
            ["launch"] = new JsonObject
            {
                ["workspaces"] = new JsonArray(new JsonObject { ["label"] = "Repo", ["path"] = unavailable, ["future"] = 42 }),
                ["defaultWorkspace"] = "Repo",
                ["remoteFolderTrust"] = false,
                ["agents"] = new JsonObject { ["claude"] = new JsonObject { ["model"] = "retained" } },
            },
        };
        File.WriteAllText(PathOfConfig, root.ToJsonString());
        var control = Control();
        var was = control.Status().Settings.Launch!;
        Assert.Equal("Repo", was.DefaultWorkspace);
        Assert.False(control.Status().RestartRequired);

        var saved = await Save(control, new SettingsRequest(Launch: was with { DefaultAgent = "claude" }, LaunchWas: was, LaunchConfirmed: true));

        Assert.False(saved.Conflict);
        Assert.True(saved.RestartRequired);
        Assert.Equal("claude", saved.Settings.Launch!.DefaultAgent);
        Assert.Equal(unavailable, Assert.Single(saved.Settings.Launch.Workspaces).Path);
        var written = JsonNode.Parse(File.ReadAllText(PathOfConfig))!;
        Assert.True(written["future"]!["keep"]!.GetValue<bool>());
        Assert.Equal(42, written["launch"]!["workspaces"]![0]!["future"]!.GetValue<int>());
        Assert.Equal("retained", written["launch"]!["agents"]!["claude"]!["model"]!.GetValue<string>());
        Assert.False(written["launch"]!["remoteFolderTrust"]!.GetValue<bool>());
        Assert.Equal(LaunchProtocol.Ask, saved.Settings.Launch.Permissions);
    }

    [Fact]
    public async Task A_stale_workspace_list_never_restores_a_revoked_folder_or_applies_other_fields()
    {
        var control = Control();
        var was = control.Status().Settings.Launch!;
        const string changed = """{"name":"Changed elsewhere","launch":{"defaultAgent":"codex"}}""";
        File.WriteAllText(PathOfConfig, changed);

        var result = await Save(control, new SettingsRequest(Name: "Must not apply",
            Launch: was with { Permissions = LaunchProtocol.AllowAll }, LaunchWas: was, LaunchConfirmed: true));

        Assert.True(result.Conflict);
        Assert.Equal("codex", result.Settings.Launch!.DefaultAgent);
        Assert.Equal(changed, File.ReadAllText(PathOfConfig));
    }

    [Fact]
    public async Task Launch_changes_require_confirmation_and_a_fresh_writable_host_and_reject_a_broken_config()
    {
        var control = Control();
        var was = control.Status().Settings.Launch!;
        var request = new SettingsRequest(Launch: was with { Enabled = false }, LaunchWas: was);
        Assert.Contains("confirm", (await Assert.ThrowsAsync<ArgumentException>(() => Save(control, request))).Message);
        Assert.False(File.Exists(PathOfConfig));

        request = request with { LaunchConfirmed = true };
        readOnly = "fixture legacy host appeared after the status";
        Assert.Contains("read-only", (await Assert.ThrowsAsync<InvalidOperationException>(() => Save(control, request))).Message);
        Assert.False(File.Exists(PathOfConfig));
        readOnly = null;
        const string broken = """{"launch":{"permissions":"not-a-permission-mode"}}""";
        File.WriteAllText(PathOfConfig, broken);
        Assert.Contains("fix config.json", (await Assert.ThrowsAsync<InvalidOperationException>(() => Save(control, request))).Message);
        Assert.Equal(broken, File.ReadAllText(PathOfConfig));
    }

    [Fact]
    public void Workspace_validation_reuses_physical_paths_and_refuses_unsafe_or_ambiguous_authorizations()
    {
        var workspace = new ApprovedWorkspace("Fixture", folder);
        var valid = HostLaunchSettings.Default with { Workspaces = [workspace], DefaultWorkspace = "Fixture" };
        Assert.Equal(valid, LaunchSettingsEditor.Check(valid, HostLaunchSettings.Default, _ => false));
        Assert.Throws<ArgumentException>(() => LaunchSettingsEditor.Check(valid, HostLaunchSettings.Default, _ => true));
        Assert.Throws<ArgumentException>(() => LaunchSettingsEditor.Check(valid with { Workspaces = [workspace, workspace with { Label = "Duplicate" }] }, HostLaunchSettings.Default, _ => false));
        Assert.Throws<ArgumentException>(() => LaunchSettingsEditor.Check(valid with { Workspaces = [workspace with { Path = "relative" }] }, HostLaunchSettings.Default, _ => false));
        Assert.Throws<ArgumentException>(() => LaunchSettingsEditor.Check(valid with { Workspaces = [workspace with { Path = Path.Combine(folder, "missing") }] }, HostLaunchSettings.Default, _ => false));
        Assert.Throws<ArgumentException>(() => LaunchSettingsEditor.Check(valid with { Workspaces = [workspace with { Isolate = true }] }, HostLaunchSettings.Default, _ => false));
        Assert.Throws<ArgumentException>(() => LaunchSettingsEditor.Check(valid with { DefaultWorkspace = "not approved" }, HostLaunchSettings.Default, _ => false));
        Assert.Throws<ArgumentException>(() => LaunchSettingsEditor.Check(valid with { Permissions = "unsafe typo" }, HostLaunchSettings.Default, _ => false));
        Assert.Throws<ArgumentException>(() => LaunchSettingsEditor.Check(valid with { Workspaces = [null!] }, HostLaunchSettings.Default, _ => false));
    }

    [Fact]
    public async Task The_app_and_config_CLI_cannot_interleave_writes_while_the_other_has_the_profile_lock()
    {
        var control = Control();
        using (HostConfigFile.Lock(folder))
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(1, ConfigCommands.Run(["config", "set", "launch.defaultAgent", "claude"], folder, output, error));
            Assert.NotEmpty(error.ToString());
            await Assert.ThrowsAsync<IOException>(() => Save(control, new SettingsRequest(Name: "Not written")));
            Assert.False(File.Exists(PathOfConfig));
        }
        var saved = await Save(control, new SettingsRequest(Name: "After release"));
        Assert.Equal("After release", saved.Settings.Name);
    }
}
