using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.Hooks;
using AgentBridge.Host;
using AgentBridge.Host.Core.Intake;
using AgentBridge.Host.Migration;
using Xunit;

// Migrating a machine from agent-ha-bridge to the host, and rolling it back (docs/migration.md,
// issue #38), against a fake installation laid out as agent-ha-bridge's installers lay one out.
public sealed class MigrationTests : IAsyncLifetime
{
    FakeAgentHaBridge _f = null!;

    public Task InitializeAsync()
    {
        _f = new FakeAgentHaBridge();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _f.DisposeAsync();

    string Text(string path) => File.ReadAllText(path);

    // Where profiles live; the fixture's is "default".
    string Root => Path.GetDirectoryName(_f.Profile)!;

    [Fact]
    public async Task A_dry_run_describes_every_change_and_makes_none()
    {
        var before = _f.Listing();

        var report = await _f.Migrator().MigrateAsync(dryRun: true);

        Assert.True(report.Ok, report.ToString());
        var text = report.ToString();
        Assert.Contains("Would migrate", text);
        Assert.Contains("copilot: re-point 3 hook(s)", text);
        Assert.Contains("claude: re-point 5 hook(s)", text);
        Assert.Contains("codex: re-point 6 hook(s)", text);
        Assert.Contains("newSession.workspaces -> launch.workspaces (2 approved)", text);
        Assert.Contains("homeAssistant.token (a secret: never copied", text);
        Assert.Contains("dashboard.publication (this host never publishes the dashboard, G7)", text);
        Assert.Contains("Dry run: nothing was changed.", text);
        Assert.Equal(before, _f.Listing());
        Assert.False(Directory.Exists(_f.HookFolder));
        Assert.Empty(_f.Runtime_.Calls);
        Assert.Equal(0, _f.Host.Restarts);
    }

    [Fact]
    public async Task The_dashboards_publication_writer_is_refused_before_anything_is_touched()
    {
        var config = FakeAgentHaBridge.DefaultConfig();
        config["dashboard"]!["publication"]!["participant"] = "desk";
        await using var writer = new FakeAgentHaBridge(config);
        var before = writer.Listing();

        var report = await writer.Migrator().MigrateAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("publication writer (desk)", report.ToString());
        Assert.Equal(before, writer.Listing());
        Assert.Empty(writer.Runtime_.Calls);
        Assert.False(File.Exists(Path.Combine(writer.Profile, MigrationRecord.FileName)));
    }

    [Fact]
    public async Task A_publication_writer_named_in_other_letter_case_is_still_the_writer_as_agent_ha_bridge_reads_it()
    {
        var config = FakeAgentHaBridge.DefaultConfig();
        var publication = config["dashboard"]!["publication"]!.DeepClone();
        publication["participant"] = "desk";
        config.Remove("dashboard");
        config["Dashboard"] = new JsonObject { ["Publication"] = publication };
        await using var writer = new FakeAgentHaBridge(config);
        var before = writer.Listing();

        var report = await writer.Migrator().MigrateAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("publication writer (desk)", report.ToString());
        Assert.Equal(before, writer.Listing());
    }

    [Fact]
    public async Task A_configuration_naming_one_setting_twice_in_different_case_is_refused_as_unreadable()
    {
        await using var odd = new FakeAgentHaBridge();
        var text = File.ReadAllText(odd.ConfigPath).TrimEnd().TrimEnd('}') + ", \"DASHBOARD\": {} }";
        File.WriteAllText(odd.ConfigPath, text);
        var before = odd.Listing();

        var report = await odd.Migrator().MigrateAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("could not be read", report.ToString());
        Assert.Equal(before, odd.Listing());
    }

    [Fact]
    public void A_relative_installation_path_is_made_absolute_before_anything_is_derived_from_it()
    {
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, _f.BridgeHome);
        if (Path.IsPathRooted(relative)) { return; }

        var (install, problem) = AgentHaBridgeInstall.Read(relative + Path.DirectorySeparatorChar);

        Assert.Null(problem);
        Assert.Equal(_f.BridgeHome, install!.BridgeHome);
        Assert.True(Path.IsPathFullyQualified(install.RuntimeRoot));
    }

    [Fact]
    public async Task A_migration_or_rollback_already_running_for_this_user_refuses_another_before_it_reads_anything()
    {
        var before = _f.Listing();
        // Another profile's migration, in another process.
        await using (new FileStream(Path.Combine(Root, "migration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose))
        {
            var report = await _f.Migrator().MigrateAsync(dryRun: false);
            Assert.False(report.Ok);
            Assert.Contains("another migrate or rollback is running", report.ToString());
            Assert.Equal(before, _f.Listing());
            Assert.Empty(_f.Runtime_.Calls);
            Assert.False((await _f.Migrator().RollbackAsync(dryRun: false)).Ok);
            // A dry run only reads.
            Assert.True((await _f.Migrator().MigrateAsync(dryRun: true)).Ok);
        }

        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
    }

    [Fact]
    public async Task A_model_set_for_every_agent_at_once_reaches_each_of_them()
    {
        var config = FakeAgentHaBridge.DefaultConfig();
        config["newSession"]!["model"] = "house-model";
        await using var f = new FakeAgentHaBridge(config);

        Assert.True((await f.Migrator().MigrateAsync(dryRun: false)).Ok);

        var agents = JsonNode.Parse(File.ReadAllText(Path.Combine(f.Profile, "config.json")))!["launch"]!["agents"]!;
        Assert.All(new[] { "copilot", "claude", "codex" }, id => Assert.Equal("house-model", (string?)agents[id]!["model"]));
    }

    [Fact]
    public void Only_no_such_task_counts_as_a_missing_task_and_a_refusal_from_the_scheduler_does_not()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Assert.True(WindowsAgentHaBridgeRuntime.TaskMissing(new System.Runtime.InteropServices.COMException("not found", unchecked((int)0x80070002))));
        Assert.True(WindowsAgentHaBridgeRuntime.TaskMissing(new FileNotFoundException()));
        Assert.False(WindowsAgentHaBridgeRuntime.TaskMissing(new System.Runtime.InteropServices.COMException("denied", unchecked((int)0x80070005))));
        Assert.False(WindowsAgentHaBridgeRuntime.TaskMissing(new UnauthorizedAccessException()));
    }

    [Theory]
    [InlineData("{\"authority\":\"home\",\"participant\":\"laptop\"}")]
    [InlineData("\"desk\"")]
    public async Task A_publication_policy_that_cannot_be_read_is_refused(string policy)
    {
        var config = FakeAgentHaBridge.DefaultConfig();
        config["dashboard"]!["publication"] = JsonNode.Parse(policy);
        await using var fake = new FakeAgentHaBridge(config);

        var report = await fake.Migrator().MigrateAsync(dryRun: true);

        Assert.False(report.Ok);
        Assert.Contains("cannot be told; migration is refused (G7)", report.ToString());
    }

    [Fact]
    public async Task A_configuration_that_cannot_be_read_is_refused_since_its_policy_cannot_be_checked()
    {
        File.WriteAllText(_f.ConfigPath, "{ not json");
        var before = _f.Listing();

        var report = await _f.Migrator().MigrateAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("could not be read", report.ToString());
        Assert.Contains("publication policy cannot be checked", report.ToString());
        Assert.Equal(before, _f.Listing());
    }

    [Fact]
    public async Task Migration_is_refused_when_no_host_serves_the_profile_to_check_the_hand_over()
    {
        _f.Host.Running = false;
        var before = _f.Listing();

        var report = await _f.Migrator().MigrateAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("no host is serving this profile", report.ToString());
        Assert.Equal(before, _f.Listing());
        Assert.Empty(_f.Runtime_.Calls);
    }

    [Fact]
    public async Task Migrating_repoints_only_agent_ha_bridges_hooks_carries_its_settings_and_hands_the_spool_to_the_host()
    {
        var ahbConfig = File.ReadAllBytes(_f.ConfigPath);
        var installation = File.ReadAllBytes(Path.Combine(_f.BridgeHome, "installation.json"));

        var report = await _f.Migrator().MigrateAsync(dryRun: false);

        Assert.True(report.Ok, report.ToString());
        var hook = _f.InstalledHook;
        Assert.Equal(File.ReadAllBytes(_f.HookSource), File.ReadAllBytes(hook));
        Assert.Equal(_f.Runtime, HookRunner.RuntimeRoot(_f.InstallationHooks).Root);

        // Copilot: each of its entries runs the host's hook with no shell; the person's stays.
        var copilot = FakeAgentHaBridge.Read(_f.CopilotHooks)["hooks"]!;
        Assert.Equal("& 'C:\\mine\\audit.ps1'", (string?)copilot["preToolUse"]![0]!["powershell"]);
        var ask = copilot["preToolUse"]![1]!;
        Assert.Equal(hook, (string?)ask["exec"]);
        Assert.Equal(["copilot", "ask_user"], ask["args"]!.AsArray().Select(a => (string)a!));
        Assert.Equal("ask_user", (string?)ask["matcher"]);
        Assert.Equal(120, (int)ask["timeoutSec"]!);
        Assert.Equal(["copilot", "agent_stop"], copilot["agentStop"]![0]!["args"]!.AsArray().Select(a => (string)a!));
        Assert.Equal(["copilot", "permission"], copilot["notification"]![0]!["args"]!.AsArray().Select(a => (string)a!));

        // Claude: quoted, by name; its byte order mark, settings and the person's hook are kept.
        Assert.True(File.ReadAllBytes(_f.ClaudeSettings).AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]));
        var claude = FakeAgentHaBridge.Read(_f.ClaudeSettings);
        Assert.Equal("opus", (string?)claude["model"]);
        Assert.Equal("C:\\mine\\guard.cmd", (string?)claude["hooks"]!["PreToolUse"]![0]!["hooks"]![0]!["command"]);
        Assert.Equal($"\"{hook}\" claude ask", (string?)claude["hooks"]!["PreToolUse"]![1]!["hooks"]![0]!["command"]);
        Assert.Equal($"\"{hook}\" claude register", (string?)claude["hooks"]!["UserPromptSubmit"]![0]!["hooks"]![0]!["command"]);

        // Codex: unquoted, all six events.
        var codex = FakeAgentHaBridge.Read(_f.CodexHooks)["hooks"]!.AsObject();
        Assert.Equal(6, codex.Count);
        Assert.All(codex, e => Assert.Equal($"{hook} codex hook", (string?)e.Value![0]!["hooks"]![0]!["command"]));

        // Settings carried with their meaning; no secret, and never the publication role.
        var host = Text(Path.Combine(_f.Profile, "config.json"));
        Assert.DoesNotContain(FakeAgentHaBridge.Token, host);
        Assert.DoesNotContain("fake-agent-token", host);
        Assert.DoesNotContain("publication", host);
        var settings = JsonNode.Parse(host)!;
        Assert.Equal(2, settings["launch"]!["workspaces"]!.AsArray().Count);
        Assert.True((bool)settings["launch"]!["workspaces"]![0]!["isolate"]!);
        Assert.Equal("claude", (string?)settings["launch"]!["defaultAgent"]);
        Assert.Equal(50, (int)settings["launch"]!["resumeCount"]!);
        Assert.Equal("ask", (string?)settings["launch"]!["permissions"]);
        Assert.Equal("opus", (string?)settings["launch"]!["agents"]!["claude"]!["model"]);
        Assert.Equal("high", (string?)settings["launch"]!["agents"]!["codex"]!["effort"]);
        Assert.Equal(FakeAgentHaBridge.Rooted("tools", "claude.exe"), (string?)settings["launch"]!["agents"]!["claude"]!["path"]);
        Assert.Equal("--verbose", (string?)settings["launch"]!["agents"]!["codex"]!["extraArgs"]![0]);
        Assert.Equal(12, (double)settings["launch"]!["worktrees"]!["idleHours"]!);
        Assert.Equal("http://ha.example:8123", (string?)settings["homeAssistant"]!["baseUrl"]);
        Assert.Null(settings["homeAssistant"]!["enabled"]);
        Assert.Equal("u-agent", (string?)settings["homeAssistant"]!["agentUserIds"]![0]);
        var (loaded, problem) = HostSettings.Load(_f.Profile);
        Assert.Null(problem);
        Assert.Empty(loaded.LaunchProblems);
        Assert.Contains("Not carried", report.ToString());
        Assert.Contains("newSession.codexStartPrompt", report.ToString());
        Assert.Contains("devBox.*", report.ToString());

        // agent-ha-bridge's own files are as they were; its task is disabled, not deleted.
        Assert.Equal(ahbConfig, File.ReadAllBytes(_f.ConfigPath));
        Assert.Equal(installation, File.ReadAllBytes(Path.Combine(_f.BridgeHome, "installation.json")));
        Assert.Equal((false, false), _f.Runtime_.Tasks[$"AgentBridgeDaemon_{FakeAgentHaBridge.Id}"]);

        // The hand-over was proven: the host takes the spool, and the hook's event was taken.
        Assert.Equal(_f.Runtime, IntakeState.Load(_f.Profile).State!.RuntimeRoot);
        Assert.True(_f.Host.TakingSpool);
        Assert.Contains("copilot agent_stop", _f.Hook.Runs);
        Assert.Empty(Directory.EnumerateFiles(SpoolContract.SpoolDirectory(_f.Runtime), "*.json"));
        Assert.Equal("migrated", MigrationRecord.Load(_f.Profile).Record!.State);
        Assert.Contains(Text(SpoolContract.LogPath(_f.Runtime)).Split('\n'), l => l.Contains("\"agent_stop\"") && l.Contains("\"spool\""));
    }

    [Fact]
    public async Task Rolling_back_restores_every_file_byte_for_byte_and_starts_agent_ha_bridge_again()
    {
        var before = _f.Listing();
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        Assert.NotEqual(before, _f.Listing());

        var dry = await _f.Migrator().RollbackAsync(dryRun: true);
        Assert.True(dry.Ok, dry.ToString());
        Assert.True(_f.Host.TakingSpool);
        Assert.False(_f.Runtime_.DaemonRunning);

        var report = await _f.Migrator().RollbackAsync(dryRun: false);

        Assert.True(report.Ok, report.ToString());
        Assert.Equal(before, _f.Listing());
        Assert.False(_f.Host.TakingSpool);
        Assert.True(_f.Runtime_.DaemonRunning);
        Assert.Equal((true, true), _f.Runtime_.Tasks[$"AgentBridgeDaemon_{FakeAgentHaBridge.Id}"]);
        Assert.Null(MigrationRecord.Load(_f.Profile).Record);
        Assert.Contains("is kept", report.ToString());
        Assert.True(File.Exists(_f.InstalledHook));

        // And again: a second migration after a rollback works the same way.
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        Assert.True((await _f.Migrator().RollbackAsync(dryRun: false)).Ok);
        Assert.Equal(before, _f.Listing());
    }

    [Fact]
    public async Task A_hook_file_edited_while_migrated_gets_only_its_own_entries_back()
    {
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        var claude = FakeAgentHaBridge.Read(_f.ClaudeSettings);
        claude["theme"] = "dark";
        File.WriteAllText(_f.ClaudeSettings, claude.ToJsonString(new() { WriteIndented = true }));

        var report = await _f.Migrator().RollbackAsync(dryRun: false);

        Assert.True(report.Ok, report.ToString());
        Assert.Contains("only the command of its 5 re-pointed hook(s) was put back", report.ToString());
        var after = FakeAgentHaBridge.Read(_f.ClaudeSettings);
        Assert.Equal("dark", (string?)after["theme"]);
        var commands = after["hooks"]!.AsObject().SelectMany(e => e.Value!.AsArray()).SelectMany(m => m!["hooks"]!.AsArray()).Select(h => (string)h!["command"]!).ToList();
        Assert.Contains(commands, c => c.StartsWith($"\"{_f.GoHook}\" claude ask ", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, c => c.Contains("agent-bridge-host-hook", StringComparison.Ordinal));
        Assert.Contains("C:\\mine\\guard.cmd", commands);
    }

    [Theory]
    [InlineData("tasks missing")]
    [InlineData("a file that is not an object")]
    [InlineData("a file without existed")]
    [InlineData("a file without its changes")]
    [InlineData("a task without enabled")]
    [InlineData("a file without after")]
    [InlineData("a file without before")]
    [InlineData("a record without hook")]
    public async Task A_record_missing_what_it_was_written_with_refuses_the_rollback_and_keeps_its_snapshot(string damage)
    {
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        var path = MigrationRecord.PathIn(_f.Profile);
        var record = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var files = record["files"]!.AsArray();
        Assert.NotEmpty(files);
        switch (damage)
        {
            case "tasks missing": record.Remove("tasks"); break;
            case "a file that is not an object": files[0] = 1; break;
            case "a file without existed": files[0]!.AsObject().Remove("existed"); break;
            case "a file without its changes": files[0]!.AsObject().Remove("changes"); break;
            case "a file without after": files[0]!.AsObject().Remove("after"); break;
            case "a file without before": files[0]!.AsObject().Remove("before"); break;
            case "a record without hook": record.Remove("hook"); break;
            default: record["tasks"]![0]!.AsObject().Remove("enabled"); break;
        }
        File.WriteAllText(path, record.ToJsonString());
        var listing = _f.Listing();
        var snapshot = Directory.GetFiles(MigrationRecord.SnapshotIn(_f.Profile), "*", SearchOption.AllDirectories);

        var report = await _f.Migrator().RollbackAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("roll back by hand", report.ToString());
        Assert.Equal(listing, _f.Listing());
        Assert.True(File.Exists(path));
        Assert.Equal(snapshot, Directory.GetFiles(MigrationRecord.SnapshotIn(_f.Profile), "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task A_failed_hand_over_rolls_back_on_its_own()
    {
        var before = _f.Listing();
        _f.Host.IgnoreIntake = true;

        var report = await _f.Migrator(verify: TimeSpan.FromSeconds(1)).MigrateAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("did not start taking the hook spool", report.ToString());
        Assert.Contains("Rolled back.", report.ToString());
        Assert.Equal(before, _f.Listing());
        Assert.True(_f.Runtime_.DaemonRunning);
        Assert.Equal((true, true), _f.Runtime_.Tasks[$"AgentBridgeDaemon_{FakeAgentHaBridge.Id}"]);
    }

    [Fact]
    public async Task A_hook_that_cannot_spool_fails_the_hand_over_although_it_gives_the_fixed_reply()
    {
        var before = _f.Listing();
        // Something where the spool folder should be: the hook gives its reply and spools nothing.
        Directory.Delete(SpoolContract.SpoolDirectory(_f.Runtime), true);
        File.WriteAllText(SpoolContract.SpoolDirectory(_f.Runtime), "not a folder");

        var report = await _f.Migrator(verify: TimeSpan.FromSeconds(2)).MigrateAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("the installed hook did not spool its event", report.ToString());
        Assert.Equal(before, _f.Listing());
        Assert.True(_f.Runtime_.DaemonRunning);
    }

    [Fact]
    public async Task A_sessions_own_agent_stop_spooled_meanwhile_does_not_prove_a_hand_over_the_check_itself_never_spooled()
    {
        var before = _f.Listing();
        // The check's run only replies; a Copilot session ending at the same moment spools its
        // agent_stop through the same hook, and the host takes it.
        _f.Hook.Unspooled = true;
        var others = 0;
        _f.Hook.Alongside = hook =>
        {
            if (_f.Hook.Runs[^1] != "copilot agent_stop") { return; }
            others++;
            Assert.Equal(0, InProcessHook.RunAs(hook, ["copilot", "agent_stop"], "{\"sessionId\":\"s1\"}", 7).Exit);
        };

        var report = await _f.Migrator(verify: TimeSpan.FromSeconds(2)).MigrateAsync(dryRun: false);

        Assert.Equal(1, others);
        Assert.False(report.Ok);
        Assert.Contains("the installed hook did not spool its event (no spool file from its run", report.ToString());
        Assert.Equal(before, _f.Listing());
        Assert.True(_f.Runtime_.DaemonRunning);
    }

    [Fact]
    public async Task A_migration_cut_short_is_rolled_back_from_its_record()
    {
        var before = _f.Listing();
        _f.Host.Interrupt = true;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _f.Migrator().MigrateAsync(dryRun: false));
        Assert.Equal("migrating", MigrationRecord.Load(_f.Profile).Record!.State);
        Assert.Contains("migration", _f.Migrator().Status(), StringComparison.OrdinalIgnoreCase);
        _f.Host.Interrupt = false;

        var report = await _f.Migrator().RollbackAsync(dryRun: false);

        Assert.True(report.Ok, report.ToString());
        Assert.Equal(before, _f.Listing());
        Assert.True(_f.Runtime_.DaemonRunning);
    }

    [Fact]
    public async Task The_host_is_restarted_on_its_restored_settings_when_rolling_back()
    {
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        Assert.NotNull(_f.Host.ConfigAtRestart[^1]);

        Assert.True((await _f.Migrator().RollbackAsync(dryRun: false)).Ok);

        // The migration created the profile's config.json, so the host restarted without one.
        Assert.Null(_f.Host.ConfigAtRestart[^1]);
        Assert.False(_f.Host.TakingSpool);
    }

    [Fact]
    public async Task A_daemon_that_will_not_stop_leaves_everything_as_it_was()
    {
        var before = _f.Listing();
        _f.Runtime_.Stubborn = true;

        var report = await _f.Migrator().MigrateAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("still running", report.ToString());
        Assert.Equal(before, _f.Listing());
        Assert.Equal((true, true), _f.Runtime_.Tasks[$"AgentBridgeDaemon_{FakeAgentHaBridge.Id}"]);
        // Its task is turned back on, but the daemon that never stopped is not started twice.
        Assert.Equal(["disable AgentBridgeDaemon_" + FakeAgentHaBridge.Id, "stop", "enable AgentBridgeDaemon_" + FakeAgentHaBridge.Id], _f.Runtime_.Calls);
    }

    [Fact]
    public async Task A_disabled_task_is_left_disabled_by_a_rollback()
    {
        var task = $"AgentBridgeDaemon_{FakeAgentHaBridge.Id}";
        _f.Runtime_.Tasks[task] = (false, false);
        _f.Runtime_.DaemonRunning = false;
        var before = _f.Listing();

        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        var report = await _f.Migrator().RollbackAsync(dryRun: false);

        Assert.True(report.Ok, report.ToString());
        Assert.Equal((false, false), _f.Runtime_.Tasks[task]);
        Assert.False(_f.Runtime_.DaemonRunning);
        Assert.Equal(before, _f.Listing());
    }

    [Fact]
    public async Task Migrating_twice_is_refused_until_rolled_back()
    {
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        var listing = _f.Listing();

        var again = await _f.Migrator().MigrateAsync(dryRun: false);

        Assert.False(again.Ok);
        Assert.Contains("already migrated", again.ToString());
        Assert.Equal(listing, _f.Listing());
    }

    [Fact]
    public async Task A_second_profile_cannot_migrate_an_installation_another_profile_has_taken()
    {
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        var other = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(_f.Profile)!, "work")).FullName;
        var listing = _f.Listing();

        var report = await _f.Migrator(profile: other).MigrateAsync(dryRun: true);

        Assert.False(report.Ok);
        Assert.Contains("profile default already takes installation", report.ToString());
        Assert.Equal(listing, _f.Listing());
    }

    [Fact]
    public async Task A_profile_that_migrated_another_installation_is_rolled_back_before_this_one_migrates()
    {
        // The hook this product installs has one pointer, so it can serve one installation.
        var other = Directory.CreateDirectory(Path.Combine(Root, "work")).FullName;
        var elsewhere = FakeAgentHaBridge.Rooted("elsewhere", ".agent-ha-bridge");
        File.WriteAllText(Path.Combine(other, IntakeState.FileName), new JsonObject
        {
            ["version"] = IntakeState.Version, ["runtimeRoot"] = Path.Combine(elsewhere, "runtime"), ["bridgeHome"] = elsewhere,
            ["installationId"] = new string('b', 32), ["migratedAt"] = DateTimeOffset.UtcNow.ToString("o"),
        }.ToJsonString());
        var listing = _f.Listing();

        var report = await _f.Migrator().MigrateAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains($"profile work has migrated agent-ha-bridge installation {new string('b', 32)}", report.ToString());
        Assert.Equal(listing, _f.Listing());
        Assert.Empty(_f.Runtime_.Calls);
    }

    [Fact]
    public async Task A_folder_the_profile_approves_is_not_approved_again_however_agent_ha_bridge_wrote_it_and_two_of_its_own_may_share_a_label()
    {
        var config = FakeAgentHaBridge.DefaultConfig();
        var docs = FakeAgentHaBridge.Rooted("repos", "docs");
        string[] apps = [FakeAgentHaBridge.Rooted("one", "app"), FakeAgentHaBridge.Rooted("two", "app")];
        config["newSession"]!["workspaces"] = new JsonArray(Path.Combine(docs, "."), apps[0], apps[1]);
        await using var f = new FakeAgentHaBridge(config);
        Directory.CreateDirectory(f.Profile);
        var hostConfig = Path.Combine(f.Profile, "config.json");
        File.WriteAllText(hostConfig, new JsonObject
        {
            ["version"] = 1,
            ["launch"] = new JsonObject { ["workspaces"] = new JsonArray(new JsonObject { ["path"] = docs, ["label"] = "documents", ["isolate"] = true }) },
        }.ToJsonString());

        Assert.True((await f.Migrator().MigrateAsync(dryRun: false)).Ok);

        var workspaces = JsonNode.Parse(Text(hostConfig))!["launch"]!["workspaces"]!.AsArray();
        Assert.Equal([docs, apps[0], apps[1]], workspaces.Select(w => w is JsonObject o ? (string)o["path"]! : (string)w!));
        Assert.True((bool)workspaces[0]!["isolate"]!);
    }

    [Fact]
    public async Task Codex_running_agent_ha_bridges_PowerShell_directly_is_refused_when_its_new_hook_path_has_a_space()
    {
        // Codex runs its command unquoted, so it cannot be re-pointed at a path with a space; an
        // entry left running the PowerShell hook would go on writing beside the host.
        var spaced = Path.Combine(_f.Home, "App Data", "bin");
        // Entries that run its Go hook hand their events to whoever takes the spool, so they
        // can stay.
        var kept = await _f.Migrator(hookFolder: spaced).MigrateAsync(dryRun: true);
        Assert.True(kept.Ok, kept.ToString());
        Assert.Contains("Codex's hooks are left on agent-ha-bridge's", kept.ToString());
        // But not while a Codex session that may have started before they were written runs: it
        // may hold the PowerShell hooks they replaced.
        _f.Runtime_.Agents[AgentBridge.Protocol.AgentKind.Codex] = 1;
        Assert.Contains("Codex's hooks (", (await _f.Migrator(hookFolder: spaced).MigrateAsync(dryRun: true)).ToString());
        _f.Runtime_.AgentsStarted[AgentBridge.Protocol.AgentKind.Codex] = DateTimeOffset.UtcNow.AddMinutes(1);
        Assert.True((await _f.Migrator(hookFolder: spaced).MigrateAsync(dryRun: true)).Ok);
        _f.Runtime_.Agents[AgentBridge.Protocol.AgentKind.Codex] = 0;
        var codex = FakeAgentHaBridge.Read(_f.CodexHooks);
        foreach (var (_, list) in codex["hooks"]!.AsObject()) { list![0]!["hooks"]![0]!["command"] = $"pwsh -NoProfile -ExecutionPolicy Bypass -File {_f.CodexScript}"; }
        File.WriteAllText(_f.CodexHooks, codex.ToJsonString());
        var listing = _f.Listing();

        var report = await _f.Migrator(hookFolder: spaced).MigrateAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("Codex runs agent-ha-bridge's PowerShell hook directly", report.ToString());
        Assert.Equal(listing, _f.Listing());
        Assert.Empty(_f.Runtime_.Calls);
    }

    [Fact]
    public async Task Hooks_that_run_agent_ha_bridges_PowerShell_directly_are_not_re_pointed_while_a_session_that_started_with_them_runs()
    {
        // An older install: Claude's SessionStart runs the script through run-hook.cmd.
        var settings = FakeAgentHaBridge.Read(_f.ClaudeSettings);
        settings["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"] = $"\"{Path.Combine(_f.ClaudeHome, "ha-bridge", "run-hook.cmd")}\" \"{Path.Combine(_f.ClaudeHome, "ha-bridge", "register-claude-session.ps1")}\"";
        File.WriteAllText(_f.ClaudeSettings, settings.ToJsonString(new() { WriteIndented = true }));
        var listing = _f.Listing();
        _f.Runtime_.Agents[AgentBridge.Protocol.AgentKind.Claude] = 1;

        var running = await _f.Migrator().MigrateAsync(dryRun: false);

        Assert.False(running.Ok);
        Assert.Contains("1 Claude process is running", running.ToString());
        Assert.Equal(listing, _f.Listing());
        Assert.Empty(_f.Runtime_.Calls);
        // Not knowing is no better.
        _f.Runtime_.Agents[AgentBridge.Protocol.AgentKind.Claude] = null;
        Assert.Contains("could not be checked", (await _f.Migrator().MigrateAsync(dryRun: true)).ToString());
        // Sessions of the agents whose hooks run the Go hook do not matter once they are known to
        // have started with them (theirs spool to the host); one that may have started before the
        // file was last written may still hold the PowerShell hooks it replaced.
        _f.Runtime_.Agents[AgentBridge.Protocol.AgentKind.Claude] = 0;
        _f.Runtime_.Agents[AgentBridge.Protocol.AgentKind.Copilot] = 2;
        _f.Runtime_.Agents[AgentBridge.Protocol.AgentKind.Codex] = 2;
        Assert.Contains("Copilot's hooks (", (await _f.Migrator().MigrateAsync(dryRun: true)).ToString());
        _f.Runtime_.AgentsStarted[AgentBridge.Protocol.AgentKind.Copilot] = DateTimeOffset.UtcNow.AddMinutes(1);
        _f.Runtime_.AgentsStarted[AgentBridge.Protocol.AgentKind.Codex] = new DateTimeOffset(File.GetLastWriteTimeUtc(_f.CodexHooks), TimeSpan.Zero);
        Assert.Contains("2 Codex processes are running from before then", (await _f.Migrator().MigrateAsync(dryRun: true)).ToString());
        _f.Runtime_.AgentsStarted[AgentBridge.Protocol.AgentKind.Codex] = DateTimeOffset.UtcNow.AddMinutes(1);
        var report = await _f.Migrator().MigrateAsync(dryRun: false);
        Assert.True(report.Ok, report.ToString());
    }

    [Fact]
    public async Task A_hook_file_that_cannot_be_read_refuses_the_migration_before_anything_is_touched()
    {
        // Nothing in it to re-point, so nothing would check for a session started with
        // agent-ha-bridge's PowerShell hooks before it was broken.
        File.WriteAllText(_f.ClaudeSettings, "{ \"hooks\": ");
        var listing = _f.Listing();

        var report = await _f.Migrator().MigrateAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains($"{_f.ClaudeSettings} could not be read as JSON", report.ToString());
        Assert.Equal(listing, _f.Listing());
        Assert.Empty(_f.Runtime_.Calls);
    }

    [Fact]
    public async Task An_entry_of_agent_ha_bridge_s_spaced_differently_is_still_recognised_as_its_own()
    {
        // As a shell reads them: two spaces, or a tab, between the words.
        var settings = FakeAgentHaBridge.Read(_f.ClaudeSettings);
        settings["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"] = $"\"{Path.Combine(_f.ClaudeHome, "ha-bridge", "run-hook.cmd")}\"  \t\"{Path.Combine(_f.ClaudeHome, "ha-bridge", "register-claude-session.ps1")}\" ";
        File.WriteAllText(_f.ClaudeSettings, settings.ToJsonString(new() { WriteIndented = true }));
        _f.Runtime_.Agents[AgentBridge.Protocol.AgentKind.Claude] = 1;

        // Still agent-ha-bridge's PowerShell, so not while a session that started with it runs,
        var running = await _f.Migrator().MigrateAsync(dryRun: true);
        Assert.Contains("1 Claude process is running", running.ToString());
        _f.Runtime_.Agents[AgentBridge.Protocol.AgentKind.Claude] = 0;

        // and re-pointed with the rest.
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        var after = FakeAgentHaBridge.Read(_f.ClaudeSettings)["hooks"]!;
        Assert.Equal($"\"{_f.InstalledHook}\" claude register", (string?)after["SessionStart"]![0]!["hooks"]![0]!["command"]);
    }

    [Fact]
    public async Task A_workspace_flag_and_numeric_tuning_agent_ha_bridge_reads_are_carried_with_their_meaning()
    {
        var config = FakeAgentHaBridge.DefaultConfig();
        var app = FakeAgentHaBridge.Rooted("repos", "cased");
        config["newSession"]!["workspaces"] = new JsonArray(new JsonObject { ["Path"] = app, ["Isolate"] = true });
        config["newSession"]!["context"] = new JsonObject { ["codex"] = 200000 };
        config["newSession"]!["contexts"] = new JsonObject { ["codex"] = new JsonArray(200000, 400000) };
        await using var f = new FakeAgentHaBridge(config);

        Assert.True((await f.Migrator().MigrateAsync(dryRun: false)).Ok);

        var launch = JsonNode.Parse(File.ReadAllText(Path.Combine(f.Profile, "config.json")))!["launch"]!;
        var workspace = launch["workspaces"]!.AsArray().Single()!.AsObject();
        Assert.Equal(["path", "isolate"], workspace.Select(p => p.Key));
        Assert.Equal(app, (string?)workspace["path"]);
        Assert.True((bool)workspace["isolate"]!);
        Assert.Equal("200000", (string?)launch["agents"]!["codex"]!["context"]);
        Assert.Equal(["200000", "400000"], launch["agents"]!["codex"]!["contexts"]!.AsArray().Select(v => (string)v!));
    }

    [Fact]
    public async Task The_default_workspace_agent_ha_bridge_matched_whatever_its_case_stays_the_default()
    {
        var config = FakeAgentHaBridge.DefaultConfig();
        config["newSession"]!["workspaces"] = new JsonArray(
            new JsonObject { ["path"] = FakeAgentHaBridge.Rooted("repos", "docs"), ["label"] = "Docs" },
            new JsonObject { ["path"] = FakeAgentHaBridge.Rooted("repos", "app"), ["label"] = "App" });
        config["newSession"]!["defaultWorkspace"] = "app";
        await using var f = new FakeAgentHaBridge(config);

        Assert.True((await f.Migrator().MigrateAsync(dryRun: false)).Ok);

        var launch = JsonNode.Parse(File.ReadAllText(Path.Combine(f.Profile, "config.json")))!["launch"]!;
        // The host matches labels exactly, and would otherwise fall back to the first, Docs.
        Assert.Equal("App", (string?)launch["defaultWorkspace"]);
    }

    [Fact]
    public async Task The_default_workspace_is_the_folder_agent_ha_bridge_chose_under_the_label_this_profile_already_gives_it()
    {
        var config = FakeAgentHaBridge.DefaultConfig();
        var app = FakeAgentHaBridge.Rooted("repos", "app");
        config["newSession"]!["workspaces"] = new JsonArray(new JsonObject { ["path"] = app, ["label"] = "App" });
        config["newSession"]!["defaultWorkspace"] = "App";
        await using var f = new FakeAgentHaBridge(config);
        Directory.CreateDirectory(f.Profile);
        var hostConfig = Path.Combine(f.Profile, "config.json");
        File.WriteAllText(hostConfig, new JsonObject
        {
            ["version"] = 1,
            ["launch"] = new JsonObject
            {
                ["workspaces"] = new JsonArray(
                    new JsonObject { ["path"] = FakeAgentHaBridge.Rooted("repos", "docs"), ["label"] = "Docs" },
                    new JsonObject { ["path"] = app, ["label"] = "MyApp" }),
            },
        }.ToJsonString());

        Assert.True((await f.Migrator().MigrateAsync(dryRun: false)).Ok);

        // agent-ha-bridge's App is not imported beside MyApp, the same folder; "App" would match nothing.
        Assert.Equal("MyApp", (string?)JsonNode.Parse(File.ReadAllText(hostConfig))!["launch"]!["defaultWorkspace"]);
    }

    [Fact]
    public async Task The_default_workspace_keeps_the_label_the_host_tells_a_second_folder_of_the_same_name_by()
    {
        string[] apps = [FakeAgentHaBridge.Rooted("one", "app"), FakeAgentHaBridge.Rooted("two", "app")];
        var config = FakeAgentHaBridge.DefaultConfig();
        config["newSession"]!["workspaces"] = new JsonArray(apps[1]);
        config["newSession"]!["defaultWorkspace"] = "app";
        await using var f = new FakeAgentHaBridge(config);
        Directory.CreateDirectory(f.Profile);
        var hostConfig = Path.Combine(f.Profile, "config.json");
        File.WriteAllText(hostConfig, new JsonObject { ["version"] = 1, ["launch"] = new JsonObject { ["workspaces"] = new JsonArray(apps[0], apps[1]) } }.ToJsonString());

        Assert.True((await f.Migrator().MigrateAsync(dryRun: false)).Ok);

        // The host offers the second as "app (<folder>)"; plain "app" is the first.
        Assert.Equal($"app ({apps[1]})", (string?)JsonNode.Parse(File.ReadAllText(hostConfig))!["launch"]!["defaultWorkspace"]);
    }

    [Fact]
    public async Task A_default_agent_ha_bridge_told_apart_from_a_label_differing_only_in_case_stays_that_folder()
    {
        string[] apps = [FakeAgentHaBridge.Rooted("one", "app"), FakeAgentHaBridge.Rooted("two", "app")];
        var config = FakeAgentHaBridge.DefaultConfig();
        config["newSession"]!["workspaces"] = new JsonArray(
            new JsonObject { ["path"] = apps[0], ["label"] = "App" },
            new JsonObject { ["path"] = apps[1], ["label"] = "app" });
        // agent-ha-bridge offered the second as "app (<folder>)": its labels ignore case.
        config["newSession"]!["defaultWorkspace"] = $"app ({apps[1]})";
        await using var f = new FakeAgentHaBridge(config);

        Assert.True((await f.Migrator().MigrateAsync(dryRun: false)).Ok);

        var launch = JsonNode.Parse(File.ReadAllText(Path.Combine(f.Profile, "config.json")))!["launch"]!;
        // The host's labels are exact, so it offers that folder as plain "app".
        Assert.Equal("app", (string?)launch["defaultWorkspace"]);
        Assert.Contains(launch["workspaces"]!.AsArray(), w => (string?)w!["path"] == apps[1] && (string?)w["label"] == "app");
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\PowerShell\\7\\pwsh.exe\" -NoProfile -ExecutionPolicy Bypass -File \"C:\\Users\\me\\.agent-ha-bridge\\hooks\\agent-bridge-daemon.ps1\"", true)]
    [InlineData("pwsh.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"C:\\Users\\me\\.agent-ha-bridge\\hooks\\agent-bridge-daemon.ps1\"", true)]
    [InlineData("powershell.exe -noprofile -file C:\\Users\\me\\.agent-ha-bridge\\hooks\\agent-bridge-daemon.ps1 -Verbose", true)]
    [InlineData("pwsh.exe -File C:\\tools\\inspect.ps1 -File \"C:\\Users\\me\\.agent-ha-bridge\\hooks\\agent-bridge-daemon.ps1\"", false)]
    [InlineData("pwsh.exe -Command \"& 'C:\\Users\\me\\.agent-ha-bridge\\hooks\\agent-bridge-daemon.ps1'\"", false)]
    [InlineData("pwsh.exe -c echo -File C:\\Users\\me\\.agent-ha-bridge\\hooks\\agent-bridge-daemon.ps1", false)]
    [InlineData("pwsh.exe -NoProfile -File C:\\elsewhere\\hooks\\agent-bridge-daemon.ps1", false)]
    [InlineData("node.exe -File C:\\Users\\me\\.agent-ha-bridge\\hooks\\agent-bridge-daemon.ps1", false)]
    public void Only_PowerShell_running_the_daemon_s_own_script_is_taken_for_the_daemon(string commandLine, bool daemon)
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Assert.Equal(daemon, WindowsAgentHaBridgeRuntime.RunsScript(commandLine, "C:\\Users\\me\\.agent-ha-bridge\\hooks\\agent-bridge-daemon.ps1"));
    }

    [Fact]
    public async Task Two_of_agent_ha_bridge_s_choices_for_one_folder_are_both_carried_and_the_default_stays_the_one_chosen()
    {
        var app = FakeAgentHaBridge.Rooted("repos", "app");
        var config = FakeAgentHaBridge.DefaultConfig();
        config["newSession"]!["workspaces"] = new JsonArray(
            new JsonObject { ["path"] = app, ["label"] = "app" },
            new JsonObject { ["path"] = app, ["label"] = "app-isolated", ["isolate"] = true });
        config["newSession"]!["defaultWorkspace"] = "app-isolated";
        await using var f = new FakeAgentHaBridge(config);

        Assert.True((await f.Migrator().MigrateAsync(dryRun: false)).Ok);

        var launch = JsonNode.Parse(File.ReadAllText(Path.Combine(f.Profile, "config.json")))!["launch"]!;
        Assert.Equal(["app", "app-isolated"], launch["workspaces"]!.AsArray().Select(w => (string?)w!["label"]));
        // A default launch still gets a worktree of its own, not the primary checkout.
        Assert.Equal("app-isolated", (string?)launch["defaultWorkspace"]);
    }

    [Fact]
    public async Task Two_choices_for_one_folder_under_one_label_keep_the_default_on_the_isolated_one()
    {
        var app = FakeAgentHaBridge.Rooted("repos", "app");
        var config = FakeAgentHaBridge.DefaultConfig();
        config["newSession"]!["workspaces"] = new JsonArray(
            new JsonObject { ["path"] = app, ["label"] = "app" },
            new JsonObject { ["path"] = app, ["label"] = "app", ["isolate"] = true });
        // Both programs offer the second as "app (<folder>)".
        config["newSession"]!["defaultWorkspace"] = $"app ({app})";
        await using var f = new FakeAgentHaBridge(config);

        Assert.True((await f.Migrator().MigrateAsync(dryRun: false)).Ok);

        var launch = JsonNode.Parse(File.ReadAllText(Path.Combine(f.Profile, "config.json")))!["launch"]!;
        Assert.Equal($"app ({app})", (string?)launch["defaultWorkspace"]);
    }

    [Fact]
    public async Task Lists_agent_ha_bridge_read_from_one_value_are_carried_as_a_list_of_one()
    {
        var config = FakeAgentHaBridge.DefaultConfig();
        config["newSession"]!["extraArgs"] = "--banner";
        config["newSession"]!["profiles"] = "work";
        config["newSession"]!["models"] = new JsonObject { ["claude"] = "opus" };
        config["newSession"]!["contexts"] = new JsonObject { ["codex"] = 200000 };
        await using var f = new FakeAgentHaBridge(config);

        Assert.True((await f.Migrator().MigrateAsync(dryRun: false)).Ok);

        var launch = JsonNode.Parse(File.ReadAllText(Path.Combine(f.Profile, "config.json")))!["launch"]!;
        Assert.Equal(["work"], launch["profiles"]!.AsArray().Select(v => (string)v!));
        Assert.Equal(["--banner"], launch["agents"]!["copilot"]!["extraArgs"]!.AsArray().Select(v => (string)v!));
        Assert.Equal(["opus"], launch["agents"]!["claude"]!["models"]!.AsArray().Select(v => (string)v!));
        Assert.Equal(["200000"], launch["agents"]!["codex"]!["contexts"]!.AsArray().Select(v => (string)v!));
    }

    [Fact]
    public async Task A_single_workspace_agent_ha_bridge_read_as_a_list_of_one_is_carried_and_stays_the_default()
    {
        var app = FakeAgentHaBridge.Rooted("repos", "app");
        var config = FakeAgentHaBridge.DefaultConfig();
        config["newSession"]!["workspaces"] = new JsonObject { ["path"] = app, ["label"] = "App", ["isolate"] = true };
        config["newSession"]!["defaultWorkspace"] = "app";
        await using var f = new FakeAgentHaBridge(config);

        Assert.True((await f.Migrator().MigrateAsync(dryRun: false)).Ok);

        var launch = JsonNode.Parse(File.ReadAllText(Path.Combine(f.Profile, "config.json")))!["launch"]!;
        var workspace = Assert.Single(launch["workspaces"]!.AsArray())!;
        Assert.Equal(app, (string?)workspace["path"]);
        Assert.True((bool)workspace["isolate"]!);
        Assert.Equal("App", (string?)launch["defaultWorkspace"]);
    }

    [Fact]
    public async Task The_default_profile_agent_ha_bridge_matched_whatever_its_case_stays_the_default()
    {
        var config = FakeAgentHaBridge.DefaultConfig();
        config["newSession"]!["profiles"] = new JsonArray("work", "Home");
        config["newSession"]!["defaultProfile"] = "home";
        await using var f = new FakeAgentHaBridge(config);

        Assert.True((await f.Migrator().MigrateAsync(dryRun: false)).Ok);

        Assert.Equal("Home", (string?)JsonNode.Parse(File.ReadAllText(Path.Combine(f.Profile, "config.json")))!["launch"]!["defaultProfile"]);
    }

    [Fact]
    public async Task The_host_is_told_the_agent_homes_whose_hooks_were_re_pointed()
    {
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);

        var (state, problem) = AgentBridge.Host.Core.Intake.IntakeState.Load(_f.Profile);
        Assert.Null(problem);
        Assert.Equal(new(_f.CopilotHome, _f.ClaudeHome, _f.CodexHome), state!.Homes);
    }

    [Fact]
    public async Task A_settings_file_an_agent_writes_during_the_migration_is_never_overwritten_and_the_migration_is_undone()
    {
        var copilot = File.ReadAllBytes(_f.CopilotHooks);
        string? written = null;
        // A Claude session already on its native hook runs /model while the daemon is being stopped.
        _f.Runtime_.OnStop = () =>
        {
            var settings = FakeAgentHaBridge.Read(_f.ClaudeSettings);
            settings["model"] = "opus";
            File.WriteAllText(_f.ClaudeSettings, written = settings.ToJsonString(new() { WriteIndented = true }));
        };

        var report = await _f.Migrator().MigrateAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("changed while it was being rewritten", report.ToString());
        Assert.Equal(written, File.ReadAllText(_f.ClaudeSettings));
        // Known never to have been written, rather than found changed since.
        Assert.Contains($"{_f.ClaudeSettings}: unchanged by the migration", report.ToString());
        // What was written before it is put back, and agent-ha-bridge runs again.
        Assert.Equal(copilot, File.ReadAllBytes(_f.CopilotHooks));
        Assert.True(_f.Runtime_.DaemonRunning);
        Assert.Null(MigrationRecord.Load(_f.Profile).Record);
    }

    [Fact]
    public async Task A_profile_config_written_during_the_migration_is_never_overwritten()
    {
        var hostConfig = Path.Combine(_f.Profile, "config.json");
        const string mine = "{\"version\":1,\"launch\":{\"enabled\":false}}";
        _f.Runtime_.OnStop = () => File.WriteAllText(hostConfig, mine);

        var report = await _f.Migrator().MigrateAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Equal(mine, File.ReadAllText(hostConfig));
        Assert.True(_f.Runtime_.DaemonRunning);
    }

    [Fact]
    public async Task A_settings_file_that_is_a_symbolic_link_is_written_through_and_stays_a_link_both_ways()
    {
        var target = Path.Combine(_f.Home, "dotfiles", "claude-settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(_f.ClaudeSettings, target);
        try { File.CreateSymbolicLink(_f.ClaudeSettings, target); }
        // Windows lets only an administrator, or developer mode, make one.
        catch (Exception e) when (e is UnauthorizedAccessException or IOException) { return; }
        var original = File.ReadAllBytes(target);

        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);

        Assert.NotNull(new FileInfo(_f.ClaudeSettings).LinkTarget);
        Assert.Contains("agent-bridge-host-hook", File.ReadAllText(target));
        Assert.True((await _f.Migrator().RollbackAsync(dryRun: false)).Ok);
        Assert.NotNull(new FileInfo(_f.ClaudeSettings).LinkTarget);
        Assert.Equal(original, File.ReadAllBytes(target));

        // And a link something else replaced with a copy of what was migrated is made again.
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        var migrated = File.ReadAllBytes(target);
        File.Delete(_f.ClaudeSettings);
        File.WriteAllBytes(_f.ClaudeSettings, migrated);
        var back = await _f.Migrator().RollbackAsync(dryRun: false);
        Assert.True(back.Ok, back.ToString());
        Assert.Equal(target, new FileInfo(_f.ClaudeSettings).LinkTarget);
        Assert.Equal(original, File.ReadAllBytes(target));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_link_made_again_on_rollback_never_leaves_the_file_missing_nor_loses_a_save_made_meanwhile(bool savedMeanwhile)
    {
        var before = _f.Listing();
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        // Migrated through a link something has since replaced with a copy.
        var path = MigrationRecord.PathIn(_f.Profile);
        var record = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var claude = record["files"]!.AsArray().Single(f => (string?)f!["path"] == _f.ClaudeSettings)!.AsObject();
        claude["link"] = "symlink:" + Path.Combine(_f.Home, "dotfiles", "claude-settings.json");
        File.WriteAllText(path, record.ToJsonString());
        var saved = FakeAgentHaBridge.Read(_f.ClaudeSettings);
        saved["model"] = "saved meanwhile";
        Migrator.MakeLink = savedMeanwhile
            ? (link, _) => { File.WriteAllText(link, "a link"); return new FileInfo(link); }
            : (_, _) => throw new UnauthorizedAccessException("a symbolic link needs developer mode");
        Migrator.BeforeLinkSwap = savedMeanwhile ? p => File.WriteAllText(p, saved.ToJsonString()) : null;
        MigrationReport report;
        try { report = await _f.Migrator().RollbackAsync(dryRun: false); }
        finally { Migrator.MakeLink = File.CreateSymbolicLink; Migrator.BeforeLinkSwap = null; }

        Assert.True(report.Ok, report.ToString());
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_f.ClaudeSettings)!, "*agent-bridge-*"));
        if (savedMeanwhile)
        {
            Assert.Contains("changed while its link", report.ToString());
            var after = FakeAgentHaBridge.Read(_f.ClaudeSettings);
            Assert.Equal("saved meanwhile", (string?)after["model"]);
            Assert.DoesNotContain("agent-bridge-host-hook", after.ToJsonString());
        }
        else
        {
            Assert.Contains("could not be made a symbolic link", report.ToString());
            Assert.Equal(before, _f.Listing());
        }
    }

    [Fact]
    public async Task A_settings_file_with_another_hard_link_is_refused_since_no_rewrite_keeps_every_name_whole()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var other = Path.Combine(_f.Home, "dotfiles", "settings-hard.json");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        Assert.True(CreateHardLink(other, _f.ClaudeSettings, IntPtr.Zero));
        var listing = _f.Listing();

        var report = await _f.Migrator().MigrateAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains($"Refused: {_f.ClaudeSettings} has another hard link", report.ToString());
        Assert.Equal(listing, _f.Listing());
        Assert.Empty(_f.Runtime_.Calls);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    static extern bool CreateHardLink(string link, string existing, IntPtr security);

    [Fact]
    public async Task A_file_that_cannot_be_put_back_never_stops_a_rollback_turning_agent_ha_bridge_back_on()
    {
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        var copilot = Path.Combine(MigrationRecord.SnapshotIn(_f.Profile), "copilot.bak");
        var claudeBefore = File.ReadAllBytes(Path.Combine(MigrationRecord.SnapshotIn(_f.Profile), "claude.bak"));
        File.Delete(copilot);

        var report = await _f.Migrator().RollbackAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains($"{_f.CopilotHooks}: could not be put back", report.ToString());
        Assert.Equal(claudeBefore, File.ReadAllBytes(_f.ClaudeSettings));
        Assert.True(_f.Runtime_.DaemonRunning);
        Assert.All(_f.Runtime_.Tasks.Values, t => Assert.True(t.Enabled));
        // Kept for the file it could not put back.
        Assert.NotNull(MigrationRecord.Load(_f.Profile).Record);
    }

    [Fact]
    public async Task An_undo_that_cannot_put_a_file_back_still_reports_and_turns_agent_ha_bridge_back_on()
    {
        _f.Runtime_.OnStop = () => File.Delete(Path.Combine(MigrationRecord.SnapshotIn(_f.Profile), "copilot.bak"));
        _f.Host.RefuseRestart = "the app is closed";

        var report = await _f.Migrator().MigrateAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("Migration failed", report.ToString());
        Assert.Contains("could not be put back", report.ToString());
        Assert.True(_f.Runtime_.DaemonRunning);
    }

    [Fact]
    public async Task Another_profile_whose_migration_record_cannot_be_read_refuses_a_new_migration()
    {
        var other = Path.Combine(Path.GetDirectoryName(_f.Profile)!, "work");
        Directory.CreateDirectory(other);
        File.WriteAllText(MigrationRecord.PathIn(other), "{ broken");
        var listing = _f.Listing();

        var report = await _f.Migrator().MigrateAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("(unreadable migration.json)", report.ToString());
        Assert.Equal(listing, _f.Listing());
        Assert.Empty(_f.Runtime_.Calls);
    }

    [Fact]
    public async Task Another_installation_s_daemon_running_does_not_count_as_this_one_back()
    {
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        _f.Runtime_.StartsNothing = true;
        _f.Runtime_.OtherDaemon = "fedcba9876543210fedcba9876543210";

        var report = await _f.Migrator().RollbackAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("was not seen running", report.ToString());
        Assert.NotNull(MigrationRecord.Load(_f.Profile).Record);
    }

    [Fact]
    public async Task A_rollback_is_not_done_until_agent_ha_bridge_is_seen_running_not_merely_when_it_cannot_be_checked()
    {
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        _f.Runtime_.StartsNothing = true;
        _f.Runtime_.Unknown = true;

        var report = await _f.Migrator().RollbackAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("was not seen running", report.ToString());
        Assert.NotNull(MigrationRecord.Load(_f.Profile).Record);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_daemon_task_registered_from_an_administrator_prompt_is_refused_before_anything_changes(bool dryRun)
    {
        _f.Runtime_.Refused.Add($"AgentBridgeDaemon_{_f.InstallationId}");
        var listing = _f.Listing();

        var report = await _f.Migrator().MigrateAsync(dryRun);

        Assert.False(report.Ok);
        Assert.Contains("registered from an administrator (elevated) prompt", report.ToString());
        Assert.Contains($"Unregister-ScheduledTask -TaskName AgentBridgeDaemon_{_f.InstallationId}", report.ToString());
        Assert.Equal(listing, _f.Listing());
        Assert.Empty(_f.Runtime_.Calls);
        Assert.Null(MigrationRecord.Load(_f.Profile).Record);
    }

    [Fact]
    public async Task A_migration_Task_Scheduler_refuses_part_way_is_undone_completely_and_can_be_tried_again()
    {
        var daemon = $"AgentBridgeDaemon_{_f.InstallationId}";
        _f.Runtime_.Refused.Add(daemon);
        _f.Runtime_.RefusalVisible = false;
        var listing = _f.Listing();

        var report = (await _f.Migrator().MigrateAsync(dryRun: false)).ToString();

        Assert.Contains("Access is denied", report);
        // The task was never turned off, so there is nothing to turn back on, and the undo says so.
        Assert.DoesNotContain("could not be turned back on", report);
        Assert.DoesNotContain("Re-enabled", report);
        Assert.Contains($"{daemon} was never turned off, and agent-ha-bridge is running.", report);
        Assert.Contains("Rolled back.", report);
        Assert.Null(MigrationRecord.Load(_f.Profile).Record);
        Assert.Equal(listing, _f.Listing());
        Assert.True(_f.Runtime_.DaemonRunning);
        Assert.DoesNotContain($"enable {daemon}", _f.Runtime_.Calls);
        Assert.DoesNotContain($"start {daemon}", _f.Runtime_.Calls);

        _f.Runtime_.Refused.Clear();
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
    }

    [Fact]
    public async Task A_rollback_turns_back_on_only_the_tasks_the_migration_turned_off()
    {
        var daemon = $"AgentBridgeDaemon_{_f.InstallationId}";
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        _f.Runtime_.Calls.Clear();

        var report = await _f.Migrator().RollbackAsync(dryRun: false);

        Assert.True(report.Ok, report.ToString());
        Assert.Contains($"* Re-enabled {daemon}; agent-ha-bridge is running again.", report.ToString());
        Assert.Equal([$"enable {daemon}", $"start {daemon}"], _f.Runtime_.Calls);
    }

    [Fact]
    public async Task A_keep_awake_task_the_scheduler_refused_to_disable_is_neither_enabled_nor_started_by_the_undo()
    {
        var daemon = $"AgentBridgeDaemon_{_f.InstallationId}";
        var keepAwake = $"AgentBridgeDevBoxKeepAwake_{_f.InstallationId}";
        _f.Runtime_.Tasks[keepAwake] = (true, false);
        _f.Runtime_.Refused.Add(keepAwake);
        _f.Runtime_.RefusalVisible = false;
        var listing = _f.Listing();

        var report = (await _f.Migrator().MigrateAsync(dryRun: false)).ToString();

        Assert.Contains("Access is denied", report);
        Assert.Contains($"* Re-enabled {daemon}; agent-ha-bridge is running again.", report);
        Assert.Contains("Rolled back.", report);
        Assert.Equal([$"disable {daemon}", $"disable {keepAwake}", $"enable {daemon}"], _f.Runtime_.Calls);
        Assert.Null(MigrationRecord.Load(_f.Profile).Record);
        Assert.Equal(listing, _f.Listing());
    }

    [Fact]
    public async Task A_daemon_that_outlived_its_supervisor_has_its_task_started_again_by_the_undo()
    {
        var daemon = $"AgentBridgeDaemon_{_f.InstallationId}";
        var listing = _f.Listing();
        _f.Runtime_.OutlivesSupervisor = true;

        var report = await _f.Migrator().MigrateAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("did not stop", report.ToString());
        Assert.Contains("Rolled back.", report.ToString());
        // Started for its supervisor: the daemon still running is not what decides it.
        Assert.Equal([$"disable {daemon}", "stop", $"enable {daemon}", $"start {daemon}"], _f.Runtime_.Calls);
        Assert.True(_f.Runtime_.SupervisorRunning);
        Assert.Null(MigrationRecord.Load(_f.Profile).Record);
        Assert.Equal(listing, _f.Listing());
    }

    [Fact]
    public async Task A_migration_cut_short_before_it_turned_off_a_stopped_daemons_task_rolls_back_without_waiting_for_that_daemon()
    {
        var daemon = $"AgentBridgeDaemon_{_f.InstallationId}";
        _f.Runtime_.Tasks[daemon] = (true, false);
        _f.Runtime_.DaemonRunning = false;
        var listing = _f.Listing();
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        Assert.False(MigrationRecord.Load(_f.Profile).Record!.DaemonRunning);
        // As a migration cut short between its snapshot and turning the task off leaves it.
        var path = MigrationRecord.PathIn(_f.Profile);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        json["tasks"]![0]!["disabled"] = false;
        File.WriteAllText(path, json.ToJsonString());
        _f.Runtime_.Tasks[daemon] = (true, false);
        _f.Runtime_.Calls.Clear();

        var report = await _f.Migrator().RollbackAsync(dryRun: false);

        Assert.True(report.Ok, report.ToString());
        Assert.Contains("was not running when the migration began; it is left as it was", report.ToString());
        Assert.Empty(_f.Runtime_.Calls);
        Assert.False(_f.Runtime_.DaemonRunning);
        Assert.Null(MigrationRecord.Load(_f.Profile).Record);
        Assert.Equal(listing, _f.Listing());
    }

    [Fact]
    public async Task A_task_that_could_not_be_turned_back_on_is_never_reported_as_still_enabled()
    {
        var daemon = $"AgentBridgeDaemon_{_f.InstallationId}";
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        _f.Runtime_.Refused.Add(daemon);
        _f.Runtime_.DaemonRunning = true;

        var report = (await _f.Migrator().RollbackAsync(dryRun: false)).ToString();

        Assert.Contains($"* {daemon} could not be turned back on", report);
        Assert.DoesNotContain("still enabled", report);
        Assert.DoesNotContain("Re-enabled", report);
        Assert.NotNull(MigrationRecord.Load(_f.Profile).Record);
    }

    [Fact]
    public async Task A_record_says_which_tasks_the_migration_turned_off()
    {
        var daemon = $"AgentBridgeDaemon_{_f.InstallationId}";
        var keepAwake = $"AgentBridgeDevBoxKeepAwake_{_f.InstallationId}";
        _f.Runtime_.Tasks[keepAwake] = (false, false);

        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);

        var record = MigrationRecord.Load(_f.Profile).Record!;
        Assert.True(record.Tasks.Single(t => t.Name == daemon).Disabled);
        Assert.False(record.Tasks.Single(t => t.Name == keepAwake).Disabled);
    }

    [Fact]
    public async Task A_record_from_a_build_that_did_not_say_which_tasks_it_turned_off_still_rolls_back()
    {
        var daemon = $"AgentBridgeDaemon_{_f.InstallationId}";
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        var path = MigrationRecord.PathIn(_f.Profile);
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!;
        foreach (var t in json["tasks"]!.AsArray()) { t!.AsObject().Remove("disabled"); }
        File.WriteAllText(path, json.ToJsonString());
        _f.Runtime_.Calls.Clear();

        var report = await _f.Migrator().RollbackAsync(dryRun: false);

        Assert.True(report.Ok, report.ToString());
        Assert.Equal([$"enable {daemon}", $"start {daemon}"], _f.Runtime_.Calls);
    }

    [Theory]
    [InlineData(" Claude ", "claude", false)]
    [InlineData("gemini", "auto", true)]
    public async Task A_launcher_is_carried_as_agent_ha_bridge_read_it(string written, string carried, bool noted)
    {
        var config = FakeAgentHaBridge.DefaultConfig();
        config["newSession"]!["launcher"] = written;
        await using var f = new FakeAgentHaBridge(config);

        var report = await f.Migrator().MigrateAsync(dryRun: false);

        Assert.True(report.Ok, report.ToString());
        var settings = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(f.Profile, HostSettings.FileName)))!;
        Assert.Equal(carried, (string?)settings["launch"]!["defaultAgent"]);
        Assert.Equal(noted, report.ToString().Contains("is not a launcher it knows", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migrating_another_installation_after_a_rollback_leaves_the_first_s_hook_pointing_at_its_own_runtime()
    {
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        Assert.True((await _f.Migrator().RollbackAsync(dryRun: false)).Ok);
        // A session started while migrated still runs this hook.
        Assert.Equal(_f.Runtime, HookRunner.RuntimeRoot(_f.InstallationHooks).Root);
        await using var other = new FakeAgentHaBridge(installationId: "fedcba9876543210fedcba9876543210");

        Assert.True((await other.Migrator(hookFolder: _f.HookFolder).MigrateAsync(dryRun: false)).Ok);

        Assert.Equal(_f.Runtime, HookRunner.RuntimeRoot(_f.InstallationHooks).Root);
        Assert.Equal(other.Runtime, HookRunner.RuntimeRoot(Path.Combine(_f.HookFolder, other.InstallationId)).Root);
    }

    [Fact]
    public async Task A_hook_update_that_cannot_be_put_in_place_leaves_the_previous_hook_where_agents_run_it()
    {
        // Only Windows lets a reader refuse a rename of the file it holds.
        if (!OperatingSystem.IsWindows()) { return; }
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        var previous = File.ReadAllText(_f.InstalledHook);
        File.WriteAllText(_f.HookSource, "MZ the next version's hook");
        FileStream? held = null;
        Migrator.BeforeHookSwap.Value = staged => held = new FileStream(staged, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            Assert.Contains("could not bring", Migrator.RefreshHook(_f.Profile, _f.HookSource));
        }
        finally
        {
            Migrator.BeforeHookSwap.Value = null;
            held?.Dispose();
        }

        Assert.Equal(previous, File.ReadAllText(_f.InstalledHook));
    }

    [Fact]
    public async Task A_hook_replacement_cut_short_is_put_back_when_the_host_starts()
    {
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        var previous = File.ReadAllText(_f.InstalledHook);
        // Moved aside, and the new one never moved in; the app is mid-update, so no source either.
        File.Move(_f.InstalledHook, _f.InstalledHook + ".old");
        File.Delete(_f.HookSource);

        Assert.Contains("put back", Migrator.RefreshHook(_f.Profile, _f.HookSource));

        Assert.Equal(previous, File.ReadAllText(_f.InstalledHook));
    }

    [Fact]
    public async Task An_app_update_s_new_hook_reaches_the_copy_migrated_agents_run_when_the_host_starts()
    {
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        var pointer = File.ReadAllBytes(Path.Combine(_f.InstallationHooks, HookRunner.PointerFile));
        Assert.Null(Migrator.RefreshHook(_f.Profile, _f.HookSource));
        File.WriteAllText(_f.HookSource, "MZ the next version's hook");

        Assert.NotNull(Migrator.RefreshHook(_f.Profile, _f.HookSource));

        Assert.Equal("MZ the next version's hook", File.ReadAllText(_f.InstalledHook));
        Assert.Equal(pointer, File.ReadAllBytes(Path.Combine(_f.InstallationHooks, HookRunner.PointerFile)));
        // Not while a migrate or rollback runs, nor once rolled back.
        File.WriteAllText(_f.HookSource, "MZ another");
        using (new FileStream(Path.Combine(Path.GetDirectoryName(_f.Profile)!, "migration.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Null(Migrator.RefreshHook(_f.Profile, _f.HookSource));
        }
        Assert.True((await _f.Migrator().RollbackAsync(dryRun: false)).Ok);
        Assert.Null(Migrator.RefreshHook(_f.Profile, _f.HookSource));
        Assert.Equal("MZ the next version's hook", File.ReadAllText(_f.InstalledHook));
    }

    [Fact]
    public async Task Rolling_back_with_nothing_migrated_says_so()
    {
        var report = await _f.Migrator().RollbackAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("Nothing to roll back", report.ToString());
        Assert.Empty(_f.Runtime_.Calls);
    }

    [Fact]
    public async Task A_profile_with_its_own_settings_keeps_them_over_agent_ha_bridges()
    {
        var hostConfig = Path.Combine(_f.Profile, "config.json");
        var other = FakeAgentHaBridge.Rooted("other");
        var mine = "{\r\n  \"version\": 1,\r\n  \"launch\": {\r\n    \"defaultAgent\": \"copilot\",\r\n    \"workspaces\": [ { \"path\": " + System.Text.Json.JsonSerializer.Serialize(other) + ", \"label\": \"app\" } ]\r\n  }\r\n}\r\n";
        File.WriteAllText(hostConfig, mine);

        var report = await _f.Migrator().MigrateAsync(dryRun: false);

        Assert.True(report.Ok, report.ToString());
        Assert.Contains("kept: launch.defaultAgent: this profile already sets it", report.ToString());
        Assert.Contains("the label app is already this profile's", report.ToString());
        var settings = JsonNode.Parse(Text(hostConfig))!;
        Assert.Equal("copilot", (string?)settings["launch"]!["defaultAgent"]);
        Assert.Equal([other, FakeAgentHaBridge.Rooted("repos", "docs")], settings["launch"]!["workspaces"]!.AsArray().Select(w => w is JsonObject o ? (string)o["path"]! : (string)w!));

        Assert.True((await _f.Migrator().RollbackAsync(dryRun: false)).Ok);
        Assert.Equal(mine, Text(hostConfig));
    }

    [Fact]
    public async Task Stopping_takes_only_this_installations_own_processes_and_honours_their_receipts()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var pwsh = Environment.GetEnvironmentVariable("PATH")!.Split(Path.PathSeparator).Select(d => Path.Combine(d, "pwsh.exe")).FirstOrDefault(File.Exists);
        if (pwsh is null) { return; }
        var install = AgentHaBridgeInstall.Read(_f.BridgeHome).Install!;
        foreach (var script in new[] { "agent-bridge-daemon.ps1", "agent-bridge-supervisor.ps1" })
        {
            File.WriteAllText(Path.Combine(_f.HooksDir, script), "Start-Sleep -Seconds 120\r\n");
        }
        // Another installation's daemon, under another home: never this migration's to stop.
        var other = Path.Combine(_f.Home, "other", "hooks", "agent-bridge-daemon.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(other)!);
        File.WriteAllText(other, "Start-Sleep -Seconds 120\r\n");
        System.Diagnostics.Process Start(string script)
        {
            var start = new System.Diagnostics.ProcessStartInfo(pwsh) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-File", script }) { start.ArgumentList.Add(a); }
            return System.Diagnostics.Process.Start(start)!;
        }
        using var supervisor = Start(Path.Combine(_f.HooksDir, "agent-bridge-supervisor.ps1"));
        using var daemon = Start(Path.Combine(_f.HooksDir, "agent-bridge-daemon.ps1"));
        using var stranger = Start(other);
        try
        {
            await Task.Delay(500);
            // A receipt naming the daemon's process id with another start time: a reused id.
            File.WriteAllText(Path.Combine(_f.Runtime, "daemon.process.json"), $"{{\"pid\":{daemon.Id},\"installationId\":\"{FakeAgentHaBridge.Id}\",\"executable\":\"{pwsh.Replace("\\", "\\\\")}\",\"startedUtcTicks\":1}}");

            new WindowsAgentHaBridgeRuntime().Stop(install);

            Assert.True(supervisor.HasExited);
            Assert.False(daemon.HasExited);
            Assert.False(stranger.HasExited);

            File.WriteAllText(Path.Combine(_f.Runtime, "daemon.process.json"), $"{{\"pid\":{daemon.Id},\"installationId\":\"{FakeAgentHaBridge.Id}\",\"executable\":\"{pwsh.Replace("\\", "\\\\")}\",\"startedUtcTicks\":{daemon.StartTime.ToUniversalTime().Ticks}}}");
            new WindowsAgentHaBridgeRuntime().Stop(install);

            Assert.True(daemon.HasExited);
            Assert.False(stranger.HasExited);
        }
        finally
        {
            foreach (var p in new[] { supervisor, daemon, stranger }) { if (!p.HasExited) { p.Kill(); } }
        }
    }

    [Fact]
    public async Task A_mistyped_flag_is_refused_before_anything_runs()
    {
        var created = 0;
        Migrator Create(MigrationOptions o) { created++; return _f.Migrator(); }
        foreach (var args in new[] { new[] { "migrate", "--dryrun" }, ["migrate", "nonsense"], ["migrate", "--bridge-home"], ["migrate", "--hook", "--dry-run"], ["rollback", "--dry"], ["migration", "status", "now"],
            ["migrate", "--profile"], ["migrate", "--profile", "--dry-run"], ["rollback", "--profile", "../elsewhere"] })
        {
            var error = new StringWriter();
            Assert.Equal(2, await MigrateCommands.RunAsync(args, Root, new StringWriter(), error, Create));
            Assert.Contains("usage: agent-bridge-host migrate", error.ToString());
        }
        Assert.Equal(0, created);
        Assert.Empty(_f.Runtime_.Calls);
    }

    [Fact]
    public async Task A_dry_run_for_a_profile_that_does_not_exist_creates_nothing()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var output = new StringWriter();

        Assert.Equal(0, await MigrateCommands.RunAsync(["migrate", "--dry-run", "--profile", "preview"], Root, output, new StringWriter(), o => _f.Migrator(profile: o.ProfileFolder)));

        Assert.Contains("Dry run: nothing was changed.", output.ToString());
        Assert.False(Directory.Exists(Path.Combine(Root, "preview")));
    }

    [Fact]
    public async Task Hooks_of_two_events_that_share_a_replacement_each_get_their_own_original_back()
    {
        // SessionStart registered through run-hook.cmd (an older install), UserPromptSubmit through
        // the Go hook: both become "claude register".
        var settings = FakeAgentHaBridge.Read(_f.ClaudeSettings);
        var launcher = $"\"{Path.Combine(_f.ClaudeHome, "ha-bridge", "run-hook.cmd")}\" \"{Path.Combine(_f.ClaudeHome, "ha-bridge", "register-claude-session.ps1")}\"";
        settings["hooks"]!["SessionStart"]![0]!["hooks"]![0]!["command"] = launcher;
        File.WriteAllText(_f.ClaudeSettings, settings.ToJsonString(new() { WriteIndented = true }));
        var goForm = (string)FakeAgentHaBridge.Read(_f.ClaudeSettings)["hooks"]!["UserPromptSubmit"]![0]!["hooks"]![0]!["command"]!;
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        var migrated = FakeAgentHaBridge.Read(_f.ClaudeSettings);
        migrated["theme"] = "dark";
        // And the person moved UserPromptSubmit's group ahead of SessionStart's in the file.
        var hooks = migrated["hooks"]!.AsObject();
        var prompt = hooks["UserPromptSubmit"]!.DeepClone();
        hooks.Remove("UserPromptSubmit");
        var reordered = new System.Text.Json.Nodes.JsonObject { ["UserPromptSubmit"] = prompt };
        foreach (var (k, v) in hooks.ToList()) { hooks.Remove(k); reordered[k] = v; }
        migrated["hooks"] = reordered;
        File.WriteAllText(_f.ClaudeSettings, migrated.ToJsonString(new() { WriteIndented = true }));

        var report = await _f.Migrator().RollbackAsync(dryRun: false);

        Assert.True(report.Ok, report.ToString());
        var after = FakeAgentHaBridge.Read(_f.ClaudeSettings)["hooks"]!;
        Assert.Equal(launcher, (string)after["SessionStart"]![0]!["hooks"]![0]!["command"]!);
        Assert.Equal(goForm, (string)after["UserPromptSubmit"]![0]!["hooks"]![0]!["command"]!);
    }

    [Fact]
    public async Task Hook_registrations_are_put_back_only_once_the_host_has_let_go_of_the_spool()
    {
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        var claim = Path.Combine(_f.Runtime, HookIntake.ClaimFile);
        var hostConfig = Path.Combine(_f.Profile, "config.json");
        var heldAtWrite = new List<string>();
        DurableFile.BeforeCommit.Value = path =>
        {
            if (string.Equals(path, hostConfig, StringComparison.OrdinalIgnoreCase)) { return; }
            try { using var probe = new FileStream(claim, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (FileNotFoundException) { return; }
            catch (IOException) { heldAtWrite.Add(path); }
        };
        try
        {
            Assert.True((await _f.Migrator().RollbackAsync(dryRun: false)).Ok);
        }
        finally { DurableFile.BeforeCommit.Value = null; }

        Assert.Empty(heldAtWrite);
    }

    [Fact]
    public async Task Agent_ha_bridge_is_not_started_while_the_old_host_still_holds_the_spool()
    {
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        // The host acknowledged the restart but its last pass has not let go yet.
        _f.Host.Linger = true;

        var report = await _f.Migrator().RollbackAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("still holds the hook spool", report.ToString());
        Assert.False(_f.Runtime_.DaemonRunning);
        // But turned back on, so it starts at the next logon whatever happens to this rollback.
        Assert.All(_f.Runtime_.Tasks.Values, t => Assert.True(t.Enabled));
        Assert.NotNull(MigrationRecord.Load(_f.Profile).Record);
        // And the hook registrations left as migrated: they hand their events to the spool.
        Assert.True(Registrations.Names(File.ReadAllBytes(_f.ClaudeSettings), _f.InstalledHook));

        _f.Host.Linger = false;
        _f.Host.Release();
        Assert.True((await _f.Migrator().RollbackAsync(dryRun: false)).Ok);
        Assert.True(_f.Runtime_.DaemonRunning);
        Assert.False(Registrations.Names(File.ReadAllBytes(_f.ClaudeSettings), _f.InstalledHook));
    }

    [Fact]
    public async Task A_migrated_hook_whose_timeout_was_changed_gets_its_command_back_and_keeps_the_timeout()
    {
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        var claude = FakeAgentHaBridge.Read(_f.ClaudeSettings);
        claude["hooks"]!["PreToolUse"]![1]!["hooks"]![0]!["timeout"] = 99;
        File.WriteAllText(_f.ClaudeSettings, claude.ToJsonString(new() { WriteIndented = true }));

        var report = await _f.Migrator().RollbackAsync(dryRun: false);

        Assert.True(report.Ok, report.ToString());
        var ask = FakeAgentHaBridge.Read(_f.ClaudeSettings)["hooks"]!["PreToolUse"]![1]!["hooks"]![0]!;
        Assert.StartsWith($"\"{_f.GoHook}\" claude ask ", (string)ask["command"]!);
        Assert.Equal(99, (int)ask["timeout"]!);
        Assert.False(Registrations.Names(File.ReadAllBytes(_f.ClaudeSettings), _f.InstalledHook));
    }

    [Fact]
    public async Task A_migrated_hook_changed_past_recognition_is_reported_and_its_snapshot_kept()
    {
        Assert.True((await _f.Migrator().MigrateAsync(dryRun: false)).Ok);
        var claude = FakeAgentHaBridge.Read(_f.ClaudeSettings);
        claude["hooks"]!["Stop"]![0]!["hooks"]![0]!["command"] = $"\"{_f.InstalledHook}\" claude stop --verbose";
        File.WriteAllText(_f.ClaudeSettings, claude.ToJsonString(new() { WriteIndented = true }));

        var report = await _f.Migrator().RollbackAsync(dryRun: false);

        Assert.False(report.Ok);
        Assert.Contains("could not be matched to what it replaced", report.ToString());
        Assert.NotNull(MigrationRecord.Load(_f.Profile).Record);
        Assert.True(Directory.Exists(MigrationRecord.SnapshotIn(_f.Profile)));
        // The rest of that file was still put back.
        var ask = FakeAgentHaBridge.Read(_f.ClaudeSettings)["hooks"]!["PreToolUse"]![1]!["hooks"]![0]!;
        Assert.StartsWith($"\"{_f.GoHook}\" claude ask ", (string)ask["command"]!);
    }

    [Fact]
    public async Task Home_Assistant_settings_the_connector_could_not_use_are_refused()
    {
        var config = FakeAgentHaBridge.DefaultConfig();
        config["homeAssistant"]!["baseUrl"] = "ftp://ha.example";
        await using var fake = new FakeAgentHaBridge(config);

        var report = await fake.Migrator().MigrateAsync(dryRun: true);

        Assert.False(report.Ok);
        Assert.Contains("homeAssistant.baseUrl", report.ToString());
    }

    [Fact]
    public void Settings_written_in_another_case_are_reported_imported_and_never_also_not_carried()
    {
        // As AgentHaBridgeInstall reads agent-ha-bridge's file, and PowerShell reads it.
        var ahb = (JsonObject)JsonNode.Parse("""
            {"NewSession":{"Launcher":"claude","CodexStartPrompt":"go"},
             "HomeAssistant":{"BaseUrl":"http://ha:8123","Token":"t"},
             "Dashboard":{"UrlPath":"agents","Publication":{"writer":"x"}},
             "DetailedActivity":true}
            """, new JsonNodeOptions { PropertyNameCaseInsensitive = true })!;

        var import = ConfigImport.Build(ahb, null);

        Assert.Contains("newSession.launcher -> launch.defaultAgent", import.Imported);
        Assert.Contains("homeAssistant.baseUrl -> homeAssistant.baseUrl", import.Imported);
        Assert.Contains("dashboard.urlPath -> homeAssistant.dashboardUrlPath", import.Imported);
        Assert.Equal(3, import.NotCarried.Count);
        Assert.Contains("newSession.CodexStartPrompt", import.NotCarried);
        Assert.Contains(import.NotCarried, n => n.StartsWith("homeAssistant.Token (a secret", StringComparison.Ordinal));
        Assert.Contains(import.NotCarried, n => n.StartsWith("dashboard.publication (this host never publishes", StringComparison.Ordinal));
    }

    [Fact]
    public void Phone_notification_settings_are_carried_into_the_connectors_section_by_name()
    {
        var ahb = (JsonObject)JsonNode.Parse("""
            {"homeAssistant":{"baseUrl":"http://ha:8123"},
             "Notifications":{"Enabled":true,"service":"ticker.notify","tickerCategory":"agents","sound":"chime"},
             "answerButtons":{"services":"notify.mobile_app_phone","maxButtons":4,"max":9}}
            """, new JsonNodeOptions { PropertyNameCaseInsensitive = true })!;

        var import = ConfigImport.Build(ahb, null);

        Assert.Empty(import.Problems);
        Assert.Contains("notifications.Enabled -> homeAssistant.notifications.enabled", import.Imported);
        Assert.Contains("answerButtons.services -> homeAssistant.answerButtons.services", import.Imported);
        Assert.Equal(["notifications.sound", "answerButtons.max"], import.NotCarried);
        var (settings, problem) = HostSettings.Parse(import.Text!, "config.json");
        Assert.Null(problem);
        Assert.Empty(settings.HomeAssistantProblems);
        var notify = settings.HomeAssistant.Notify;
        Assert.True(notify.Enabled);
        Assert.Equal("ticker.notify", notify.Service);
        Assert.Equal("agents", notify.TickerCategory);
        Assert.Equal(["notify.mobile_app_phone"], notify.AnswerButtonServices);
        Assert.Equal(4, notify.MaxButtons);

        // One service named alone, as agent-ha-bridge reads it: a list of one.
        Assert.Equal("notify.mobile_app_phone", (string?)JsonNode.Parse(import.Text!)!["homeAssistant"]!["answerButtons"]!["services"]![0]);
        // A setting this profile already has is kept.
        var again = ConfigImport.Build(ahb, """{"version":1,"homeAssistant":{"notifications":{"enabled":false}}}""");
        Assert.Contains(again.Kept, k => k.StartsWith("homeAssistant.notifications.enabled", StringComparison.Ordinal));
    }

    [Fact]
    public void Update_check_settings_are_carried_and_the_rest_of_the_daemons_update_section_is_not()
    {
        var ahb = (JsonObject)JsonNode.Parse("""
            {"updates":{"checkForUpdates":false,"CheckHours":2,"repository":"danswett/agent-ha-bridge","token":"secret-value","installedVersion":"1.40.0"}}
            """, new JsonNodeOptions { PropertyNameCaseInsensitive = true })!;

        var import = ConfigImport.Build(ahb, null);

        Assert.Empty(import.Problems);
        Assert.Contains("updates.checkForUpdates -> homeAssistant.updates.checkForUpdates", import.Imported);
        Assert.Contains("updates.CheckHours -> homeAssistant.updates.checkHours", import.Imported);
        Assert.Equal(["updates.repository", "updates.token (a secret: never copied)", "updates.installedVersion"], import.NotCarried);
        Assert.DoesNotContain("secret-value", import.Text!, StringComparison.Ordinal);
        var (settings, problem) = HostSettings.Parse(import.Text!, "config.json");
        Assert.Null(problem);
        Assert.Empty(settings.HomeAssistantProblems);
        Assert.False(settings.HomeAssistant.CheckForUpdates);
        Assert.Equal(2, settings.HomeAssistant.CheckHours);
    }

    [Fact]
    public async Task The_cli_runs_status_and_a_dry_run_and_refuses_unknown_words()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Migrator Create(MigrationOptions o) => _f.Migrator();

        Assert.Equal(2, await MigrateCommands.RunAsync(["migration", "nonsense"], Root, output, error, Create));
        Assert.Contains("usage: agent-bridge-host migrate", error.ToString());
        if (!OperatingSystem.IsWindows()) { return; }
        Assert.Equal(0, await MigrateCommands.RunAsync(["migration", "status"], Root, output, error, Create));
        Assert.Contains("Not migrated", output.ToString());
        Assert.Equal(0, await MigrateCommands.RunAsync(["migrate", "--dry-run"], Root, output, error, Create));
        Assert.Contains("Dry run: nothing was changed.", output.ToString());
        Assert.Equal(1, await MigrateCommands.RunAsync(["rollback"], Root, output, error, Create));
    }
}
