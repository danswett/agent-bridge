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
            AgentHaBridgeActive = () => _agentHaBridge,
            UseFileSystemWatcher = watch,
            PruneInterval = TimeSpan.FromDays(1),
        }, _f.Processes, _f.Time, m => { lock (_log) { _log.Add(m); } });
        _intakes.Add(intake);
        return intake;
    }

    // A spool file as either bridge's hook writes it, under a name that sorts where given.
    string Spooled(string name, string agent, string hook, object ev, params int[] ancestors) => SpooledAt(Fixture.T0, name, agent, hook, ev, ancestors);

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
        Assert.Equal(Fixture.T0, At(Registration("agent-bridge-claude", "s1"), "HookStatusAt"));
        Assert.Equal(Fixture.T0, At(Registration("agent-bridge-claude", "s1"), "Updated"));
        Assert.Equal(Fixture.T0, At(Registration("agent-bridge-codex", "x1"), "Updated"));
        using var marker = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(_f.CodexRegistrations, "x1.approval.json")));
        Assert.Equal(Fixture.T0, At(marker.RootElement, "Created"));
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
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell", tool_input = new { command = "git push" }, cwd = "/work/codex", model = "gpt-5" }, 50, 60);
        var intake = Intake();
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
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        var first = Intake();
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
    public void An_approval_asked_after_the_last_unheard_Codex_hook_is_still_pending_when_the_host_returns()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        // Before the approval was asked (as its hook recorded), though handled after.
        SpoolContract.Record(_f.Runtime, "codex", "hook", "reply", "no daemon heartbeat", Fixture.T0.AddSeconds(-5), 3);
        SpoolContract.Record(_f.Runtime, "claude", "stop", "reply", "no daemon heartbeat", _f.Time.GetLocalNow().AddSeconds(5), 3);
        Assert.NotNull(HookStats.LastUnspooled(_f.Runtime, "codex"));

        Intake().Pass();
        Intake().Pass();

        Assert.True(File.Exists(Path.Combine(_f.CodexRegistrations, "x1.approval.json")));
    }

    [Fact]
    public void An_approval_still_waiting_in_the_spool_from_before_an_outage_arms_nothing()
    {
        // The host died with its heartbeat fresh, so the PermissionRequest was spooled; then the
        // heartbeat went stale and the answer at the terminal reached nobody.
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        SpoolContract.Record(_f.Runtime, "codex", "hook", "reply", "no daemon heartbeat", Fixture.T0.AddSeconds(5), 3);
        var intake = Intake();
        var heard = intake.Subscribe();

        Assert.Equal(1, intake.Pass());

        Assert.False(File.Exists(Path.Combine(_f.CodexRegistrations, "x1.approval.json")));
        Assert.True(heard.TryRead(out var n));
        Assert.Null(n.ApprovalId);

        // A request asked after it arms as usual.
        File.WriteAllText(Path.Combine(Spool, "0002.json"), JsonSerializer.Serialize(new
        {
            v = 1, agent = "codex", hook = "hook", ancestors = new[] { 60 }, receivedAt = Fixture.T0.AddSeconds(30).ToString("o"),
            @event = new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" },
        }));
        Assert.Equal(1, intake.Pass());
        Assert.True(File.Exists(Path.Combine(_f.CodexRegistrations, "x1.approval.json")));
    }

    [Fact]
    public void Two_approvals_handled_in_the_same_millisecond_get_different_decision_ids()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        Spooled("0002", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "apply_patch" }, 60);
        var intake = Intake();
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
        SpooledAt(Fixture.T0, "0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        SpooledAt(Fixture.T0.AddSeconds(10), "0002", "codex", "hook", new { session_id = "x1", hook_event_name = "PreToolUse", tool_name = "shell" }, 60);
        Intake().Pass();
        SpooledAt(Fixture.T0, "0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);

        Assert.Equal(1, Intake().Pass());

        Assert.False(File.Exists(Path.Combine(_f.CodexRegistrations, "x1.approval.json")));
        Assert.Equal("working", Registration("agent-bridge-codex", "x1").GetProperty("Status").GetString());
    }

    [Fact]
    public void An_approval_whose_answering_hook_failed_to_spool_is_withdrawn_on_the_next_pass()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        var intake = Intake();
        intake.Pass();
        var marker = Path.Combine(_f.CodexRegistrations, "x1.approval.json");
        Assert.True(File.Exists(marker));

        // The heartbeat is fresh, but the PreToolUse could not be spooled.
        SpoolContract.Record(_f.Runtime, "codex", "hook", "reply", "spool not writable", Fixture.T0.AddSeconds(5), 3);
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
        SpoolContract.Record(_f.Runtime, "codex", "hook", "spool", null, Fixture.T0.AddSeconds(-5), 3);
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
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        Intake().Pass();
        var marker = Path.Combine(_f.CodexRegistrations, "x1.approval.json");
        SpoolContract.Record(_f.Runtime, "codex", "hook", "reply", "no daemon heartbeat", Fixture.T0.AddSeconds(5), 3);
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
        File.WriteAllText(Path.Combine(Spool, "0001.json"), "{\"v\":1,\"agent\":\"copilot\",\"hook\":\"agent_stop\",\"ancestors\":[],\"receivedAt\":\"" + Fixture.T0.ToString("o") + "\",\"event\":" + deep + "}");
        Assert.True(SpoolContract.IsJson(System.Text.Encoding.UTF8.GetBytes(deep)));
        var intake = Intake();

        Assert.Equal(1, intake.Pass());
        Assert.Equal(0, intake.Dropped);
    }

    [Fact]
    public void A_resumed_Codex_session_does_not_offer_the_approval_its_previous_run_died_waiting_on()
    {
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe");
        Spooled("0001", "codex", "hook", new { session_id = "x1", hook_event_name = "PermissionRequest", tool_name = "shell" }, 60);
        Intake().Pass();
        Assert.True(File.Exists(Path.Combine(_f.CodexRegistrations, "x1.approval.json")));

        // That Codex exited without SessionEnd; the same session is resumed in a new one.
        _f.Processes.Run(AgentKind.Codex, 70, "codex.exe");
        Spooled("0002", "codex", "hook", new { session_id = "x1", hook_event_name = "SessionStart" }, 70);
        Intake().Pass();

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
        _f.Processes.Run(AgentKind.Codex, 60, "codex.exe", Fixture.T0.AddMinutes(-30));
        _f.Processes.Run(AgentKind.Codex, 70, "codex.exe", Fixture.T0.AddMinutes(-5));
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
        _f.CodexApprovalMarker("running", "d1");

        var intake = new HookIntake(new HookIntakeOptions { RuntimeRoot = _f.Runtime, ClaudeHome = _f.ClaudeHome, UseFileSystemWatcher = false }, _f.Processes, _f.Time);
        _intakes.Add(intake);
        intake.Pass();
        // Not before a minute has passed since the host started.
        Assert.Equal(3, Directory.GetFiles(_f.ClaudeRegistrations).Length);
        _f.Time.Advance(TimeSpan.FromMinutes(2));
        intake.Pass();
        Assert.Equal(["dead-recent.json", "live.json"], Directory.GetFiles(_f.ClaudeRegistrations).Select(Path.GetFileName).Order());
        Assert.Equal(["running.approval.json", "running.json"], Directory.GetFiles(_f.CodexRegistrations).Select(Path.GetFileName).Order());

        var blind = new HookIntake(new HookIntakeOptions { RuntimeRoot = _f.Runtime, ClaudeHome = _f.ClaudeHome, UseFileSystemWatcher = false }, new Blind(), _f.Time);
        _intakes.Add(blind);
        _f.ClaudeRegistration("dead-old", 31, null);
        _f.Time.Advance(TimeSpan.FromMinutes(2));
        blind.Pass();
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

        var saved = new IntakeState(_f.Runtime, Path.GetDirectoryName(_f.Runtime)!, new string('a', 32), Fixture.T0);
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
