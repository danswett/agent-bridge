using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Hooks;
using AgentBridge.Host.Core.Intake;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Migration;
using Xunit;

// An agent-ha-bridge installation laid out under a temporary HOME the way its install.ps1,
// install-claude.ps1 and install-codex.ps1 leave one (native hook in place, every client
// selected), with the person's own hooks and settings beside its entries. Nothing here reads
// or writes this machine's real ~/.agent-ha-bridge, ~/.copilot, ~/.claude or ~/.codex.
sealed class FakeAgentHaBridge : IAsyncDisposable
{
    public const string Id = "0123456789abcdef0123456789abcdef";
    public const string Token = "fake-ha-token-never-copied";

    public string Home { get; } = Directory.CreateTempSubdirectory("abmigrate-home-").FullName;
    public string BridgeHome => Path.Combine(Home, ".agent-ha-bridge");
    public string CopilotHome => Path.Combine(Home, ".copilot");
    public string ClaudeHome => Path.Combine(Home, ".claude");
    public string CodexHome => Path.Combine(Home, ".codex");
    public string HooksDir => Path.Combine(BridgeHome, "hooks");
    public string Runtime => Path.Combine(BridgeHome, "runtime");
    public string GoHook => Path.Combine(BridgeHome, "bin", "agent-bridge-hook.exe");
    public string Profile => Path.Combine(Home, "AppData", "Local", "AgentBridge", "profiles", "default");
    public string HookFolder => Path.Combine(Home, "AppData", "Local", "AgentBridge", "bin");
    // Each installation's hook in a folder of its own, with its pointer.
    public string InstallationHooks => Path.Combine(HookFolder, InstallationId);
    public string InstalledHook => Path.Combine(InstallationHooks, OperatingSystem.IsWindows() ? "agent-bridge-host-hook.exe" : "agent-bridge-host-hook");
    public string HookSource => Path.Combine(Home, "app", "agent-bridge-host-hook.exe");
    public string CopilotHooks => Path.Combine(CopilotHome, "hooks", "decision-notifier.json");
    public string ClaudeSettings => Path.Combine(ClaudeHome, "settings.json");
    public string CodexHooks => Path.Combine(BridgeHome, "codex-bridge", "plugins", "agent-ha-bridge", "hooks.json");
    public string CodexScript => Path.Combine(BridgeHome, "codex-bridge", "plugins", "agent-ha-bridge", "hooks", "codex-bridge-hook.ps1");
    public string ConfigPath => Path.Combine(BridgeHome, "config.json");

    public FakeRuntime Runtime_ { get; }
    public FakeHost Host { get; }
    public InProcessHook Hook { get; }

    // Another installation's id, for a test that needs two.
    public string InstallationId { get; }

    public FakeAgentHaBridge(JsonObject? config = null, string installationId = Id)
    {
        InstallationId = installationId;
        Directory.CreateDirectory(Profile);
        Write(Path.Combine(BridgeHome, "installation.json"), Json(new JsonObject
        {
            ["schemaVersion"] = 1, ["id"] = InstallationId, ["bridgeHome"] = BridgeHome, ["home"] = Home, ["configPath"] = ConfigPath, ["isolated"] = false,
            ["copilotHome"] = CopilotHome, ["claudeHome"] = ClaudeHome, ["codexHome"] = CodexHome, ["desktopConfig"] = null, ["testRegistryId"] = null,
        }));
        Write(ConfigPath, Json(config ?? DefaultConfig()));
        foreach (var script in new[] { "notify-agent-response.ps1", "route-ask-user-v3.ps1", "notify-home-assistant.ps1", "agent-bridge-daemon.ps1", "agent-bridge-supervisor.ps1", "agent-bridge-launch.vbs" })
        {
            Write(Path.Combine(HooksDir, script), "# agent-ha-bridge\r\n");
        }
        Write(GoHook, "MZ fake go hook");
        Directory.CreateDirectory(SpoolContract.SpoolDirectory(Runtime));
        Directory.CreateDirectory(Path.Combine(Runtime, SpoolContract.ClaudeFolder));
        Write(Path.Combine(Runtime, "daemon.process.json"), "{\"pid\":4242,\"installationId\":\"" + InstallationId + "\",\"executable\":\"C:\\\\pwsh.exe\",\"startedUtcTicks\":1}\r\n");

        // ~/.copilot/hooks/decision-notifier.json: install.ps1's native (exec) form, after the
        // person's own preToolUse hook, which must come through untouched.
        Write(CopilotHooks, Json(new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["preToolUse"] = new JsonArray(
                    new JsonObject { ["type"] = "command", ["matcher"] = "bash", ["powershell"] = "& 'C:\\mine\\audit.ps1'", ["timeoutSec"] = 5 },
                    new JsonObject { ["type"] = "command", ["matcher"] = "ask_user", ["timeoutSec"] = 120, ["exec"] = GoHook, ["args"] = new JsonArray("copilot", "ask_user", Path.Combine(HooksDir, "route-ask-user-v3.ps1")) }),
                ["agentStop"] = new JsonArray(
                    new JsonObject { ["type"] = "command", ["timeoutSec"] = 30, ["exec"] = GoHook, ["args"] = new JsonArray("copilot", "agent_stop", Path.Combine(HooksDir, "notify-agent-response.ps1")) }),
                ["notification"] = new JsonArray(
                    new JsonObject { ["type"] = "command", ["matcher"] = "permission_prompt", ["timeoutSec"] = 15, ["exec"] = GoHook, ["args"] = new JsonArray("copilot", "permission", Path.Combine(HooksDir, "notify-home-assistant.ps1")) }),
            },
            ["version"] = 1,
        }));
        Write(Path.Combine(CopilotHome, "instructions", "agent-ha-bridge.instructions.md"), $"<!-- agent-ha-bridge-owner:{InstallationId} -->\r\n# agent-ha-bridge\r\n");

        // ~/.claude/settings.json, with a byte order mark (Windows PowerShell's UTF8), the
        // person's settings and a hook of their own.
        var adapter = Path.Combine(ClaudeHome, "ha-bridge");
        JsonObject Claude(string matcher, string script, string name, int timeout) => new()
        {
            ["matcher"] = matcher,
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = $"\"{GoHook}\" claude {name} \"{Path.Combine(adapter, script)}\"", ["timeout"] = timeout }),
        };
        Write(ClaudeSettings, "\uFEFF" + Json(new JsonObject
        {
            ["model"] = "opus",
            ["permissions"] = new JsonObject { ["allow"] = new JsonArray("Bash(git status)") },
            ["hooks"] = new JsonObject
            {
                ["PreToolUse"] = new JsonArray(
                    new JsonObject { ["matcher"] = "Bash", ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = "C:\\mine\\guard.cmd", ["timeout"] = 5 }) },
                    Claude("AskUserQuestion", "route-askuserquestion.ps1", "ask", 30)),
                ["Notification"] = new JsonArray(Claude("", "route-notification.ps1", "notification", 30)),
                ["Stop"] = new JsonArray(Claude("", "notify-claude-stop.ps1", "stop", 30)),
                ["SessionStart"] = new JsonArray(Claude("", "register-claude-session.ps1", "register", 15)),
                ["UserPromptSubmit"] = new JsonArray(Claude("", "register-claude-session.ps1", "register", 15)),
            },
        }));
        foreach (var script in new[] { "route-askuserquestion.ps1", "route-notification.ps1", "notify-claude-stop.ps1", "register-claude-session.ps1", "run-hook.cmd" })
        {
            Write(Path.Combine(adapter, script), "rem agent-ha-bridge\r\n");
        }
        Write(Path.Combine(ClaudeHome, "CLAUDE.md"), $"My notes.\r\n\r\n<!-- agent-ha-bridge-owner:{InstallationId} -->\r\nguidance\r\n<!-- /agent-ha-bridge -->\r\n");

        // The Codex plugin install-codex.ps1 registers, in its native form.
        var codex = new JsonObject();
        foreach (var e in new[] { "SessionStart", "UserPromptSubmit", "PermissionRequest", "PreToolUse", "Stop", "SessionEnd" })
        {
            codex[e] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = $"{GoHook} codex hook {CodexScript}", ["timeout"] = 20 }) });
        }
        Write(CodexHooks, Json(new JsonObject { ["description"] = "Copilot Home Assistant bridge", ["hooks"] = codex }));
        Write(CodexScript, "# codex hook\r\n");
        Write(Path.Combine(CodexHome, "AGENTS.md"), $"<!-- agent-ha-bridge-owner:{InstallationId} -->\r\nguidance\r\n<!-- /agent-ha-bridge -->\r\n");
        Write(Path.Combine(CodexHome, "config.toml"), "model = \"gpt-5\"\r\n\r\n[hooks.state]\r\n\"agent-ha-bridge\" = \"trusted\"\r\n");

        Write(HookSource, "MZ the product's hook");

        Runtime_ = new FakeRuntime(this);
        Host = new FakeHost(this);
        Hook = new InProcessHook();
    }

    // An absolute path on this platform: the settings carried over must load here.
    public static string Rooted(params string[] parts) => Path.Combine([OperatingSystem.IsWindows() ? "C:\\" : "/", .. parts]);

    public static JsonObject DefaultConfig() => new()
    {
        ["homeAssistant"] = new JsonObject
        {
            ["baseUrl"] = "http://ha.example:8123", ["token"] = Token, ["tokenEnvVar"] = "", ["agentToken"] = "fake-agent-token",
            ["agentTokenEnvVar"] = "AGENT_HA_AGENT_TOKEN", ["agentUserIds"] = new JsonArray("u-agent"),
        },
        ["newSession"] = new JsonObject
        {
            ["enabled"] = true,
            ["workspaces"] = new JsonArray(new JsonObject { ["label"] = "app", ["path"] = Rooted("repos", "app"), ["isolate"] = true }, Rooted("repos", "docs")),
            ["defaultWorkspace"] = "app", ["launcher"] = "claude", ["profiles"] = new JsonArray("work"), ["defaultProfile"] = "work",
            ["resumeCount"] = 80, ["allowAllTools"] = false,
            ["model"] = new JsonObject { ["copilot"] = "gpt-5", ["claude"] = "opus" },
            ["effort"] = new JsonObject { ["codex"] = "high" },
            ["extraArgs"] = new JsonArray("--verbose"),
            ["claudePath"] = Rooted("tools", "claude.exe"),
            ["worktreeRoot"] = "~/repos/wt", ["worktreeLimit"] = 10, ["worktreeIdleHours"] = 12,
            ["codexStartPrompt"] = "hi", ["discoverWorkspaces"] = true,
        },
        ["dashboard"] = new JsonObject
        {
            ["urlPath"] = "agent-decisions",
            ["publication"] = new JsonObject { ["authority"] = "home", ["participant"] = "laptop", ["writer"] = "desk", ["generation"] = 3 },
        },
        ["detailedActivity"] = true,
        ["devBox"] = new JsonObject { ["keepAwake"] = false },
        ["usage"] = new JsonObject { ["enabled"] = true },
        ["answerButtons"] = new JsonObject { ["max"] = 4 },
    };

    public Migrator Migrator(TimeSpan? verify = null, string? profile = null, string? hookFolder = null) => new(new MigrationOptions
    {
        ProfileFolder = profile ?? Profile,
        BridgeHome = BridgeHome,
        HookFolder = hookFolder ?? HookFolder,
        HookSource = HookSource,
        LocalAppData = Path.Combine(Home, "AppData", "Local"),
        StopTimeout = TimeSpan.FromSeconds(2),
        VerifyTimeout = verify ?? TimeSpan.FromSeconds(15),
        PollEvery = TimeSpan.FromMilliseconds(25),
    }, Runtime_, Host, Hook);

    // Every file under HOME and its hash, but for what the daemon and the hook change by
    // running (the runtime folder), the hook a migration installs, which stays, and the lock a
    // migrate or rollback holds while it runs.
    public SortedDictionary<string, string> Listing()
    {
        var listing = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(Home, "*", SearchOption.AllDirectories))
        {
            if (file.StartsWith(Runtime + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { continue; }
            if (file.StartsWith(HookFolder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) { continue; }
            if (Path.GetFileName(file) == "migration.lock") { continue; }
            listing[Path.GetRelativePath(Home, file)] = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(file)));
        }
        return listing;
    }

    public static JsonObject Read(string path) => (JsonObject)JsonNode.Parse(Registrations.Decode(File.ReadAllBytes(path)))!;

    static string Json(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = true, IndentSize = 2, NewLine = "\r\n", Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }) + "\r\n";

    static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text));
    }

    public async ValueTask DisposeAsync()
    {
        await Host.DisposeAsync();
        try { Directory.Delete(Home, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

// agent-ha-bridge's tasks and processes, as a migration sees them.
sealed class FakeRuntime(FakeAgentHaBridge fake) : IAgentHaBridgeRuntime
{
    public Dictionary<string, (bool Enabled, bool Running)> Tasks { get; } = new()
    {
        [$"AgentBridgeDaemon_{fake.InstallationId}"] = (true, true),
    };
    public bool DaemonRunning { get; set; } = true;
    public bool SupervisorRunning { get; set; } = true;
    // A daemon that does not stop when it is told to.
    public bool Stubborn { get; set; }
    // A daemon that does not stop, after its supervisor did.
    public bool OutlivesSupervisor { get; set; }
    // A task that starts, but no daemon that is seen.
    public bool StartsNothing { get; set; }
    // Tasks Task Scheduler refuses to change or run for this user (registered from an elevated
    // prompt). With RefusalVisible, Task reports them as not modifiable; without, it cannot tell.
    public HashSet<string> Refused { get; } = [];
    public bool RefusalVisible { get; set; } = true;
    // Tasks Task Scheduler cannot be asked about at all (its service refusing the query).
    public HashSet<string> Unreadable { get; } = [];
    public List<string> Calls { get; } = [];

    public ScheduledTaskState? Task(string name) => Unreadable.Contains(name)
        ? throw new IOException($"Task Scheduler refused {name}: The RPC server is unavailable.")
        : Tasks.TryGetValue(name, out var t)
        ? new(t.Enabled, t.Running, RefusalVisible ? !Refused.Contains(name) : null)
        : null;

    public void SetEnabled(string name, bool enabled)
    {
        Calls.Add($"{(enabled ? "enable" : "disable")} {name}");
        if (Refused.Contains(name)) { throw new IOException($"Task Scheduler refused {name}: Access is denied."); }
        Tasks[name] = (enabled, Tasks[name].Running);
    }

    public void Start(string name)
    {
        Calls.Add($"start {name}");
        if (Refused.Contains(name)) { throw new IOException($"Task Scheduler refused {name}: Access is denied."); }
        if (!Tasks[name].Enabled) { throw new IOException("disabled"); }
        Tasks[name] = (true, true);
        if (name.StartsWith("AgentBridgeDaemon_", StringComparison.Ordinal) && !StartsNothing) { DaemonRunning = true; SupervisorRunning = true; }
    }

    // Runs as the daemon is stopped: after the snapshot, before any settings file is written.
    public Action? OnStop { get; set; }

    public void Stop(AgentHaBridgeInstall install)
    {
        Assert.Equal(fake.BridgeHome, install.BridgeHome);
        Calls.Add("stop");
        OnStop?.Invoke();
        if (Stubborn) { return; }
        SupervisorRunning = false;
        if (OutlivesSupervisor) { throw new IOException("agent-ha-bridge's daemon (process 4242) did not stop"); }
        DaemonRunning = false;
        foreach (var name in Tasks.Keys.ToList()) { Tasks[name] = (Tasks[name].Enabled, false); }
    }

    // The probe cannot tell (the listing and the scheduler both refused it).
    public bool Unknown { get; set; }

    public string? Active() => DaemonRunning ? "agent-ha-bridge is running for this user (AgentBridgeDaemon_" + fake.InstallationId + ")"
        : Unknown ? "whether agent-ha-bridge is running for this user could not be checked; answer at the machine" : null;

    // Another installation's daemon, running whatever this one does.
    public string? OtherDaemon { get; set; }

    public bool Seen(string installationId) => (DaemonRunning && installationId == fake.InstallationId) || installationId == OtherDaemon;

    public bool SupervisorSeen(string installationId) => SupervisorRunning && installationId == fake.InstallationId;

    // Each agent's running processes (none unless set); null when they could not all be identified.
    public Dictionary<AgentBridge.Protocol.AgentKind, int?> Agents { get; } = [];

    // When each agent's processes started; unknown when not given.
    public Dictionary<AgentBridge.Protocol.AgentKind, DateTimeOffset> AgentsStarted { get; } = [];
    public int? Running(AgentBridge.Protocol.AgentKind agent, DateTimeOffset? startedBefore = null) =>
        !Agents.TryGetValue(agent, out var n) ? 0
        : startedBefore is { } before && AgentsStarted.TryGetValue(agent, out var started) && started >= before ? 0
        : n;
}

// The host serving the profile: a restart reads intake.json afresh and runs the real intake,
// standing back while the fake daemon runs, as serve does.
sealed class FakeHost(FakeAgentHaBridge fake) : IMigrationHost, IAsyncDisposable
{
    HookIntake? _intake;
    public bool Running { get; set; } = true;
    public int Restarts { get; private set; }
    // Says the host will not restart (the migration's check then fails).
    public string? RefuseRestart { get; set; }
    // Serves without taking the spool, as a host too old to know intake.json would.
    public bool IgnoreIntake { get; set; }
    // The profile's config.json as each restart read it (null: none).
    public List<string?> ConfigAtRestart { get; } = [];
    // The migration is cut short here, as by the process being ended.
    public bool Interrupt { get; set; }

    public Task<bool> RunningAsync(CancellationToken cancellationToken) => Task.FromResult(Running);

    public async Task<string?> RestartAsync(CancellationToken cancellationToken)
    {
        if (Interrupt) { throw new OperationCanceledException("the migration was cut short"); }
        if (RefuseRestart is not null) { return RefuseRestart; }
        Restarts++;
        var config = Path.Combine(fake.Profile, "config.json");
        ConfigAtRestart.Add(File.Exists(config) ? File.ReadAllText(config) : null);
        if (_intake is not null) { await _intake.DisposeAsync(); _intake = null; }
        if (Linger && _lingering is null)
        {
            _lingering = new FileStream(Path.Combine(fake.Runtime, HookIntake.ClaimFile), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        if (!IgnoreIntake && IntakeState.Load(fake.Profile).State is { } state)
        {
            _intake = new HookIntake(new HookIntakeOptions
            {
                RuntimeRoot = state.RuntimeRoot,
                ClaudeHome = fake.ClaudeHome,
                AgentHaBridgeActive = fake.Runtime_.Active,
                SweepInterval = TimeSpan.FromMilliseconds(100),
            }, new SystemProcessInspector());
            _intake.Start();
        }
        return null;
    }

    public bool TakingSpool => _intake is not null;

    // Acknowledges a restart while its last pass still holds the spool's claim, until Release().
    public bool Linger { get; set; }
    FileStream? _lingering;

    public void Release()
    {
        _lingering?.Dispose();
        _lingering = null;
    }

    public async ValueTask DisposeAsync()
    {
        Release();
        if (_intake is not null) { await _intake.DisposeAsync(); }
    }
}

// The installed hook, run in this process: the real hook logic, reading the real pointer the
// migration wrote beside the program.
sealed class InProcessHook : IHookRunner
{
    public List<string> Runs { get; } = [];
    // Each run as a process of its own, so its spool file is named by a pid of its own.
    int _pid = 40000;
    // Runs given the fixed reply alone (--fallback): nothing is spooled.
    public bool Unspooled { get; set; }
    // Runs with each, before it: another session's hook, say.
    public Action<string>? Alongside { get; set; }

    public (int Exit, string Stdout, int Pid) Run(string executable, IReadOnlyList<string> arguments, string stdin)
    {
        Assert.True(File.Exists(executable), $"{executable} was not installed");
        Runs.Add(string.Join(' ', arguments));
        Alongside?.Invoke(executable);
        return RunAs(executable, Unspooled && arguments.Count > 1 ? [.. arguments, "--fallback"] : arguments, stdin, ++_pid);
    }

    public static (int Exit, string Stdout, int Pid) RunAs(string executable, IReadOnlyList<string> arguments, string stdin, int pid)
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(stdin));
        using var output = new MemoryStream();
        var exit = HookRunner.Run(arguments, input, output, new HookEnvironment { ExecutableDirectory = Path.GetDirectoryName(executable), Ancestors = () => [], Version = "test", ProcessId = pid });
        return (exit, Encoding.UTF8.GetString(output.ToArray()), pid);
    }
}
