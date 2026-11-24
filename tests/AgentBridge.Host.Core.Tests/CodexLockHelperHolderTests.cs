using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;
using Xunit;

// #3644: Codex 0.172 runs a `codex-code-mode-host.exe` beside the terminal's `codex.exe`, and that
// helper is what holds the thread lock open (VM7, launch 6d4c38eb). Counting only listed Codex
// processes as holders, the lock named nobody and the session was never found. A helper of a
// terminal holds the lock for that terminal.
public class CodexLockHelperHolderTests
{
    const string A = "01a20ede-712d-76a2-9e4b-e86353e2e4ce";
    const int Terminal = 105844;
    const int Helper = 105992;
    const string TerminalCommand = @"C:\Users\dswett\AppData\Local\Microsoft\WinGet\Links\codex.exe --no-daemon -c model_reasoning_summary=detailed --ask-for-approval never -- test";
    const string DaemonCommand = @"\\?\C:\Users\dswett\.codex\packages\app-server-daemon\releases\0.172.0-x86_64-pc-windows-msvc\bin\codex.exe app-server --listen unix:// --analytics-default-enabled --managed-daemon";

    static (Fixture F, string Lock) Vm7()
    {
        var f = new Fixture();
        f.Processes.Run(AgentKind.Codex, Terminal, TerminalCommand);
        f.Processes.Run(AgentKind.Codex, 35168, DaemonCommand);
        return (f, f.CodexLock(A));
    }

    [Fact]
    public async Task A_thread_lock_held_by_the_helper_of_a_Codex_terminal_is_that_terminals_session()
    {
        var (f, lockFile) = Vm7();
        using var _ = f;
        f.Processes.Holders[lockFile] = [Helper];
        // codex-code-mode-host is not a codex process the host lists; it runs under the terminal.
        f.Processes.Helpers[Helper] = [Terminal, 48160];
        await using var source = f.Source();

        await source.RefreshAsync();

        var s = Assert.Single(source.Snapshot());
        Assert.Equal(A, s.Id);
        Assert.Equal(Terminal, s.ProcessId);
    }

    [Fact]
    public async Task A_terminal_and_its_own_helper_holding_the_lock_together_are_one_holder()
    {
        var (f, lockFile) = Vm7();
        using var _ = f;
        f.Processes.Holders[lockFile] = [Terminal, Helper];
        f.Processes.Helpers[Helper] = [Terminal];
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Equal(Terminal, Assert.Single(source.Snapshot()).ProcessId);
    }

    [Fact]
    public async Task A_helper_of_the_shared_daemon_names_no_terminal_so_no_session()
    {
        var (f, lockFile) = Vm7();
        using var _ = f;
        f.Processes.Holders[lockFile] = [Helper];
        f.Processes.Helpers[Helper] = [35168];
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task Helpers_of_two_terminals_holding_one_lock_name_nobody()
    {
        var (f, lockFile) = Vm7();
        using var _ = f;
        f.Processes.Run(AgentKind.Codex, 105900, TerminalCommand);
        f.Processes.Holders[lockFile] = [Helper, 105999];
        f.Processes.Helpers[Helper] = [Terminal];
        f.Processes.Helpers[105999] = [105900];
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task A_holder_that_is_no_codex_helper_or_whose_parents_run_no_codex_is_still_nobodys_session()
    {
        var (f, lockFile) = Vm7();
        using var _ = f;
        // An indexer (no helper name), and a helper whose parents are gone.
        f.Processes.Holders[lockFile] = [4242, Helper];
        f.Processes.Helpers[Helper] = [777, 48160];
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Theory]
    [InlineData("codex-code-mode-host", true)]
    [InlineData("Codex-Code-Mode-Host", true)]
    [InlineData("codex-windows-sandbox-setup", true)]
    [InlineData("codex", false)]
    [InlineData("codex-code-mode-host-extra", false)]
    [InlineData("node", false)]
    public void Only_Codexs_own_helper_programs_are_taken_for_helpers(string name, bool helper) =>
        Assert.Equal(helper, SystemProcessInspector.IsCodexHelper(name));
}
