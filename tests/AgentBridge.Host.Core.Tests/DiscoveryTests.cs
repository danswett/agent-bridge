using AgentBridge.Host.Core;
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
    public async Task An_ended_Codex_registration_is_not_a_session_even_while_its_process_still_runs()
    {
        using var f = new Fixture();
        f.CodexRegistration(A, 800, f.CodexTranscript(A), status: "ended", ended: true);
        f.Processes.Run(AgentKind.Codex, 800);
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
