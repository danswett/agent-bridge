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
        Assert.Equal(_f.Runtime, HookRunner.RuntimeRoot(_f.HookFolder).Root);

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
        Assert.Contains("only its 5 re-pointed hook(s) were put back", report.ToString());
        var after = FakeAgentHaBridge.Read(_f.ClaudeSettings);
        Assert.Equal("dark", (string?)after["theme"]);
        var commands = after["hooks"]!.AsObject().SelectMany(e => e.Value!.AsArray()).SelectMany(m => m!["hooks"]!.AsArray()).Select(h => (string)h!["command"]!).ToList();
        Assert.Contains(commands, c => c.StartsWith($"\"{_f.GoHook}\" claude ask ", StringComparison.Ordinal));
        Assert.DoesNotContain(commands, c => c.Contains("agent-bridge-host-hook", StringComparison.Ordinal));
        Assert.Contains("C:\\mine\\guard.cmd", commands);
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
        Assert.Equal(["disable AgentBridgeDaemon_" + FakeAgentHaBridge.Id, "stop", "enable AgentBridgeDaemon_" + FakeAgentHaBridge.Id, "start AgentBridgeDaemon_" + FakeAgentHaBridge.Id], _f.Runtime_.Calls);
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
    public async Task The_cli_runs_status_and_a_dry_run_and_refuses_unknown_words()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Migrator Create(MigrationOptions o) => _f.Migrator();

        Assert.Equal(2, await MigrateCommands.RunAsync(["migration", "nonsense"], _f.Profile, output, error, Create));
        Assert.Contains("usage: agent-bridge-host migrate", error.ToString());
        if (!OperatingSystem.IsWindows()) { return; }
        Assert.Equal(0, await MigrateCommands.RunAsync(["migration", "status"], _f.Profile, output, error, Create));
        Assert.Contains("Not migrated", output.ToString());
        Assert.Equal(0, await MigrateCommands.RunAsync(["migrate", "--dry-run"], _f.Profile, output, error, Create));
        Assert.Contains("Dry run: nothing was changed.", output.ToString());
        Assert.Equal(1, await MigrateCommands.RunAsync(["rollback"], _f.Profile, output, error, Create));
    }
}
