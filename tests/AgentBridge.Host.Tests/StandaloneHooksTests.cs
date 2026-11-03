using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using AgentBridge.Hooks;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Intake;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Host.Migration;
using Xunit;

public sealed class StandaloneHooksTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "ab-hooks-" + Guid.NewGuid().ToString("N"));
    readonly CodexRunner codex = new();
    readonly Locator locator = new();
    string? active;
    string Profile => Path.Combine(root, "profiles", "default");
    string Source => Path.Combine(root, "source.exe");
    string Hook => Path.Combine(Profile, "hooks", "agent-bridge-host-hook.exe");
    string Legacy => Path.Combine(root, "legacy");
    IntakeState.AgentHomes Homes => new(Path.Combine(root, "copilot"), Path.Combine(root, "claude"), Path.Combine(root, "codex"));
    string Owner(string agent) => Path.Combine(agent switch { "copilot" => Homes.Copilot, "claude" => Homes.Claude, _ => Homes.Codex }, "agent-bridge-owner.json");
    string Registration(string agent) => agent switch
    {
        "copilot" => Path.Combine(Homes.Copilot, "hooks", "agent-bridge.json"),
        "claude" => Path.Combine(Homes.Claude, "settings.json"),
        _ => Path.Combine(Profile, "hooks", "codex", "plugins", "agent-bridge", "hooks.json"),
    };
    StandaloneHooks Setup => new(Profile, Homes, Source, Legacy, () => active, locator, codex);

    public StandaloneHooksTests()
    {
        Directory.CreateDirectory(root);
        File.WriteAllText(Source, "native hook fixture");
        codex.Root = Path.Combine(Profile, "hooks", "codex");
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
    static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
    static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    string[] Listing() => Directory.GetFiles(root, "*", SearchOption.AllDirectories)
        .Order().Select(p => p + ":" + Convert.ToHexString(File.ReadAllBytes(p))).ToArray();

    [Theory]
    [InlineData("copilot", "preToolUse", "agentStop", "notification")]
    [InlineData("claude", "PreToolUse", "SessionStart", "UserPromptSubmit")]
    [InlineData("codex", "PermissionRequest", "SessionStart", "SessionEnd")]
    public async Task Fresh_setup_is_repeatable_and_points_new_sessions_at_the_private_intake(string agent, params string[] events)
    {
        await Setup.InstallAsync(agent, false, TextWriter.Null);
        var (state, problem) = IntakeState.Load(Profile);
        Assert.Null(problem);
        Assert.True(state!.Standalone);
        Assert.Equal(Homes, state.Homes);
        Assert.Equal(Path.Combine(Profile, "runtime"), state.RuntimeRoot);
        Assert.Equal(state.RuntimeRoot, (string?)Read(Path.Combine(Profile, "hooks", HookRunner.PointerFile))["runtimeRoot"]);
        Assert.Equal(File.ReadAllBytes(Source), File.ReadAllBytes(Hook));
        var hooks = Read(Registration(agent))["hooks"]!.AsObject();
        foreach (var name in events) { Assert.Single(hooks[name]!.AsArray()); }
        var before = File.ReadAllText(Registration(agent));
        await Setup.InstallAsync(agent, false, TextWriter.Null);
        Assert.Equal(before, File.ReadAllText(Registration(agent)));
        Assert.Equal(state, IntakeState.Load(Profile).State);
        if (agent == "codex")
        {
            Assert.Equal(1, codex.Calls.Count(c => c.SequenceEqual(new[] { "plugin", "marketplace", "add", codex.Root })));
            Assert.Equal(3, (int)hooks["SessionEnd"]![0]!["hooks"]![0]!["timeout"]!);
        }
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    [InlineData("codex")]
    public async Task A_dry_run_does_not_create_any_files(string agent)
    {
        var before = Listing();
        await Setup.InstallAsync(agent, true, TextWriter.Null);
        Assert.Equal(before, Listing());
        Assert.All(codex.Calls, c => Assert.Equal("list", c[2]));
    }

    [Fact]
    public async Task Claude_setup_and_removal_preserve_other_settings_and_hooks_including_later_edits()
    {
        var original = """
            {"model":"user-model","hooks":{"SessionStart":[{"hooks":[{"type":"command","command":"user-hook"}]}]}}
            """;
        Write(Registration("claude"), original);
        await Setup.InstallAsync("claude", false, TextWriter.Null);
        Assert.Equal(original, File.ReadAllText(Path.Combine(Profile, "hooks", "backups", "claude-settings.json")));
        var settings = Read(Registration("claude"));
        settings["new-setting"] = true;
        // A person's hook in a group that also holds one of ours must survive removal.
        settings["hooks"]!["SessionStart"]![1]!["hooks"]!.AsArray().Add(new JsonObject { ["command"] = "later-hook" });
        Write(Registration("claude"), settings.ToJsonString());
        await Setup.RemoveAsync("claude", false, TextWriter.Null);
        var remaining = Read(Registration("claude"));
        Assert.Equal("user-model", (string?)remaining["model"]);
        Assert.True((bool)remaining["new-setting"]!);
        Assert.Equal(2, remaining["hooks"]!["SessionStart"]!.AsArray().Count);
        Assert.DoesNotContain(Hook, remaining.ToJsonString());
        Assert.False(File.Exists(Owner("claude")));
        Assert.True(File.Exists(Hook)); // Already-running sessions still need their executable.
        Assert.NotNull(IntakeState.Load(Profile).State);
        Assert.Equal(original, File.ReadAllText(Path.Combine(Profile, "hooks", "backups", "claude-settings.json")));
    }

    [Fact]
    public async Task Claude_removal_recognizes_a_differently_cased_Windows_profile_without_orphaning_hooks()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        await Setup.InstallAsync("claude", false, TextWriter.Null);
        var sameProfile = new StandaloneHooks(Profile.ToUpperInvariant(), Homes, Source, Legacy, () => active, locator, codex);

        await sameProfile.RemoveAsync("claude", false, TextWriter.Null);

        Assert.Null(Read(Registration("claude"))["hooks"]);
        Assert.False(File.Exists(Owner("claude")));
        await Setup.InstallAsync("claude", false, TextWriter.Null);
        Assert.Single(Read(Registration("claude"))["hooks"]!["SessionStart"]!.AsArray());
    }

    [Fact]
    public async Task Equivalent_agent_home_paths_allow_reinstall_and_removal_without_changing_the_saved_homes()
    {
        await Setup.InstallAsync("copilot", false, TextWriter.Null);
        await Setup.InstallAsync("claude", false, TextWriter.Null);
        string Equivalent(string path) => (OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path) + Path.DirectorySeparatorChar;
        var equivalent = new IntakeState.AgentHomes(Equivalent(Homes.Copilot), Equivalent(Homes.Claude), Equivalent(Homes.Codex));
        var setup = new StandaloneHooks(Profile, equivalent, Source, Legacy, () => active, locator, codex);

        await setup.InstallAsync("copilot", false, TextWriter.Null);
        await setup.RemoveAsync("claude", false, TextWriter.Null);

        Assert.False(File.Exists(Owner("claude")));
        Assert.Equal(Homes, IntakeState.Load(Profile).State!.Homes);
    }

    [Fact]
    public async Task A_genuinely_different_agent_home_still_refuses_setup_without_mutation()
    {
        await Setup.InstallAsync("copilot", false, TextWriter.Null);
        var other = Homes with { Claude = Path.Combine(root, "other-claude") };
        var setup = new StandaloneHooks(Profile, other, Source, Legacy, () => active, locator, codex);
        var before = Listing();

        await Assert.ThrowsAsync<InvalidOperationException>(() => setup.InstallAsync("copilot", false, TextWriter.Null));

        Assert.Equal(before, Listing());
    }

    [Theory]
    [InlineData("copilot", "claude")]
    [InlineData("copilot", "codex")]
    [InlineData("claude", "codex")]
    public async Task Shared_agent_homes_are_refused_before_any_hook_or_ownership_write(string first, string second)
    {
        var homes = Homes;
        var shared = first == "copilot" ? homes.Copilot : homes.Claude;
        shared = (OperatingSystem.IsWindows() ? shared.ToUpperInvariant() : shared) + Path.DirectorySeparatorChar;
        homes = second == "claude" ? homes with { Claude = shared } : homes with { Codex = shared };
        var setup = new StandaloneHooks(Profile, homes, Source, Legacy, () => active, locator, codex);
        var before = Listing();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.InstallAsync(first, false, TextWriter.Null));

        Assert.Contains("separate agent homes", error.Message);
        Assert.Equal(before, Listing());
        Assert.Empty(codex.Calls);
    }

    [Theory]
    [InlineData("[]", true)]
    [InlineData("{\"hooks\":false}", true)]
    [InlineData("{\"hooks\":false}", false)]
    public async Task Invalid_Claude_settings_return_a_failed_command_with_a_diagnostic_including_dry_runs(string settings, bool dryRun)
    {
        Write(Registration("claude"), settings);
        var before = Listing();
        using var error = new StringWriter();

        var result = await HookCommands.RunSetupAsync(Setup, ["claude"], true, dryRun, TextWriter.Null, error);

        Assert.Equal(1, result);
        Assert.Contains("Hook setup incomplete:", error.ToString());
        Assert.Contains("it was left alone", error.ToString());
        Assert.Equal(before, Listing());
    }

    [Fact]
    public async Task Removing_an_agent_from_preexisting_shared_homes_preserves_the_other_agents_hooks_and_ownership()
    {
        await Setup.InstallAsync("claude", false, TextWriter.Null);
        var homes = Homes with { Copilot = Homes.Claude };
        (IntakeState.Load(Profile).State! with { Homes = homes }).Save(Profile);
        Write(Path.Combine(homes.Copilot, "hooks", "agent-bridge.json"), "{\"version\":1,\"hooks\":{}}");
        var setup = new StandaloneHooks(Profile, homes, Source, Legacy, () => active, locator, codex);
        var before = Listing();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.RemoveAsync("copilot", false, TextWriter.Null));

        Assert.Contains("separate agent homes", error.Message);
        Assert.Equal(before, Listing());
        Assert.True(File.Exists(Owner("claude")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CLI_setup_recovers_an_unknown_Claude_mode_only_from_its_matching_invocation_profile(bool explicitConfigDir)
    {
        var current = new SessionSourceOptions
        {
            CopilotHome = Homes.Copilot, ClaudeHome = Homes.Claude + Path.DirectorySeparatorChar, CodexHome = Homes.Codex,
            ClaudeConfigDirExplicit = explicitConfigDir,
        };

        Assert.Equal(explicitConfigDir, HookCommands.ClaudeModeFor(Homes, current, setup: true));
        Assert.Null(HookCommands.ClaudeModeFor(Homes, current, setup: false));
        Assert.Null(HookCommands.ClaudeModeFor(Homes with { Claude = Path.Combine(root, "other-claude") }, current, setup: true));
        Assert.Equal(!explicitConfigDir, HookCommands.ClaudeModeFor(Homes with { ClaudeConfigDirExplicit = !explicitConfigDir }, current, setup: true));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Retrying_setup_records_a_previously_unknown_Claude_mode_and_does_not_reset_it(bool explicitConfigDir)
    {
        await Setup.InstallAsync("copilot", false, TextWriter.Null);
        var homes = Homes with { ClaudeConfigDirExplicit = explicitConfigDir };
        var setup = new StandaloneHooks(Profile, homes, Source, Legacy, () => active, locator, codex);

        await setup.InstallAsync("copilot", false, TextWriter.Null);
        await Setup.InstallAsync("copilot", false, TextWriter.Null);

        Assert.Equal(homes, IntakeState.Load(Profile).State!.Homes);
    }

    void ConfigurePath(string agent, string path) => Write(Path.Combine(Profile, HostSettings.FileName),
        new JsonObject { ["launch"] = new JsonObject { ["agents"] = new JsonObject { [agent] = new JsonObject { ["path"] = path } } } }.ToJsonString());

    [Theory]
    [InlineData("copilot", "copilot")]
    [InlineData("copilot", "agency")]
    [InlineData("claude", "claude")]
    [InlineData("codex", "codex")]
    public async Task Hook_setup_uses_the_profiles_configured_CLI_even_when_it_is_not_on_PATH(string agent, string configured)
    {
        ConfigurePath(configured, Path.Combine(root, "custom-cli.exe"));
        locator.Locate = (_, config) => config.Path;

        await Setup.InstallAsync(agent, false, TextWriter.Null);

        Assert.True(File.Exists(Owner(agent)));
        Assert.NotNull(IntakeState.Load(Profile).State);
    }

    [Fact]
    public async Task Codex_removal_uses_the_profiles_configured_CLI_even_when_it_is_not_on_PATH()
    {
        await Setup.InstallAsync("codex", false, TextWriter.Null);
        ConfigurePath("codex", Path.Combine(root, "custom-codex.exe"));
        locator.Locate = (_, config) => config.Path;

        await Setup.RemoveAsync("codex", false, TextWriter.Null);

        Assert.False(codex.Present);
        Assert.False(File.Exists(Owner("codex")));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"launch\":{\"agents\":false}}")]
    public async Task Broken_profile_settings_do_not_silently_select_a_different_CLI_or_write_hooks(string config)
    {
        Write(Path.Combine(Profile, HostSettings.FileName), config);
        var before = Listing();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Setup.InstallAsync("claude", false, TextWriter.Null));

        Assert.Equal(before, Listing());
    }

    [Fact]
    public async Task Refresh_replaces_the_Windows_hook_while_its_previous_image_is_running()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        File.Copy(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"), Source, overwrite: true);
        await Setup.InstallAsync("copilot", false, TextWriter.Null);
        using var running = Process.Start(new ProcessStartInfo(Hook, "/d /c set /p answer=ready")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true,
        })!;
        try
        {
            Assert.True(await running.StandardOutput.ReadAsync(new char[1], 0, 1).WaitAsync(TimeSpan.FromSeconds(5)) > 0);
            Assert.False(running.HasExited);
            File.WriteAllText(Source, "the next hook version");

            StandaloneHooks.Refresh(Profile, Source);

            Assert.Equal("the next hook version", File.ReadAllText(Hook));
            Assert.False(running.HasExited);
        }
        finally
        {
            if (!running.HasExited) { running.Kill(entireProcessTree: true); }
            await running.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Startup_refresh_does_not_race_a_hook_setup_or_migration_holding_the_shared_lock()
    {
        await Setup.InstallAsync("copilot", false, TextWriter.Null);
        var previous = File.ReadAllBytes(Hook);
        File.WriteAllText(Source, "the next hook version");
        using (new FileStream(Path.Combine(Path.GetDirectoryName(Profile)!, "migration.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Throws<IOException>(() => StandaloneHooks.Refresh(Profile, Source));
            Assert.Equal(previous, File.ReadAllBytes(Hook));
        }

        StandaloneHooks.Refresh(Profile, Source);
        Assert.Equal("the next hook version", File.ReadAllText(Hook));
    }

    [Fact]
    public async Task An_interrupted_refresh_restores_the_previous_hook_even_when_the_new_source_is_missing()
    {
        await Setup.InstallAsync("copilot", false, TextWriter.Null);
        var previous = File.ReadAllBytes(Hook);
        File.Move(Hook, Hook + ".old");
        File.Delete(Source);

        Assert.Throws<FileNotFoundException>(() => StandaloneHooks.Refresh(Profile, Source));

        Assert.Equal(previous, File.ReadAllBytes(Hook));
    }

    [Fact]
    public async Task Copilot_removal_leaves_other_entries_added_to_the_owned_file_and_can_be_reinstalled()
    {
        await Setup.InstallAsync("copilot", false, TextWriter.Null);
        var settings = Read(Registration("copilot"));
        settings["hooks"]!["agentStop"]!.AsArray().Add(new JsonObject { ["type"] = "command", ["exec"] = "other-hook" });
        Write(Registration("copilot"), settings.ToJsonString());
        await Setup.RemoveAsync("copilot", false, TextWriter.Null);
        var entries = Read(Registration("copilot"))["hooks"]!["agentStop"]!.AsArray();
        Assert.Single(entries);
        Assert.Equal("other-hook", (string?)entries[0]!["exec"]);
        await Setup.InstallAsync("copilot", false, TextWriter.Null);
        Assert.Equal(2, Read(Registration("copilot"))["hooks"]!["agentStop"]!.AsArray().Count);
    }

    [Fact]
    public async Task Copilot_can_be_reinstalled_after_removing_its_last_hook()
    {
        await Setup.InstallAsync("copilot", false, TextWriter.Null);
        await Setup.RemoveAsync("copilot", false, TextWriter.Null);
        Assert.False(File.Exists(Registration("copilot")));
        Assert.False(File.Exists(Owner("copilot")));
        await Setup.InstallAsync("copilot", false, TextWriter.Null);
        Assert.Single(Read(Registration("copilot"))["hooks"]!["agentStop"]!.AsArray());
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("active")]
    [InlineData("owner")]
    [InlineData("migration")]
    [InlineData("legacy-hooks")]
    [InlineData("unowned-hooks")]
    public async Task Legacy_or_foreign_ownership_refuses_setup_without_mutation(string reason)
    {
        switch (reason)
        {
            case "legacy": Directory.CreateDirectory(Legacy); break;
            case "active": active = "probe could not check"; break;
            case "owner": Write(Owner("copilot"), new JsonObject { ["version"] = 1, ["profile"] = Path.Combine(root, "other") }.ToJsonString()); break;
            case "migration": new IntakeState(Path.Combine(Legacy, "runtime"), Legacy, "legacy-id", DateTimeOffset.UtcNow).Save(Profile); break;
            case "legacy-hooks": Write(Path.Combine(Homes.Copilot, "hooks", "decision-notifier.json"), "{}"); break;
            case "unowned-hooks": Write(Registration("copilot"), "{}"); break;
        }
        var before = Listing();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Setup.InstallAsync("copilot", false, TextWriter.Null));
        Assert.Equal(before, Listing());
    }

    [Fact]
    public async Task Malformed_settings_are_not_replaced_or_claimed()
    {
        Write(Registration("claude"), "{broken");
        var before = Listing();
        await Assert.ThrowsAnyAsync<System.Text.Json.JsonException>(() => Setup.InstallAsync("claude", false, TextWriter.Null));
        Assert.Equal(before, Listing());
    }

    [Fact]
    public async Task Claude_metadata_mentioning_the_bridge_is_not_mistaken_for_unowned_hooks()
    {
        Write(Registration("claude"), """{"description":"Working on agent-bridge","hooks":{"SessionStart":[{"hooks":[{"command":"user-hook"}]}]}}""");
        await Setup.InstallAsync("claude", false, TextWriter.Null);
        Assert.Equal("Working on agent-bridge", (string?)Read(Registration("claude"))["description"]);
        Assert.Equal(2, Read(Registration("claude"))["hooks"]!["SessionStart"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("claude", "agent-bridge-host-hook.exe claude register")]
    [InlineData("claude", "pwsh -File C:\\tools\\ha-bridge\\register.ps1")]
    [InlineData("copilot", "agent-bridge-host-hook.exe claude register")]
    [InlineData("copilot", "pwsh -File C:\\tools\\ha-bridge\\register.ps1")]
    [InlineData("codex", "agent-bridge-host-hook.exe claude register")]
    [InlineData("codex", "pwsh -File C:\\tools\\ha-bridge\\register.ps1")]
    public async Task Unowned_Claude_bridge_commands_are_refused_even_without_a_legacy_directory(string agent, string command)
    {
        Write(Registration("claude"), new JsonObject
        {
            ["hooks"] = new JsonObject { ["SessionStart"] = new JsonArray(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject { ["command"] = command }),
            }) },
        }.ToJsonString());
        var before = Listing();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Setup.InstallAsync(agent, false, TextWriter.Null));
        Assert.Equal(before, Listing());
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("codex")]
    public async Task Owned_standalone_Claude_commands_do_not_prevent_setting_up_another_agent(string agent)
    {
        await Setup.InstallAsync("claude", false, TextWriter.Null);

        await Setup.InstallAsync(agent, false, TextWriter.Null);

        Assert.True(File.Exists(Owner(agent)));
        Assert.True(File.Exists(Owner("claude")));
    }

    [Fact]
    public async Task A_Claude_ownership_marker_does_not_hide_an_additional_legacy_command()
    {
        await Setup.InstallAsync("claude", false, TextWriter.Null);
        var settings = Read(Registration("claude"));
        settings["hooks"]!["SessionStart"]![0]!["hooks"]!.AsArray().Add(new JsonObject { ["command"] = "pwsh -File C:\\tools\\ha-bridge\\register.ps1" });
        Write(Registration("claude"), settings.ToJsonString());
        var before = Listing();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Setup.InstallAsync("copilot", false, TextWriter.Null));

        Assert.Equal(before, Listing());
    }

    [Fact]
    public async Task Codex_failure_is_explicit_and_retry_uses_the_owned_marketplace_without_changing_trust()
    {
        var trust = Path.Combine(Homes.Codex, "config.toml");
        Write(trust, "[hooks.state]\n\"user-hook\" = \"trusted\"\n");
        codex.FailPlugin = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Setup.InstallAsync("codex", false, TextWriter.Null));
        Assert.True(File.Exists(Owner("codex")));
        Assert.Null(IntakeState.Load(Profile).State);
        codex.FailPlugin = false;
        await Setup.InstallAsync("codex", false, TextWriter.Null);
        await Setup.RemoveAsync("codex", false, TextWriter.Null);
        Assert.False(codex.Present);
        Assert.False(File.Exists(Owner("codex")));
        Assert.Equal("[hooks.state]\n\"user-hook\" = \"trusted\"\n", File.ReadAllText(trust));
        Assert.Equal(1, codex.Calls.Count(c => c[2] == "add" && c[1] == "marketplace"));
    }

    [Fact]
    public async Task Codex_marketplace_errors_identify_the_target_without_echoing_private_CLI_output()
    {
        codex.FailMarketplace = true;

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Setup.InstallAsync("codex", false, TextWriter.Null));

        Assert.Contains($"plugin marketplace add {codex.Root}", error.Message);
        Assert.DoesNotContain("private-cli-output", error.Message);
        Assert.Contains("hook operation is incomplete", error.Message);
    }

    [Fact]
    public async Task A_foreign_Codex_marketplace_is_not_claimed()
    {
        Directory.CreateDirectory(Homes.Codex);
        codex.Present = true;
        var before = Listing();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Setup.InstallAsync("codex", false, TextWriter.Null));
        Assert.Equal(before, Listing());
    }

    [Theory]
    [InlineData("codex", "agent-ha-bridge", false)]
    [InlineData("codex", "copilot-ha-bridge", false)]
    [InlineData("codex", "agent-ha-bridge", true)]
    [InlineData("codex", "copilot-ha-bridge", true)]
    [InlineData("copilot", "agent-ha-bridge", false)]
    [InlineData("copilot", "copilot-ha-bridge", false)]
    [InlineData("copilot", "agent-ha-bridge", true)]
    [InlineData("copilot", "copilot-ha-bridge", true)]
    [InlineData("claude", "agent-ha-bridge", false)]
    [InlineData("claude", "copilot-ha-bridge", false)]
    [InlineData("claude", "agent-ha-bridge", true)]
    [InlineData("claude", "copilot-ha-bridge", true)]
    public async Task A_legacy_Codex_marketplace_refuses_setup_even_when_its_old_directory_is_gone(string agent, string marketplace, bool owned)
    {
        if (owned) { await Setup.InstallAsync("codex", false, TextWriter.Null); }
        Directory.CreateDirectory(Homes.Codex);
        codex.LegacyMarketplace = marketplace;
        var before = Listing();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Setup.InstallAsync(agent, false, TextWriter.Null));

        Assert.Equal(before, Listing());
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    public async Task An_existing_Codex_home_must_be_checkable_before_another_agent_is_registered(string agent)
    {
        Directory.CreateDirectory(Homes.Codex);
        locator.Locate = (launcher, _) => launcher.Id == "codex" ? null : "fixture-" + launcher.Id;
        var before = Listing();

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Setup.InstallAsync(agent, false, TextWriter.Null));

        Assert.Contains("existing Codex home", error.Message);
        Assert.Equal(before, Listing());
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    public async Task Another_agents_setup_checks_the_configured_Codex_CLI_without_installing_Codex_hooks(string agent)
    {
        Directory.CreateDirectory(Homes.Codex);
        var executable = Path.Combine(root, "configured-codex.exe");
        ConfigurePath("codex", executable);
        locator.Locate = (launcher, config) =>
        {
            if (launcher.Id != "codex") { return "fixture-" + launcher.Id; }
            Assert.Equal(executable, config.Path);
            return config.Path;
        };

        await Setup.InstallAsync(agent, false, TextWriter.Null);

        Assert.NotEmpty(codex.Calls);
        Assert.All(codex.Calls, c => Assert.Equal("list", c[2]));
        Assert.True(File.Exists(Owner(agent)));
        Assert.False(File.Exists(Owner("codex")));
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    public async Task A_failed_Codex_legacy_probe_does_not_register_another_agent(string agent)
    {
        Directory.CreateDirectory(Homes.Codex);
        codex.FailListing = true;
        var before = Listing();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Setup.InstallAsync(agent, false, TextWriter.Null));

        Assert.Equal(before, Listing());
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    public async Task Legacy_Codex_marketplaces_also_block_removing_another_agents_hooks(string agent)
    {
        await Setup.InstallAsync(agent, false, TextWriter.Null);
        Directory.CreateDirectory(Homes.Codex);
        codex.LegacyMarketplace = "agent-ha-bridge";
        var before = Listing();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Setup.RemoveAsync(agent, false, TextWriter.Null));

        Assert.Equal(before, Listing());
    }

    [Theory]
    [InlineData("copilot")]
    [InlineData("claude")]
    public async Task Legacy_activity_is_rechecked_after_the_Codex_probe_before_registering_another_agent(string agent)
    {
        Directory.CreateDirectory(Homes.Codex);
        codex.OnList = () => active = "fixture legacy process started during probe";
        var before = Listing();

        await Assert.ThrowsAsync<InvalidOperationException>(() => Setup.InstallAsync(agent, false, TextWriter.Null));

        Assert.Equal(before, Listing());
    }

    [Fact]
    public async Task New_hooks_spool_to_the_runtime_the_fresh_host_consumes()
    {
        await Setup.InstallAsync("copilot", false, TextWriter.Null);
        var runtime = IntakeState.Load(Profile).State!.RuntimeRoot;
        await using var intake = new HookIntake(new HookIntakeOptions
        {
            RuntimeRoot = runtime, ClaudeHome = Homes.Claude, AgentHaBridgeActive = () => null, UseFileSystemWatcher = false,
        }, new Processes());
        intake.Pass();
        var notices = intake.Subscribe();
        using var input = new MemoryStream(Encoding.UTF8.GetBytes("""{"sessionId":"fresh-session"}"""));
        using var output = new MemoryStream();
        var exit = HookRunner.Run(["copilot", "agent_stop"], input, output, new HookEnvironment
        {
            ExecutableDirectory = Path.GetDirectoryName(Hook)!,
        });
        Assert.Equal(0, exit);
        Assert.Equal("{}\n", Encoding.UTF8.GetString(output.ToArray()));
        Assert.Equal(1, intake.Pass());
        Assert.True(notices.TryRead(out var notice));
        Assert.Equal("fresh-session", notice.SessionId);
    }

    [Theory]
    [InlineData(new[] { "hooks", "install", "copilot" }, 4, 1)]
    [InlineData(new[] { "hooks", "remove", "codex" }, 4, 1)]
    [InlineData(new[] { "hooks", "install", "copilot", "--profile", "--dry-run" }, 2, 0)]
    [InlineData(new[] { "hooks", "remove", "codex", "--profile", "--dry-run" }, 2, 0)]
    [InlineData(new[] { "hooks", "install", "claude", "--dry-run", "--bogus" }, 2, 0)]
    public async Task Only_valid_parsed_write_commands_reach_the_management_guard(string[] args, int expected, int guardCalls)
    {
        var calls = 0;
        var before = Listing();
        var result = await HookCommands.SetupAsync(args, Path.Combine(root, "profiles"), new SessionSourceOptions
        {
            CopilotHome = Homes.Copilot, ClaudeHome = Homes.Claude, CodexHome = Homes.Codex,
        }, TextWriter.Null, TextWriter.Null, () => { calls++; return true; });
        Assert.Equal(expected, result);
        Assert.Equal(guardCalls, calls);
        Assert.Equal(before, Listing());
    }

    [Fact]
    public async Task A_valid_dry_run_never_asks_to_bypass_the_management_guard()
    {
        var calls = 0;
        var before = Listing();
        await HookCommands.SetupAsync(["hooks", "remove", "copilot", "--dry-run"], Path.Combine(root, "profiles"), new SessionSourceOptions
        {
            CopilotHome = Homes.Copilot, ClaudeHome = Homes.Claude, CodexHome = Homes.Codex,
        }, TextWriter.Null, TextWriter.Null, () => { calls++; return true; });
        Assert.Equal(0, calls);
        Assert.Equal(before, Listing());
    }

    sealed class Locator : IAgentLocator
    {
        public Func<Launcher, AgentConfig, string?> Locate = (launcher, _) => "fixture-" + launcher.Id;
        public string? Find(Launcher launcher, AgentConfig config) => Locate(launcher, config);
    }

    sealed class Processes : IProcessInspector
    {
        public IReadOnlyList<AgentProcess> GetAgentProcesses(AgentKind agent) => [];
    }

    sealed class CodexRunner : ICommandRunner
    {
        public string Root = "";
        public bool Present;
        public bool FailPlugin;
        public bool FailMarketplace;
        public bool FailListing;
        public Action? OnList;
        public string? LegacyMarketplace;
        public readonly List<string[]> Calls = [];
        public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add(arguments.ToArray());
            if (arguments.SequenceEqual(new[] { "plugin", "marketplace", "list", "--json" }))
            {
                OnList?.Invoke();
                if (FailListing) { return Task.FromResult(new CommandResult(true, false, 1, "private-cli-output")); }
                var path = OperatingSystem.IsWindows() ? @"\\?\" + Root : Root;
                var entry = new JsonObject { ["name"] = "agent-bridge", ["root"] = path };
                var marketplaces = Present ? new JsonArray(entry) : new JsonArray();
                if (LegacyMarketplace is { } legacy) { marketplaces.Add(new JsonObject { ["name"] = legacy, ["root"] = Path.Combine(Root, "missing-legacy") }); }
                return Task.FromResult(new CommandResult(true, false, 0,
                    new JsonObject { ["marketplaces"] = marketplaces }.ToJsonString()));
            }
            if (arguments[1] == "marketplace")
            {
                if (FailMarketplace) { return Task.FromResult(new CommandResult(true, false, 1, "private-cli-output")); }
                Present = arguments[2] == "add";
            }
            return Task.FromResult(new CommandResult(true, false, FailPlugin && arguments[1] == "add" ? 1 : 0, ""));
        }
    }
}
