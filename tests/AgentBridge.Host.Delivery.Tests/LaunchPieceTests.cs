using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

// The pieces a launch is made of, each against the real thing where it is cheap: the command
// lines agent-ha-bridge builds, Windows' own parsing of them (through cmd.exe for a batch
// shim, as an npm install of Copilot or Codex is), git worktrees in throwaway repositories,
// and the stores resumable sessions are read from. No agent is ever started: the program
// launched is the tests' own console echo.
public sealed class LaunchPieceTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("ablaunch-").FullName;

    public void Dispose()
    {
        // git marks some of its files read-only.
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) { File.SetAttributes(f, FileAttributes.Normal); }
        try { Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static Launcher L(string id) => Launchers.Find(id)!;

    // ---- command lines, exactly as agent-ha-bridge's Get-BridgeNewSessionArguments ----

    [Fact]
    public void A_new_copilot_session_is_named_up_front_and_its_prompt_goes_last()
    {
        Assert.Equal(["--session-id", "S", "--banner", "--model", "gpt-6.1-sol", "--reasoning-effort", "high", "--context", "long_context", "--allow-all", "--x", "-i", "Fix it"],
            Launchers.Arguments(L("copilot"), "S", resume: false, "gpt-6.1-sol", "high", "long_context", allowAll: true, ["--x", " "], "", "Fix it"));
        // Resuming is the same --session-id, and nothing is passed for what was left to the agent.
        Assert.Equal(["--session-id", "S", "--banner"], Launchers.Arguments(L("copilot"), "S", resume: true, "", "", "", false, [], "", ""));
    }

    [Fact]
    public void Agency_starts_copilot_with_its_profile_only_and_passes_copilots_options_on()
    {
        Assert.Equal(["copilot", "--profile-only", "home", "--session-id", "S", "--banner", "-i", "go"],
            Launchers.Arguments(L("agency"), "S", false, "", "", "", false, [], "home", "go"));
        Assert.Equal(["copilot", "--session-id", "S", "--banner"], Launchers.Arguments(L("agency"), "S", false, "", "", "", false, [], "", ""));
    }

    [Fact]
    public void Claude_resumes_with_its_own_flag_and_takes_the_prompt_after_a_double_dash()
    {
        Assert.Equal(["--session-id", "S", "--model", "opus", "--effort", "max", "--autocompact", "1m", "--dangerously-skip-permissions", "--", "--not-a-flag"],
            Launchers.Arguments(L("claude"), "S", false, "opus", "max", "1m", true, [], "", "--not-a-flag"));
        Assert.Equal(["--resume", "S"], Launchers.Arguments(L("claude"), "S", true, "", "", "", false, [], "", ""));
    }

    [Fact]
    public void Codex_takes_effort_and_context_as_overrides_and_its_resume_id_after_the_extras()
    {
        Assert.Equal(["--no-daemon", "-c", "model_reasoning_summary=detailed", "-c", "model_reasoning_effort=high", "-c", "model_context_window=400000",
            "--model", "gpt-6-sol", "--ask-for-approval", "never", "--", "go"],
            Launchers.Arguments(L("codex"), "", false, "gpt-6-sol", "high", "400000", true, [], "", "go"));
        Assert.Equal(["--no-daemon", "-c", "model_reasoning_summary=detailed", "resume", "--x", "S", "--", "go"],
            Launchers.Arguments(L("codex"), "S", true, "", "", "", false, ["--x"], "", "go"));
    }

    // ---- what Windows makes of those lines ----

    static readonly string[] Nasty =
    [
        "--banner", "-i",
        "a & calc | x < y > z ^ caret %PATH% %% 100% !USERNAME! \"quoted\" 'single' (paren) trailing\\",
        @"C:\path with space\", "", "émoji 🚀 — “smart”", "%%cd:~,%", "\\\"", "plain",
    ];

    [WindowsFact]
    public void An_executable_reads_back_exactly_the_arguments_it_was_given()
    {
        var line = WindowsCommandLine.For(Echo, ["--args", Out("exe.json"), .. Nasty], out var problem);
        Assert.Null(problem);
        Assert.Equal(Nasty, RunAndRead(line!.Value, "exe.json"));
    }

    [WindowsFact]
    public void A_batch_shim_passes_every_argument_on_through_cmd_exe_without_running_or_expanding_any_of_it()
    {
        // As npm's shims are: the script hands %* to the real program.
        var shim = Path.Combine(root, "agent.cmd");
        File.WriteAllText(shim, $"@\"{Echo}\" --args \"{Out("bat.json")}\" %*\r\n");
        var canary = Path.Combine(root, "ran.txt");
        string[] args = [.. Nasty, $"x & echo pwned > \"{canary}\"", $"\" & echo pwned > \"{canary}\" & \""];
        var line = WindowsCommandLine.For(shim, args, out var problem);
        Assert.Null(problem);
        Assert.EndsWith("cmd.exe", line!.Value.Application);
        Assert.Equal(args, RunAndRead(line.Value, "bat.json"));
        Assert.False(File.Exists(canary));
    }

    [WindowsFact]
    public async Task Agency_installed_as_a_batch_shim_still_lists_its_profiles_from_a_folder_cmd_exe_would_misread_unquoted()
    {
        // Program Files (x86): an unquoted ( or & is where cmd.exe goes wrong.
        var folder = Directory.CreateDirectory(Path.Combine(root, "Program Files (x86) & co")).FullName;
        var shim = Path.Combine(folder, "agency.cmd");
        File.WriteAllText(shim, $"@echo Profiles:\r\n@echo   home\r\n@echo   work\r\n@\"{Echo}\" --args \"{Out("agency.json")}\" %*\r\n");

        var ran = await new BatchFileRunner(new AgentBridge.Host.Core.Usage.CommandRunner()).RunAsync(shim, ["config", "profiles"], TimeSpan.FromSeconds(30));
        Assert.Equal((true, 0), (ran.Ran, ran.ExitCode));
        Assert.Equal(["config", "profiles"], Read("agency.json"));
        Assert.Equal(["home", "work"], await new AgencyCatalog(new BatchFileRunner(new AgentBridge.Host.Core.Usage.CommandRunner())).ProfilesAsync(shim));
    }

    [Fact]
    public void A_line_too_long_for_cmd_exe_is_refused_rather_than_cut()
    {
        Assert.Null(WindowsCommandLine.For(@"C:\npm\copilot.cmd", ["-i", new string('a', 9000)], out var problem));
        Assert.Contains("cmd.exe takes at most 8191", problem);
        Assert.NotNull(WindowsCommandLine.For(@"C:\bin\copilot.exe", ["-i", new string('a', 9000)], out _));
        Assert.Null(WindowsCommandLine.For(@"C:\bin\copilot.exe", ["-i", new string('a', 33000)], out problem));
        Assert.Contains("Windows takes at most", problem);
    }

    [WindowsFact]
    public void The_starter_opens_a_console_of_the_agents_own_in_the_working_directory_and_can_tell_when_it_has_gone()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var starter = new WindowsAgentStarter(hidden: true);
        var cwd = Directory.CreateDirectory(Path.Combine(root, "work dir")).FullName;
        var started = starter.Start(new StartSpec(Echo, ["--args", Out("start.json"), "-i", "a prompt"], cwd));
        Assert.True(started.ProcessId > 0);
        Assert.True(Math.Abs((started.StartedAt - DateTimeOffset.UtcNow).TotalSeconds) < 60);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        Assert.Equal(["-i", "a prompt"], ReadOnceWritten("start.json", deadline));
        while (starter.Running(started) && DateTime.UtcNow < deadline) { Thread.Sleep(50); }
        Assert.False(starter.Running(started));
        Assert.False(starter.Running(started with { StartedAt = started.StartedAt.AddMinutes(-5) }));
    }

    [WindowsFact]
    public void A_started_agent_gets_the_session_variable_on_top_of_the_hosts_own_environment()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        Environment.SetEnvironmentVariable("AGENT_BRIDGE_TEST_INHERITED", "kept");
        var shim = Path.Combine(root, "env.cmd");
        File.WriteAllText(shim, $"@echo %{Launchers.SessionVariable}% %AGENT_BRIDGE_TEST_INHERITED%> \"{Out("env.txt")}\"\r\n");
        var started = new WindowsAgentStarter(hidden: true).Start(new StartSpec(shim, [], root, new Dictionary<string, string> { [Launchers.SessionVariable] = "5b0e1f2a" }));

        Assert.True(started.ProcessId > 0);
        var deadline = DateTime.UtcNow.AddSeconds(20);
        string? said = null;
        while (said is null && DateTime.UtcNow < deadline)
        {
            try { said = File.ReadAllText(Out("env.txt")).Trim(); }
            catch (IOException) { Thread.Sleep(50); }
        }
        Assert.Equal("5b0e1f2a kept", said);
    }

    [WindowsFact]
    public void A_process_is_the_launchs_own_only_if_it_runs_under_the_process_started_for_it()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        var starter = new WindowsAgentStarter(hidden: true);
        // As npm installs an agent: cmd.exe runs the shim, and the agent runs under it.
        var shim = Path.Combine(root, "agent.cmd");
        File.WriteAllText(shim, $"@\"{Echo}\" \"{Out("agent.log")}\"\r\n");
        var started = starter.Start(new StartSpec(shim, [], root));
        int agent = 0;
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(20);
            while (agent == 0 && DateTime.UtcNow < deadline)
            {
                try
                {
                    // Shared, since the agent holds its log open for writing.
                    using var log = new StreamReader(new FileStream(Out("agent.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete));
                    if (log.ReadToEnd().Split(' ') is ["ready", var pid, ..]) { agent = int.Parse(pid); }
                }
                catch (IOException) { }
                Thread.Sleep(50);
            }
            Assert.NotEqual(0, agent);
            Assert.True(starter.Owns(started, agent));
            Assert.True(starter.Owns(started, started.ProcessId));
            Assert.False(starter.Owns(started, Environment.ProcessId));
        }
        finally
        {
            if (agent != 0) { try { Process.GetProcessById(agent).Kill(); } catch (ArgumentException) { } }
        }
        var gone = DateTime.UtcNow.AddSeconds(20);
        while (starter.Running(started) && DateTime.UtcNow < gone) { Thread.Sleep(50); }
        // Once the launcher has gone, nothing can be traced to it.
        Assert.Null(starter.Owns(started, agent));
    }

    static string Echo => Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "agent-bridge-console-echo.exe" : "agent-bridge-console-echo");

    string Out(string name) => Path.Combine(root, name);

    string[] Read(string name) => JsonSerializer.Deserialize<string[]>(File.ReadAllText(Out(name)))!;

    // For a file another process is writing. It exists from the moment the writer creates it,
    // and is locked until the writer has finished, and sometimes for a few milliseconds more
    // by something else (a virus scanner, not visible to the Restart Manager). Reading as soon
    // as it appeared, as the starter's test once did, got "being used by another process":
    // 61 times in 300 when polling for it under load.
    string[] ReadOnceWritten(string name, DateTime deadline)
    {
        while (true)
        {
            try { return Read(name); }
            catch (Exception e) when ((e is IOException or JsonException) && DateTime.UtcNow < deadline) { Thread.Sleep(50); }
        }
    }

    string[] RunAndRead((string Application, string CommandLine) line, string name)
    {
        // The line as CreateProcess gets it, run windowless: what follows the program is passed on as is.
        var program = $"\"{line.Application}\"";
        var rest = line.CommandLine.StartsWith(program, StringComparison.Ordinal) ? line.CommandLine[program.Length..].TrimStart() : line.CommandLine[(line.CommandLine.IndexOf(' ') + 1)..];
        using var p = Process.Start(new ProcessStartInfo(line.Application, rest) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.WaitForExit(20000);
        return Read(name);
    }

    // ---- folders ----

    [Fact]
    public void System_folders_and_drive_roots_are_never_places_to_launch()
    {
        Assert.True(Folders.IsSystemDirectory(Path.GetPathRoot(root)!));
        Assert.True(Folders.IsSystemDirectory(Path.Combine(Path.GetTempPath(), "x")) || !OperatingSystem.IsWindows());
        if (OperatingSystem.IsWindows()) { Assert.True(Folders.IsSystemDirectory(Path.Combine(Environment.GetEnvironmentVariable("WINDIR")!, "System32"))); }
        Assert.False(Folders.IsSystemDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "repos", "app")));
    }

    [Fact]
    public void A_folder_is_in_a_workspace_by_where_it_really_is_and_a_sibling_with_the_same_prefix_is_not()
    {
        var app = Directory.CreateDirectory(Path.Combine(root, "app")).FullName;
        var sub = Directory.CreateDirectory(Path.Combine(app, "src")).FullName;
        var sibling = Directory.CreateDirectory(Path.Combine(root, "app-other")).FullName;
        IReadOnlyList<WorkspaceConfig> ws = [new("App", app, false)];
        Assert.Equal("App", Folders.Containing(ws, sub)?.Label);
        Assert.Equal("App", Folders.Containing(ws, app + Path.DirectorySeparatorChar)?.Label);
        Assert.Null(Folders.Containing(ws, sibling));
        Assert.Null(Folders.Containing(ws, Path.Combine(app, "missing")));

        // A link inside an approved folder that leads out of it does not carry approval with it.
        var link = Path.Combine(app, "escape");
        try { Directory.CreateSymbolicLink(link, sibling); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        Assert.Null(Folders.Containing(ws, link));
        Assert.Equal(Folders.Physical(sibling), Folders.Physical(link));
    }

    // ---- worktrees, against real git ----

    string Repo(bool subfolder = false)
    {
        var origin = Path.Combine(root, "origin");
        Git(root, "init", "-q", "-b", "main", origin);
        File.WriteAllText(Path.Combine(origin, "README.md"), "hello\n");
        Directory.CreateDirectory(Path.Combine(origin, "src", "svc"));
        File.WriteAllText(Path.Combine(origin, "src", "svc", "a.txt"), "a\n");
        Git(origin, "add", ".");
        Git(origin, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "one");
        var work = Path.Combine(root, "work");
        Git(root, "clone", "-q", origin, work);
        return subfolder ? Path.Combine(work, "src", "svc") : work;
    }

    static void Git(string dir, params string[] args)
    {
        var r = AgentBridge.Host.Delivery.Launch.Git.Run(dir, args);
        Assert.True(r.Ok, $"git {string.Join(' ', args)}: {r.Said}");
    }

    LaunchConfig WorktreeConfig(int limit = 10) => LaunchConfig.Default with { WorktreeRoot = Path.Combine(root, "wt"), WorktreeLimit = limit };

    [Fact]
    public void An_isolated_launch_gets_a_detached_worktree_at_the_fetched_base_named_by_time_and_marked_as_this_hosts()
    {
        var work = Repo();
        var made = Worktrees.Create(work, WorktreeConfig(), () => new DateTime(2026, 10, 28, 13, 14, 43));
        Assert.Null(made.Problem);
        Assert.Equal(Path.Combine(root, "wt", "work-20261028-131443"), made.Worktree);
        Assert.Equal(made.Worktree, made.Path);
        Assert.True(File.Exists(Path.Combine(made.Path!, "README.md")));
        Assert.True(Worktrees.IsManaged(made.Worktree!));
        Assert.False(Worktrees.IsManaged(work));
        // Detached: no branch is checked out in it.
        Assert.False(AgentBridge.Host.Delivery.Launch.Git.Run(made.Path!, ["symbolic-ref", "--quiet", "HEAD"]).Ok);
        Assert.Contains("at origin/main", made.Detail);

        // The same second again gets a name of its own.
        var again = Worktrees.Create(work, WorktreeConfig(), () => new DateTime(2026, 10, 28, 13, 14, 43));
        Assert.Equal(Path.Combine(root, "wt", "work-20261028-131443-1"), again.Worktree);
    }

    [Fact]
    public void A_workspace_in_a_subfolder_of_its_repository_launches_in_the_same_subfolder_of_the_worktree_and_resumes_there()
    {
        var svc = Repo(subfolder: true);
        var made = Worktrees.Create(svc, WorktreeConfig(), () => new DateTime(2026, 10, 28, 9, 0, 0));
        Assert.Equal(Path.Combine(made.Worktree!, "src", "svc"), made.Path);

        IReadOnlyList<WorkspaceConfig> ws = [new("Svc", svc, true)];
        var managed = Worktrees.Managed(ws, made.Path!);
        Assert.Equal(("Svc", made.Worktree), (managed?.Workspace.Label, managed?.Worktree));
        // Elsewhere in the worktree is outside what the workspace approves.
        Assert.Null(Worktrees.Managed(ws, made.Worktree!));
        // Not isolated: its worktrees are nothing to it.
        Assert.Null(Worktrees.Managed([new("Svc", svc, false)], made.Path!));
    }

    [Fact]
    public void A_session_in_one_of_agent_ha_bridges_worktrees_is_resumable_there_but_the_worktree_is_never_this_hosts_to_lock()
    {
        var work = Repo();
        var theirs = Path.Combine(root, "wt", "theirs");
        Git(work, "worktree", "add", "--detach", theirs, "HEAD");
        IReadOnlyList<WorkspaceConfig> ws = [new("Work", work, true)];
        Assert.Null(Worktrees.Managed(ws, theirs));

        File.WriteAllText(Path.Combine(Worktrees.AdminFolder(theirs)!, Worktrees.AgentHaBridgeMarker), DateTime.Now.ToString("o"));

        Assert.Equal("Work", Worktrees.Managed(ws, theirs)?.Workspace.Label);
        Assert.False(Worktrees.IsManaged(theirs));
        Assert.Equal((null, null), Worktrees.Reserve(theirs));
        Assert.False(File.Exists(Path.Combine(Worktrees.AdminFolder(theirs)!, "locked")));
    }

    [Fact]
    public void The_cap_counts_only_this_hosts_worktrees_and_refuses_rather_than_falling_back()
    {
        var work = Repo();
        // One of agent-ha-bridge's, with its own marker: never counted, never touched.
        var theirs = Path.Combine(root, "wt", "theirs");
        Git(work, "worktree", "add", "--detach", theirs, "HEAD");
        File.WriteAllText(Path.Combine(Worktrees.AdminFolder(theirs)!, "agent-bridge-created"), DateTime.Now.ToString("o"));

        Assert.Null(Worktrees.Create(work, WorktreeConfig(limit: 1), () => new DateTime(2026, 10, 28, 9, 0, 0)).Problem);
        var refused = Worktrees.Create(work, WorktreeConfig(limit: 1), () => new DateTime(2026, 10, 28, 9, 0, 1));
        Assert.Null(refused.Path);
        Assert.StartsWith("1 worktrees this host created already exist for this repository (limit 1)", refused.Problem);
        Assert.True(Directory.Exists(theirs));
    }

    [Fact]
    public void A_workspace_folder_that_the_fetched_base_turns_into_a_link_out_of_the_worktree_is_never_launched_in()
    {
        var work = Repo();
        var outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        var origin = Path.Combine(root, "origin");
        // origin replaces src/svc, a folder in the checkout, with a link to a folder elsewhere.
        File.WriteAllText(Path.Combine(root, "link-target"), outside);
        var blob = AgentBridge.Host.Delivery.Launch.Git.Run(origin, ["hash-object", "-w", Path.Combine(root, "link-target")]).Output.Trim();
        Git(origin, "rm", "-r", "-q", "--cached", "src/svc");
        Git(origin, "update-index", "--add", "--cacheinfo", $"120000,{blob},src/svc");
        Git(origin, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", "link");

        var made = Worktrees.Create(Path.Combine(work, "src", "svc"), WorktreeConfig(), () => new DateTime(2026, 10, 28, 9, 0, 0));
        // Where git can make the link it is caught as one; where it cannot (Windows without the
        // privilege), git writes a file there instead, and the folder is absent. Never a launch.
        Assert.Null(made.Path);
        Assert.Matches("a link out of the isolated worktree|absent from the isolated base", made.Problem);
    }

    [Fact]
    public void A_folder_outside_git_fails_closed()
    {
        var plain = Directory.CreateDirectory(Path.Combine(root, "plain")).FullName;
        var made = Worktrees.Create(plain, WorktreeConfig(), null);
        Assert.Null(made.Path);
        Assert.StartsWith("Cannot establish the repository for isolation", made.Problem);
        Assert.False(Directory.Exists(Path.Combine(root, "wt")));
    }

    [Fact]
    public void A_worktree_is_locked_while_its_session_starts_and_only_this_launchs_lock_is_lifted()
    {
        var work = Repo();
        var made = Worktrees.Create(work, WorktreeConfig(), () => new DateTime(2026, 10, 28, 9, 0, 0));
        var (token, problem) = Worktrees.Reserve(made.Worktree!);
        Assert.Null(problem);
        Assert.StartsWith(Worktrees.ReservationPrefix, token);
        Assert.Equal(token, File.ReadAllText(Path.Combine(Worktrees.AdminFolder(made.Worktree!)!, "locked")).Trim());
        // Already locked, by anyone: left as it is.
        Assert.Equal(((string?)null, (string?)null), Worktrees.Reserve(made.Worktree!));
        Assert.False(Worktrees.Release(made.Worktree!, Worktrees.ReservationPrefix + new string('0', 32)));
        Assert.True(Worktrees.Release(made.Worktree!, token!));
        Assert.False(File.Exists(Path.Combine(Worktrees.AdminFolder(made.Worktree!)!, "locked")));
    }

    // ---- what can be resumed ----

    [Fact]
    public void Resumable_sessions_come_from_each_agents_store_with_a_title_and_the_folder_they_ran_in()
    {
        var copilot = Path.Combine(root, ".copilot");
        var claude = Path.Combine(root, ".claude");
        var codex = Path.Combine(root, ".codex");
        string Id(int n) => $"{n:D8}-0000-4000-8000-000000000000";

        var named = Directory.CreateDirectory(Path.Combine(copilot, "session-state", Id(1))).FullName;
        File.WriteAllText(Path.Combine(named, "workspace.yaml"), "id: x\ncwd: C:\\repos\\app\nsummary: Fix the flaky test\nupdated_at: 2026-10-28T03:30:00.000Z\n");
        var prompted = Directory.CreateDirectory(Path.Combine(copilot, "session-state", Id(2))).FullName;
        File.WriteAllText(Path.Combine(prompted, "workspace.yaml"), "cwd: C:\\repos\\app\n");
        File.WriteAllText(Path.Combine(prompted, "events.jsonl"), """{"type":"user.message","data":{"content":"<context>x</context>"}}""" + "\n" + """{"type":"user.message","data":{"content":"  Write\nthe docs "}}""" + "\n");
        Directory.CreateDirectory(Path.Combine(copilot, "session-state", Id(3)));

        var project = Directory.CreateDirectory(Path.Combine(claude, "projects", "-repos-app")).FullName;
        File.WriteAllText(Path.Combine(project, Id(4) + ".jsonl"),
            """{"type":"user","cwd":"C:\\repos\\app","message":{"content":[{"type":"text","text":"Plan the release"}]}}""" + "\n" + """{"type":"ai-title","aiTitle":"Release plan"}""" + "\n");

        var rollout = Directory.CreateDirectory(Path.Combine(codex, "sessions", "2026", "10", "28")).FullName;
        File.WriteAllText(Path.Combine(rollout, $"rollout-2026-10-28T04-00-00-{Id(5)}.jsonl"),
            $$$"""{"type":"session_meta","payload":{"id":"{{{Id(5)}}}","cwd":"C:\\repos\\api"}}""" + "\n" + """{"type":"event_msg","payload":{"type":"user_message","message":"Add pagination"}}""" + "\n");
        File.WriteAllText(Path.Combine(codex, "session_index.jsonl"), $$"""{"id":"{{Id(5)}}","thread_name":"Pagination"}""" + "\n");
        // Unnamed, from a Codex that records the prompt in the newer item_completed event.
        File.WriteAllText(Path.Combine(rollout, $"rollout-2026-10-28T05-00-00-{Id(6)}.jsonl"),
            $$$"""{"type":"session_meta","payload":{"id":"{{{Id(6)}}}","cwd":"C:\\repos\\api"}}""" + "\n"
            + """{"type":"event_msg","payload":{"type":"item_completed","item":{"type":"UserMessage","id":"u1","content":[{"type":"text","text":"Measure four"}]}}}""" + "\n");

        var entries = new ResumableStore(copilot, claude, codex).Read(["copilot", "claude", "codex"]);
        var byId = entries.ToDictionary(e => e.Id);
        Assert.Equal(("Fix the flaky test", @"C:\repos\app", AgentKind.Copilot), (byId[Id(1)].Summary, byId[Id(1)].Folder, byId[Id(1)].Agent));
        Assert.Equal("Write the docs", byId[Id(2)].Summary);
        // Opened and closed without a prompt: nothing to go back to.
        Assert.False(byId.ContainsKey(Id(3)));
        Assert.Equal(("Release plan", @"C:\repos\app", AgentKind.Claude), (byId[Id(4)].Summary, byId[Id(4)].Folder, byId[Id(4)].Agent));
        Assert.Equal(("Pagination", @"C:\repos\api", AgentKind.Codex), (byId[Id(5)].Summary, byId[Id(5)].Folder, byId[Id(5)].Agent));
        Assert.Equal("Measure four", byId[Id(6)].Summary);
        Assert.DoesNotContain(entries, e => e.Agent == AgentKind.Claude && !byId.ContainsKey(e.Id));
        Assert.DoesNotContain(new ResumableStore(copilot, claude, codex).Read(["codex"]), e => e.Agent != AgentKind.Codex);
    }

    [Fact]
    public void A_copilot_session_used_recently_is_offered_however_old_its_folder_and_however_many_others_there_are()
    {
        var copilot = Path.Combine(root, ".copilot");
        var state = Path.Combine(copilot, "session-state");
        string Id(int n) => $"{n:D8}-0000-4000-8000-000000000000";
        for (var i = 1; i <= ResumableStore.ScanLimit; i++)
        {
            var d = Directory.CreateDirectory(Path.Combine(state, Id(i))).FullName;
            File.WriteAllText(Path.Combine(d, "workspace.yaml"), $"cwd: C:\\repos\\app\nsummary: Session {i}\n");
        }
        // Started long ago and reopened just now: only its transcript says so.
        var old = Directory.CreateDirectory(Path.Combine(state, Id(9999))).FullName;
        File.WriteAllText(Path.Combine(old, "workspace.yaml"), "cwd: C:\\repos\\app\nsummary: The old one\n");
        File.WriteAllText(Path.Combine(old, "events.jsonl"), """{"type":"user.message","data":{"content":"again"}}""" + "\n");
        File.SetLastWriteTimeUtc(Path.Combine(old, "workspace.yaml"), new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Directory.SetLastWriteTimeUtc(old, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Contains(new ResumableStore(copilot, root, root).Read(["copilot"]), e => e.Id == Id(9999));
    }

    [Fact]
    public void Agencys_listings_are_read_as_it_prints_them()
    {
        Assert.Equal(["home", "work"], AgencyCatalog.ParseProfiles("Agency 1.2\nProfiles:\n  home\n    MCPs: none\n    Plugins: none\n  work\n    MCPs: github\nDone.\n"));
        Assert.Empty(AgencyCatalog.ParseProfiles("No profiles configured.\n")!);
        Assert.Null(AgencyCatalog.ParseProfiles("error: unknown command"));

        var sessions = AgencyCatalog.ParseSessions("""
            agency v1.2 (log: C:\x.log)
            {"sessions":[
              {"session_id":"0F3C0000-0000-4000-8000-000000000001","can_resume":true,"summary":"Ship it","folder":"C:\\repos\\app","updated_at":"2026-10-28T03:00:00Z"},
              {"session_id":"0f3c0000-0000-4000-8000-000000000002","can_resume":false,"summary":"VS Code","folder":"C:\\repos\\app"}
            ]}
            """);
        var only = Assert.Single(sessions);
        Assert.Equal(("0f3c0000-0000-4000-8000-000000000001", "agency", "Ship it", AgentKind.Copilot), (only.Id, only.Launcher, only.Summary, only.Agent));
    }

    [Fact]
    public void Questions_an_agent_stops_on_before_it_starts_are_told_apart_by_what_it_draws()
    {
        Assert.Equal(StartupQuestion.Trust, StartupQuestions.Read(["╭ Confirm folder trust ─╮", "│ C:\\repos\\app │", "Do you trust the files in this folder?", "❯ 1. Yes"]));
        Assert.Equal(StartupQuestion.Trust, StartupQuestions.Read([" Do you trust the files in this folder?", " ❯ 1. Yes, I trust this folder", "   2. No, exit"]));
        Assert.Equal(StartupQuestion.Trust, StartupQuestions.Read(["Bypass Permissions mode", "❯ 1. No, exit", "  2. Yes, I accept"]));
        Assert.Equal(StartupQuestion.HooksReview, StartupQuestions.Read(["Hooks need review"]));
        Assert.Equal(StartupQuestion.None, StartupQuestions.Read(["> Welcome to Copilot", "Yes, I accept the risk of reading this"]));
        Assert.Equal(StartupQuestion.None, StartupQuestions.Read(null));
    }

    // ---- config ----

    static (LaunchConfig, IReadOnlyList<string>) Config(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return LaunchConfig.Read(doc.RootElement.Clone());
    }

    [Fact]
    public void Nothing_is_approved_and_permission_prompts_stay_on_until_a_person_says_otherwise()
    {
        var (config, problems) = LaunchConfig.Read(null);
        Assert.Empty(config.Workspaces);
        Assert.Equal(("ask", "auto", "~/repos/wt", 10, 12), (config.Permissions, config.DefaultAgent, config.WorktreeRoot, config.WorktreeLimit, config.ResumeCount));
        Assert.True(config.Enabled);
        Assert.Empty(problems);
    }

    [Fact]
    public void A_workspace_entry_that_is_wrong_is_left_out_and_a_section_that_is_wrong_turns_launching_off()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var (config, problems) = Config("""
            { "workspaces": [ "~/repos/a", { "path": "~/repos/b", "label": "B", "isolate": true }, { "path": "~/repos/c", "isolate": "yes" }, "relative/d", { "path": "~/repos/a2", "label": "a" } ],
              "agents": { "copilot": { "model": "gpt-6.1-sol", "models": ["gpt-6.1-sol", "claude-opus-5.5"] }, "gemini": {} } }
            """);
        Assert.Equal([("a", Path.Combine(home, "repos", "a"), false), ("B", Path.Combine(home, "repos", "b"), true), ($"a ({Path.Combine(home, "repos", "a2")})", Path.Combine(home, "repos", "a2"), false)],
            config.Workspaces.Select(w => (w.Label, w.Path, w.Isolate)));
        Assert.Contains(problems, p => p.Contains("isolate is not true or false"));
        Assert.Contains(problems, p => p.Contains("is not an absolute path"));
        Assert.Contains(problems, p => p.Contains("gemini"));
        Assert.Equal("gpt-6.1-sol", config.Agent("copilot").Model);
        Assert.True(config.Enabled);

        var (off, why) = Config("""{ "permissions": "always" }""");
        Assert.False(off.Enabled);
        Assert.Contains(why, p => p.EndsWith("launching is off", StringComparison.Ordinal));
        Assert.False(Config("""{ "worktrees": { "limit": 2.5 } }""").Item1.Enabled);
    }

    [Fact]
    public void Transfers_default_to_25_MiB_each_way_and_take_at_most_200_MiB()
    {
        Assert.Equal(new TransferConfig { Serve = true, Receive = true, MaxBytes = 25L << 20, MaxSourceBytes = 250L << 20 }, LaunchConfig.Read(null).Config.Transfer);

        var (config, problems) = Config("""{ "transfer": { "serve": false, "maxBytes": 209715200 } }""");
        Assert.Empty(problems);
        Assert.Equal((false, true, 200L << 20, 2000L << 20), (config.Transfer.Serve, config.Transfer.Receive, config.Transfer.MaxBytes, config.Transfer.MaxSourceBytes));

        // Over the ceiling, or a flag that is not a boolean: launching is off rather than guessed.
        Assert.False(Config("""{ "transfer": { "maxBytes": 209715201 } }""").Item1.Enabled);
        Assert.False(Config("""{ "transfer": { "serve": "no" } }""").Item1.Enabled);
    }
}
