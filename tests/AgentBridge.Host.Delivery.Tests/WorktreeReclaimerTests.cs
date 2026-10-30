using System.Text.Json.Nodes;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

// The worktree reclaimer, against real git repositories and worktrees in a throwaway folder:
// agent-ha-bridge's Remove-BridgeFinishedWorktree, protection by protection. Liveness comes
// from a fixed snapshot, or from the real usage reader over a synthetic agent home; nothing
// here reads the machine's own processes or sessions.
public sealed class WorktreeReclaimerTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("abreclaim-").FullName;
    readonly List<string> logs = [];
    readonly FixedUsage nobody = new(new WorktreeUsage(true, [], ""));
    string work = "";
    int trees;

    public WorktreeReclaimerTests() => work = Repo("work");

    public void Dispose()
    {
        foreach (var f in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) { File.SetAttributes(f, FileAttributes.Normal); }
        try { Directory.Delete(root, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // A clone of a fresh origin that ignores local data and agent-ha-bridge's built hook.
    string Repo(string name, Action<string>? beforeCommit = null)
    {
        var origin = Path.Combine(root, name + "-origin");
        Git(root, "init", "-q", "-b", "main", origin);
        // Bytes as written, whatever this machine's git is configured to do with line endings.
        Git(origin, "config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(origin, "README.md"), "hello\n");
        File.WriteAllText(Path.Combine(origin, ".gitignore"), "hook/agent-bridge-hook\nhook/agent-bridge-hook.exe\n.env\n");
        Directory.CreateDirectory(Path.Combine(origin, "hook"));
        File.WriteAllText(Path.Combine(origin, "hook", "main.go"), "package main\n");
        Directory.CreateDirectory(Path.Combine(origin, "src", "svc"));
        File.WriteAllText(Path.Combine(origin, "src", "svc", "a.txt"), "a\n");
        Git(origin, "add", ".");
        beforeCommit?.Invoke(origin);
        Commit(origin, "one");
        var clone = Path.Combine(root, name);
        Git(root, "clone", "-q", "-c", "core.autocrlf=false", origin, clone);
        return clone;
    }

    static void Git(string dir, params string[] args)
    {
        var r = AgentBridge.Host.Delivery.Launch.Git.Run(dir, args);
        Assert.True(r.Ok, $"git {string.Join(' ', args)}: {r.Said}");
    }

    static void Commit(string dir, string message) => Git(dir, "-c", "user.name=t", "-c", "user.email=t@example.com", "commit", "-q", "-m", message);

    LaunchConfig Config(int limit = 10, double idleHours = 12) =>
        LaunchConfig.Default with { WorktreeRoot = Path.Combine(root, "wt"), WorktreeLimit = limit, WorktreeIdleHours = idleHours };

    // A worktree this host made for the repository, its marker dated hoursAgo.
    string Tree(double hoursAgo = 13, string? repository = null)
    {
        var result = Worktrees.Create(repository ?? work, Config(limit: 0), () => new DateTime(2026, 10, 28, 9, 0, 0).AddMinutes(trees++));
        Assert.Null(result.Problem);
        Age(result.Worktree!, hoursAgo);
        return result.Worktree!;
    }

    static void Age(string worktree, double hoursAgo) =>
        File.WriteAllText(Path.Combine(Worktrees.AdminFolder(worktree)!, Worktrees.Marker), DateTimeOffset.Now.AddHours(-hoursAgo).ToString("o"));

    int Reclaim(IWorktreeUsage? usage = null, double idleHours = 12) => Worktrees.Reclaim(work, Config(idleHours: idleHours), usage ?? nobody, logs.Add);

    string Retained(string worktree) => Assert.Single(logs, l => l.StartsWith($"retained worktree {Path.GetFileName(worktree)}: ", StringComparison.Ordinal));

    // Every file of a checkout outside its .git, with its content.
    static string Checkout(string dir) => string.Join("\n", Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
        .Where(f => !Path.GetRelativePath(dir, f).StartsWith(".git", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Select(f => f + "=" + File.ReadAllText(f)));

    static string Head(string dir) => AgentBridge.Host.Delivery.Launch.Git.Run(dir, ["rev-parse", "HEAD"]).Output.Trim();

    // ---- the happy path ----

    [Fact]
    public void A_clean_detached_idle_worktree_this_host_made_is_removed_and_nothing_else_is()
    {
        var finished = Tree();
        var admin = Worktrees.AdminFolder(finished)!;
        var before = Checkout(work);

        Assert.Equal(1, Reclaim());

        Assert.False(Directory.Exists(finished));
        Assert.Contains($"removed finished worktree {Path.GetFileName(finished)}", logs);
        // The repository and its checkout are untouched; the administration folder is left to
        // git's own maintenance, without this host's locks or private index in it.
        Assert.Equal(before, Checkout(work));
        Assert.True(Directory.Exists(admin));
        Assert.Empty(Directory.GetFiles(admin, "*.lock"));
        Assert.Empty(Directory.GetFiles(admin, "agent-bridge-host-cleanup-*"));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "wt"), ".agent-bridge-host-cleanup-*"));
        // Nothing logged names a path.
        Assert.DoesNotContain(logs, l => l.Contains(root, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_tree_holding_only_the_built_hook_is_finished_and_the_hook_goes_with_it()
    {
        var built = Tree();
        File.WriteAllText(Path.Combine(built, "hook", "agent-bridge-hook.exe"), "binary");

        Assert.Equal(1, Reclaim());
        Assert.False(Directory.Exists(built));
    }

    // ---- what stays ----

    [Fact]
    public void A_worktree_younger_than_the_idle_hours_is_retained()
    {
        var young = Tree(hoursAgo: 1);
        Assert.Equal(0, Reclaim());
        Assert.True(Directory.Exists(young));
        Assert.EndsWith("it is younger than launch.worktrees.idleHours", Retained(young));
        // And zero idle hours makes it old enough.
        Assert.Equal(1, Reclaim(idleHours: 0));
    }

    [Fact]
    public void An_untracked_file_or_an_uncommitted_edit_is_enough_to_keep_a_worktree()
    {
        var untracked = Tree();
        File.WriteAllText(Path.Combine(untracked, "notes.txt"), "mine");
        var edited = Tree();
        File.AppendAllText(Path.Combine(edited, "README.md"), "more\n");

        Assert.Equal(0, Reclaim());
        Assert.Equal("mine", File.ReadAllText(Path.Combine(untracked, "notes.txt")));
        Assert.Equal("hello\nmore\n", File.ReadAllText(Path.Combine(edited, "README.md")));
        Assert.EndsWith("it has uncommitted or untracked files", Retained(untracked));
        Assert.EndsWith("it has uncommitted or untracked files", Retained(edited));
    }

    [Fact]
    public void Ignored_local_data_is_kept_and_one_build_output_does_not_excuse_data_beside_it()
    {
        var env = Tree();
        File.WriteAllText(Path.Combine(env, ".env"), "SECRET=1");
        var both = Tree();
        File.WriteAllText(Path.Combine(both, ".env"), "SECRET=2");
        File.WriteAllText(Path.Combine(both, "hook", "agent-bridge-hook.exe"), "binary");

        Assert.Equal(0, Reclaim());
        Assert.Equal("SECRET=1", File.ReadAllText(Path.Combine(env, ".env")));
        Assert.Equal("SECRET=2", File.ReadAllText(Path.Combine(both, ".env")));
        Assert.True(File.Exists(Path.Combine(both, "hook", "agent-bridge-hook.exe")));
        Assert.EndsWith("it has ignored files, which may be someone's local data", Retained(env));
        Assert.EndsWith("it has ignored files, which may be someone's local data", Retained(both));
    }

    [Fact]
    public void A_checked_out_branch_is_never_finished_however_old()
    {
        var branched = Tree();
        Git(branched, "switch", "-q", "-c", "feat/x");
        Assert.Equal(0, Reclaim());
        Assert.True(Directory.Exists(branched));
        Assert.EndsWith("a branch is checked out in it", Retained(branched));
    }

    [Fact]
    public void A_commit_made_on_a_detached_head_is_never_finished()
    {
        var committed = Tree();
        File.WriteAllText(Path.Combine(committed, "new.txt"), "work");
        Git(committed, "add", "new.txt");
        Commit(committed, "work");
        var head = Head(committed);

        Assert.Equal(0, Reclaim());
        Assert.Equal(head, Head(committed));
        Assert.EndsWith("it has commits that are not in the base", Retained(committed));
    }

    [Fact]
    public void Without_an_origin_the_base_is_the_checkouts_own_commit_so_a_commit_in_the_worktree_still_counts()
    {
        // The base falls back to HEAD; read in the worktree, that would be the worktree's own.
        var local = Path.Combine(root, "local");
        Git(root, "init", "-q", "-b", "main", local);
        File.WriteAllText(Path.Combine(local, "README.md"), "hello\n");
        Git(local, "add", ".");
        Commit(local, "one");
        work = local;
        var committed = Tree();
        File.WriteAllText(Path.Combine(committed, "new.txt"), "work");
        Git(committed, "add", "new.txt");
        Commit(committed, "work");

        Assert.Equal(0, Reclaim());
        Assert.True(File.Exists(Path.Combine(committed, "new.txt")));
        Assert.EndsWith("it has commits that are not in the base", Retained(committed));
    }

    [Fact]
    public void A_locked_worktree_is_retained_and_its_lock_is_left_alone()
    {
        var locked = Tree();
        var (token, _) = Worktrees.Reserve(locked);
        Assert.NotNull(token);

        Assert.Equal(0, Reclaim());
        Assert.True(Directory.Exists(locked));
        Assert.Equal(token, File.ReadAllText(Path.Combine(Worktrees.AdminFolder(locked)!, "locked")).Trim());
        Assert.EndsWith("it is locked (a launch still starting, or someone's lock)", Retained(locked));
    }

    [Fact]
    public void A_live_session_at_or_below_a_worktree_keeps_it_but_one_in_a_sibling_with_a_longer_name_does_not()
    {
        var below = Tree();
        var at = Tree();
        var sibling = Tree();
        var lookalike = Directory.CreateDirectory(sibling + "0").FullName;
        var usage = new FixedUsage(new WorktreeUsage(true, [Path.Combine(below, "src", "svc"), at, lookalike], ""));

        Assert.Equal(1, Reclaim(usage));
        Assert.True(Directory.Exists(below));
        Assert.True(Directory.Exists(at));
        Assert.False(Directory.Exists(sibling));
        Assert.EndsWith("a live session is working in it", Retained(below));
        Assert.EndsWith("a live session is working in it", Retained(at));
    }

    [Fact]
    public void Unknown_liveness_retains_every_worktree()
    {
        var a = Tree();
        var b = Tree();

        Assert.Equal(0, Reclaim(new FixedUsage(WorktreeUsage.Unknown("an Agency launcher is active"))));
        // No reader at all is unknown too.
        Assert.Equal(0, Worktrees.Reclaim(work, Config(), null, logs.Add));

        Assert.True(Directory.Exists(a) && Directory.Exists(b));
        Assert.Contains(logs, l => l.EndsWith("whether a session is using them cannot be established (an Agency launcher is active)", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Nothing_is_reclaimed_while_session_discovery_is_held_and_finished_trees_go_once_it_is_complete()
    {
        var finished = Tree();
        using var f = new Fixture();
        f.Processes.Unreadable(AgentKind.Copilot);
        await using var sessions = f.Source();
        await sessions.RefreshAsync();

        Assert.Equal(0, Reclaim(new HeldWorktreeUsage(sessions, nobody)));
        Assert.True(Directory.Exists(finished));
        Assert.Contains(logs, l => l.EndsWith("cannot be established (session discovery is held, waiting on the copilot processes could not be listed)", StringComparison.Ordinal));

        f.Processes.Unreadable(AgentKind.Copilot, false);
        await sessions.RefreshAsync();
        Assert.Equal(1, Reclaim(new HeldWorktreeUsage(sessions, nobody)));
        Assert.False(Directory.Exists(finished));
    }

    [Fact]
    public void A_hold_that_begins_while_a_worktree_is_being_checked_keeps_it()
    {
        var finished = Tree();
        var sessions = new Holding();
        // Clear at the first look; held by the second, taken with git's locks held.
        var usage = new HeldWorktreeUsage(sessions, new FixedUsage(new WorktreeUsage(true, [], ""), new WorktreeUsage(true, [], "")));
        sessions.HoldAfter = 1;

        Assert.Equal(0, Reclaim(usage));
        Assert.True(File.Exists(Path.Combine(finished, "README.md")));
    }

    [Fact]
    public void A_hold_that_begins_during_the_last_read_under_gits_locks_keeps_the_tree()
    {
        var finished = Tree();
        var sessions = new Holding { HoldAfter = int.MaxValue };
        // Clear before and through the first look; held from inside the second read on.
        var usage = new HeldWorktreeUsage(sessions, new HoldsOnRead(sessions, 2));

        Assert.Equal(0, Reclaim(usage));
        Assert.True(File.Exists(Path.Combine(finished, "README.md")));
    }

    sealed class HoldsOnRead(Holding sessions, int on) : IWorktreeUsage
    {
        int reads;
        public WorktreeUsage Read()
        {
            if (++reads == on) { sessions.HoldAfter = -1; }
            return new WorktreeUsage(true, [], "");
        }
    }

    // Held from the HoldAfter+1th question on.
    sealed class Holding : AgentBridge.Host.Core.ISessionSource
    {
        int asked;
        public int HoldAfter { get; set; }
        public DiscoveryHeld? Held => ++asked > HoldAfter ? new DiscoveryHeld("an unrecognised copilot process (pid 1)", [AgentKind.Copilot]) : null;
        public long Revision => 0;
        public event EventHandler<AgentBridge.Host.Core.SessionChange>? Changed { add { } remove { } }
        public IReadOnlyList<Session> Snapshot() => [];
        public SessionList List(string? cursor = null) => new([], 0);
        public Session? Get(string id) => null;
        public AgentBridge.Host.Core.SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void A_session_that_starts_while_a_worktree_is_being_checked_keeps_it()
    {
        var finished = Tree();
        // Clear at the first look, live by the second, taken with git's locks held.
        var usage = new FixedUsage(new WorktreeUsage(true, [], ""), new WorktreeUsage(true, [finished], ""));

        Assert.Equal(0, Reclaim(usage));
        Assert.True(File.Exists(Path.Combine(finished, "README.md")));
        Assert.EndsWith("a live session is working in it", Retained(finished));
        Assert.Empty(Directory.GetFiles(Worktrees.AdminFolder(finished)!, "*.lock"));
    }

    [Fact]
    public void An_unreadable_ownership_time_cannot_prove_a_worktree_old()
    {
        var unreadable = Tree();
        File.WriteAllText(Path.Combine(Worktrees.AdminFolder(unreadable)!, Worktrees.Marker), "yesterday");

        Assert.Equal(0, Reclaim());
        Assert.True(Directory.Exists(unreadable));
        Assert.EndsWith("its ownership marker has no readable time", Retained(unreadable));
    }

    [Theory]
    [InlineData("120000")]
    [InlineData("160000")]
    public void A_worktree_whose_base_tracks_a_link_or_a_submodule_is_retained(string mode)
    {
        var linked = Repo("linked", origin =>
        {
            File.WriteAllText(Path.Combine(origin, "target"), "README.md");
            var blob = AgentBridge.Host.Delivery.Launch.Git.Run(origin, ["hash-object", "-w", "target"]).Output.Trim();
            // A link's entry names a blob; a submodule's names a commit in a repository that
            // is never fetched, so any id will do.
            Git(origin, "update-index", "--add", "--cacheinfo", $"{mode},{(mode == "120000" ? blob : new string('1', blob.Length))},alias");
            File.Delete(Path.Combine(origin, "target"));
        });
        work = linked;
        var tree = Tree();

        Assert.Equal(0, Reclaim());
        Assert.True(Directory.Exists(tree));
        Assert.True(File.Exists(Path.Combine(tree, "README.md")));
        Assert.EndsWith("it holds a link, a submodule, a conflict, or a tracked file that is missing or unreadable", Retained(tree));
    }

    [Fact]
    public void Git_already_working_in_a_tree_keeps_it_and_its_lock_is_not_this_hosts_to_remove()
    {
        var busy = Tree();
        var indexLock = Path.Combine(Worktrees.AdminFolder(busy)!, "index.lock");
        File.WriteAllText(indexLock, "");

        Assert.Equal(0, Reclaim());
        Assert.True(File.Exists(Path.Combine(busy, "README.md")));
        Assert.True(File.Exists(indexLock));
        Assert.False(File.Exists(Path.Combine(Worktrees.AdminFolder(busy)!, "HEAD.lock")));
        Assert.EndsWith("git is working in it", Retained(busy));
    }

    [Fact]
    public void Data_arriving_after_the_checks_stops_removal_without_a_recursive_delete_and_the_tracked_files_come_back()
    {
        var tree = Tree();
        var late = Path.Combine(tree, "src", "svc", ".env");
        var index = File.ReadAllBytes(Path.Combine(Worktrees.AdminFolder(tree)!, "index"));
        // The second look, taken with git's locks held, is where something writes into it.
        var usage = new FixedUsage(new WorktreeUsage(true, [], ""), new WorktreeUsage(true, [], "")) { OnRead = n => { if (n == 2) { File.WriteAllText(late, "late ignored user data"); } } };

        Assert.Equal(0, Reclaim(usage));
        Assert.Equal("late ignored user data", File.ReadAllText(late));
        Assert.True(File.Exists(Path.Combine(tree, ".git")));
        Assert.Equal("hello\n", File.ReadAllText(Path.Combine(tree, "README.md")));
        Assert.Equal("a\n", File.ReadAllText(Path.Combine(tree, "src", "svc", "a.txt")));
        Assert.Equal(index, File.ReadAllBytes(Path.Combine(Worktrees.AdminFolder(tree)!, "index")));
        Assert.Empty(Directory.GetFiles(Worktrees.AdminFolder(tree)!, "*.lock"));
        Assert.Contains("its removal stopped without deleting anything recursively", Retained(tree));
        Assert.Contains("the tracked files it had removed were restored", Retained(tree));
    }

    // ---- what is never this host's ----

    [Fact]
    public void A_worktree_without_this_hosts_marker_is_never_touched()
    {
        var byHand = Path.Combine(root, "wt", "by-hand");
        Git(work, "worktree", "add", "-q", "--detach", byHand, "HEAD");
        var theirs = Path.Combine(root, "wt", "theirs");
        Git(work, "worktree", "add", "-q", "--detach", theirs, "HEAD");
        File.WriteAllText(Path.Combine(Worktrees.AdminFolder(theirs)!, "agent-bridge-created"), DateTime.Now.AddDays(-30).ToString("o"));

        Assert.Equal(0, Reclaim(idleHours: 0));
        Assert.True(File.Exists(Path.Combine(byHand, "README.md")));
        Assert.True(File.Exists(Path.Combine(theirs, "README.md")));
        Assert.Empty(logs);
    }

    [Fact]
    public void A_worktree_of_another_repository_in_the_same_root_is_never_touched()
    {
        var other = Repo("other");
        var theirs = Tree(repository: other);
        var plain = Directory.CreateDirectory(Path.Combine(root, "wt", "not-a-worktree")).FullName;
        File.WriteAllText(Path.Combine(plain, "data.txt"), "keep");

        Assert.Equal(0, Reclaim(idleHours: 0));
        Assert.True(File.Exists(Path.Combine(theirs, "README.md")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(plain, "data.txt")));
        // It is that repository's own reclaim that may remove it.
        Assert.Equal(1, Worktrees.Reclaim(other, Config(), nobody, logs.Add));
        Assert.False(Directory.Exists(theirs));
    }

    // ---- before each isolated launch ----

    [Fact]
    public void The_cap_is_counted_after_finished_worktrees_are_removed()
    {
        var finished = Tree();
        var config = Config(limit: 1);

        var made = Worktrees.Create(work, config, () => new DateTime(2026, 10, 29, 9, 0, 0), nobody, logs.Add);
        Assert.Null(made.Problem);
        Assert.False(Directory.Exists(finished));
        Assert.Contains("removed 1 finished", made.Detail);

        // The new one is young, so it stays, and the cap holds.
        var refused = Worktrees.Create(work, config, () => new DateTime(2026, 10, 29, 9, 0, 1), nobody, logs.Add);
        Assert.StartsWith("1 worktrees this host created already exist for this repository (limit 1)", refused.Problem);
        Assert.True(Directory.Exists(made.Worktree));
    }

    // ---- the usage snapshot ----

    [Fact]
    public void The_usage_snapshot_finds_live_copilot_claude_and_codex_sessions_by_their_working_directories()
    {
        using var f = new Fixture();
        var copilotCwd = Directory.CreateDirectory(Path.Combine(root, "copilot-cwd")).FullName;
        var claudeCwd = Directory.CreateDirectory(Path.Combine(root, "claude-cwd")).FullName;
        var codexCwd = Directory.CreateDirectory(Path.Combine(root, "codex-cwd")).FullName;
        var id = "0f3c1a2b-0000-4000-8000-000000000001";
        f.CopilotSession(id, lockPid: null);
        SetCwd(f, id, copilotCwd);
        f.Processes.Run(AgentKind.Copilot, 101, $"copilot --session-id {id}");
        Registration(f.ClaudeRegistrations, "c1", 202, claudeCwd);
        f.Processes.Run(AgentKind.Claude, 202);
        Registration(f.CodexRegistrations, "x1", 303, codexCwd);
        f.Processes.Run(AgentKind.Codex, 303);
        // An ended Codex session and one whose process has gone are not live.
        Registration(f.CodexRegistrations, "x2", 304, "/gone", ended: true);
        Registration(f.ClaudeRegistrations, "c2", 205, "/gone");

        var usage = Reader(f).Read();
        Assert.True(usage.Known, usage.Detail);
        Assert.Equal(new[] { copilotCwd, claudeCwd, codexCwd }.Select(d => Folders.Physical(d)!).ToArray(), usage.Directories.ToArray());
        Assert.True(usage.InUse(copilotCwd));
        Assert.False(usage.InUse(Path.Combine(root, "elsewhere")));
    }

    [Fact]
    public void Anything_the_usage_snapshot_cannot_account_for_makes_it_unknown()
    {
        using (var f = new Fixture())
        {
            Assert.Equal("an Agency launcher is active, so a session still starting cannot be ruled out", Reader(f, agency: true).Read().Detail);
        }
        using (var f = new Fixture())
        {
            f.Processes.Run(AgentKind.Copilot, 101, "copilot");
            Assert.Equal("a live Copilot or Agency process has no session with a working directory", Reader(f).Read().Detail);
        }
        using (var f = new Fixture())
        {
            Registration(f.ClaudeRegistrations, "c1", 202, root);
            Assert.Equal("a Claude registration names a different live process, so ownership is uncertain", Reader(f, exists: true).Read().Detail);
        }
        using (var f = new Fixture())
        {
            Registration(f.CodexRegistrations, "x1", 303, Path.Combine(root, "deleted"));
            f.Processes.Run(AgentKind.Codex, 303);
            Assert.Equal("a live Codex session has a working directory that cannot be resolved", Reader(f).Read().Detail);
        }
        using (var f = new Fixture())
        {
            Directory.CreateDirectory(f.ClaudeRegistrations);
            File.WriteAllText(Path.Combine(f.ClaudeRegistrations, "c1.json"), "{ not json");
            Assert.False(Reader(f).Read().Known);
        }
        var blind = new BlindProcesses();
        Assert.Equal("a possible Copilot process could not be identified",
            new AgentWorktreeUsage(blind, Path.Combine(root, "none"), [], [], () => false, _ => false).Read().Detail);
    }

    static AgentWorktreeUsage Reader(Fixture f, bool agency = false, bool exists = false) =>
        new(f.Processes, f.CopilotHome, [f.ClaudeRegistrations], [f.CodexRegistrations], () => agency, _ => exists, f.Time);

    static void SetCwd(Fixture f, string id, string cwd)
    {
        var yaml = Path.Combine(f.CopilotHome, "session-state", id, "workspace.yaml");
        File.WriteAllText(yaml, File.ReadAllText(yaml).Replace("cwd: /work/repo", $"cwd: {cwd}", StringComparison.Ordinal));
    }

    static void Registration(string folder, string id, int pid, string cwd, bool ended = false)
    {
        Directory.CreateDirectory(folder);
        var json = new JsonObject { ["SessionId"] = id, ["ProcessId"] = pid, ["WorkingDirectory"] = cwd, ["Updated"] = Fixture.T0.ToString("o"), ["Ended"] = ended };
        File.WriteAllText(Path.Combine(folder, id + ".json"), json.ToJsonString());
    }

    // Answers with each snapshot in turn, the last one for ever after.
    sealed class FixedUsage(params WorktreeUsage[] answers) : IWorktreeUsage
    {
        int reads;
        public Action<int>? OnRead { get; init; }

        public WorktreeUsage Read()
        {
            reads++;
            OnRead?.Invoke(reads);
            return answers[Math.Min(reads, answers.Length) - 1];
        }
    }

    sealed class BlindProcesses : IProcessInspector
    {
        public IReadOnlyList<AgentProcess> GetAgentProcesses(AgentKind agent) => [];
        public AgentProcessObservation Observe(AgentKind agent) => new([], false);
    }
}
