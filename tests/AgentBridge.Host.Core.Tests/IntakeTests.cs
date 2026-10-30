using System.Text;
using System.Text.Json;
using AgentBridge.Hooks;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Discovery;
using AgentBridge.Host.Core.Intake;
using AgentBridge.Protocol;
using Xunit;

// Ported from agent-ha-bridge's tests/test-hook-spool.ps1, test-hook-functions.ps1 and
// test-hook-status.ps1, for the host's intake: what the daemon did with the spool, and what the
// hook functions recorded locally, without Home Assistant.
public sealed class IntakeTests : IDisposable
{
    readonly Fixture _f = new();
    readonly List<string> _log = [];
    string? _agentHaBridge;
    readonly List<HookIntake> _intakes = [];

    public void Dispose()
    {
        foreach (var i in _intakes) { i.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        _f.Dispose();
    }

    string Spool => SpoolContract.SpoolDirectory(_f.Runtime);

    // A host's intake. Like a restarted host, a new one replaces the last (only one takes a spool
    // at a time), unless it is meant to run alongside.
    HookIntake Intake(bool watch = false, bool alongside = false)
    {
        if (!alongside)
        {
            foreach (var i in _intakes) { i.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
            _intakes.Clear();
        }
        var intake = new HookIntake(new HookIntakeOptions
        {
            RuntimeRoot = _f.Runtime,
            ClaudeHome = _f.ClaudeHome,
            AgentHaBridgeActive = () => _probe is { } probe ? probe() : _agentHaBridge,
            UseFileSystemWatcher = watch,
            PruneInterval = TimeSpan.FromDays(1),
        }, _f.Processes, _f.Time, m => { lock (_log) { _log.Add(m); } });
        _intakes.Add(intake);
        return intake;
    }

    // Asked instead of _agentHaBridge when set, for a test that needs the answer to change mid-pass.
    Func<string?>? _probe;

    // When the hooks in a test ran: Fixture.T0, or just after a Running() host took the spool.
    DateTimeOffset _base = Fixture.T0;

    // A host that has already taken the spool (its first pass found nothing to do), so what is
    // spooled after this is asked while it runs, not left from before it started: a host taking
    // the spool withdraws every approval asked before then. Hooks then run up to 50 s later,
    // inside the heartbeat's 60 s, as they would while the host keeps it fresh.
    HookIntake Running(bool watch = false)
    {
        var intake = Intake(watch);
        intake.Pass();
        _f.Time.Advance(TimeSpan.FromSeconds(1));
        _base = _f.Time.GetUtcNow();
        _f.Time.Advance(TimeSpan.FromSeconds(50));
        return intake;
    }

    // A spool file as either bridge's hook writes it, under a name that sorts where given.
    string Spooled(string name, string agent, string hook, object ev, params int[] ancestors) => SpooledAt(_base, name, agent, hook, ev, ancestors);

    string SpooledAt(DateTimeOffset receivedAt, string name, string agent, string hook, object ev, params int[] ancestors)
    {
        Directory.CreateDirectory(Spool);
        var body = JsonSerializer.Serialize(new { v = 1, agent, hook, ancestors, receivedAt = receivedAt.ToString("o"), @event = ev });
        var path = Path.Combine(Spool, name + ".json");
        File.WriteAllText(path, body);
        return path;
    }

    JsonElement Registration(string folder, string id)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_f.Runtime, folder, id + ".json")));
        return doc.RootElement.Clone();
    }

    [Fact]
    public void A_machine_with_no_spool_folder_costs_one_check_and_handles_nothing()
    {
        Assert.Equal(0, Intake().Pass());
        Assert.False(Directory.Exists(Spool) && Directory.GetFiles(Spool).Length > 0);
    }

    [Fact]
    public void Every_complete_event_is_handled_in_the_order_it_was_written_and_then_removed()
    {
        _f.Processes.Run(AgentKind.Claude, 30);
        Spooled("0002", "codex", "hook", new { session_id = "c", hook_event_name = "SessionStart" });
        Spooled("0001", "claude", "stop", new { session_id = "b" }, 20, 30);
        Spooled("0000", "claude", "register", new { session_id = "a", hook_event_name = "SessionStart" });
        File.WriteAllText(Path.Combine(Spool, "0003.json.tmp"), "{ half");

        var intake = Intake();
        var heard = intake.Subscribe();
        Assert.Equal(3, intake.Pass());

        var order = new List<string>();
        while (heard.TryRead(out var n)) { order.Add($"{n.Key()}:{n.SessionId}"); }
        Assert.Equal(["claude/register:a", "claude/stop:b", "codex/hook:c"], order);
        Assert.Equal(["0003.json.tmp"], Directory.GetFiles(Spool).Select(Path.GetFileName));
        Assert.Equal(30, Registration("agent-bridge-claude", "b").GetProperty("ProcessId").GetInt32());
    }

    [Fact]
    public void A_failed_event_is_kept_for_one_more_try_then_dropped_and_logged()
    {
        var unknown = Spooled("0031", "gemini", "stop", new { });
        File.WriteAllText(Path.Combine(Directory.GetParent(unknown)!.FullName, "0032.json"), "not json");
        var intake = Intake();

        intake.Pass();
        Assert.True(File.Exists(unknown));
        intake.Pass();
        // The second bad file waited for the first to be dropped, and takes its own two tries.
        intake.Pass();
        Assert.Empty(Directory.GetFiles(Spool));
        Assert.Contains(_log, l => l.StartsWith("dropped spooled hook 0031.json after 2 attempts: no handler for gemini/stop", StringComparison.Ordinal));
        Assert.Contains(_log, l => l.StartsWith("dropped spooled hook 0032.json after 2 attempts", StringComparison.Ordinal));
        Assert.Equal(2, intake.Dropped);
    }

    [Fact]
    public void While_agent_ha_bridge_runs_the_intake_writes_no_heartbeat_and_neither_reads_nor_deletes_the_spool()
    {
        _agentHaBridge = "agent-ha-bridge is running for this user (AgentBridgeDaemon_0123)";
        var file = Spooled("0001", "claude", "register", new { session_id = "a", hook_event_name = "UserPromptSubmit" });
        var intake = Intake();
        var before = _f.Listing();

        Assert.Equal(0, intake.Pass());
        Assert.Equal(before, _f.Listing());
        Assert.False(File.Exists(SpoolContract.HeartbeatPath(_f.Runtime)));
        Assert.Equal(_agentHaBridge, intake.Paused);

        // And once it has stopped, the host takes the spool again.
        _agentHaBridge = null;
        Assert.Equal(1, intake.Pass());
        Assert.False(File.Exists(file));
        Assert.Null(intake.Paused);
        Assert.True(File.Exists(SpoolContract.HeartbeatPath(_f.Runtime)));
    }

    [Fact]
    public async Task Only_one_host_takes_a_spool_and_a_second_waits_until_the_first_lets_go()
    {
        var file = Spooled("0001", "claude", "register", new { session_id = "a" });
        var first = Intake();
        Assert.Equal(1, first.Pass());

        var second = Intake(alongside: true);
        Spooled("0002", "claude", "register", new { session_id = "b" });
        Assert.Equal(0, second.Pass());
        Assert.Contains("another host is taking this spool", second.Paused);
        Assert.True(File.Exists(Path.Combine(Spool, "0002.json")));

        await first.DisposeAsync();
        _intakes.Remove(first);
        Assert.False(File.Exists(Path.Combine(_f.Runtime, HookIntake.ClaimFile)));
        Assert.Equal(1, second.Pass());
        Assert.Null(second.Paused);
        Assert.False(File.Exists(file));
    }

    [Fact]
    public void Agent_ha_bridge_starting_part_way_through_a_pass_stops_it_at_the_next_file()
    {
        Spooled("0001", "claude", "register", new { session_id = "a" });
        var second = Spooled("0002", "claude", "register", new { session_id = "b" });
        // Asked once for the pass, then before each file.
        var checks = 0;
        var gated = new HookIntake(new HookIntakeOptions
        {
            RuntimeRoot = _f.Runtime,
            ClaudeHome = _f.ClaudeHome,
            AgentHaBridgeActive = () => ++checks > 2 ? "the daemon started" : null,
            UseFileSystemWatcher = false,
        }, _f.Processes, _f.Time, _log.Add);
        _intakes.Add(gated);

        Assert.Equal(1, gated.Pass());
        Assert.True(File.Exists(second));
        Assert.Equal("the daemon started", gated.Paused);
    }

    [Fact]
    public void An_event_that_fails_holds_back_the_ones_after_it_so_a_retried_approval_is_never_rearmed_after_its_answer()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var asked = Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        var answered = Spooled("0002", "codex", "hook", new { session_id = "x1", hook_event_name = "PreToolUse", tool_name = "shell" }, 60);
        var intake = Intake();

        // A transient failure: something holds the first file for the whole of a pass.
        using (new FileStream(asked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(0, intake.Pass());
        }
        Assert.True(File.Exists(answered));

        Assert.Equal(2, intake.Pass());
        Assert.False(File.Exists(Path.Combine(_f.CodexRegistrations, "x1.approval.json")));
        Assert.Equal("working", Registration("agent-bridge-codex", "x1").GetProperty("Status").GetString());
    }

    [Fact]
    public void Registrations_are_not_pruned_once_agent_ha_bridge_has_started_during_the_pass()
    {
        _f.ClaudeRegistration("dead-old", 31, null);
        Spooled("0001", "copilot", "agent_stop", new { });
        // The pass, then the file, then the prune: agent-ha-bridge is running by the third.
        var checks = 0;
        var intake = new HookIntake(new HookIntakeOptions
        {
            RuntimeRoot = _f.Runtime,
            ClaudeHome = _f.ClaudeHome,
            AgentHaBridgeActive = () => ++checks > 2 ? "the daemon started" : null,
            UseFileSystemWatcher = false,
        }, _f.Processes, _f.Time, _log.Add);
        _intakes.Add(intake);
        _f.Time.Advance(TimeSpan.FromHours(6));

        Assert.Equal(1, intake.Pass());
        Assert.True(File.Exists(Path.Combine(_f.ClaudeRegistrations, "dead-old.json")));
        Assert.Equal("the daemon started", intake.Paused);
    }

    [Fact]
    public async Task The_heartbeat_is_kept_while_taking_events_and_only_the_hosts_own_is_removed_when_it_stops()
    {
        var intake = Intake();
        intake.Pass();
        var heartbeat = SpoolContract.HeartbeatPath(_f.Runtime);
        Assert.Equal(Environment.ProcessId.ToString(), File.ReadAllText(heartbeat));
        Assert.True(SpoolContract.ConsumerState(_f.Runtime, DateTimeOffset.Now).Alive);
        await intake.DisposeAsync();
        Assert.False(File.Exists(heartbeat));

        // A daemon that has taken over keeps its heartbeat.
        var second = Intake();
        second.Pass();
        File.WriteAllText(heartbeat, "999999");
        await second.DisposeAsync();
        Assert.Equal("999999", File.ReadAllText(heartbeat));
    }

    [Fact]
    public async Task A_file_a_hook_renames_into_place_is_noticed_at_once_and_handled()
    {
        _f.Processes.Run(AgentKind.Claude, 30);
        await using var intake = new HookIntake(new HookIntakeOptions
        {
            RuntimeRoot = _f.Runtime,
            ClaudeHome = _f.ClaudeHome,
            SweepInterval = TimeSpan.FromMinutes(5),
        }, _f.Processes, _f.Time, _log.Add);
        var heard = intake.Subscribe();
        intake.Start();
        // The first pass makes the folder and starts watching it.
        for (var i = 0; i < 100 && !Directory.Exists(Spool); i++) { await Task.Delay(20); }
        await Task.Delay(100);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        SpoolContract.Write(_f.Runtime, "claude", "register", "{\"session_id\":\"watched\",\"hook_event_name\":\"SessionStart\"}"u8, [30], DateTimeOffset.Now, 1);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var notice = await heard.ReadAsync(timeout.Token);
        Assert.Equal("watched", notice.SessionId);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed} with a five-minute sweep");
    }

    // ---------------------------------------------------------------- Claude

    [Fact]
    public void A_Claude_prompt_registers_the_session_as_working_owned_by_the_claude_process_past_a_shell_that_has_exited()
    {
        _f.Processes.Run(AgentKind.Claude, 30);
        var transcript = _f.ClaudeTranscript("s1");
        Spooled("0001", "claude", "register", new { session_id = "s1", hook_event_name = "UserPromptSubmit", transcript_path = transcript, cwd = "/work/repo" }, 10, 20, 30, 40);
        var intake = Intake();
        var heard = intake.Subscribe();
        intake.Pass();

        var r = Registration("agent-bridge-claude", "s1");
        Assert.Equal(["SessionId", "ProcessId", "TranscriptPath", "WorkingDirectory", "Updated", "HookStatus", "HookStatusAt"], r.EnumerateObject().Select(p => p.Name));
        Assert.Equal(30, r.GetProperty("ProcessId").GetInt32());
        Assert.Equal("working", r.GetProperty("HookStatus").GetString());
        Assert.Equal(transcript, r.GetProperty("TranscriptPath").GetString());
        Assert.True(heard.TryRead(out var n));
        Assert.Equal((30, "working"), (n.OwnerPid, n.Status));

        // And discovery reads it as the live session it is.
        var found = RegistrationDiscovery.Find(AgentKind.Claude, [_f.ClaudeRegistrations], _f.Processes.GetAgentProcesses(AgentKind.Claude), _ => null);
        Assert.Equal("s1", Assert.Single(found).Id);
    }

    [Fact]
    public void An_event_that_waited_in_the_spool_is_dated_when_its_hook_ran_not_when_it_was_handled()
    {
        // Spooled at T0; the host comes back an hour later. Its working status must not read as
        // newer than the transcript's finished turn since.
        _f.Processes.Run(AgentKind.Claude, 30);
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        Spooled("0001", "claude", "register", new { session_id = "s1", hook_event_name = "UserPromptSubmit", transcript_path = _f.ClaudeTranscript("s1") }, 30);
        Spooled("0002", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);

        Intake().Pass();

        DateTimeOffset At(JsonElement e, string field) => DateTimeOffset.Parse(e.GetProperty(field).GetString()!, System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(_base, At(Registration("agent-bridge-claude", "s1"), "HookStatusAt"));
        Assert.Equal(_base, At(Registration("agent-bridge-claude", "s1"), "Updated"));
        Assert.Equal(_base, At(Registration("agent-bridge-codex", "x1"), "Updated"));
    }

    [Fact]
    public void A_later_Claude_event_without_a_status_or_a_process_keeps_what_was_recorded()
    {
        _f.Processes.Run(AgentKind.Claude, 30);
        Spooled("0001", "claude", "notification", new { session_id = "s1", notification_type = "permission_prompt", message = "Claude needs your permission to use Bash" }, 30);
        Spooled("0002", "claude", "register", new { session_id = "s1", hook_event_name = "SessionStart" });
        Intake().Pass();

        var r = Registration("agent-bridge-claude", "s1");
        Assert.Equal(30, r.GetProperty("ProcessId").GetInt32());
        Assert.Equal("waiting", r.GetProperty("HookStatus").GetString());
        Assert.NotEqual("", r.GetProperty("HookStatusAt").GetString());
    }

    [Fact]
    public void Claude_finishing_a_turn_marks_it_idle_and_the_idle_reminder_after_it_does_not_mark_it_waiting()
    {
        _f.Processes.Run(AgentKind.Claude, 30);
        Spooled("0001", "claude", "stop", new { session_id = "s1", last_assistant_message = "done" }, 30);
        Spooled("0002", "claude", "notification", new { session_id = "s1", message = "Claude is waiting for your input" }, 30);
        Intake().Pass();
        Assert.Equal("idle", Registration("agent-bridge-claude", "s1").GetProperty("HookStatus").GetString());
    }

    [Fact]
    public void A_Claude_tool_other_than_AskUserQuestion_and_an_event_without_a_session_record_nothing()
    {
        Spooled("0001", "claude", "ask", new { session_id = "s1", tool_name = "Bash" });
        Spooled("0002", "claude", "register", new { hook_event_name = "SessionStart" });
        Assert.Equal(2, Intake().Pass());
        Assert.False(Directory.Exists(_f.ClaudeRegistrations) && Directory.GetFiles(_f.ClaudeRegistrations).Length > 0);
    }

    [Fact]
    public void A_Claude_transcript_the_event_did_not_name_is_found_in_its_project_folder()
    {
        var transcript = _f.ClaudeTranscript("s1");
        Fixture.Append(transcript, "{}");
        Spooled("0001", "claude", "stop", new { session_id = "s1" });
        Intake().Pass();
        Assert.Equal(transcript, Registration("agent-bridge-claude", "s1").GetProperty("TranscriptPath").GetString());
    }

    // ---------------------------------------------------------------- Codex

    [Fact]
    public void A_Codex_approval_is_recorded_waiting_with_a_marker_the_answer_path_reads_and_no_Home_Assistant()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var intake = Running();
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell", tool_input = new { command = "git push" }, cwd = "/work/codex", model = "gpt-5" }, 50, 60);
        var heard = intake.Subscribe();
        intake.Pass();

        var r = Registration("agent-bridge-codex", "x1");
        Assert.Equal(["SessionId", "ProcessId", "TranscriptPath", "WorkingDirectory", "Model", "Status", "Activity", "Ended", "Updated"], r.EnumerateObject().Select(p => p.Name));
        Assert.Equal(("waiting", "Needs approval: git push", 60), (r.GetProperty("Status").GetString(), r.GetProperty("Activity").GetString(), r.GetProperty("ProcessId").GetInt32()));

        var marker = ApprovalMarkerRead.Read(Path.Combine(_f.CodexRegistrations, "x1.json"), "x1");
        Assert.Equal(ApprovalMarkerState.Present, marker.State);
        Assert.Equal("Needs approval: git push", marker.Question);
        Assert.False(marker.Attempted);
        Assert.StartsWith("agent_bridge_x1-", marker.DecisionId);
        Assert.True(heard.TryRead(out var n));
        Assert.Equal(marker.DecisionId, n.ApprovalId);

        // Discovery puts the approval on the session as a question a client can answer.
        var found = Assert.Single(RegistrationDiscovery.Find(AgentKind.Codex, [_f.CodexRegistrations], _f.Processes.GetAgentProcesses(AgentKind.Codex), _ => null));
        Assert.Equal(marker.DecisionId, found.Registration!.Approval!.DecisionId);
    }

    [Fact]
    public void The_next_Codex_event_retires_the_approval_marker_whichever_input_answered_it()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        Spooled("0002", "codex", "hook", new { session_id = "x1", hook_event_name = "PreToolUse", tool_name = "shell", tool_input = new { command = "git push" } }, 60);
        Intake().Pass();
        Assert.False(File.Exists(Path.Combine(_f.CodexRegistrations, "x1.approval.json")));
        var r = Registration("agent-bridge-codex", "x1");
        Assert.Equal(("working", "Running: shell - git push"), (r.GetProperty("Status").GetString(), r.GetProperty("Activity").GetString()));
    }

    [Fact]
    public async Task An_approval_asked_before_a_Codex_hook_ran_with_nobody_taking_events_is_withdrawn_when_the_host_returns()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var first = Running();
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        first.Pass();
        var marker = Path.Combine(_f.CodexRegistrations, "x1.approval.json");
        Assert.True(File.Exists(marker));

        // The host stops; the person answers at the terminal, and Codex's PreToolUse gets only
        // the fixed reply. The same Codex is still running when the host comes back.
        await first.DisposeAsync();
        _intakes.Remove(first);
        SpoolContract.Record(_f.Runtime, "codex", "hook", "reply", "no daemon heartbeat", _f.Time.GetLocalNow().AddSeconds(5), 3);
        Intake().Pass();

        Assert.False(File.Exists(marker));
        Assert.Contains(_log, l => l.StartsWith("withdrew Codex approval x1.approval.json", StringComparison.Ordinal));
    }

    [Fact]
    public void An_approval_asked_after_the_last_unheard_Codex_hook_is_still_pending()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var intake = Running();
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        // Before the approval was asked (as its hook recorded), though read after; and another
        // agent's unheard run after it says nothing about Codex.
        SpoolContract.Record(_f.Runtime, "codex", "hook", "reply", "spool not writable", _base.AddSeconds(-0.5), 3);
        SpoolContract.Record(_f.Runtime, "claude", "stop", "reply", "spool not writable", _base.AddSeconds(5), 3);
        Assert.NotNull(HookStats.LastUnspooled(_f.Runtime, "codex"));

        intake.Pass();
        intake.Pass();

        Assert.True(File.Exists(Path.Combine(_f.CodexRegistrations, "x1.approval.json")));
        Assert.Null(intake.ApprovalsUnsettled);
    }

    [Fact]
    public async Task Every_approval_asked_before_the_host_takes_the_spool_again_is_withdrawn_whatever_the_hook_log_says()
    {
        // A hook that could not spool may not have been able to log either (a full disk), so a
        // clean log proves nothing about what was answered at the terminal meanwhile.
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var first = Running();
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        first.Pass();
        var marker = Path.Combine(_f.CodexRegistrations, "x1.approval.json");
        Assert.True(File.Exists(marker));
        await first.DisposeAsync();
        _intakes.Remove(first);

        var again = Intake();
        Assert.NotNull(again.ApprovalsUnsettled);
        again.Pass();

        Assert.False(File.Exists(marker));
        Assert.Null(again.ApprovalsUnsettled);
    }

    [Fact]
    public void A_heartbeat_left_to_go_stale_withdraws_every_approval_asked_before_it_is_fresh_again()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var intake = Running();
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        intake.Pass();
        var marker = Path.Combine(_f.CodexRegistrations, "x1.approval.json");
        Assert.True(File.Exists(marker));

        // A pass a few seconds late keeps it; one after hooks stopped spooling does not.
        _f.Time.Advance(TimeSpan.FromSeconds(30));
        intake.Pass();
        Assert.True(File.Exists(marker));
        _f.Time.Advance(SpoolContract.HeartbeatMaxAge);
        intake.Pass();
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void An_approval_still_waiting_in_the_spool_from_before_an_outage_arms_nothing()
    {
        // The host died with its heartbeat fresh, so the PermissionRequest was spooled; then the
        // heartbeat went stale and the answer at the terminal reached nobody.
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        SpoolContract.Record(_f.Runtime, "codex", "hook", "reply", "no daemon heartbeat", _base.AddSeconds(5), 3);
        var intake = Intake();
        var heard = intake.Subscribe();

        Assert.Equal(1, intake.Pass());

        Assert.False(File.Exists(Path.Combine(_f.CodexRegistrations, "x1.approval.json")));
        Assert.True(heard.TryRead(out var n));
        Assert.Null(n.ApprovalId);

        // A request asked after the host took the spool arms as usual.
        File.WriteAllText(Path.Combine(Spool, "0002.json"), JsonSerializer.Serialize(new
        {
            v = 1, agent = "codex", hook = "hook", ancestors = new[] { 60 }, receivedAt = _f.Time.GetUtcNow().AddSeconds(1).ToString("o"),
            @event = new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" },
        }));
        Assert.Equal(1, intake.Pass());
        Assert.True(File.Exists(Path.Combine(_f.CodexRegistrations, "x1.approval.json")));
    }

    [Fact]
    public void Two_approvals_handled_in_the_same_millisecond_get_different_decision_ids()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var intake = Running();
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        Spooled("0002", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "apply_patch" }, 60);
        var heard = intake.Subscribe();

        Assert.Equal(2, intake.Pass());

        var ids = new List<string?>();
        while (heard.TryRead(out var n)) { ids.Add(n.ApprovalId); }
        Assert.Equal(2, ids.Distinct().Count(i => i is not null));
    }

    [Fact]
    public void A_replayed_approval_its_session_has_moved_past_arms_nothing()
    {
        // A PermissionRequest whose delete failed, handled again after a restart, behind the
        // PreToolUse that settled it.
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        SpooledAt(_base, "0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        SpooledAt(_base.AddSeconds(10), "0002", "codex", "hook", new { session_id = "x1", hook_event_name = "PreToolUse", tool_name = "shell" }, 60);
        Intake().Pass();
        SpooledAt(_base, "0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);

        Assert.Equal(1, Intake().Pass());

        Assert.False(File.Exists(Path.Combine(_f.CodexRegistrations, "x1.approval.json")));
        Assert.Equal("working", Registration("agent-bridge-codex", "x1").GetProperty("Status").GetString());
    }

    [Fact]
    public void A_pending_approval_handled_again_after_a_restart_is_withdrawn_and_arms_nothing_so_no_answer_is_typed_twice()
    {
        // Its delete failed and the host restarted before anything newer for the session; an
        // answer had already been tried on it.
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var first = Running();
        SpooledAt(_base, "0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        first.Pass();
        var path = Path.Combine(_f.CodexRegistrations, "x1.approval.json");
        Assert.True(File.Exists(path));
        SpooledAt(_base, "0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        var intake = Intake();
        var heard = intake.Subscribe();

        Assert.Equal(1, intake.Pass());

        Assert.False(File.Exists(path));
        Assert.True(heard.TryRead(out var n));
        Assert.Null(n.ApprovalId);
    }

    [Fact]
    public void A_rotation_the_running_host_saw_withdraws_nothing_but_one_it_missed_does()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var log = SpoolContract.LogPath(_f.Runtime);
        Directory.CreateDirectory(_f.Runtime);
        SpoolContract.Record(_f.Runtime, "claude", "stop", "spool", null, _base, 3);
        var intake = Running();
        SpooledAt(_base, "0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        intake.Pass();
        var marker = Path.Combine(_f.CodexRegistrations, "x1.approval.json");
        Assert.True(File.Exists(marker));

        // The log it was reading becomes .1.
        File.Move(log, log + ".1");
        SpoolContract.Record(_f.Runtime, "claude", "stop", "spool", null, _base.AddSeconds(10), 3);
        intake.Pass();
        Assert.True(File.Exists(marker));

        // Rotated twice between two passes: the log it was reading is gone unread.
        File.Move(log, log + ".1", overwrite: true);
        SpoolContract.Record(_f.Runtime, "claude", "stop", "spool", null, _base.AddSeconds(20), 3);
        File.SetLastWriteTimeUtc(log + ".1", DateTime.UtcNow.AddMinutes(1));
        File.Move(log, log + ".1", overwrite: true);
        SpoolContract.Record(_f.Runtime, "claude", "stop", "spool", null, _base.AddSeconds(30), 3);
        intake.Pass();
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void Settling_approvals_reads_a_marker_another_process_is_rewriting_without_locking_it_out()
    {
        // Only Windows enforces share modes.
        if (!OperatingSystem.IsWindows()) { return; }
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var intake = Running();
        SpooledAt(_base.AddSeconds(20), "0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        intake.Pass();
        var marker = Path.Combine(_f.CodexRegistrations, "x1.approval.json");
        SpoolContract.Record(_f.Runtime, "codex", "hook", "reply", "spool not writable", _base.AddSeconds(10), 3);

        // agent-ha-bridge's daemon starting, part-way through rewriting the marker.
        using (new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            intake.Pass();
        }

        // Asked after the unheard run, so it is still pending: read, not given up as unreadable.
        Assert.True(File.Exists(marker));
    }

    [Fact]
    public void Codex_approvals_are_not_answerable_until_a_pass_has_settled_them_against_a_readable_hook_log()
    {
        var intake = Intake();
        Assert.NotNull(intake.ApprovalsUnsettled);

        intake.Pass();
        Assert.Null(intake.ApprovalsUnsettled);

        if (!OperatingSystem.IsWindows()) { return; }
        Directory.CreateDirectory(_f.Runtime);
        SpoolContract.Record(_f.Runtime, "claude", "stop", "spool", null, _base, 3);
        using (new FileStream(SpoolContract.LogPath(_f.Runtime), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            intake.Pass();
            Assert.NotNull(intake.ApprovalsUnsettled);
        }
        intake.Pass();
        Assert.Null(intake.ApprovalsUnsettled);
    }

    [Fact]
    public void A_later_unheard_Codex_run_keeps_approvals_unanswerable_until_every_marker_it_covers_is_withdrawn()
    {
        // Only Windows lets a reader refuse a delete.
        if (!OperatingSystem.IsWindows()) { return; }
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var intake = Running();
        SpooledAt(_base, "0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        intake.Pass();
        Assert.Null(intake.ApprovalsUnsettled);
        var marker = Path.Combine(_f.CodexRegistrations, "x1.approval.json");
        SpoolContract.Record(_f.Runtime, "codex", "hook", "reply", "spool not writable", _base.AddSeconds(5), 3);

        using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            intake.Pass();
            Assert.True(File.Exists(marker));
            Assert.NotNull(intake.ApprovalsUnsettled);
        }
        intake.Pass();

        Assert.False(File.Exists(marker));
        Assert.Null(intake.ApprovalsUnsettled);
    }

    [Fact]
    public void Codex_approvals_are_refused_on_a_migrated_profile_whose_intake_cannot_start_and_free_on_one_never_migrated()
    {
        Assert.Null(HookIntake.AnswerGate(migrated: false, problem: null, () => null));
        Assert.Contains("intake.json is not valid", HookIntake.AnswerGate(false, "intake.json is not valid", () => null)!());
        Assert.Contains("still starting", HookIntake.AnswerGate(true, null, () => null)!());
        var intake = Intake();
        var gate = HookIntake.AnswerGate(true, null, () => intake)!;
        Assert.NotNull(gate());
        intake.Pass();
        Assert.Null(gate());
    }

    [Fact]
    public void An_approval_whose_answering_hook_failed_to_spool_is_withdrawn_on_the_next_pass()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var intake = Running();
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        intake.Pass();
        var marker = Path.Combine(_f.CodexRegistrations, "x1.approval.json");
        Assert.True(File.Exists(marker));

        // The heartbeat is fresh, but the PreToolUse could not be spooled.
        SpoolContract.Record(_f.Runtime, "codex", "hook", "reply", "spool not writable", _base.AddSeconds(5), 3);
        intake.Pass();

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void While_the_hook_log_cannot_be_read_no_approval_is_offered()
    {
        // Only Windows refuses a reader outright.
        if (!OperatingSystem.IsWindows()) { return; }
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        SpoolContract.Record(_f.Runtime, "codex", "hook", "spool", null, _base.AddSeconds(-5), 3);
        var intake = Intake();

        using (new FileStream(SpoolContract.LogPath(_f.Runtime), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.Equal(1, intake.Pass());
        }

        Assert.False(File.Exists(Path.Combine(_f.CodexRegistrations, "x1.approval.json")));
    }

    [Fact]
    public void Withdrawing_approvals_stops_at_once_when_agent_ha_bridge_starts()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var first = Running();
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        first.Pass();
        var marker = Path.Combine(_f.CodexRegistrations, "x1.approval.json");
        Assert.True(File.Exists(marker));
        SpoolContract.Record(_f.Runtime, "codex", "hook", "reply", "no daemon heartbeat", _base.AddSeconds(5), 3);
        foreach (var i in _intakes) { i.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        _intakes.Clear();
        // The pass, then the marker: agent-ha-bridge is running by the second.
        var checks = 0;
        var gated = new HookIntake(new HookIntakeOptions
        {
            RuntimeRoot = _f.Runtime,
            ClaudeHome = _f.ClaudeHome,
            AgentHaBridgeActive = () => ++checks > 1 ? "the daemon started" : null,
            UseFileSystemWatcher = false,
        }, _f.Processes, _f.Time, _log.Add);
        _intakes.Add(gated);

        Assert.Equal(0, gated.Pass());

        Assert.True(File.Exists(marker));
        Assert.Equal("the daemon started", gated.Paused);
    }

    [Fact]
    public void Pruning_stops_at_once_when_agent_ha_bridge_starts()
    {
        _f.ClaudeRegistration("dead-old", 31, null);
        // The pass, the prune, then the first delete: agent-ha-bridge is running by the third.
        var checks = 0;
        var gated = new HookIntake(new HookIntakeOptions
        {
            RuntimeRoot = _f.Runtime,
            ClaudeHome = _f.ClaudeHome,
            AgentHaBridgeActive = () => ++checks > 2 ? "the daemon started" : null,
            UseFileSystemWatcher = false,
            PruneInterval = TimeSpan.Zero,
        }, _f.Processes, _f.Time, _log.Add);
        _intakes.Add(gated);
        _f.Time.Advance(TimeSpan.FromHours(6));

        gated.Pass();

        Assert.True(File.Exists(Path.Combine(_f.ClaudeRegistrations, "dead-old.json")));
        Assert.Equal("the daemon started", gated.Paused);
    }

    [Fact]
    public void The_heartbeat_is_kept_fresh_while_a_long_backlog_drains()
    {
        Spooled("0001", "copilot", "agent_stop", new { });
        Spooled("0002", "copilot", "agent_stop", new { });
        var heartbeat = SpoolContract.HeartbeatPath(_f.Runtime);
        // Each file takes half a minute; the heartbeat written before the drain goes away.
        var checks = 0;
        var slow = new HookIntake(new HookIntakeOptions
        {
            RuntimeRoot = _f.Runtime,
            ClaudeHome = _f.ClaudeHome,
            AgentHaBridgeActive = () =>
            {
                if (++checks > 1)
                {
                    _f.Time.Advance(TimeSpan.FromSeconds(30));
                    File.Delete(heartbeat);
                }
                return null;
            },
            UseFileSystemWatcher = false,
            PruneInterval = TimeSpan.FromDays(1),
        }, _f.Processes, _f.Time, _log.Add);
        _intakes.Add(slow);

        Assert.Equal(2, slow.Pass());

        Assert.True(File.Exists(heartbeat));
    }

    [Fact]
    public void A_deeply_nested_event_a_hook_accepted_is_handled_not_dropped()
    {
        var deep = "{}";
        for (var i = 0; i < 200; i++) { deep = "{\"a\":" + deep + "}"; }
        Directory.CreateDirectory(Spool);
        File.WriteAllText(Path.Combine(Spool, "0001.json"), "{\"v\":1,\"agent\":\"copilot\",\"hook\":\"agent_stop\",\"ancestors\":[],\"receivedAt\":\"" + _base.ToString("o") + "\",\"event\":" + deep + "}");
        Assert.True(SpoolContract.IsJson(System.Text.Encoding.UTF8.GetBytes(deep)));
        var intake = Intake();

        Assert.Equal(1, intake.Pass());
        Assert.Equal(0, intake.Dropped);
    }

    [Fact]
    public void A_Codex_window_bound_before_its_pid_was_reused_or_claimed_by_a_live_session_is_not_taken()
    {
        _f.Processes.Run(AgentKind.Codex, 50, "codex app-server");
        // 70 is a new process under a reused pid: "mine" recorded it an hour before it started,
        // and "other" has claimed it since.
        _f.Processes.Run(AgentKind.Codex, 70, "codex.exe", _base);
        _f.Processes.Run(AgentKind.Codex, 80, "codex.exe", _base.AddMinutes(-10));
        _f.CodexRegistration("mine", 70, null, updated: _base.AddHours(-1));
        _f.CodexRegistration("other", 70, null, updated: _base.AddMinutes(5));
        // And a stale claim on 80, from before it started, claims nothing.
        _f.CodexRegistration("stale", 80, null, updated: _base.AddHours(-2));
        SpooledAt(_base.AddMinutes(10), "0001", "codex", "hook", new { session_id = "mine", hook_event_name = "UserPromptSubmit" }, 50);

        Intake().Pass();

        Assert.Equal(80, Registration("agent-bridge-codex", "mine").GetProperty("ProcessId").GetInt32());
    }

    [Fact]
    public void Approvals_are_settled_even_while_the_heartbeat_cannot_be_written()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var first = Running();
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        first.Pass();
        var marker = Path.Combine(_f.CodexRegistrations, "x1.approval.json");
        Assert.True(File.Exists(marker));
        SpoolContract.Record(_f.Runtime, "codex", "hook", "reply", "no daemon heartbeat", _base.AddSeconds(5), 3);
        foreach (var i in _intakes) { i.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
        _intakes.Clear();
        // Something where the heartbeat should be.
        File.Delete(SpoolContract.HeartbeatPath(_f.Runtime));
        Directory.CreateDirectory(SpoolContract.HeartbeatPath(_f.Runtime));

        Intake(alongside: true).Pass();

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void An_approval_that_could_not_be_withdrawn_is_withdrawn_on_a_later_pass()
    {
        // Only Windows lets a reader refuse a delete.
        if (!OperatingSystem.IsWindows()) { return; }
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var intake = Running();
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        intake.Pass();
        var marker = Path.Combine(_f.CodexRegistrations, "x1.approval.json");
        Assert.True(File.Exists(marker));
        SpoolContract.Record(_f.Runtime, "codex", "hook", "reply", "no daemon heartbeat", _base.AddSeconds(5), 3);

        using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read)) { intake.Pass(); }
        Assert.True(File.Exists(marker));
        intake.Pass();

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void A_marker_its_next_event_could_not_remove_is_withdrawn_on_a_later_pass_and_blocks_answers_until_then()
    {
        // Only Windows lets a reader refuse a delete.
        if (!OperatingSystem.IsWindows()) { return; }
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var intake = Running();
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        intake.Pass();
        var marker = Path.Combine(_f.CodexRegistrations, "x1.approval.json");
        SpooledAt(_base.AddSeconds(5), "0002", "codex", "hook", new { session_id = "x1", hook_event_name = "PreToolUse", tool_name = "shell" }, 60);

        // Answered at the terminal; its PreToolUse arrives while a reader holds the marker.
        using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(1, intake.Pass());
            Assert.Equal("working", Registration("agent-bridge-codex", "x1").GetProperty("Status").GetString());
            Assert.True(File.Exists(marker));
            Assert.NotNull(intake.ApprovalsUnsettled);
            intake.Pass();
            Assert.NotNull(intake.ApprovalsUnsettled);
        }
        intake.Pass();

        Assert.False(File.Exists(marker));
        Assert.Null(intake.ApprovalsUnsettled);
    }

    [Fact]
    public void A_cut_off_a_sweep_could_not_finish_is_kept_when_the_log_becomes_readable_again()
    {
        // Only Windows lets a reader refuse a read or a delete.
        if (!OperatingSystem.IsWindows()) { return; }
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var intake = Running();
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        SpoolContract.Record(_f.Runtime, "claude", "stop", "spool", null, _base, 3);
        intake.Pass();
        var marker = Path.Combine(_f.CodexRegistrations, "x1.approval.json");

        using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            using (new FileStream(SpoolContract.LogPath(_f.Runtime), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                intake.Pass();
            }
            // The log says nothing unheard happened; the withdrawal asked for while it could not
            // be read still stands.
            intake.Pass();
            Assert.True(File.Exists(marker));
            Assert.NotNull(intake.ApprovalsUnsettled);
        }
        intake.Pass();

        Assert.False(File.Exists(marker));
        Assert.Null(intake.ApprovalsUnsettled);
    }

    [Fact]
    public void A_heartbeat_that_cannot_be_renewed_past_the_hooks_60_s_still_withdraws_approvals()
    {
        if (!OperatingSystem.IsWindows()) { return; }
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var intake = Running();
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        intake.Pass();
        var marker = Path.Combine(_f.CodexRegistrations, "x1.approval.json");

        using (new FileStream(SpoolContract.HeartbeatPath(_f.Runtime), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            _f.Time.Advance(SpoolContract.HeartbeatMaxAge);
            intake.Pass();
        }

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void A_Codex_event_dropped_after_its_retries_withdraws_every_approval_it_may_have_retired()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var intake = Running();
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        intake.Pass();
        var marker = Path.Combine(_f.CodexRegistrations, "x1.approval.json");
        File.WriteAllText(Path.Combine(Spool, "0002.json"), "{ not an event");

        intake.Pass();
        Assert.True(File.Exists(marker));
        intake.Pass();
        Assert.NotNull(intake.ApprovalsUnsettled);
        intake.Pass();

        Assert.False(File.Exists(marker));
    }

    [Fact]
    public void Retried_withdrawals_stop_at_the_next_marker_once_agent_ha_bridge_starts()
    {
        // Only Windows lets a reader refuse a delete.
        if (!OperatingSystem.IsWindows()) { return; }
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var intake = Running();
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        Spooled("0002", "codex", "hook", new { session_id = "x2", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        intake.Pass();
        string[] markers = [Path.Combine(_f.CodexRegistrations, "x1.approval.json"), Path.Combine(_f.CodexRegistrations, "x2.approval.json")];
        SpooledAt(_base.AddSeconds(1), "0003", "codex", "hook", new { session_id = "x1", hook_event_name = "PreToolUse", tool_name = "shell" }, 60);
        SpooledAt(_base.AddSeconds(2), "0004", "codex", "hook", new { session_id = "x2", hook_event_name = "PreToolUse", tool_name = "shell" }, 60);
        using (new FileStream(markers[0], FileMode.Open, FileAccess.Read, FileShare.Read))
        using (new FileStream(markers[1], FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            intake.Pass();
        }
        // Agent-ha-bridge starts between the two retries.
        var checks = 0;
        _probe = () => ++checks > 2 ? "the daemon started" : null;

        intake.Pass();

        Assert.Equal(1, markers.Count(File.Exists));
        Assert.Equal("the daemon started", intake.Paused);
    }

    [Fact]
    public void A_new_approval_is_not_offered_while_the_one_before_it_cannot_be_withdrawn()
    {
        // Only Windows lets a reader refuse a delete.
        if (!OperatingSystem.IsWindows()) { return; }
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var intake = Running();
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        intake.Pass();
        var marker = Path.Combine(_f.CodexRegistrations, "x1.approval.json");
        var first = File.ReadAllText(marker);
        SpooledAt(_base.AddSeconds(5), "0002", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "apply_patch" }, 60);
        var heard = intake.Subscribe();

        using (new FileStream(marker, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(1, intake.Pass());
            Assert.True(heard.TryRead(out var n));
            Assert.Null(n.ApprovalId);
            Assert.NotNull(intake.ApprovalsUnsettled);
        }
        intake.Pass();

        // The old one is gone and nothing replaced it: that prompt is answered at the terminal.
        Assert.False(File.Exists(marker));
        Assert.Null(intake.ApprovalsUnsettled);
        Assert.NotEqual("", first);
    }

    [Fact]
    public void A_host_stopping_part_way_through_a_backlog_leaves_the_rest_for_the_next_one()
    {
        Spooled("0001", "claude", "register", new { session_id = "s1", hook_event_name = "SessionStart" });
        var rest = Spooled("0002", "claude", "register", new { session_id = "s2", hook_event_name = "SessionStart" });
        using var stop = new CancellationTokenSource();
        var checks = 0;
        // The pass's own check, then the first file's: the host is told to stop there.
        _probe = () => { if (++checks == 2) { stop.Cancel(); } return null; };
        var intake = Intake();

        intake.Start(stop.Token);
        Assert.True(stop.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)));
        intake.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _intakes.Remove(intake);

        Assert.Equal(1, intake.Handled);
        Assert.True(File.Exists(rest));
    }

    [Fact]
    public void An_event_the_Go_hook_spooled_HTML_escaped_past_16_MiB_is_still_handled()
    {
        // json.Marshal writes each < as \u003c, after the hook's 16 MiB check.
        Directory.CreateDirectory(Spool);
        var escaped = string.Concat(Enumerable.Repeat("\\u003c", 3 << 20));
        var body = "{\"v\":1,\"agent\":\"claude\",\"hook\":\"register\",\"ancestors\":[],\"receivedAt\":\"" + _base.ToString("o")
            + "\",\"event\":{\"session_id\":\"s1\",\"hook_event_name\":\"SessionStart\",\"prompt\":\"" + escaped + "\"}}";
        File.WriteAllText(Path.Combine(Spool, "0001.json"), body);
        Assert.True(new FileInfo(Path.Combine(Spool, "0001.json")).Length > SpoolContract.MaxEventBytes + 64 * 1024);

        Assert.Equal(1, Intake().Pass());

        Assert.Equal("s1", Registration("agent-bridge-claude", "s1").GetProperty("SessionId").GetString());
    }

    [Fact]
    public void A_live_runtime_whose_command_line_could_not_be_read_keeps_its_registrations()
    {
        _f.CodexRegistration("unread", 61, null);
        _f.ClaudeRegistration("unread", 31, null);
        File.SetLastWriteTimeUtc(Path.Combine(_f.ClaudeRegistrations, "unread.json"), DateTime.UtcNow.AddDays(-1));
        _f.Time.Advance(TimeSpan.FromHours(5));
        // On Windows the list is complete with such a process in it.
        var intake = new HookIntake(new HookIntakeOptions { RuntimeRoot = _f.Runtime, ClaudeHome = _f.ClaudeHome, UseFileSystemWatcher = false, PruneInterval = TimeSpan.Zero }, new Unread(), _f.Time);
        _intakes.Add(intake);

        intake.Pass();

        Assert.True(File.Exists(Path.Combine(_f.CodexRegistrations, "unread.json")));
        Assert.True(File.Exists(Path.Combine(_f.ClaudeRegistrations, "unread.json")));
    }

    sealed class Unread : AgentBridge.Host.Core.Processes.IProcessInspector
    {
        public IReadOnlyList<AgentBridge.Host.Core.Processes.AgentProcess> GetAgentProcesses(AgentKind agent) => [];
        public AgentBridge.Host.Core.Processes.AgentProcessObservation Observe(AgentKind agent) =>
            new([], true) { Unidentified = [new AgentBridge.Host.Core.Processes.AgentProcess(agent == AgentKind.Codex ? 61 : 31, null, null)] };
    }

    [Fact]
    public void Hook_health_is_read_without_ever_refusing_a_hook_appending_to_the_log()
    {
        // Only Windows enforces the sharing a reader asks for.
        if (!OperatingSystem.IsWindows()) { return; }
        Directory.CreateDirectory(_f.Runtime);
        SpoolContract.Record(_f.Runtime, "codex", "hook", "reply", "no daemon heartbeat", _f.Time.GetLocalNow(), 3);
        // A hook part-way through appending its line, sharing as the hooks do.
        using var hook = new FileStream(SpoolContract.LogPath(_f.Runtime), FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);

        Assert.Equal(1, HookStats.Read(_f.Runtime, _f.Time.GetLocalNow(), TimeSpan.FromHours(1)).Total);
        Assert.NotNull(HookStats.LastUnspooled(_f.Runtime, "codex"));
    }

    [Fact]
    public void A_resumed_Codex_session_does_not_offer_the_approval_its_previous_run_died_waiting_on()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var intake = Running();
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        intake.Pass();
        Assert.True(File.Exists(Path.Combine(_f.CodexRegistrations, "x1.approval.json")));

        // That Codex exited without SessionEnd; the same session is resumed in a new one.
        _f.Processes.Run(AgentKind.Codex, 70, "codex.exe");
        SpooledAt(_base.AddSeconds(5), "0002", "codex", "hook", new { session_id = "x1", hook_event_name = "SessionStart" }, 70);
        intake.Pass();

        Assert.False(File.Exists(Path.Combine(_f.CodexRegistrations, "x1.approval.json")));
        Assert.Equal(70, Registration("agent-bridge-codex", "x1").GetProperty("ProcessId").GetInt32());
    }

    [Fact]
    public void An_event_handled_but_not_deleted_is_never_handled_again_while_its_file_lingers()
    {
        // Only Windows lets a reader refuse a delete.
        if (!OperatingSystem.IsWindows()) { return; }
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        var asked = Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        Spooled("0002", "codex", "hook", new { session_id = "x1", hook_event_name = "PreToolUse", tool_name = "shell" }, 60);
        var intake = Intake();

        using (new FileStream(asked, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Equal(2, intake.Pass());
            Assert.True(File.Exists(asked));
        }
        Assert.Equal(0, intake.Pass());

        Assert.False(File.Exists(asked));
        Assert.False(File.Exists(Path.Combine(_f.CodexRegistrations, "x1.approval.json")));
        Assert.Equal(2, intake.Handled);
    }

    [Fact]
    public void Codex_hooks_under_its_shared_app_server_are_owned_by_the_window_above_it()
    {
        _f.Processes.Run(AgentKind.Codex, 50, @"C:\Users\u\.codex\packages\app-server-daemon\codex.exe app-server");
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        Spooled("0001", "codex", "hook", new { session_id = "s", hook_event_name = "SessionStart" }, 10, 20, 50, 60);
        Intake().Pass();
        Assert.Equal(60, Registration("agent-bridge-codex", "s").GetProperty("ProcessId").GetInt32());
    }

    [Fact]
    public void A_Codex_window_another_live_session_has_claimed_is_not_taken_from_under_the_shared_app_server()
    {
        _f.Processes.Run(AgentKind.Codex, 50, "codex app-server");
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe", _base.AddMinutes(-30));
        _f.Processes.Run(AgentKind.Codex, 70, "codex.exe", _base.AddMinutes(-5));
        _f.CodexRegistration("other", 60, null);
        Spooled("0001", "codex", "hook", new { session_id = "mine", hook_event_name = "SessionStart" }, 50, 60);
        Intake().Pass();
        // The newest window no other session has claimed.
        Assert.Equal(70, Registration("agent-bridge-codex", "mine").GetProperty("ProcessId").GetInt32());
    }

    [Fact]
    public void Codex_keeps_what_SessionStart_said_marks_the_registration_ended_at_SessionEnd_and_ignores_events_it_does_not_know()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        Spooled("0001", "codex", "hook", new { session_id = "s", hook_event_name = "SessionStart", cwd = "/work/codex", model = "gpt-5", transcript_path = "/t.jsonl" }, 60);
        Spooled("0002", "codex", "hook", new { session_id = "s", hook_event_name = "Stop", last_assistant_message = "\n\nAll done.\nMore." });
        Intake().Pass();
        var r = Registration("agent-bridge-codex", "s");
        Assert.Equal(("idle", "All done.", "gpt-5", "/work/codex", 60), (r.GetProperty("Status").GetString(), r.GetProperty("Activity").GetString(), r.GetProperty("Model").GetString(), r.GetProperty("WorkingDirectory").GetString(), r.GetProperty("ProcessId").GetInt32()));

        var before = File.ReadAllText(Path.Combine(_f.CodexRegistrations, "s.json"));
        Spooled("0003", "codex", "hook", new { session_id = "s", hook_event_name = "SubagentStart" });
        Intake().Pass();
        Assert.Equal(before, File.ReadAllText(Path.Combine(_f.CodexRegistrations, "s.json")));

        Spooled("0004", "codex", "hook", new { session_id = "s", hook_event_name = "SessionEnd" });
        Intake().Pass();
        r = Registration("agent-bridge-codex", "s");
        Assert.True(r.GetProperty("Ended").GetBoolean());
        Assert.Equal("ended", r.GetProperty("Status").GetString());
    }

    // ---------------------------------------------------------------- Copilot

    [Fact]
    public void Copilot_hook_events_are_heard_but_write_nothing_since_its_sessions_are_read_from_its_own_folder()
    {
        Spooled("0001", "copilot", "ask_user", new { toolName = "ask_user", toolArgs = "{\"question\":\"Which?\"}" });
        Spooled("0002", "copilot", "agent_stop", new { });
        var intake = Intake();
        var heard = intake.Subscribe();
        var before = _f.Listing().Keys.Where(k => !k.Contains(SpoolContract.SpoolFolder)).ToList();
        Assert.Equal(2, intake.Pass());
        Assert.True(heard.TryRead(out var ask));
        Assert.Equal("copilot/ask_user", ask.Key());
        Assert.Equal(before.Append(Path.Combine("home", ".agent-ha-bridge", "runtime", SpoolContract.HeartbeatFile)).Order(), _f.Listing().Keys.Where(k => !k.Contains(SpoolContract.SpoolFolder)).Order());
    }

    // ---------------------------------------------------------------- upkeep

    [Fact]
    public void Registrations_for_sessions_that_are_over_are_pruned_as_the_daemon_did_and_only_on_a_complete_process_list()
    {
        _f.Processes.Run(AgentKind.Claude, 30);
        _f.Processes.Run(AgentKind.Codex, 60);
        _f.ClaudeRegistration("live", 30, null);
        _f.ClaudeRegistration("dead-old", 31, null);
        _f.Time.Advance(TimeSpan.FromHours(5));
        _f.ClaudeRegistration("dead-recent", 32, null);
        File.SetLastWriteTimeUtc(Path.Combine(_f.ClaudeRegistrations, "dead-recent.json"), DateTime.UtcNow);
        var recent = JsonSerializer.Serialize(new { SessionId = "dead-recent", ProcessId = 32, Updated = _f.Time.GetUtcNow().ToString("o") });
        File.WriteAllText(Path.Combine(_f.ClaudeRegistrations, "dead-recent.json"), recent);
        _f.CodexRegistration("ended", 60, null, ended: true);
        _f.CodexRegistration("gone", 61, null);
        _f.CodexRegistration("running", 60, null);

        var intake = new HookIntake(new HookIntakeOptions { RuntimeRoot = _f.Runtime, ClaudeHome = _f.ClaudeHome, UseFileSystemWatcher = false }, _f.Processes, _f.Time);
        _intakes.Add(intake);
        intake.Pass();
        // An approval asked since the host took the spool.
        _f.CodexApprovalMarker("running", "d1", created: _f.Time.GetUtcNow().AddSeconds(1));
        // Not before a minute has passed since the host started.
        Assert.Equal(3, Directory.GetFiles(_f.ClaudeRegistrations).Length);
        for (var i = 0; i < 3; i++)
        {
            _f.Time.Advance(TimeSpan.FromSeconds(45));
            intake.Pass();
        }
        Assert.Equal(["dead-recent.json", "live.json"], Directory.GetFiles(_f.ClaudeRegistrations).Select(Path.GetFileName).Order());
        Assert.Equal(["running.approval.json", "running.json"], Directory.GetFiles(_f.CodexRegistrations).Select(Path.GetFileName).Order());

        // A host that cannot see every process (the first has stopped, so this one takes the spool).
        intake.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _intakes.Remove(intake);
        var blind = new HookIntake(new HookIntakeOptions { RuntimeRoot = _f.Runtime, ClaudeHome = _f.ClaudeHome, UseFileSystemWatcher = false }, new Blind(), _f.Time);
        _intakes.Add(blind);
        _f.ClaudeRegistration("dead-old", 31, null);
        _f.Time.Advance(TimeSpan.FromMinutes(2));
        blind.Pass();
        Assert.Null(blind.Paused);
        Assert.True(File.Exists(Path.Combine(_f.ClaudeRegistrations, "dead-old.json")));
    }

    sealed class Blind : AgentBridge.Host.Core.Processes.IProcessInspector
    {
        public IReadOnlyList<AgentBridge.Host.Core.Processes.AgentProcess> GetAgentProcesses(AgentKind agent) => [];
        public AgentBridge.Host.Core.Processes.AgentProcessObservation Observe(AgentKind agent) => new([], false);
    }

    [Fact]
    public void Only_a_profile_with_a_readable_intake_record_takes_the_spool()
    {
        var profile = Path.Combine(_f.Root, "profile");
        Directory.CreateDirectory(profile);
        Assert.Equal((null, null), IntakeState.Load(profile));

        File.WriteAllText(Path.Combine(profile, IntakeState.FileName), "{ broken");
        var (state, problem) = IntakeState.Load(profile);
        Assert.Null(state);
        Assert.Contains("hook intake is off", problem);

        var saved = new IntakeState(_f.Runtime, Path.GetDirectoryName(_f.Runtime)!, new string('a', 32), _base);
        saved.Save(profile);
        Assert.Equal(saved, IntakeState.Load(profile).State);
        Assert.True(IntakeState.Delete(profile));
        Assert.Null(IntakeState.Load(profile).State);
    }

    [Fact]
    public void Hook_health_counts_each_path_and_folds_the_numbers_out_of_the_reasons()
    {
        var log = SpoolContract.LogPath(_f.Runtime);
        Directory.CreateDirectory(_f.Runtime);
        var now = DateTimeOffset.Now;
        var lines = new[]
        {
            Line(now.AddMinutes(-1), "spool", null, 12),
            Line(now.AddMinutes(-2), "spool", null, 20),
            Line(now.AddMinutes(-3), "reply", "daemon heartbeat 75s old", 15),
            Line(now.AddMinutes(-4), "reply", "daemon heartbeat 300s old", 16),
            Line(now.AddMinutes(-5), "fallback", "no daemon heartbeat", 420),
            Line(now.AddDays(-2), "spool", null, 1),
        };
        File.WriteAllLines(log, lines);
        var stats = HookStats.Read(_f.Runtime, now, TimeSpan.FromHours(24));
        Assert.Equal((5, 2, 1, 2, 3), (stats.Total, stats.Spooled, stats.Fallback, stats.ReplyOnly, stats.NotSpooled));
        Assert.Equal(2, stats.ByReason["daemon heartbeat Ns old"]);
        Assert.Equal(1, stats.ByReason["no daemon heartbeat"]);
        Assert.Equal(12, stats.SpoolMedianMs is { } m ? Math.Min(m, 12) : -1);
        Assert.StartsWith("5 runs in the last 24 h, 3 not spooled (60%): daemon heartbeat Ns old x2, no daemon heartbeat x1", stats.Describe(TimeSpan.FromHours(24)));

        static string Line(DateTimeOffset at, string path, string? reason, int ms) =>
            JsonSerializer.Serialize(new Dictionary<string, object?> { ["at"] = at.ToString("o"), ["agent"] = "claude", ["hook"] = "stop", ["path"] = path, ["reason"] = reason, ["ms"] = ms });
    }
}

static class NoticeKeys
{
    public static string Key(this HookNotice n) => n.Agent + "/" + n.Hook;
}
