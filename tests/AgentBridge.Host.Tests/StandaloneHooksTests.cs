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
    [InlineData("agent-bridge-host-hook.exe claude register")]
    [InlineData("pwsh -File C:\\tools\\ha-bridge\\register.ps1")]
    public async Task Unowned_Claude_bridge_commands_are_refused_even_without_a_legacy_directory(string command)
    {
        Write(Registration("claude"), new JsonObject
        {
            ["hooks"] = new JsonObject { ["SessionStart"] = new JsonArray(new JsonObject
            {
                ["hooks"] = new JsonArray(new JsonObject { ["command"] = command }),
            }) },
        }.ToJsonString());
        var before = Listing();
        await Assert.ThrowsAsync<InvalidOperationException>(() => Setup.InstallAsync("claude", false, TextWriter.Null));
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
        public string? Find(Launcher launcher, AgentConfig config) => "fixture-" + launcher.Id;
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
        public readonly List<string[]> Calls = [];
        public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken cancellationToken)
        {
            Calls.Add(arguments.ToArray());
            if (arguments.SequenceEqual(new[] { "plugin", "marketplace", "list", "--json" }))
            {
                var path = OperatingSystem.IsWindows() ? @"\\?\" + Root : Root;
                var entry = new JsonObject { ["name"] = "agent-bridge", ["root"] = path };
                return Task.FromResult(new CommandResult(true, false, 0,
                    new JsonObject { ["marketplaces"] = Present ? new JsonArray(entry) : new JsonArray() }.ToJsonString()));
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
