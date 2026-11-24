using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Discovery;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;
using Xunit;

// #3644: a Codex launched from Slipgates never appeared on VM7 (Codex 0.172). Its hook registration
// names no process, and a pidless registration only ever held or was carried on without: nothing
// bound it to a process, so no session was listed. The host knows which Codex it launched (pid,
// start, folder), so a registration that is that launch's, by folder and time, is its session.
public class PendingLaunchBindingTests
{
    const string C = "01a20ede-712d-76a2-9e4b-e86353e2e4ce";
    const string D = "44444444-4444-4444-8444-444444444444";

    // VM7's launch 6d4c38eb, as `codex-diagnose-DSWETT-DEV-VM7-20261124-1048.txt` listed it.
    const int Launched = 105844;
    const string LaunchedCommand = @"C:\Users\dswett\AppData\Local\Microsoft\WinGet\Links\codex.exe --no-daemon -c model_reasoning_summary=detailed --ask-for-approval never -- test";
    const string LaunchFolder = @"c:\users\dswett\repos";
    // What Codex's hook recorded for the same folder: another case, and the extended-length prefix.
    const string RegisteredFolder = @"\\?\C:\Users\dswett\repos";

    sealed class Vm7 : IDisposable
    {
        public Fixture F { get; } = new();
        public DateTimeOffset Started { get; }
        public List<PendingCodexLaunch> Launches { get; } = [];
        public List<string> Log { get; } = [];

        public Vm7()
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
        using var vm = new Vm7();
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
        using var vm = new Vm7();
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
        using var vm = new Vm7();
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
        using var vm = new Vm7();
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
        using var vm = new Vm7();
        vm.Register(C, cwd);
        await using var source = vm.Source();
        await source.RefreshAsync();

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
        using var vm = new Vm7();
        vm.Register(C, at: vm.Started.AddSeconds(-2));
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task No_launch_record_means_no_binding_and_a_process_nothing_accounts_for_holds_as_before()
    {
        using var vm = new Vm7();
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
        using var vm = new Vm7();
        vm.Launches[0] = vm.Launches[0] with { StartedAt = vm.Started.AddMinutes(-3) };
        vm.Register(C);
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task Two_launches_in_one_folder_cannot_tell_which_registration_is_whose_so_neither_binds()
    {
        using var vm = new Vm7();
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
        using var vm = new Vm7();
        vm.Register(C);
        vm.Register(D, after: TimeSpan.FromSeconds(1));
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task A_codex_app_server_is_never_taken_for_a_launchs_session()
    {
        using var vm = new Vm7();
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
        using var vm = new Vm7();
        vm.F.CodexRegistration(D, Launched, null, updated: vm.F.Time.GetUtcNow(), cwd: RegisteredFolder);
        vm.Register(C);
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Equal(D, Assert.Single(source.Snapshot()).Id);
        Assert.Equal(Launched, source.ProcessOf(D)!.Id);
    }
}
