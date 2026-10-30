using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Protocol;
using Xunit;

// §6.3 invariant 6: a discovery pass that could not account for everything holds whatever would
// be decided from a session's absence, and never reads as "every session ended". The cases are
// agent-ha-bridge's tests/test-daemon-discovery.ps1 and test-resume-discovery.ps1.
public class DiscoveryHoldTests
{
    const string A = "11111111-1111-4111-8111-111111111111";
    const string B = "22222222-2222-4222-8222-222222222222";
    const string C = "33333333-3333-4333-8333-333333333333";

    [Fact]
    public async Task A_process_list_that_cannot_be_read_in_full_is_not_read_as_no_sessions()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(A, lockPid: 100);
        Fixture.Append(events, Copilot.User("hello", 1));
        f.Processes.Run(AgentKind.Copilot, 100);
        var log = new List<string>();
        await using var source = f.Source(log: log.Add);
        await source.RefreshAsync();
        Assert.Null(source.Held);

        // Denied: the list came back without the process.
        f.Processes.Unreadable(AgentKind.Copilot);
        f.Processes.Exit(AgentKind.Copilot, 100);
        await source.RefreshAsync();

        Assert.Equal(A, Assert.Single(source.Snapshot()).Id);
        Assert.Equal(new DiscoveryHeld("the copilot processes could not be listed", [AgentKind.Copilot]), source.Held, HeldComparer.Instance);
        Assert.Contains(log, l => l.StartsWith("session discovery held (the copilot processes could not be listed)", StringComparison.Ordinal));

        // A complete pass that finds it gone retires it, and says the hold is over.
        f.Processes.Unreadable(AgentKind.Copilot, false);
        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
        Assert.Null(source.Held);
        Assert.Contains("session discovery complete again", log);
    }

    [Fact]
    public async Task A_session_kept_by_a_hold_goes_on_being_read()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(A, lockPid: 100);
        Fixture.Append(events, Copilot.User("hello", 1));
        f.Processes.Run(AgentKind.Copilot, 100);
        await using var source = f.Source();
        await source.RefreshAsync();
        f.Processes.Unreadable(AgentKind.Copilot);
        f.Processes.Exit(AgentKind.Copilot, 100);
        await source.RefreshAsync();

        Fixture.Append(events, Copilot.User("still here", 2));
        await source.RefreshAsync();

        Assert.Equal("still here", source.Get(A)!.Activity.UserMessage);
    }

    [Fact]
    public async Task A_hold_on_one_agent_does_not_keep_another_agents_ended_sessions()
    {
        using var f = new Fixture();
        f.CopilotSession(A, lockPid: 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        var transcript = f.ClaudeTranscript(B);
        Fixture.Append(transcript, Claude.User("hi", 1));
        f.ClaudeRegistration(B, 500, transcript);
        f.Processes.Run(AgentKind.Claude, 500);
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Equal(2, source.Snapshot().Count);

        f.Processes.Unreadable(AgentKind.Copilot);
        f.Processes.Exit(AgentKind.Copilot, 100);
        f.Processes.Exit(AgentKind.Claude, 500);
        await source.RefreshAsync();

        Assert.Equal(A, Assert.Single(source.Snapshot()).Id);
        Assert.Equal([AgentKind.Copilot], source.Held!.Agents);
    }

    [Fact]
    public async Task A_runtime_whose_command_line_could_not_be_read_this_time_keeps_the_session_it_was_running()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(A, lockPid: null);
        Fixture.Append(events, Copilot.User("resumed", 1));
        f.Processes.Run(AgentKind.Copilot, 300, $"node C:/npm/node_modules/@github/copilot/index.js --session-id {A}");
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());

        // node.exe is still there; what it is running could not be read on this pass.
        f.Processes.Unidentify(AgentKind.Copilot, 300);
        await source.RefreshAsync();

        Assert.Equal(A, Assert.Single(source.Snapshot()).Id);
        Assert.Equal("a copilot process that could not be read (pid 300)", source.Held!.Reason);
    }

    [Fact]
    public async Task A_copilot_lock_held_by_a_runtime_never_read_holds_from_the_first_pass()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(A, lockPid: 300);
        Fixture.Append(events, Copilot.User("hi", 1));
        f.Processes.Run(AgentKind.Copilot, 300, "node index.js");
        f.Processes.Unidentify(AgentKind.Copilot, 300);
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Equal("a copilot process that could not be read (pid 300)", source.Held?.Reason);
    }

    [Fact]
    public async Task A_registration_naming_a_runtime_never_read_holds_from_the_first_pass_unless_it_ended_or_is_older_than_it()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(B);
        Fixture.Append(transcript, Claude.User("hi", 1));
        f.ClaudeRegistration(B, 500, transcript);
        f.Processes.Run(AgentKind.Claude, 500, "node cli.js");
        f.Processes.Unidentify(AgentKind.Claude, 500);
        await using var source = f.Source();

        await source.RefreshAsync();
        Assert.Equal("a claude process that could not be read (pid 500)", source.Held?.Reason);

        // Its pid since taken by a runtime started after the registration was last written.
        f.Processes.Unidentify(AgentKind.Claude, 500, Fixture.T0.AddMinutes(1));
        await source.RefreshAsync();
        Assert.Null(source.Held);
    }

    [Fact]
    public async Task A_session_whose_pid_an_unreadable_runtime_has_since_reused_ends_and_holds_nothing()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(A, lockPid: null);
        Fixture.Append(events, Copilot.User("resumed", 1));
        f.Processes.Run(AgentKind.Copilot, 300, $"node C:/npm/node_modules/@github/copilot/index.js --session-id {A}");
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());

        // The session's node exited and another node, started later, now has pid 300.
        f.Processes.Unidentify(AgentKind.Copilot, 300, Fixture.T0);
        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
        Assert.Null(source.Held);
    }

    [Fact]
    public async Task Every_other_users_unreadable_runtime_holds_nothing()
    {
        using var f = new Fixture();
        f.Processes.Run(AgentKind.Copilot, 300, "node somebody-elses-service.js");
        f.Processes.Unidentify(AgentKind.Copilot, 300);
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Null(source.Held);
    }

    [Fact]
    public async Task A_flickering_command_line_does_not_drop_a_resumed_session()
    {
        using var f = new Fixture();
        var events = f.CopilotSession(A, lockPid: null);
        Fixture.Append(events, Copilot.User("resumed", 1));
        var started = Fixture.T0.AddMinutes(-5);
        f.Processes.Run(AgentKind.Copilot, 300, $"copilot --resume --session-id {A}", started);
        await using var source = f.Source();
        await source.RefreshAsync();

        // The same process, its command line unreadable on this pass: nothing now says which
        // session it is in, and it has no lock to fall back on.
        f.Processes.Run(AgentKind.Copilot, 300, null, started);
        await source.RefreshAsync();
        Assert.Equal(A, Assert.Single(source.Snapshot()).Id);
        Assert.Equal("an unrecognised copilot process (pid 300)", source.Held!.Reason);

        f.Processes.Run(AgentKind.Copilot, 300, $"copilot --resume --session-id {A}", started);
        await source.RefreshAsync();
        Assert.Equal(A, Assert.Single(source.Snapshot()).Id);
        Assert.Null(source.Held);
    }

    [Fact]
    public async Task A_registration_changed_during_the_read_is_not_believed_and_holds_until_it_reads_whole()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(B);
        Fixture.Append(transcript, Claude.User("hi", 1));
        f.ClaudeRegistration(B, 500, transcript);
        f.Processes.Run(AgentKind.Claude, 500);
        var rewrite = false;
        await using var source = f.Source(afterRegistrationRead: path =>
        {
            // The hook rewrites it between the read and the check.
            if (rewrite) { File.AppendAllText(path, " "); }
        });
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());

        rewrite = true;
        await source.RefreshAsync();
        Assert.Equal(B, Assert.Single(source.Snapshot()).Id);
        // And so its process is one nothing accounts for.
        Assert.Equal("a claude registration changed while it was read, an unrecognised claude process (pid 500)", source.Held!.Reason);
        Assert.Equal([AgentKind.Claude], source.Held.Agents);

        rewrite = false;
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());
        Assert.Null(source.Held);
    }

    [Fact]
    public async Task A_registration_that_cannot_be_parsed_keeps_its_session_rather_than_retiring_it()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(B);
        Fixture.Append(transcript, Claude.User("hi", 1));
        f.ClaudeRegistration(B, 500, transcript);
        f.Processes.Run(AgentKind.Claude, 500);
        await using var source = f.Source();
        await source.RefreshAsync();

        // Caught half written.
        File.WriteAllText(Path.Combine(f.ClaudeRegistrations, B + ".json"), "{\"SessionId\": \"" + B);
        await source.RefreshAsync();

        Assert.Equal(B, Assert.Single(source.Snapshot()).Id);
        Assert.Equal("a claude registration could not be read, an unrecognised claude process (pid 500)", source.Held!.Reason);
    }

    [Fact]
    public async Task A_process_that_has_only_just_appeared_holds_and_once_silent_long_enough_is_carried_on_without_saying_so_once()
    {
        using var f = new Fixture();
        f.CopilotSession(A, lockPid: 100);
        f.Processes.Run(AgentKind.Copilot, 100);
        var log = new List<string>();
        await using var source = f.Source(log: log.Add);
        await source.RefreshAsync();

        // Something nobody's session accounts for, and A's process gone.
        f.Processes.Run(AgentKind.Copilot, 200, "copilot");
        f.Processes.Exit(AgentKind.Copilot, 100);
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());
        Assert.Equal("an unrecognised copilot process (pid 200)", source.Held!.Reason);

        f.Time.Advance(TimeSpan.FromSeconds(119));
        await source.RefreshAsync();
        Assert.NotNull(source.Held);

        f.Time.Advance(TimeSpan.FromSeconds(1));
        await source.RefreshAsync();
        Assert.Null(source.Held);
        Assert.Empty(source.Snapshot());
        f.Time.Advance(TimeSpan.FromMinutes(1));
        await source.RefreshAsync();
        Assert.Single(log, l => l == "carrying on without copilot process 200: unaccounted for 2 min, so it is not a session that is still starting up");
    }

    [Fact]
    public async Task A_process_that_vanished_for_one_pass_keeps_the_clock_it_had_already_run()
    {
        using var f = new Fixture();
        f.Processes.Run(AgentKind.Copilot, 200, "copilot");
        await using var source = f.Source();
        await source.RefreshAsync();
        f.Time.Advance(TimeSpan.FromSeconds(90));

        f.Processes.Exit(AgentKind.Copilot, 200);
        await source.RefreshAsync();
        f.Processes.Run(AgentKind.Copilot, 200, "copilot");
        f.Time.Advance(TimeSpan.FromSeconds(30));
        await source.RefreshAsync();

        Assert.Null(source.Held);
    }

    [Fact]
    public async Task Registering_clears_the_clock_so_losing_that_registration_earns_a_fresh_grace()
    {
        using var f = new Fixture();
        f.Processes.Run(AgentKind.Copilot, 200, "copilot");
        await using var source = f.Source();
        await source.RefreshAsync();
        f.Time.Advance(TimeSpan.FromSeconds(90));

        f.CopilotSession(C, lockPid: 200);
        await source.RefreshAsync();
        Assert.Null(source.Held);

        File.Delete(Path.Combine(f.CopilotHome, "session-state", C, "inuse.200.lock"));
        f.Time.Advance(TimeSpan.FromSeconds(60));
        await source.RefreshAsync();

        Assert.NotNull(source.Held);
    }

    [Fact]
    public async Task A_reused_pid_starts_its_own_clock_rather_than_inheriting_the_silence()
    {
        using var f = new Fixture();
        f.Processes.Run(AgentKind.Copilot, 200, "copilot", Fixture.T0);
        await using var source = f.Source();
        await source.RefreshAsync();
        f.Time.Advance(TimeSpan.FromMinutes(3));
        await source.RefreshAsync();
        Assert.Null(source.Held);

        // A new process, given the same pid.
        f.Processes.Run(AgentKind.Copilot, 200, "copilot", Fixture.T0.AddHours(2));
        await source.RefreshAsync();

        Assert.Equal("an unrecognised copilot process (pid 200)", source.Held!.Reason);
    }

    [Fact]
    public async Task A_process_no_route_can_date_is_never_aged_out_and_says_why_once()
    {
        using var f = new Fixture();
        f.Processes.RunUndated(AgentKind.Copilot, 200, "copilot");
        var log = new List<string>();
        await using var source = f.Source(log: log.Add);
        await source.RefreshAsync();

        f.Time.Advance(TimeSpan.FromHours(1));
        await source.RefreshAsync();

        Assert.NotNull(source.Held);
        Assert.Single(log, l => l.StartsWith("holding for copilot process 200: it has no readable start time", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_embedded_headless_CLI_holds_nothing_but_the_word_in_a_quoted_prompt_is_not_the_option()
    {
        using var f = new Fixture();
        f.Processes.Run(AgentKind.Copilot, 200, "copilot.exe --headless --stdio");
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Null(source.Held);

        f.Processes.Run(AgentKind.Copilot, 201, "copilot -i \"why does --headless hold discovery?\"");
        await source.RefreshAsync();
        Assert.Equal("an unrecognised copilot process (pid 201)", source.Held!.Reason);
    }

    [Theory]
    [InlineData("copilot.exe --headless --stdio", true)]
    [InlineData("node C:/npm/node_modules/@github/copilot/index.js --log-level info --headless --stdio", true)]
    [InlineData("copilot --headless --stdio --session-id 11111111-1111-4111-8111-111111111111", false)]
    // macOS's ps does not quote, so a prompt's words arrive as arguments of their own.
    [InlineData("copilot -i Explain --headless mode", false)]
    [InlineData("copilot --prompt=Explain --headless", false)]
    // A prompt with escaped quotes in it is still one argument.
    [InlineData("copilot -p \"a \\\" --headless \\\" b\"", false)]
    [InlineData("copilot -- --headless", false)]
    public void Scouts_embedded_CLI_is_told_by_its_own_headless_argument_not_a_prompts_words(string commandLine, bool embedded)
    {
        Assert.Equal(embedded, CommandLine.IsEmbedded(commandLine));
    }

    [Fact]
    public async Task A_hold_on_one_agent_is_said_at_once_even_while_another_agents_clears()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.CodexRegistrations);
        f.Processes.Run(AgentKind.Copilot, 200, "copilot");
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Equal([AgentKind.Copilot], source.Held!.Agents);
        using var subscription = source.Subscribe();
        Assert.True(subscription.Events.TryRead(out _));

        f.Processes.Exit(AgentKind.Copilot, 200);
        f.Processes.Run(AgentKind.Codex, 960, "codex");
        await source.RefreshAsync();

        // Codex is held before the pass lists anything, and Copilot only lets go after.
        var said = new List<DiscoveryHeld?>();
        while (subscription.Events.TryRead(out var e)) { if (e.Data is DiscoveryChanged c) { said.Add(c.DiscoveryHeld); } }
        Assert.Equal([[AgentKind.Copilot, AgentKind.Codex], [AgentKind.Codex]], said.Select(h => h!.Agents.ToArray()));
        Assert.Equal("an unrecognised codex process (pid 960)", said[0]!.Reason);
    }

    [Theory]
    [InlineData("codex.exe app-server --listen stdio", true)]
    [InlineData("node C:/npm/node_modules/@openai/codex/bin/codex.js -c model=o3 app-server", true)]
    [InlineData("/usr/local/bin/codex --profile work app-server", true)]
    [InlineData(@"C:\Users\me\.codex\packages\app-server-daemon\codex.exe --listen ws://127.0.0.1:0", true)]
    [InlineData("codex.exe \"Explain the app-server protocol\"", false)]
    [InlineData("codex Explain the app-server protocol", false)]
    [InlineData("codex --model app-server", false)]
    [InlineData("codex resume 0199a213-81c0-7800-8aa1-bbab2a035a53", false)]
    [InlineData("codex.exe --no-daemon -c model_reasoning_summary=detailed -- app-server", false)]
    [InlineData("codex.exe -- \"Explain /app-server-daemon/ in this repo\"", false)]
    public void Codexs_app_server_is_the_subcommand_not_the_word_in_a_prompt(string commandLine, bool appServer)
    {
        Assert.Equal(appServer, CommandLine.IsCodexAppServer(commandLine));
    }

    [Fact]
    public async Task A_codex_session_whose_prompt_mentions_the_app_server_holds_until_it_registers()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.CodexRegistrations);
        f.Processes.Run(AgentKind.Codex, 601, "codex.exe \"Explain the app-server protocol\"");
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Equal("an unrecognised codex process (pid 601)", source.Held?.Reason);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_process_that_took_an_old_registrations_pid_is_not_accounted_for_by_it(bool ended)
    {
        using var f = new Fixture();
        f.CodexRegistration(C, 700, f.CodexTranscript(C), ended: ended, updated: Fixture.T0.AddHours(-3));
        f.Processes.Run(AgentKind.Codex, 700, "codex.exe", Fixture.T0.AddMinutes(-1));
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
        Assert.Equal("an unrecognised codex process (pid 700)", source.Held?.Reason);
    }

    [Fact]
    public async Task Codexs_shared_app_server_is_nobodys_session_and_holds_nothing()
    {
        using var f = new Fixture();
        Directory.CreateDirectory(f.CodexRegistrations);
        f.Processes.Run(AgentKind.Codex, 600, "codex.exe app-server --listen stdio");
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Null(source.Held);
    }

    [Fact]
    public async Task A_session_a_hold_kept_unseen_is_listed_before_the_hold_is_said_to_clear()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(B);
        Fixture.Append(transcript, Claude.User("hi", 1));
        Directory.CreateDirectory(f.ClaudeRegistrations);
        // Caught half written from the start, so its session was never listed.
        File.WriteAllText(Path.Combine(f.ClaudeRegistrations, B + ".json"), "{\"SessionId\": \"" + B);
        f.Processes.Run(AgentKind.Claude, 500);
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Empty(source.Snapshot());
        using var subscription = source.Subscribe();
        Assert.True(subscription.Events.TryRead(out _));

        f.ClaudeRegistration(B, 500, transcript);
        await source.RefreshAsync();

        // Never "not held" and "not listed" at once: a resume in between would start a second CLI.
        var said = new List<SessionEvent>();
        while (subscription.Events.TryRead(out var e)) { said.Add(e); }
        Assert.Equal([Events.SessionUpsert, Events.DiscoveryChanged], said.Select(e => e.Name));
        Assert.Null(((DiscoveryChanged)said[1].Data).DiscoveryHeld);
        Assert.Equal(B, Assert.Single(source.Snapshot()).Id);
    }

    [Fact]
    public async Task A_linked_copilot_session_folder_holds_rather_than_being_read_or_ignored()
    {
        using var f = new Fixture();
        f.CopilotSession(A, lockPid: 100);
        f.Processes.Run(AgentKind.Copilot, 100, "copilot");
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Null(source.Held);

        var elsewhere = Directory.CreateDirectory(Path.Combine(f.Root, "elsewhere")).FullName;
        Link(Path.Combine(f.CopilotHome, "session-state", C), elsewhere);
        await source.RefreshAsync();

        Assert.Equal(A, Assert.Single(source.Snapshot()).Id);
        Assert.Equal("a copilot session folder is a link", source.Held?.Reason);
    }

    [Fact]
    public async Task A_linked_codex_thread_lock_folder_holds_and_keeps_the_session_its_lock_gave()
    {
        using var f = new Fixture();
        var lockFile = f.CodexLock(A);
        f.Processes.Run(AgentKind.Codex, 800, "codex.exe --no-daemon");
        f.Processes.Holders[lockFile] = [800];
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());

        var locks = Path.GetDirectoryName(lockFile)!;
        Directory.Move(locks, locks + "-real");
        Link(locks, locks + "-real");
        await source.RefreshAsync();

        Assert.Equal(A, Assert.Single(source.Snapshot()).Id);
        Assert.StartsWith("codex's thread lock folder is a link", source.Held?.Reason);
    }

    [Fact]
    public async Task A_linked_registration_folder_holds_and_keeps_the_sessions_it_had()
    {
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(B);
        Fixture.Append(transcript, Claude.User("hi", 1));
        f.ClaudeRegistration(B, 500, transcript);
        f.Processes.Run(AgentKind.Claude, 500);
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());

        var moved = f.ClaudeRegistrations + "-real";
        Directory.Move(f.ClaudeRegistrations, moved);
        Link(f.ClaudeRegistrations, moved);
        await source.RefreshAsync();

        Assert.Equal(B, Assert.Single(source.Snapshot()).Id);
        Assert.StartsWith("a claude registration folder is a link", source.Held?.Reason);
    }

    // Permissions are the only way to make a folder unreadable to its owner, and only Unix
    // honours them for that; CI runs these on Linux.
    [Fact]
    public async Task A_registration_folder_that_cannot_be_looked_into_is_not_an_agent_with_no_sessions()
    {
        if (!Unix()) { return; }
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(B);
        Fixture.Append(transcript, Claude.User("hi", 1));
        f.ClaudeRegistration(B, 500, transcript);
        f.Processes.Run(AgentKind.Claude, 500);
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());

        var mode = File.GetUnixFileMode(f.Runtime);
        File.SetUnixFileMode(f.Runtime, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            await source.RefreshAsync();

            Assert.Equal(B, Assert.Single(source.Snapshot()).Id);
            Assert.StartsWith("a claude registration folder could not be read", source.Held?.Reason);
        }
        finally { File.SetUnixFileMode(f.Runtime, mode); }
    }

    [Fact]
    public async Task A_registration_whose_details_cannot_be_read_is_not_taken_for_deleted()
    {
        if (!Unix()) { return; }
        using var f = new Fixture();
        var transcript = f.ClaudeTranscript(B);
        Fixture.Append(transcript, Claude.User("hi", 1));
        f.ClaudeRegistration(B, 500, transcript);
        f.Processes.Run(AgentKind.Claude, 500);
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Single(source.Snapshot());

        // Listable, but nothing in it can be looked at.
        var mode = File.GetUnixFileMode(f.ClaudeRegistrations);
        File.SetUnixFileMode(f.ClaudeRegistrations, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        try
        {
            await source.RefreshAsync();

            Assert.Equal(B, Assert.Single(source.Snapshot()).Id);
            Assert.StartsWith("a claude registration could not be read", source.Held?.Reason);
        }
        finally { File.SetUnixFileMode(f.ClaudeRegistrations, mode); }
    }

    // Root reads anything, so permissions prove nothing under it.
    static bool Unix() => !OperatingSystem.IsWindows() && Environment.UserName != "root";

    // A symbolic link, or on Windows without the privilege for one, a junction: both are
    // reparse points, which is what discovery looks for.
    static void Link(string link, string target)
    {
        try { Directory.CreateSymbolicLink(link, target); }
        catch (Exception e) when (OperatingSystem.IsWindows() && e is IOException or UnauthorizedAccessException)
        {
            using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            })!;
            mklink.WaitForExit();
            Assert.Equal(0, mklink.ExitCode);
        }
    }

    [Fact]
    public async Task Claude_Code_s_own_files_account_for_its_sessions_and_sdk_runs_and_one_that_cannot_be_read_holds()
    {
        using var f = new Fixture();
        Fixture.Append(f.ClaudeTranscript(A), Claude.User("no hooks here", 1));
        f.ClaudeSessionFile(700, A);
        f.Processes.Run(AgentKind.Claude, 700);
        f.ClaudeSessionFile(701, B, kind: "sdk");
        f.Processes.Run(AgentKind.Claude, 701);
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Equal(A, Assert.Single(source.Snapshot()).Id);
        Assert.Null(source.Held);

        // Caught half written.
        File.WriteAllText(Path.Combine(f.ClaudeHome, "sessions", "700.json"), "{\"pid\": 700, \"sess");
        await source.RefreshAsync();

        Assert.Equal(A, Assert.Single(source.Snapshot()).Id);
        Assert.StartsWith("a claude session file could not be read (pid 700)", source.Held?.Reason);
    }

    [Fact]
    public async Task An_agent_that_was_never_installed_is_absence_not_doubt()
    {
        using var f = new Fixture();
        // No registration folder: nothing could account for a Claude process, so none is asked.
        f.Processes.Run(AgentKind.Claude, 500);
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Null(source.Held);
    }

    [Fact]
    public async Task Subscribers_and_the_list_say_what_holds_discovery_and_hear_when_it_clears()
    {
        using var f = new Fixture();
        f.Processes.Run(AgentKind.Copilot, 200, "copilot");
        await using var source = f.Source();
        await source.RefreshAsync();

        Assert.Equal("an unrecognised copilot process (pid 200)", source.List().DiscoveryHeld!.Reason);
        using var subscription = source.Subscribe();
        Assert.True(subscription.Events.TryRead(out var synced));
        Assert.Equal(Events.SessionsSynced, synced.Name);
        Assert.Equal([AgentKind.Copilot], ((SessionsSynced)synced.Data).DiscoveryHeld!.Agents);

        f.Processes.Exit(AgentKind.Copilot, 200);
        await source.RefreshAsync();
        await source.RefreshAsync();

        Assert.True(subscription.Events.TryRead(out var changed));
        Assert.Equal(Events.DiscoveryChanged, changed.Name);
        Assert.Null(((DiscoveryChanged)changed.Data).DiscoveryHeld);
        // Said once, not on every pass.
        Assert.False(subscription.Events.TryRead(out _));
        Assert.Null(source.List().DiscoveryHeld);
    }

    sealed class HeldComparer : IEqualityComparer<DiscoveryHeld?>
    {
        public static readonly HeldComparer Instance = new();
        public bool Equals(DiscoveryHeld? x, DiscoveryHeld? y) => x is null ? y is null : y is not null && x.Reason == y.Reason && x.Agents.SequenceEqual(y.Agents);
        public int GetHashCode(DiscoveryHeld? obj) => obj?.Reason.GetHashCode(StringComparison.Ordinal) ?? 0;
    }
}
