using AgentBridge.Host;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Local;
using Xunit;

// Who a local connection is (§6.3 invariant 13), from synthetic process chains: nothing here
// depends on what really runs the tests.
public sealed class CallerGuardTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 29, 20, 0, 0, TimeSpan.Zero);
    const string App = @"C:\Users\me\AppData\Local\AgentBridge\current\AgentBridge.exe";

    static PeerProcess P(int id, string image, int minutes = 0) => new(id, T0.AddMinutes(minutes), image);

    static CallerGuard Guard(params KnownProcess[] known) => new(() => known, App);

    [Fact]
    public void The_app_beside_the_host_started_from_the_desktop_is_the_app()
    {
        var caller = Guard().Classify([P(10, App, 5), P(4, @"C:\Windows\explorer.exe")]);

        Assert.Equal(new Caller(CallerKind.App, "the Agent Bridge app"), caller);
    }

    [Fact]
    public void The_app_started_from_inside_an_agent_session_is_that_session_not_the_app()
    {
        var caller = Guard(new KnownProcess(20, T0, "the copilot session 1a2b3c4d")).Classify([P(30, App, 9), P(25, @"C:\Program Files\PowerShell\7\pwsh.exe", 5), P(20, @"C:\nodejs\node.exe")]);

        Assert.Equal(new Caller(CallerKind.Agent, "pid 30, inside the copilot session 1a2b3c4d (pid 20)"), caller);
    }

    [Fact]
    public void An_agent_CLI_in_the_ancestry_is_an_agent_by_its_name_alone()
    {
        var caller = Guard().Classify([P(30, @"C:\tools\agent-bridge-host.exe", 9), P(25, @"C:\Windows\System32\cmd.exe", 5), P(20, @"C:\Users\me\.local\bin\Claude.exe")]);

        Assert.Equal(CallerKind.Agent, caller.Kind);
        Assert.Equal("pid 30, inside a claude process (pid 20)", caller.Description);
    }

    [Fact]
    public void A_session_whose_pid_has_been_reused_is_not_the_process_now_holding_it()
    {
        var caller = Guard(new KnownProcess(20, T0.AddHours(-3), "the copilot session 1a2b3c4d")).Classify([P(30, App, 9), P(20, @"C:\Windows\explorer.exe")]);

        Assert.Equal(CallerKind.App, caller.Kind);
    }

    [Fact]
    public void Anything_else_of_the_users_is_neither_app_nor_agent_and_one_that_cannot_be_traced_is_not_trusted()
    {
        Assert.Equal(new Caller(CallerKind.Other, "pwsh.exe (pid 30), which is not the Agent Bridge app beside this host"),
            Guard().Classify([P(30, @"C:\Program Files\PowerShell\7\pwsh.exe"), P(4, @"C:\Windows\explorer.exe")]));
        // The app's name somewhere else is not the app beside this host.
        Assert.Equal(CallerKind.Other, Guard().Classify([P(30, @"C:\Temp\AgentBridge.exe")]).Kind);
        Assert.Equal(new Caller(CallerKind.Other, "a process the host could not trace"), Guard().Classify(Array.Empty<PeerProcess>()));
    }

    [Fact]
    public void An_ancestry_read_only_in_part_is_not_the_app_but_an_agent_in_the_part_read_still_counts()
    {
        var partial = new ProcessAncestry([P(10, App, 5)], Complete: false);

        Assert.Equal(new Caller(CallerKind.Other, "AgentBridge.exe (pid 10), whose ancestry could not be read in full"), Guard().Classify(partial));
        Assert.Equal(CallerKind.Agent, Guard().Classify(new ProcessAncestry([P(30, App, 9), P(20, @"C:\bin\copilot.exe")], Complete: false)).Kind);
    }

    [Fact]
    public void The_hosts_command_line_knows_it_runs_inside_a_session_by_its_ancestry_or_by_the_variable_a_launch_sets()
    {
        var inAgent = new ProcessAncestry([P(30, @"C:\app\agent-bridge-host.exe", 9), P(20, @"C:\bin\copilot.exe")], true);
        var atDesk = new ProcessAncestry([P(30, @"C:\app\agent-bridge-host.exe", 9), P(25, @"C:\Windows\explorer.exe")], true);

        Assert.Equal("inside a copilot process (pid 20)", AgentAncestry.Inside(null, inAgent, () => []));
        Assert.Null(AgentAncestry.Inside(null, atDesk, () => []));
        // Started detached, its parent gone: the variable every launched agent inherits says it.
        Assert.Equal("inside a session the Bridge Host launched (AGENT_BRIDGE_SESSION is set)", AgentAncestry.Inside("5b0e…", atDesk, () => []));
        Assert.Equal(Launchers.SessionVariable, AgentAncestry.SessionVariable);
        // Not knowing is not leave: macOS without ps, or an ancestor /proc will not show.
        Assert.StartsWith("in a process tree that could not be traced in full", AgentAncestry.Inside(null, atDesk with { Complete = false }, () => []));
        Assert.StartsWith("in a process tree that could not be traced in full", AgentAncestry.Inside(null, ProcessAncestry.Unknown, () => []));
    }

    [Fact]
    public void A_connection_is_classified_once_for_its_session_ops_but_afresh_for_managing_the_host()
    {
        var scans = 0;
        var time = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(T0);
        var guard = new CallerGuard(() => { scans++; return []; }, App, time: time,
            ancestryOf: _ => new ProcessAncestry([P(10, App, 5), P(4, @"C:\Windows\explorer.exe")], true));
        var channel = new Channel();

        for (var chunk = 0; chunk < 1000; chunk++) { Assert.Equal(CallerKind.App, guard.Classify(channel).Kind); }
        Assert.Equal(1, scans);

        guard.Classify(channel, fresh: true);
        Assert.Equal(2, scans);
        time.Advance(CallerGuard.MaxAge);
        guard.Classify(channel);
        Assert.Equal(3, scans);
        // Another connection is classified on its own.
        guard.Classify(new Channel());
        Assert.Equal(4, scans);
    }

    sealed class Channel : AgentBridge.Transport.IProtocolChannel
    {
        public string PeerId => "local-10";
        public string PeerName => "app";
        public string PeerKind => "local";
        public bool IsOpen => true;
        public Task Closed => Task.CompletedTask;
        public Task<long> SendEventAsync(string name, object? data = null, CancellationToken ct = default) => Task.FromResult(0L);
    }

    [Fact]
    public void The_app_the_guard_lets_manage_the_host_is_the_executable_the_app_ships_as()
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "AgentBridge.slnx"))) { root = Path.GetDirectoryName(root)!; }
        var project = System.Xml.Linq.XDocument.Load(Path.Combine(root, "src", "AgentBridge.App", "AgentBridge.App.csproj"));
        var assembly = project.Descendants("AssemblyName").Single().Value;
        var pack = File.ReadAllText(Path.Combine(root, "build", "pack-app.ps1"));

        Assert.Equal(CallerGuard.AppExecutable, assembly + ".exe");
        Assert.Contains($"-e {CallerGuard.AppExecutable}", pack, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "workspace", "add")]
    [InlineData(true, "workspace", "remove")]
    [InlineData(true, "config", "set")]
    [InlineData(true, "config", "unset")]
    [InlineData(false, "workspace", "list")]
    [InlineData(false, "config", "show")]
    public void Only_the_config_commands_that_change_the_file_are_refused_inside_a_session(bool writes, string noun, string verb)
    {
        Assert.Equal(writes, ConfigCommands.Writes([noun, verb, "x", "y"]));
    }

    [Theory]
    [InlineData(true, "token", "set")]
    [InlineData(true, "token", "clear")]
    [InlineData(false, "status", null)]
    [InlineData(false, "token", null)]
    public void Only_the_home_assistant_commands_that_change_the_token_are_refused_inside_a_session(bool writes, string noun, string? verb)
    {
        Assert.Equal(writes, HaCommands.Writes(verb is null ? ["ha", noun] : ["ha", noun, verb]));
    }
}
