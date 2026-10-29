using AgentBridge.Host;
using AgentBridge.Local;
using Xunit;

// Who a local connection is (§6.3 invariant 13), from synthetic process chains: nothing here
// depends on what really runs the tests.
public sealed class CallerGuardTests
{
    static readonly DateTimeOffset T0 = new(2026, 10, 29, 20, 0, 0, TimeSpan.Zero);
    const string App = @"C:\Users\me\AppData\Local\AgentBridge\current\AgentBridge.App.exe";

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
        Assert.Equal(CallerKind.Other, Guard().Classify([P(30, @"C:\Temp\AgentBridge.App.exe")]).Kind);
        Assert.Equal(new Caller(CallerKind.Other, "a process the host could not trace"), Guard().Classify(Array.Empty<PeerProcess>()));
    }

    [Fact]
    public void The_hosts_command_line_knows_it_runs_inside_a_session_by_its_ancestry_or_by_the_variable_a_launch_sets()
    {
        IReadOnlyList<PeerProcess> inAgent = [P(30, @"C:\app\agent-bridge-host.exe", 9), P(20, @"C:\bin\copilot.exe")];
        IReadOnlyList<PeerProcess> atDesk = [P(30, @"C:\app\agent-bridge-host.exe", 9), P(25, @"C:\Windows\explorer.exe")];

        Assert.Equal("inside a copilot process (pid 20)", KnownAgents.Inside(null, inAgent, () => []));
        Assert.Null(KnownAgents.Inside(null, atDesk, () => []));
        // Started detached, its parent gone: the variable every launched agent inherits says it.
        Assert.Equal("inside a session the Bridge Host launched (AGENT_BRIDGE_SESSION is set)", KnownAgents.Inside("5b0e…", atDesk, () => []));
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
}
