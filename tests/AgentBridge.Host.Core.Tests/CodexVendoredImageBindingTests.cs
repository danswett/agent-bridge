using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Discovery;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;
using Xunit;

// #3644, the second cause: a Codex launched from Slipgates on a machine whose Codex came from WinGet never showed,
// though the launch binding was in the host. The executable WinGet runs is named codex-x86_64-pc-windows-msvc.exe
// (its command line still names WinGet\Links\codex.exe), and the host asked the OS for processes named "codex"
// only: the launched process was never listed, so nothing could be bound to it and only the daemon, no session,
// was seen. These tests list processes by image as the host does, and the host says why a launch is left unbound.
public class CodexVendoredImageBindingTests
{
    const string C = "01a21091-5144-73d3-8b6f-38d44c098bf3";
    const int Launched = 108864;
    const string Vendored = "codex-x86_64-pc-windows-msvc";
    const string LinksCommand = @"C:\Users\dswett\AppData\Local\Microsoft\WinGet\Links\codex.exe --no-daemon -c model_reasoning_summary=detailed --ask-for-approval never -- test";
    const string Daemon = @"\\?\C:\Users\dswett\.codex\packages\app-server-daemon\releases\0.172.0-x86_64-pc-windows-msvc\bin\codex.exe app-server --listen unix:// --managed-daemon";
    const string Folder = @"C:\users\dswett\repos";

    // The reported machine: the launch (vendored image, the Links command line) and the two daemons (image codex).
    sealed class Vm : IDisposable
    {
        public Fixture F { get; } = new();
        public DateTimeOffset Started { get; }
        public List<PendingCodexLaunch> Launches { get; } = [];
        public List<string> Log { get; } = [];

        public Vm(string image = Vendored, string command = LinksCommand)
        {
            Started = F.Time.GetUtcNow();
            F.Processes.Run(AgentKind.Codex, Launched, command, Started, image);
            F.Processes.Run(AgentKind.Codex, 35168, Daemon, Started.AddHours(-8), "codex");
            F.Processes.Run(AgentKind.Codex, 50800, Daemon, Started.AddHours(-8), "codex");
            Launches.Add(new(Launched, Started, Folder));
        }

        public SessionSource Source()
        {
            var source = F.Source(log: Log.Add);
            source.PendingCodexLaunches = () => [.. Launches];
            return source;
        }

        // The hook fires 3.2 s into the launch and names no process.
        public void Register(string id = C, string cwd = Folder, double after = 3.2)
        {
            F.Time.Advance(TimeSpan.FromSeconds(after));
            var rollout = F.CodexTranscript(id);
            if (!File.Exists(rollout)) { Fixture.Append(rollout, Codex.Line("turn_context", new { model = "gpt-fixture" }, 1)); }
            F.CodexRegistration(id, 0, rollout, updated: F.Time.GetUtcNow(), cwd: cwd);
        }

        public int Said(string part) => Log.Count(l => l.Contains(part, StringComparison.Ordinal));

        public void Dispose() => F.Dispose();
    }

    [Theory]
    [InlineData("codex", true)]
    [InlineData("Codex", true)]
    [InlineData("codex-x86_64-pc-windows-msvc", true)]
    [InlineData("codex-aarch64-pc-windows-msvc", true)]
    [InlineData("codex-aarch64-apple-darwin", true)]
    [InlineData("codex-x86_64-unknown-linux-musl", true)]
    [InlineData("node", true)]
    [InlineData("codex-code-mode-host", false)]
    [InlineData("codex-windows-sandbox-setup", false)]
    [InlineData("codex-linux-sandbox", false)]
    [InlineData("codex-x86_64", false)]
    [InlineData("claude", false)]
    [InlineData("", false)]
    public void The_host_lists_codex_and_its_vendored_binary_but_not_its_helpers(string image, bool listed) =>
        Assert.Equal(listed, SystemProcessInspector.IsCandidateImage(AgentKind.Codex, "codex", image));

    [Fact]
    public void A_vendored_codex_image_is_listed_for_codex_alone()
    {
        Assert.False(SystemProcessInspector.IsCandidateImage(AgentKind.Claude, "claude", Vendored));
        Assert.False(SystemProcessInspector.IsCandidateImage(AgentKind.Copilot, "copilot", Vendored));
    }

    [Fact]
    public async Task The_reported_machine_binds_the_launched_vendored_codex_to_its_registration_written_seconds_after_the_start()
    {
        using var vm = new Vm();
        vm.Register();
        await using var source = vm.Source();

        await source.RefreshAsync();

        var session = Assert.Single(source.Snapshot());
        Assert.Equal(C, session.Id);
        Assert.Equal(Launched, source.ProcessOf(C)!.Id);
        Assert.Null(source.Held);
        Assert.Equal(1, vm.Said($"codex registration {C} names no process: bound to codex process {Launched}"));
    }

    [Fact]
    public async Task A_launch_whose_image_the_host_does_not_list_is_left_unbound_and_the_host_says_so_once()
    {
        using var vm = new Vm(image: "codex-code-mode-host");
        vm.Register();
        await using var source = vm.Source();

        await source.RefreshAsync();
        vm.F.Time.Advance(TimeSpan.FromSeconds(1));
        await source.RefreshAsync();
        vm.F.Time.Advance(TimeSpan.FromSeconds(1));
        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
        Assert.Equal(1, vm.Said($"codex launch {Launched} in {Folder} is not bound to a registration: it is not among the running codex processes"));
    }

    [Fact]
    public async Task A_launch_started_apart_from_its_process_is_left_unbound_and_the_host_says_the_gap()
    {
        using var vm = new Vm();
        vm.Launches[0] = vm.Launches[0] with { StartedAt = vm.Started.AddSeconds(-10) };
        vm.Register();
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
        Assert.Equal(1, vm.Said("the process started 10 s from the launch, over the 2 s allowed"));
    }

    [Fact]
    public async Task A_launch_another_registration_already_accounts_for_is_left_unbound_and_the_host_says_so()
    {
        using var vm = new Vm();
        vm.F.CodexRegistration("22222222-2222-4222-8222-222222222222", Launched, null, updated: vm.F.Time.GetUtcNow(), cwd: Folder);
        vm.Register();
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Equal(1, vm.Said("is not bound to a registration: another session already accounts for its process"));
    }

    [Fact]
    public async Task A_launch_that_is_no_terminal_is_left_unbound_and_the_host_says_so()
    {
        using var vm = new Vm(command: LinksCommand.Replace("--no-daemon", "exec --no-daemon", StringComparison.Ordinal).Replace("-- test", "test", StringComparison.Ordinal));
        vm.Register();
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
        Assert.Equal(1, vm.Said("is not bound to a registration: its command line is not an interactive terminal's"));
    }

    [Fact]
    public async Task A_registration_for_another_folder_is_left_unbound_and_the_host_names_both_folders()
    {
        using var vm = new Vm();
        vm.Register(cwd: @"c:\users\dswett\other");
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
        Assert.Equal(1, vm.Said($@"registration {C} is for folder 'c:\users\dswett\other', not '{Folder}'"));
    }

    [Fact]
    public async Task Another_codex_that_could_have_written_the_registration_is_named_and_nothing_is_bound()
    {
        using var vm = new Vm();
        vm.F.Processes.Run(AgentKind.Codex, 777, @"C:\tools\codex.exe", vm.Started.AddMinutes(-5), "codex");
        vm.Register();
        await using var source = vm.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
        Assert.Equal(1, vm.Said($"is not bound to a registration: codex process 777 could also have written registration {C}"));
    }

    [Fact]
    public async Task A_registration_no_running_codex_could_have_written_is_carried_on_without_with_each_codexs_reason()
    {
        using var vm = new Vm();
        vm.Launches.Clear();
        vm.F.Processes.Exit(AgentKind.Codex, Launched);
        vm.F.Processes.Exit(AgentKind.Codex, 50800);
        vm.F.Processes.Run(AgentKind.Codex, 40000, @"C:\tools\codex.exe exec fix", vm.Started.AddMinutes(-5), "codex");
        vm.Register();
        vm.F.Processes.Run(AgentKind.Codex, 50000, @"C:\tools\codex.exe", vm.F.Time.GetUtcNow().AddMinutes(1), "codex");
        await using var source = vm.Source();
        vm.F.Time.Advance(Unaccounted.Grace + TimeSpan.FromSeconds(1));

        await source.RefreshAsync();

        Assert.Single(vm.Log, l => l == $"carrying on without codex registration {C}: it names no process, was written 2 min ago, and no running codex could have written it " +
            "(pid 35168: accounted for by a session or as a server; pid 40000: not interactive (a server or a subcommand); pid 50000: started after the write)");
    }
}
