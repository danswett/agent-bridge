using System.Text.Json;
using System.Security.AccessControl;
using System.Security.Principal;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.AgentMessaging;
using AgentBridge.Host.Lite;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

namespace AgentBridge.Host.Tests;

public sealed class LiteLaunchLogTests : IDisposable
{
    const string Id = "11111111-1111-1111-1111-111111111111";
    readonly string root = Path.Combine(Path.GetTempPath(), "lite-launch-" + Guid.NewGuid().ToString("N"));
    readonly P256KeyPair key = P256KeyPair.Generate();
    readonly List<string> log = [];
    SessionProcess? process = new(42, DateTimeOffset.UnixEpoch.AddTicks(1234567));
    public LiteLaunchLogTests() => Directory.CreateDirectory(root);
    public void Dispose() { key.Dispose(); Directory.Delete(root, true); }
    LiteLaunchLog Open() => new(id => id == Id ? process : null, root, key, log.Add);
    static void Launch(LiteLaunchLog launches, string mode = LaunchProtocol.Ask) =>
        launches.Launched(Id, new LaunchedWith { Agent = "copilot", Permissions = mode });
    static Task<GateDecision> All(LiteLaunchLog launches, AgentMessagingSettings settings) =>
        new AllowlistedAllTargetsGate(launches, () => settings).CheckAsync(null!, Id);
    static AgentMessagingSettings Enabled => new()
    {
        Remote = AgentMessagingSettings.OwnAccount, RemoteSet = true,
        AllowAllLaunchedTargets = true
    };

    [Fact]
    public async Task A_restart_keeps_a_host_launched_forced_ask_target_with_exact_process_ticks()
    {
        Launch(Open());
        Assert.True((await new ForcedAskGate(Open()).CheckAsync(null!, Id)).Allowed);
        Assert.Single(Directory.GetFiles(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_changed_pid_or_reused_pid_is_refused_and_pruned_across_restarts(bool reuse)
    {
        var launches = Open(); Launch(launches);
        var original = process;
        process = reuse ? process! with { StartedAt = process.StartedAt!.Value.AddTicks(1) } : process! with { Id = 43 };
        Assert.Null(launches.PermissionsOf(Id));
        Assert.Null(Open().PermissionsOf(Id));
        process = original;
        Assert.Null(Open().PermissionsOf(Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_hand_started_session_never_receives_even_if_listed_or_all_launched_is_enabled(bool listed)
    {
        var settings = Enabled with { AllowAllTargetSessions = listed ? [Id] : [] };
        Assert.False((await All(Open(), settings)).Allowed);
        Assert.False((await new ForcedAskGate(Open()).CheckAsync(null!, Id)).Allowed);
    }

    [Fact]
    public async Task The_all_launched_switch_survives_restart_requires_remote_and_leaves_the_exact_list_path_unchanged()
    {
        Launch(Open(), LaunchProtocol.AllowAll);
        var launches = Open();
        Assert.True((await All(launches, Enabled)).Allowed);
        Assert.False((await All(launches, Enabled with { Remote = AgentMessagingSettings.Off })).Allowed);
        Assert.False((await All(launches, Enabled with { AllowAllLaunchedTargets = false })).Allowed);
        var listed = Enabled with { AllowAllLaunchedTargets = false, AllowAllTargets = true, AllowAllTargetSessions = [Id] };
        Assert.True((await All(launches, listed)).Allowed);
        Assert.False((await All(launches, listed with { AllowAllTargets = false })).Allowed);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"allowAllLaunchedTargets":true}""")]
    [InlineData("""{"remote":"off","allowAllLaunchedTargets":true}""")]
    [InlineData("""{"remote":"own-account","allowAllLaunchedTargets":"yes"}""")]
    [InlineData("""{"remote":"own-account","allowAllLaunchedTargets":null}""")]
    public void The_new_setting_defaults_off_and_invalid_or_disabled_settings_never_open_it(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.False(AgentMessagingSettings.Read(doc.RootElement).Settings.AllowAllLaunchedTargets);
    }

    [Fact]
    public void A_person_can_set_the_switch_with_the_existing_config_command()
    {
        Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.remote", "own-account"], root, TextWriter.Null, TextWriter.Null));
        Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.allowAllLaunchedTargets", "true"], root, TextWriter.Null, TextWriter.Null));
        Assert.True(HostSettings.CurrentAgentMessaging(root).AllowAllLaunchedTargets);
        Assert.Equal(0, ConfigCommands.Run(["config", "set", "agentMessaging.allowAllLaunchedTargets", "false"], root, TextWriter.Null, TextWriter.Null));
        Assert.False(HostSettings.CurrentAgentMessaging(root).AllowAllLaunchedTargets);
        new LiteKill(root).Kill();
        Assert.False(new LiteKill(root).On);
        Assert.False(HostSettings.CurrentAgentMessaging(root).AllowAllLaunchedTargets);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_corrupt_or_tampered_launch_file_loads_empty(bool validJson)
    {
        Launch(Open());
        var path = Assert.Single(Directory.GetFiles(root));
        File.WriteAllText(path, validJson ? """{"11111111-1111-1111-1111-111111111111":{"Permissions":"allowAll","Process":{"Id":42,"StartedAt":"1970-01-01T00:00:00Z"}}}""" : "{broken");
        Assert.Null(Open().PermissionsOf(Id));
        Assert.NotEmpty(log);
    }

    [Fact]
    public void A_dead_session_is_pruned_and_an_unknown_start_time_never_counts()
    {
        Launch(Open()); var original = process; process = null;
        Assert.Null(Open().PermissionsOf(Id));
        process = original; Assert.Null(Open().PermissionsOf(Id));
        process = process! with { StartedAt = null };
        var launches = Open(); Launch(launches); Assert.Null(launches.PermissionsOf(Id));
    }

    [Fact]
    public void An_authenticated_file_changed_by_one_byte_or_read_under_another_host_key_is_empty()
    {
        Launch(Open());
        using var other = P256KeyPair.Generate();
        Assert.Null(new LiteLaunchLog(_ => process, root, other, log.Add).PermissionsOf(Id));
        var path = Path.Combine(root, LiteLaunchLog.FileName);
        var bytes = File.ReadAllBytes(path); bytes[^1] ^= 1; File.WriteAllBytes(path, bytes);
        Assert.Null(Open().PermissionsOf(Id));
        Assert.Equal(2, log.Count);
    }

    [Fact]
    public void An_unreadable_record_or_failed_write_grants_nothing_and_is_logged()
    {
        Directory.CreateDirectory(Path.Combine(root, LiteLaunchLog.FileName));
        var launches = Open(); Assert.Null(launches.PermissionsOf(Id));
        Launch(launches); Assert.Null(launches.PermissionsOf(Id));
        Assert.NotEmpty(log);
    }

    [Fact]
    public void The_launch_record_has_a_same_user_only_acl()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Launch(Open());
        var acl = new FileInfo(Path.Combine(root, LiteLaunchLog.FileName)).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        Assert.True(acl.AreAccessRulesProtected);
        Assert.Equal(WindowsIdentity.GetCurrent().User, acl.GetOwner(typeof(SecurityIdentifier)));
        Assert.Equal(WindowsIdentity.GetCurrent().User, Assert.Single(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()).IdentityReference);
    }

    [Fact]
    public void An_oversized_record_is_empty()
    {
        using (var file = File.Create(Path.Combine(root, LiteLaunchLog.FileName))) { file.SetLength(1024 * 1024 + 1); }
        Assert.Null(Open().PermissionsOf(Id));
        Assert.NotEmpty(log);
    }
}
