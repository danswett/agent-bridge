using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Discovery;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;
using Xunit;

public class DiscoveryTests
{
    const string A = "11111111-1111-4111-8111-111111111111";
    const string B = "22222222-2222-4222-8222-222222222222";

    [Fact]
    public async Task A_Copilot_session_whose_lock_is_held_by_a_live_process_is_listed_with_its_workspace()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(A, lockPid: 100, name: "Plan the fixture");
        Fixture.Append(events, Copilot.User("hello", 1));
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source();

        await source.RefreshAsync();

        var s = Assert.Single(source.Snapshot());
        Assert.Equal(A, s.Id);
        Assert.Equal(AgentKind.Copilot, s.Agent);
        Assert.Equal("Plan the fixture", s.Title);
        Assert.Equal("/work/repo", s.Cwd);
        Assert.Equal("octo/repo", s.Repository);
        Assert.Equal("main", s.Branch);
        Assert.Equal(100, s.ProcessId);
        Assert.Equal(DateTimeOffset.Parse("2026-10-28T03:00:00Z"), s.StartedAt);
    }

    [Fact]
    public async Task A_Copilot_lock_left_by_a_process_that_has_exited_is_not_a_session()
    {
        using var f = new Fixture();
        f.CopilotSession(A, lockPid: 100);
        f.Processes.Run(AgentKind.Copilot, 200);
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task A_resumed_Copilot_session_is_found_from_its_command_line_without_any_lock()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(A, lockPid: null);
        Fixture.Append(events, Copilot.User("resumed", 1));
        f.Processes.Run(AgentKind.Copilot, 300, $"copilot --resume --session-id {A}");
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Equal(A, Assert.Single(source.Snapshot()).Id);
    }

    [Fact]
    public async Task A_process_that_resumed_another_session_from_inside_the_CLI_is_shown_in_the_session_it_moved_to()
    {
        using var f = new Fixture();
        var started = Fixture.T0.AddMinutes(-10);
        // The command line still names A, where the CLI started; it then resumed B, whose
        // transcript it has been writing since under a lock of its own.
        var a = f.CopilotSession(A, lockPid: 400, lockWrite: started.AddSeconds(1));
        var b = f.CopilotSession(B, lockPid: 400, lockWrite: started.AddMinutes(1));
        Fixture.Append(a, Copilot.User("first", 1));
        File.SetLastWriteTimeUtc(a, started.AddSeconds(2).UtcDateTime);
        Fixture.Append(b, Copilot.User("second", 2));
        File.SetLastWriteTimeUtc(b, started.AddMinutes(5).UtcDateTime);
        f.Processes.Run(AgentKind.Copilot, 400, $"copilot --session-id={A}", started);
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Equal(B, Assert.Single(source.Snapshot()).Id);
    }

    [Fact]
    public async Task A_pid_holding_locks_in_two_folders_shows_only_the_one_written_most_recently()
    {
        using var f = new Fixture();
        var a = f.CopilotSession(A, lockPid: 500);
        var b = f.CopilotSession(B, lockPid: 500);
        Fixture.Append(a, Copilot.User("old", 1));
        Fixture.Append(b, Copilot.User("new", 2));
        File.SetLastWriteTimeUtc(a, Fixture.T0.UtcDateTime);
        File.SetLastWriteTimeUtc(b, Fixture.T0.AddMinutes(1).UtcDateTime);
        f.Processes.Run(AgentKind.Copilot, 500);
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Equal(B, Assert.Single(source.Snapshot()).Id);
    }

    [Fact]
    public async Task A_Copilot_session_that_has_not_written_a_transcript_yet_is_still_listed_as_idle()
    {
        using var f = new Fixture();
        f.CopilotSession(A, lockPid: 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source();

        await source.RefreshAsync();

        var s = Assert.Single(source.Snapshot());
        Assert.Equal(SessionStatus.Idle, s.Status);
        Assert.Null(s.Activity.TurnCount);
    }

    [Fact]
    public async Task A_Claude_registration_is_a_session_while_its_process_runs_and_is_removed_when_it_exits()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(A);
        Fixture.Append(transcript, Claude.User("hi claude", 1));
        f.ClaudeRegistration(A, 700, transcript);
        f.Processes.Run(AgentKind.Claude, 700);
        await using var source = f.Source();
        await source.RefreshAsync();
        var subscription = source.Subscribe();

        var s = Assert.Single(source.Snapshot());
        Assert.Equal(AgentKind.Claude, s.Agent);
        Assert.Equal(700, s.ProcessId);

        f.Processes.Exit(AgentKind.Claude, 700);
        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
        Assert.Null(source.Get(A));
        var events = Drain(subscription);
        Assert.Equal(Events.SessionRemove, events[^1].Name);
        Assert.Equal(A, ((SessionRemoved)events[^1].Data).Id);
    }

    [Fact]
    public async Task A_Claude_session_with_no_hook_installed_is_found_from_Claude_Code_s_own_session_file_while_its_process_runs()
    {
        using var f = new Fixture();
        Fixture.Append(f.ClaudeTranscript(A), Claude.User("no hooks here", 1));
        f.ClaudeSessionFile(700, A);
        f.Processes.Run(AgentKind.Claude, 700);
        await using var source = f.Source();

        await source.RefreshAsync();

        var s = Assert.Single(source.Snapshot());
        Assert.Equal(AgentKind.Claude, s.Agent);
        Assert.Equal(700, s.ProcessId);
        Assert.Equal("no hooks here", s.Title);

        f.Processes.Exit(AgentKind.Claude, 700);
        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task A_Claude_session_file_older_than_the_process_now_holding_its_pid_or_not_interactive_or_misnamed_is_not_a_session()
    {
        using var f = new Fixture();
        // Left by a CLI that crashed at T0; pid 700 now belongs to one started an hour later.
        f.ClaudeSessionFile(700, A, startedAt: Fixture.T0);
        f.Processes.Run(AgentKind.Claude, 700, startedAt: Fixture.T0.AddHours(1));
        f.ClaudeSessionFile(701, B, kind: "sdk");
        f.Processes.Run(AgentKind.Claude, 701);
        f.ClaudeSessionFile(702, "33333333-3333-4333-8333-333333333333", fileName: "703.json");
        f.Processes.Run(AgentKind.Claude, 702);
        f.Processes.Run(AgentKind.Claude, 703);
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task An_agent_ha_bridge_registration_wins_over_Claude_Code_s_own_file_for_the_same_session()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(A);
        Fixture.Append(transcript, Claude.User("both", 1));
        f.ClaudeRegistration(A, 700, transcript, hookStatus: "working", hookAt: Fixture.T0.AddMinutes(5));
        f.ClaudeSessionFile(700, A);
        f.Processes.Run(AgentKind.Claude, 700);
        await using var source = f.Source();

        await source.RefreshAsync();

        var s = Assert.Single(source.Snapshot());
        Assert.Equal(SessionStatus.Working, s.Status);
        Assert.NotNull(source.Files(A)?.Registration);
    }

    [Fact]
    public async Task A_Codex_session_run_without_the_daemon_is_found_from_the_terminal_holding_its_thread_lock()
    {
        using var f = new Fixture();
        Fixture.Append(f.CodexTranscript(A), Codex.Event(new { type = "user_message", message = "no hooks here" }, 1));
        var lockFile = f.CodexLock(A);
        f.Processes.Run(AgentKind.Codex, 800, "codex.exe --no-daemon -c model_reasoning_summary=detailed");
        f.Processes.Holders[lockFile] = [800];
        await using var source = f.Source();

        await source.RefreshAsync();
        await source.RefreshAsync();

        var s = Assert.Single(source.Snapshot());
        Assert.Equal(AgentKind.Codex, s.Agent);
        Assert.Equal(800, s.ProcessId);
        // Asked once for the lock, not on every pass, while the process it named runs.
        Assert.Equal(1, f.Processes.HolderQueries);

        f.Processes.Exit(AgentKind.Codex, 800);
        File.Delete(lockFile);
        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task A_later_process_given_the_pid_of_a_Codex_lock_holder_is_asked_about_and_not_handed_the_session()
    {
        using var f = new Fixture();
        var lockFile = f.CodexLock(A);
        f.Processes.Run(AgentKind.Codex, 800, "codex.exe --no-daemon", Fixture.T0.AddHours(-2));
        f.Processes.Holders[lockFile] = [800];
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());

        // The holder exits leaving its lock behind, and a new Codex takes its pid.
        f.Processes.Run(AgentKind.Codex, 800, "codex.exe --no-daemon", Fixture.T0.AddMinutes(-1));
        f.Processes.Holders.Remove(lockFile);
        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
        Assert.Equal(2, f.Processes.HolderQueries);
    }

    [Fact]
    public async Task A_Codex_lock_holder_whose_command_line_cannot_be_read_names_no_session_until_it_can()
    {
        using var f = new Fixture();
        var lockFile = f.CodexLock(A);
        // Possibly the shared daemon: ending it would end every session it serves.
        f.Processes.Run(AgentKind.Codex, 900, commandLine: null);
        f.Processes.Holders[lockFile] = [900];
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Empty(source.Snapshot());

        f.Processes.Run(AgentKind.Codex, 900, "codex.exe --no-daemon");
        await source.RefreshAsync();

        Assert.Equal(900, Assert.Single(source.Snapshot()).ProcessId);
    }

    [Fact]
    public async Task A_Codex_thread_lock_held_by_the_shared_daemon_or_by_two_terminals_names_no_session()
    {
        using var f = new Fixture();
        var daemonHeld = f.CodexLock(A);
        f.Processes.Run(AgentKind.Codex, 900, "\"C:\\Users\\me\\.codex\\packages\\app-server-daemon\\bin\\codex.exe\" app-server --listen unix:// --managed-daemon");
        f.Processes.Run(AgentKind.Codex, 901, "codex.exe");
        f.Processes.Holders[daemonHeld] = [900];
        var shared = f.CodexLock(B);
        f.Processes.Run(AgentKind.Codex, 902, "codex.exe --no-daemon");
        f.Processes.Holders[shared] = [901, 902];
        // Global options ahead of the subcommand still make it a server.
        var optioned = f.CodexLock("33333333-3333-4333-8333-333333333333");
        f.Processes.Run(AgentKind.Codex, 903, "codex.exe -c model=example app-server --listen stdio://");
        f.Processes.Holders[optioned] = [903];
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public void Windows_names_the_process_that_has_a_file_open_and_nobody_once_it_is_closed()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        using var f = new Fixture();
        var path = f.CodexLock(A);
        var inspector = new SystemProcessInspector();

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.Contains(Environment.ProcessId, inspector.FileHolders(path));
        }

        Assert.DoesNotContain(Environment.ProcessId, inspector.FileHolders(path));
    }

    [Fact]
    public async Task An_ended_Codex_registration_is_not_a_session_even_while_its_process_still_runs()
    {
        using var f = new Fixture();
        f.CodexRegistration(A, 800, f.CodexTranscript(A), status: "ended", ended: true);
        f.Processes.Run(AgentKind.Codex, 800, "codex.exe --no-daemon");
        // Its terminal still holding the thread's lock does not bring it back.
        f.Processes.Holders[f.CodexLock(A)] = [800];
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task A_Codex_lock_a_terminal_shares_with_the_daemon_or_an_unreadable_Codex_names_nobody_until_the_terminal_holds_it_alone()
    {
        using var f = new Fixture();
        var withDaemon = f.CodexLock(A);
        var withUnknown = f.CodexLock(B);
        f.Processes.Run(AgentKind.Codex, 800, "codex.exe --no-daemon");
        f.Processes.Run(AgentKind.Codex, 801, "codex.exe --no-daemon");
        f.Processes.Run(AgentKind.Codex, 900, "codex.exe app-server --listen unix:// --managed-daemon");
        f.Processes.Run(AgentKind.Codex, 901, commandLine: null);
        f.Processes.Holders[withDaemon] = [900, 800];
        // A process that is not Codex (an indexer, a backup) holding it too is not a rival.
        f.Processes.Holders[withUnknown] = [801, 901, 4242];
        await using var source = f.Source();

        await source.RefreshAsync();
        Assert.Empty(source.Snapshot());

        // Nothing was bound, so once each terminal holds its lock alone it is found, on the
        // next look after Recheck.
        f.Processes.Holders[withDaemon] = [800];
        f.Processes.Holders[withUnknown] = [801, 4242];
        f.Time.Advance(CodexLockDiscovery.Recheck);
        await source.RefreshAsync();

        Assert.Equal([800, 801], source.Snapshot().Select(s => s.ProcessId!.Value).Order());
    }

    [Fact]
    public async Task A_Codex_terminal_that_lets_its_thread_go_or_comes_to_share_it_while_it_runs_is_no_longer_that_session()
    {
        using var f = new Fixture();
        var lockFile = f.CodexLock(A);
        f.Processes.Run(AgentKind.Codex, 800, "codex.exe --no-daemon");
        f.Processes.Holders[lockFile] = [800];
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());

        // Another terminal comes to hold it too, while the first keeps running.
        f.Processes.Run(AgentKind.Codex, 801, "codex.exe --no-daemon");
        f.Processes.Holders[lockFile] = [800, 801];
        f.Time.Advance(CodexLockDiscovery.Recheck);
        await source.RefreshAsync();
        Assert.Empty(source.Snapshot());

        // Or nobody does.
        f.Processes.Holders[lockFile] = [800];
        f.Time.Advance(CodexLockDiscovery.Recheck);
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());
        f.Processes.Holders.Remove(lockFile);
        f.Time.Advance(CodexLockDiscovery.Recheck);
        await source.RefreshAsync();
        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task A_Codex_lock_nobody_holds_is_asked_about_again_only_after_Recheck_or_once_another_terminal_runs()
    {
        using var f = new Fixture();
        var lockFile = f.CodexLock(A);
        // Only the daemon running: no terminal could hold a session's lock, so none is asked about.
        f.Processes.Run(AgentKind.Codex, 900, "codex.exe app-server --listen unix:// --managed-daemon");
        f.Processes.Holders[lockFile] = [900];
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Equal(0, f.Processes.HolderQueries);

        f.Processes.Run(AgentKind.Codex, 800, "codex.exe --no-daemon");
        await source.RefreshAsync();
        await source.RefreshAsync();
        Assert.Equal(1, f.Processes.HolderQueries);

        f.Processes.Run(AgentKind.Codex, 801, "codex.exe --no-daemon");
        await source.RefreshAsync();
        Assert.Equal(2, f.Processes.HolderQueries);

        f.Time.Advance(CodexLockDiscovery.Recheck);
        await source.RefreshAsync();
        Assert.Equal(3, f.Processes.HolderQueries);
        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task A_Codex_terminal_holding_several_thread_locks_is_the_session_whose_rollout_is_its_own_thread_written_since_it_started()
    {
        using var f = new Fixture();
        const string Sub = "33333333-3333-4333-8333-333333333333";
        // Its own thread, a sub-agent's, and an old thread whose rollout it only touched.
        Fixture.Append(f.CodexTranscript(A), Meta(A, A, "user"), Codex.Event(new { type = "user_message", message = "mine" }, 10));
        Fixture.Append(f.CodexTranscript(Sub), Meta(Sub, A, "subagent"), Codex.Event(new { type = "user_message", message = "delegated" }, 11));
        Fixture.Append(f.CodexTranscript(B), Meta(B, B, "user"), Codex.Event(new { type = "user_message", message = "long ago" }, -7200));
        f.Processes.Run(AgentKind.Codex, 800, "codex.exe --no-daemon", Fixture.T0.AddMinutes(-5));
        f.Processes.Holders[f.CodexLock(A)] = [800];
        f.Processes.Holders[f.CodexLock(Sub)] = [800];
        f.Processes.Holders[f.CodexLock(B)] = [800];
        await using var source = f.Source();

        await source.RefreshAsync();

        var s = Assert.Single(source.Snapshot());
        Assert.Equal((A, 800), (s.Id, s.ProcessId!.Value));

        // Two that could each be its own: neither is taken.
        Fixture.Append(f.CodexTranscript(B), Codex.Event(new { type = "user_message", message = "again" }, 20));
        f.Time.Advance(CodexLockDiscovery.Recheck);
        await source.RefreshAsync();
        Assert.Empty(source.Snapshot());

        static string Meta(string id, string session, string threadSource) =>
            Codex.Line("session_meta", new { id, session_id = session, thread_source = threadSource, source = threadSource == "user" ? (object)"cli" : new { subagent = "review" } }, 0);
    }

    [Fact]
    public async Task A_Codex_terminal_holding_only_a_sub_agents_lock_is_not_that_sub_agents_session()
    {
        using var f = new Fixture();
        const string Sub = "33333333-3333-4333-8333-333333333333";
        Fixture.Append(f.CodexTranscript(A), Codex.Line("session_meta", new { id = A, session_id = A, thread_source = "user", source = "cli" }, 0));
        Fixture.Append(f.CodexTranscript(Sub), Codex.Line("session_meta", new { id = Sub, session_id = A, thread_source = "subagent", source = new { subagent = "review" } }, 1));
        f.Processes.Run(AgentKind.Codex, 800, "codex.exe --no-daemon");
        f.Processes.Run(AgentKind.Codex, 900, "codex.exe app-server --listen unix:// --managed-daemon");
        // Its own thread's lock is shared, so names nobody; the sub-agent's is its alone.
        f.Processes.Holders[f.CodexLock(A)] = [800, 900];
        f.Processes.Holders[f.CodexLock(Sub)] = [800];
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task A_Codex_rollout_whose_last_record_is_larger_than_one_read_still_tells_when_it_was_written()
    {
        using var f = new Fixture();
        const string Sub = "33333333-3333-4333-8333-333333333333";
        Fixture.Append(f.CodexTranscript(A), Codex.Line("session_meta", new { id = A, session_id = A, thread_source = "user", source = "cli" }, 0),
            Codex.Item(new { type = "function_call_output", call_id = "c1", output = new string('x', 300 * 1024) }, 10));
        Fixture.Append(f.CodexTranscript(Sub), Codex.Line("session_meta", new { id = Sub, session_id = A, thread_source = "subagent", source = new { subagent = "review" } }, 1));
        f.Processes.Run(AgentKind.Codex, 800, "codex.exe --no-daemon", Fixture.T0.AddMinutes(-5));
        f.Processes.Holders[f.CodexLock(A)] = [800];
        f.Processes.Holders[f.CodexLock(Sub)] = [800];
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Equal(A, Assert.Single(source.Snapshot()).Id);
    }

    [Theory]
    [InlineData("codex.exe --no-daemon -c model_reasoning_summary=detailed -- \"Launch test: exec this.\"", true)]
    [InlineData("codex.exe --no-daemon -c model_reasoning_summary=detailed resume 01a1897d-fd4f-7a93-8e0f-e0ed9760aa6e", true)]
    [InlineData("codex.exe --no-daemon fork --last", true)]
    [InlineData("codex.exe --no-daemon \"exec the plan\"", true)]
    [InlineData("codex.exe -m exec", true)]
    [InlineData("codex.exe", true)]
    [InlineData("codex.exe --no-daemon exec \"do it\"", false)]
    [InlineData("codex.exe --no-daemon -c k=v e \"do it\"", false)]
    [InlineData("codex.exe review --uncommitted", false)]
    [InlineData("codex.exe migrate-rollouts", false)]
    [InlineData("codex.exe -c model=example app-server --listen stdio://", false)]
    [InlineData(null, false)]
    public void A_Codex_terminal_is_the_interactive_CLI_not_a_headless_run_or_a_server(string? commandLine, bool terminal) =>
        Assert.Equal(terminal, CodexLockDiscovery.IsTerminal(commandLine));

    [Fact]
    public async Task A_headless_Codex_run_holding_a_thread_lock_is_not_a_session()
    {
        using var f = new Fixture();
        f.Processes.Run(AgentKind.Codex, 800, "codex.exe --no-daemon exec \"summarise the repo\"");
        f.Processes.Holders[f.CodexLock(A)] = [800];
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task A_registration_filed_under_another_session_name_is_ignored()
    {
        using var f = new Fixture();
        f.ClaudeRegistration(A, 700, f.ClaudeTranscript(A), fileName: B + ".json");
        f.Processes.Run(AgentKind.Claude, 700);
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public async Task A_Claude_registration_without_a_transcript_path_finds_its_transcript_by_session_id()
    {
        using var f = new Fixture();
        Fixture.Append(f.ClaudeTranscript(A), Claude.User("found by id", 1));
        f.ClaudeRegistration(A, 700, transcript: null);
        f.Processes.Run(AgentKind.Claude, 700);
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Equal("found by id", Assert.Single(source.Snapshot()).Title);
    }

    [Fact]
    public async Task A_lock_written_before_the_process_now_holding_its_pid_started_is_not_that_process_session()
    {
        using var f = new Fixture();
        // A's lock is a dead CLI's leftover from T0; pid 100 now belongs to a CLI that
        // started an hour later and whose own session is B.
        var stale = f.CopilotSession(A, lockPid: 100, lockWrite: Fixture.T0);
        var current = f.CopilotSession(B, lockPid: 100, lockWrite: Fixture.T0.AddHours(1).AddSeconds(5));
        Fixture.Append(current, Copilot.User("current", 1));
        Fixture.Append(stale, Copilot.User("stale", 2));
        File.SetLastWriteTimeUtc(current, Fixture.T0.AddHours(1).AddMinutes(1).UtcDateTime);
        File.SetLastWriteTimeUtc(stale, Fixture.T0.AddHours(2).UtcDateTime);
        f.Processes.Run(AgentKind.Copilot, 100, commandLine: null, startedAt: Fixture.T0.AddHours(1));
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Equal(B, Assert.Single(source.Snapshot()).Id);
    }

    [Fact]
    public async Task A_registration_older_than_the_process_now_holding_its_pid_is_not_live()
    {
        using var f = new Fixture();
        f.ClaudeRegistration(A, 700, f.ClaudeTranscript(A));
        // The registration was last written at T0; pid 700 now belongs to a process that
        // started an hour later, so it cannot be the session that wrote it.
        f.Processes.Run(AgentKind.Claude, 700, startedAt: Fixture.T0.AddHours(1));
        f.CodexRegistration(B, 800, f.CodexTranscript(B));
        f.Processes.Run(AgentKind.Codex, 800, startedAt: Fixture.T0.AddHours(1));
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
    }

    [Fact]
    public void The_Copilot_session_id_is_read_from_the_options_and_not_from_a_prompt_that_mentions_one()
    {
        Assert.Equal(A, CommandLine.CopilotSessionId($"copilot --session-id {A} --model x"));
        Assert.Equal(A, CommandLine.CopilotSessionId($"\"C:\\Program Files\\copilot.exe\" --session-id={A}"));
        Assert.Null(CommandLine.CopilotSessionId($"copilot -p \"resume --session-id {A} please\""));
        Assert.Null(CommandLine.CopilotSessionId($"copilot --session-id {A} --session-id {B}"));
        Assert.Null(CommandLine.CopilotSessionId("copilot --session-id not-a-uuid"));
    }

    [Fact]
    public void Node_and_bun_hosted_agents_are_candidates_on_every_platform_and_recognised_by_their_script()
    {
        Assert.Contains("node", SystemProcessInspector.CandidateNames("copilot"));
        Assert.Contains("bun", SystemProcessInspector.CandidateNames("claude"));
        Assert.True(SystemProcessInspector.IsAgentScript(AgentKind.Copilot, "copilot",
            "\"C:\\Program Files\\nodejs\\node.exe\" C:\\Users\\u\\AppData\\Roaming\\npm\\node_modules\\@github\\copilot\\index.js --session-id x"));
        Assert.True(SystemProcessInspector.IsAgentScript(AgentKind.Codex, "codex", "node /usr/lib/node_modules/@openai/codex/bin/codex.js"));
        Assert.False(SystemProcessInspector.IsAgentScript(AgentKind.Copilot, "copilot", "node C:\\work\\server.js --mention copilot"));
    }

    [Fact]
    public void This_platforms_command_line_reader_reads_a_live_process_and_answers_null_for_a_dead_one()
    {
        var own = SystemProcessInspector.ReadCommandLine(Environment.ProcessId);

        Assert.Contains("testhost", own, StringComparison.OrdinalIgnoreCase);
        Assert.Null(SystemProcessInspector.ReadCommandLine(int.MaxValue - 7));
    }

    internal static List<SessionEvent> Drain(SessionSubscription subscription)
    {
        var events = new List<SessionEvent>();
        while (subscription.Events.TryRead(out var e)) { events.Add(e); }
        return events;
    }
}
