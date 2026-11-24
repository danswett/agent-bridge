using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Discovery;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;
using Xunit;

// #3644: a Codex launched from Slipgates never appeared on the reported machine (Codex 0.172). Its hook registration
// names no process, and a pidless registration only ever held or was carried on without: nothing
// bound it to a process, so no session was listed. The host knows which Codex it launched (pid,
// start, folder), so a registration that is that launch's, by folder and time, is its session.
public class PendingLaunchBindingTests
{
    const string C = "01a20ede-712d-76a2-9e4b-e86353e2e4ce";
    const string D = "44444444-4444-4444-8444-444444444444";

    // The reported launch 6d4c38eb, as the Chief's codex-diagnose evidence for #3644 listed it.
    const int Launched = 105844;
    const string LaunchedCommand = @"C:\Users\dswett\AppData\Local\Microsoft\WinGet\Links\codex.exe --no-daemon -c model_reasoning_summary=detailed --ask-for-approval never -- test";
    const string LaunchFolder = @"C:\Users\dswett\repos";
    // What Codex's hook recorded for the same folder: the extended-length prefix and a trailing separator.
    const string RegisteredFolder = @"\\?\C:\Users\dswett\repos\";

    sealed class Machine : IDisposable
    {
        public Fixture F { get; } = new();
        public DateTimeOffset Started { get; }
        public List<PendingCodexLaunch> Launches { get; } = [];
        public List<string> Log { get; } = [];

        public Machine()
        {
            Started = F.Time.GetUtcNow();
            F.Processes.Run(AgentKind.Codex, Launched, LaunchedCommand, Started);
            // The managed daemon the host also runs: nobody's session.
            F.Processes.Run(AgentKind.Codex, 35168, @"\\?\C:\Users\dswett\.codex\packages\app-server-daemon\releases\0.172.0-x86_64-pc-windows-msvc\bin\codex.exe app-server --listen unix:// --analytics-default-enabled --managed-daemon", Started.AddMinutes(-9));
            Launches.Add(new(Launched, Started, LaunchFolder));
        }

        public SessionSource Source()
        {
            var source = F.Source(log: Log.Add);
            source.PendingCodexLaunches = () => [.. Launches];
            return source;
        }

        // The hook fires some seconds into the launch, and names no process.
        public void Register(string id, string cwd = RegisteredFolder, TimeSpan? after = null, DateTimeOffset? at = null)
        {
            if (at is null) { F.Time.Advance(after ?? TimeSpan.FromSeconds(54)); }
            var rollout = F.CodexTranscript(id);
            if (!File.Exists(rollout)) { Fixture.Append(rollout, Codex.Line("turn_context", new { model = "gpt-fixture" }, 1)); }
            F.CodexRegistration(id, 0, rollout, updated: at ?? F.Time.GetUtcNow(), cwd: cwd);
        }

        public void Dispose() => F.Dispose();
    }

    [Fact]
    public async Task A_registration_naming_no_process_is_the_session_of_the_codex_this_host_launched_in_that_folder_and_is_not_held()
    {
        using var vm = new Machine();
        vm.Register(C);
        await using var source = vm.Source();

        await source.RefreshAsync();

        var session = Assert.Single(source.Snapshot());
        Assert.Equal(C, session.Id);
        Assert.Equal(Launched, source.ProcessOf(C)!.Id);
        Assert.Null(source.Held);
    }

    [Fact]
    public async Task The_binding_outlives_the_launch_leaving_the_pending_list_while_the_hook_keeps_rewriting_the_file_with_no_process()
    {
        using var vm = new Machine();
        vm.Register(C);
        await using var source = vm.Source();
        await source.RefreshAsync();

        // The launch confirmed and is no longer followed; the next hook event writes the file again.
        vm.Launches.Clear();
        vm.F.Time.Advance(TimeSpan.FromMinutes(10));
        vm.Register(C, after: TimeSpan.Zero);
        await source.RefreshAsync();
        vm.F.Time.Advance(TimeSpan.FromHours(2));
        await source.RefreshAsync();

        Assert.Equal(Launched, source.ProcessOf(C)!.Id);
        Assert.Equal(C, Assert.Single(source.Snapshot()).Id);
        Assert.Null(source.Held);
        Assert.DoesNotContain(vm.Log, l => l.StartsWith("carrying on without codex registration", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_bound_session_ends_with_the_process_it_was_bound_to()
    {
        using var vm = new Machine();
        vm.Register(C);
        await using var source = vm.Source();
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());

        vm.Launches.Clear();
        vm.F.Processes.Exit(AgentKind.Codex, Launched);
        await source.RefreshAsync();
        // Its registration holds for the grace, as any that names no process does, and is then carried on without.
        vm.F.Time.Advance(Unaccounted.Grace + TimeSpan.FromSeconds(1));
        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task A_pid_another_process_took_after_the_launch_is_not_the_session()
    {
        using var vm = new Machine();
        vm.Register(C);
        await using var source = vm.Source();
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());

        // The same pid, started later: the launch's Codex has gone.
        vm.Launches.Clear();
        vm.F.Processes.Run(AgentKind.Codex, Launched, LaunchedCommand, vm.F.Time.GetUtcNow().AddMinutes(5));
        vm.F.Time.Advance(TimeSpan.FromMinutes(6));
        await source.RefreshAsync();
        vm.F.Time.Advance(Unaccounted.Grace + TimeSpan.FromSeconds(1));
        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Theory]
    [InlineData(@"c:\users\dswett\other")]
    [InlineData("")]
    [InlineData(@"c:\users\dswett\repos\app")]
    public async Task A_registration_from_another_folder_is_not_the_launchs_and_is_carried_on_without_after_the_grace(string cwd)
    {
        using var vm = new Machine();
        vm.Register(C, cwd);
        await using var source = vm.Source();
        await source.RefreshAsync();
        Assert.Empty(source.Snapshot());

        vm.F.Time.Advance(Unaccounted.Grace + TimeSpan.FromSeconds(1));
        vm.F.Processes.Exit(AgentKind.Codex, Launched);
        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
        Assert.Null(source.Held);
        Assert.Contains(vm.Log, l => l.StartsWith($"carrying on without codex registration {C}", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_registration_written_before_the_launch_started_is_an_earlier_sessions_and_is_not_bound()
    {
        using var vm = new Machine();
        vm.Register(C, at: vm.Started.AddSeconds(-2));
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task No_launch_record_means_no_binding_and_a_process_nothing_accounts_for_holds_as_before()
    {
        using var vm = new Machine();
        vm.Launches.Clear();
        vm.Register(C);
        await using var source = vm.Source();

        await source.RefreshAsync();
        vm.F.Time.Advance(TimeSpan.FromMinutes(5));
        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
        Assert.Equal($"a codex registration names no process (codex pid {Launched} could have written it)", source.Held?.Reason);
    }

    [Fact]
    public async Task A_launch_the_host_has_with_another_start_than_the_process_running_under_its_pid_binds_nothing()
    {
        using var vm = new Machine();
        vm.Launches[0] = vm.Launches[0] with { StartedAt = vm.Started.AddMinutes(-3) };
        vm.Register(C);
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task Two_launches_in_one_folder_cannot_tell_which_registration_is_whose_so_neither_binds()
    {
        using var vm = new Machine();
        vm.F.Processes.Run(AgentKind.Codex, 105900, LaunchedCommand, vm.Started.AddSeconds(2));
        vm.Launches.Add(new(105900, vm.Started.AddSeconds(2), LaunchFolder));
        vm.Register(C);
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task Two_registrations_in_the_launchs_folder_cannot_tell_which_is_its_so_neither_binds()
    {
        using var vm = new Machine();
        vm.Register(C);
        vm.Register(D, after: TimeSpan.FromSeconds(1));
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task A_codex_app_server_is_never_taken_for_a_launchs_session()
    {
        using var vm = new Machine();
        // A launch record naming the daemon: its command line is a server's, never a window.
        vm.Launches.Clear();
        vm.Launches.Add(new(35168, vm.Started.AddMinutes(-9), LaunchFolder));
        vm.Register(C);
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task A_launch_does_not_take_a_process_another_registration_already_names()
    {
        using var vm = new Machine();
        vm.F.CodexRegistration(D, Launched, null, updated: vm.F.Time.GetUtcNow(), cwd: RegisteredFolder);
        vm.Register(C);
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Equal(D, Assert.Single(source.Snapshot()).Id);
        Assert.Equal(Launched, source.ProcessOf(D)!.Id);
    }

    // The reader's probe: the person's other Codex (started long before) fires a hook while the launch starts, and
    // nothing authenticates a registration but its folder and write time.
    [Fact]
    public async Task A_second_running_codex_that_could_have_written_the_registration_means_nothing_is_bound()
    {
        using var vm = new Machine();
        vm.F.Processes.Run(AgentKind.Codex, 200, @"C:\tools\codex.exe --no-daemon", vm.Started.AddMinutes(-30));
        // Its hook fires before the launch's own registration is written: the only registration, in the launch's folder.
        vm.Register(D, after: TimeSpan.FromSeconds(20));
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
        Assert.Null(source.ProcessOf(D));
    }

    [Fact]
    public async Task A_session_the_thread_locks_already_list_with_its_true_process_is_never_pointed_at_the_launch()
    {
        using var vm = new Machine();
        vm.F.Processes.Run(AgentKind.Codex, 200, @"C:\tools\codex.exe --no-daemon", vm.Started.AddMinutes(-30));
        vm.F.Processes.Holders[vm.F.CodexLock(D)] = [200];
        vm.Register(D, after: TimeSpan.FromSeconds(20));
        vm.Register(C, after: TimeSpan.FromSeconds(34));
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Equal(200, source.ProcessOf(D)!.Id);
        Assert.Equal(Launched, source.ProcessOf(C)!.Id);
    }

    [Fact]
    public async Task A_binding_is_forgotten_once_its_registration_is_gone()
    {
        using var vm = new Machine();
        vm.Register(C);
        await using var source = vm.Source();
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());
        vm.Launches.Clear();
        File.Delete(Path.Combine(vm.F.CodexRegistrations, C + ".json"));
        await source.RefreshAsync();
        vm.F.Time.Advance(Unaccounted.Grace + TimeSpan.FromSeconds(1));
        await source.RefreshAsync();

        // The same id registers again, naming no process, with no launch to say whose it is.
        vm.Register(C, after: TimeSpan.FromSeconds(1));
        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task A_host_restarted_while_the_bound_process_runs_binds_the_session_again_from_what_it_listed()
    {
        using var vm = new Machine();
        var state = Path.Combine(vm.F.Root, "profile", "sessions-listed.json");
        vm.Register(C);
        await using (var before = vm.F.Source(stateFile: state))
        {
            before.PendingCodexLaunches = () => [.. vm.Launches];
            await before.RefreshAsync();
            Assert.Single(before.Snapshot());
        }
        vm.Launches.Clear();

        await using var after = vm.F.Source(stateFile: state);
        await after.RefreshAsync();

        Assert.Equal(Launched, Assert.Single(after.Snapshot()).ProcessId);
        Assert.Null(after.Held);
    }

    [Fact]
    public async Task On_Windows_a_folder_is_the_same_whatever_its_case()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var vm = new Machine();
        vm.Register(C, @"\\?\c:\USERS\dswett\REPOS\");
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Equal(C, Assert.Single(source.Snapshot()).Id);
    }
}