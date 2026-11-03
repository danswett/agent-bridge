using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Discovery;
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
    const string D = "44444444-4444-4444-8444-444444444444";

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

    // #437: a Codex launch waiting on its hooks-trust question fired its hooks before they could
    // tell which Codex was theirs, so its registration named no process, and the Codex exited
    // before its SessionEnd. Nothing rewrote the file, and discovery held for 4.5 hours.
    [Fact]
    public async Task A_codex_registration_naming_no_process_left_by_a_launch_that_exited_stops_holding_after_the_grace_and_says_so_once()
    {
        using var f = new Fixture();
        f.Processes.Run(AgentKind.Codex, 960, "codex.exe");
        f.CodexRegistration(C, 0, null, updated: f.Time.GetUtcNow());
        var log = new List<string>();
        await using var source = f.Source(log: log.Add);
        await source.RefreshAsync();
        Assert.Equal("a codex registration names no process, an unrecognised codex process (pid 960)", source.Held?.Reason);

        f.Processes.Exit(AgentKind.Codex, 960);
        f.Time.Advance(TimeSpan.FromSeconds(119));
        await source.RefreshAsync();
        Assert.Equal("a codex registration names no process", source.Held?.Reason);

        f.Time.Advance(TimeSpan.FromSeconds(1));
        await source.RefreshAsync();
        Assert.Null(source.Held);
        f.Time.Advance(TimeSpan.FromHours(4.5));
        await source.RefreshAsync();
        Assert.Null(source.Held);
        Assert.Single(log, l => l == $"carrying on without codex registration {C}: it names no process, was written 2 min ago, and no running codex could have written it");
    }

    [Fact]
    public async Task A_codex_registration_naming_no_process_holds_naming_the_codex_that_could_be_its_session_and_says_so_once()
    {
        // Past the grace, and past the process's own: a Codex still waiting on its question is a
        // live session nothing else shows. Found in review: the hold read as #437's own, while
        // the log said it carried on without that process. Gone, it holds no more.
        using var f = new Fixture();
        f.CodexRegistration(C, 0, null, updated: f.Time.GetUtcNow());
        f.Processes.Run(AgentKind.Codex, 960, "codex.exe");
        var log = new List<string>();
        await using var source = f.Source(log: log.Add);
        await source.RefreshAsync();
        f.Time.Advance(TimeSpan.FromMinutes(30));
        await source.RefreshAsync();
        f.Time.Advance(TimeSpan.FromMinutes(29));
        await source.RefreshAsync();
        Assert.Equal("a codex registration names no process (codex pid 960 could have written it)", source.Held?.Reason);
        Assert.Contains(log, l => l.StartsWith("carrying on without codex process 960:", StringComparison.Ordinal));
        Assert.Single(log, l => l.StartsWith($"holding for codex registration {C}: it names no process, and codex process 960, which nothing accounts for, could have written it", StringComparison.Ordinal));

        f.Processes.Exit(AgentKind.Codex, 960);
        await source.RefreshAsync();

        Assert.Null(source.Held);
    }

    [Fact]
    public async Task A_codex_that_could_be_the_session_a_registration_naming_no_process_is_for_holds_it_for_an_hour_at_most()
    {
        // Found in review: whatever only looks like a session, where what launched what cannot be
        // read, held discovery for as long as it ran, as #437 did.
        using var f = new Fixture();
        f.CodexRegistration(C, 0, null, updated: f.Time.GetUtcNow());
        f.Processes.Run(AgentKind.Codex, 960, "codex.exe");
        var log = new List<string>();
        await using var source = f.Source(log: log.Add);
        await source.RefreshAsync();
        f.Time.Advance(SessionSource.PidlessWriterLimit - TimeSpan.FromSeconds(1));
        await source.RefreshAsync();
        Assert.Equal("a codex registration names no process (codex pid 960 could have written it)", source.Held?.Reason);

        f.Time.Advance(TimeSpan.FromSeconds(1));
        await source.RefreshAsync();
        f.Time.Advance(TimeSpan.FromHours(4.5));
        await source.RefreshAsync();

        Assert.Null(source.Held);
        Assert.Single(log, l => l == $"carrying on without codex registration {C}: it names no process, and codex process 960, which could have written it, has held it the most it may, 60 min");
    }

    // Found in review: npx, bunx, pnpm dlx and a version manager's shim read as an interactive
    // Codex, and held a registration naming no process for as long as the session they launched
    // ran. The session is the executable they launched, registered with its own pid.
    [Theory]
    [InlineData("\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\Program Files\\nodejs\\node_modules\\npm\\bin\\npx-cli.js\" -y @openai/codex@latest")]
    [InlineData("bun x @openai/codex")]
    [InlineData("node /usr/lib/node_modules/pnpm/bin/pnpm.cjs dlx @openai/codex")]
    [InlineData("C:\\Users\\u\\AppData\\Local\\Volta\\bin\\codex.exe")]
    public async Task A_launcher_of_another_codex_session_is_not_the_session_a_registration_naming_no_process_is_for(string launcher)
    {
        using var f = new Fixture();
        f.CodexRegistration(C, 0, null, updated: f.Time.GetUtcNow());
        f.CodexRegistration(D, 961, f.CodexTranscript(D));
        f.Processes.Run(AgentKind.Codex, 960, launcher);
        f.Processes.Run(AgentKind.Codex, 961, "codex.exe");
        f.Processes.Launches(960);
        await using var source = f.Source();
        await source.RefreshAsync();

        f.Time.Advance(Unaccounted.Grace);
        await source.RefreshAsync();

        Assert.Null(source.Held);
        Assert.Equal(D, Assert.Single(source.Snapshot()).Id);
    }

    [Fact]
    public async Task A_session_launched_through_npx_waiting_on_its_question_still_holds_by_its_own_executable()
    {
        using var f = new Fixture();
        f.CodexRegistration(C, 0, null, updated: f.Time.GetUtcNow());
        f.Processes.Run(AgentKind.Codex, 960, "node /usr/lib/node_modules/npm/bin/npx-cli.js -y @openai/codex");
        f.Processes.Run(AgentKind.Codex, 961, "/home/u/.npm/_npx/0a1b/node_modules/@openai/codex-linux-x64/vendor/x86_64-unknown-linux-musl/codex/codex");
        f.Processes.Launches(960);
        await using var source = f.Source();
        await source.RefreshAsync();

        f.Time.Advance(TimeSpan.FromMinutes(30));
        await source.RefreshAsync();

        Assert.Equal("a codex registration names no process (codex pid 961 could have written it)", source.Held?.Reason);
    }

    [Fact]
    public async Task A_codex_that_started_after_a_registration_naming_no_process_was_written_is_not_its_session()
    {
        using var f = new Fixture();
        f.CodexRegistration(C, 0, null);
        f.Processes.Run(AgentKind.Codex, 970, "codex.exe", startedAt: f.Time.GetUtcNow());
        await using var source = f.Source();

        await source.RefreshAsync();
        Assert.Equal("an unrecognised codex process (pid 970)", source.Held?.Reason);

        f.Time.Advance(Unaccounted.Grace);
        await source.RefreshAsync();
        Assert.Null(source.Held);
    }

    [Fact]
    public async Task A_codex_server_running_since_before_a_registration_naming_no_process_was_written_is_not_its_session()
    {
        // Found in review: an IDE's `codex mcp-server` nothing accounts for held it for as long as
        // the IDE ran, as #437 did. Only its own unrecognised-process grace holds.
        using var f = new Fixture();
        f.CodexRegistration(C, 0, null);
        f.Processes.Run(AgentKind.Codex, 980, "codex mcp-server");
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Equal("an unrecognised codex process (pid 980)", source.Held?.Reason);

        f.Time.Advance(TimeSpan.FromHours(4.5));
        await source.RefreshAsync();

        Assert.Null(source.Held);
    }

    // Found in review: a server's name anywhere in the arguments ruled out a terminal waiting on
    // its question. What runs is told by the subcommand.
    [Theory]
    [InlineData("codex.exe --profile mcp-server", true)]
    [InlineData("codex.exe -- app-server", true)]
    [InlineData("codex.exe \"Explain the app-server protocol\"", true)]
    [InlineData("codex.exe -c k=v mcp-server", false)]
    [InlineData("codex.exe exec \"list the files\"", false)]
    [InlineData("node /usr/lib/node_modules/@openai/codex/bin/codex.js", true)]
    [InlineData("node /usr/lib/node_modules/@openai/codex/bin/codex.js mcp-server", false)]
    [InlineData("node /usr/lib/node_modules/@openai/codex/bin/codex.js exec \"list the files\"", false)]
    [InlineData("node --require /work/preload.cjs /usr/lib/node_modules/@openai/codex/bin/codex.js mcp-server", false)]
    [InlineData("node --require /work/preload.cjs /usr/lib/node_modules/@openai/codex/bin/codex.js exec \"list the files\"", false)]
    [InlineData("node --require /work/preload.cjs /usr/lib/node_modules/@openai/codex/bin/codex.js", true)]
    [InlineData("node --require /work/codex-preload.cjs /usr/lib/node_modules/@openai/codex/bin/codex.js exec list-files", false)]
    [InlineData("node --require /work/codex-preload.cjs /usr/lib/node_modules/@openai/codex/bin/codex.js mcp-server", false)]
    [InlineData("/opt/codex/codex-x86_64-unknown-linux-musl exec list-files", false)]
    [InlineData("codex.exe --remote-auth-token-env app-server --remote ws://localhost:4500", true)]
    // Found in review: a preload named codex was taken for Codex, and what followed for its subcommand.
    [InlineData("node --require /work/codex.cjs /usr/lib/node_modules/@openai/codex/bin/codex.js exec list", false)]
    [InlineData("node -r /work/codex.cjs /usr/lib/node_modules/@openai/codex/bin/codex.js mcp-server", false)]
    [InlineData("bun --preload /work/codex.ts /usr/lib/node_modules/@openai/codex/bin/codex.js exec list", false)]
    [InlineData("node --import /work/codex.mjs /work/vendored/codex.js exec list", false)]
    [InlineData("node --require /work/codex.cjs /usr/lib/node_modules/@openai/codex/bin/codex.js", true)]
    // Found in review: a loader's path in the prompt, and a preload given inline.
    [InlineData("node --require /work/preload.cjs /usr/local/bin/codex exec \"Review /usr/local/lib/node_modules/@openai/codex/bin/codex.js\"", false)]
    [InlineData("node --require=/work/codex.cjs /work/vendored/codex.js exec list", false)]
    [InlineData("node --require=/work/codex.cjs /work/vendored/codex.js", true)]
    public async Task Only_an_interactive_codex_by_its_subcommand_could_be_the_session_a_registration_naming_no_process_is_for(string commandLine, bool holds)
    {
        using var f = new Fixture();
        f.CodexRegistration(C, 0, null, updated: f.Time.GetUtcNow());
        f.Processes.Run(AgentKind.Codex, 960, commandLine);
        await using var source = f.Source();
        await source.RefreshAsync();

        f.Time.Advance(TimeSpan.FromMinutes(30));
        await source.RefreshAsync();

        Assert.Equal(holds ? "a codex registration names no process (codex pid 960 could have written it)" : null, source.Held?.Reason);
    }

    [Fact]
    public async Task A_codex_whose_command_line_cannot_be_read_could_be_the_session_a_registration_naming_no_process_is_for()
    {
        using var f = new Fixture();
        f.CodexRegistration(C, 0, null, updated: f.Time.GetUtcNow());
        f.Processes.Run(AgentKind.Codex, 990);
        await using var source = f.Source();
        await source.RefreshAsync();

        f.Time.Advance(TimeSpan.FromMinutes(30));
        await source.RefreshAsync();

        Assert.Equal("a codex registration names no process (codex pid 990 could have written it)", source.Held?.Reason);
    }

    // A second of slack, as for a registration's own process: start times the OS rounds.
    [Theory]
    [InlineData(1000, true)]
    [InlineData(1001, false)]
    public async Task A_codex_that_started_up_to_a_second_after_a_registration_naming_no_process_was_written_could_have_written_it(int afterMs, bool holds)
    {
        using var f = new Fixture();
        var written = f.Time.GetUtcNow();
        f.CodexRegistration(C, 0, null, updated: written);
        f.Processes.Run(AgentKind.Codex, 960, "codex.exe", startedAt: written.AddMilliseconds(afterMs));
        await using var source = f.Source();
        await source.RefreshAsync();

        f.Time.Advance(TimeSpan.FromMinutes(30));
        await source.RefreshAsync();

        Assert.Equal(holds ? "a codex registration names no process (codex pid 960 could have written it)" : null, source.Held?.Reason);
    }

    [Fact]
    public async Task A_codex_registration_naming_no_process_with_no_time_of_its_own_is_dated_by_its_file()
    {
        using var f = new Fixture();
        f.CodexRegistration(C, 0, null);
        var path = Path.Combine(f.CodexRegistrations, C + ".json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        json.Remove("Updated");
        File.WriteAllText(path, json.ToJsonString());
        File.SetLastWriteTimeUtc(path, f.Time.GetUtcNow().UtcDateTime);
        await using var source = f.Source();
        await source.RefreshAsync();
        f.Time.Advance(TimeSpan.FromSeconds(119));
        await source.RefreshAsync();
        Assert.Equal("a codex registration names no process", source.Held?.Reason);

        f.Time.Advance(TimeSpan.FromSeconds(1));
        await source.RefreshAsync();

        Assert.Null(source.Held);
    }

    [Fact]
    public async Task A_codex_registration_naming_no_process_that_cannot_be_listed_with_the_processes_holds()
    {
        using var f = new Fixture();
        f.CodexRegistration(C, 0, null);
        f.Processes.Unreadable(AgentKind.Codex);
        await using var source = f.Source();
        await source.RefreshAsync();
        Assert.Equal("a codex registration names no process, the codex processes could not be listed", source.Held?.Reason);

        f.Processes.Unreadable(AgentKind.Codex, false);
        await source.RefreshAsync();

        Assert.Null(source.Held);
    }

    [Fact]
    public async Task A_codex_registration_naming_no_process_carried_on_without_is_said_once_across_a_pass_that_held()
    {
        // Found in review: a pass that could not list the processes held, and forgot it was said.
        using var f = new Fixture();
        f.CodexRegistration(C, 0, null);
        var log = new List<string>();
        await using var source = f.Source(log: log.Add);
        await source.RefreshAsync();
        f.Processes.Unreadable(AgentKind.Codex);
        await source.RefreshAsync();
        f.Processes.Unreadable(AgentKind.Codex, false);

        await source.RefreshAsync();

        Assert.Null(source.Held);
        Assert.Single(log, l => l.StartsWith($"carrying on without codex registration {C}:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_codex_registration_naming_no_process_rewritten_while_it_is_read_holds_until_it_reads_whole()
    {
        using var f = new Fixture();
        f.CodexRegistration(C, 0, null);
        var rewrite = true;
        await using var source = f.Source(afterRegistrationRead: path =>
        {
            if (rewrite) { File.AppendAllText(path, " "); }
        });
        await source.RefreshAsync();
        Assert.Equal("a codex registration changed while it was read", source.Held?.Reason);

        rewrite = false;
        await source.RefreshAsync();

        Assert.Null(source.Held);
    }

    [Fact]
    public async Task A_claude_registration_naming_no_process_holds_as_it_did_while_a_codex_one_is_carried_on_without()
    {
        using var f = new Fixture();
        f.ClaudeRegistration(B, 0, null);
        f.CodexRegistration(C, 0, null);
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Equal(new DiscoveryHeld("a claude registration names no process", [AgentKind.Claude]), source.Held, HeldComparer.Instance);
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
    [InlineData("codex --remote-auth-token-env app-server --remote ws://localhost:4500", false)]
    [InlineData("node --require /work/codex-preload.cjs /usr/lib/node_modules/@openai/codex/bin/codex.js app-server", true)]
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
    public async Task A_session_found_live_that_cannot_be_read_holds_its_agent_until_it_can()
    {
        using var f = new Fixture();
        // A registration whose transcript path no file system takes: found, but not readable.
        f.ClaudeRegistration(B, 500, "C:/work/\0/transcript.jsonl");
        f.Processes.Run(AgentKind.Claude, 500);
        await using var source = f.Source();

        await source.RefreshAsync();

        Assert.Empty(source.Snapshot());
        Assert.Equal(new DiscoveryHeld("a claude session that could not be read", [AgentKind.Claude]), source.Held, HeldComparer.Instance);

        var transcript = f.ClaudeTranscript(B);
        Fixture.Append(transcript, Claude.User("hi", 1));
        f.ClaudeRegistration(B, 500, transcript);
        await source.RefreshAsync();

        Assert.Equal(B, Assert.Single(source.Snapshot()).Id);
        Assert.Null(source.Held);
    }

    [Fact]
    [Trait("Category", "Process")]
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
    [Trait("Category", "Process")]
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
    [Trait("Category", "Process")]
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

    // #147, as rehearsal run 2 met it: Codex sessions listed, then kept only by a hold that did not
    // clear (here a Codex process that cannot be dated, and each session's registration caught half
    // written), when the host restarted. Returns where the host keeps what it listed.
    static async Task<string> ListedThenKeptOnlyByAHold(Fixture f, params (string Id, int Pid)[] codex)
    {
        if (codex.Length == 0) { codex = [(C, 950)]; }
        var state = Path.Combine(f.Root, "profile", "sessions-listed.json");
        foreach (var (id, pid) in codex)
        {
            var rollout = f.CodexTranscript(id);
            Fixture.Append(rollout,
                Codex.Line("session_meta", new { id, timestamp = Copilot.At(0), cwd = "/work/codex" }, 0),
                Codex.Event(new { type = "user_message", message = "still running" }, 1));
            f.CodexRegistration(id, pid, rollout);
            f.Processes.Run(AgentKind.Codex, pid, "codex.exe");
        }
        await using var before = f.Source(stateFile: state);
        await before.RefreshAsync();
        Assert.Equal(codex.Length, before.Snapshot().Count);

        f.Processes.RunUndated(AgentKind.Codex, 960, "codex.exe");
        foreach (var (id, _) in codex) { File.WriteAllText(Path.Combine(f.CodexRegistrations, id + ".json"), "{\"SessionId\": \"" + id); }
        await before.RefreshAsync();
        Assert.Equal(codex.Length, before.Snapshot().Count);
        Assert.Equal([AgentKind.Codex], before.Held!.Agents);
        return state;
    }

    [Fact]
    public async Task A_restart_under_a_hold_keeps_a_live_codex_session_listed_that_only_the_hold_was_keeping()
    {
        using var f = new Fixture();
        var state = await ListedThenKeptOnlyByAHold(f);

        var log = new List<string>();
        await using var after = f.Source(log: log.Add, stateFile: state);
        await after.RefreshAsync();

        Assert.Equal([AgentKind.Codex], after.Held!.Agents);
        var session = Assert.Single(after.Snapshot());
        Assert.Equal(C, session.Id);
        Assert.Equal(950, session.ProcessId);
        Assert.Equal("still running", after.Get(C)!.Activity.UserMessage);
        Assert.Contains("kept 1 session(s) listed across the restart while discovery is held: codex 33333333 (pid 950)", log);

        // Found again on its own once its registration reads whole, and still listed with nothing held.
        f.CodexRegistration(C, 950, f.CodexTranscript(C));
        f.Processes.Exit(AgentKind.Codex, 960);
        await after.RefreshAsync();
        Assert.Equal(C, Assert.Single(after.Snapshot()).Id);
        Assert.Null(after.Held);
    }

    [Fact]
    public async Task A_session_kept_across_a_restart_that_ends_during_the_hold_is_removed_only_once_the_hold_clears()
    {
        using var f = new Fixture();
        var state = await ListedThenKeptOnlyByAHold(f);
        await using var after = f.Source(stateFile: state);
        await after.RefreshAsync();
        Assert.Equal(C, Assert.Single(after.Snapshot()).Id);

        f.Processes.Exit(AgentKind.Codex, 950);
        await after.RefreshAsync();
        Assert.Equal(C, Assert.Single(after.Snapshot()).Id);
        Assert.NotNull(after.Held);

        // Nothing holds any more, and nothing finds it: it has ended.
        f.Processes.Exit(AgentKind.Codex, 960);
        File.Delete(Path.Combine(f.CodexRegistrations, C + ".json"));
        await after.RefreshAsync();
        Assert.Empty(after.Snapshot());
        Assert.Null(after.Held);
    }

    [Fact]
    public async Task A_restart_brings_back_only_the_sessions_whose_own_process_still_runs_not_one_whose_pid_another_process_now_has()
    {
        using var f = new Fixture();
        var state = await ListedThenKeptOnlyByAHold(f, (C, 950), (B, 951));

        // B's codex exited while the host was down, and a later one was given its pid.
        f.Processes.Run(AgentKind.Codex, 951, "codex.exe", Fixture.T0.AddMinutes(30));
        await using var after = f.Source(stateFile: state);
        await after.RefreshAsync();

        Assert.NotNull(after.Held);
        Assert.Equal(C, Assert.Single(after.Snapshot()).Id);
    }

    // Found in review: only a session's identity is kept across a restart, and its files are found
    // again as discovery finds them, whatever the file says.
    [Fact]
    public async Task A_restart_reads_a_kept_session_from_where_discovery_finds_it_not_from_a_path_in_the_file()
    {
        using var f = new Fixture();
        var state = await ListedThenKeptOnlyByAHold(f);
        var decoy = Path.Combine(f.Root, "elsewhere", "decoy.jsonl");
        Fixture.Append(decoy, Codex.Event(new { type = "user_message", message = "not this one" }, 1));
        var kept = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(state))!;
        kept["sessions"]![0]!["transcript"] = decoy;
        File.WriteAllText(state, kept.ToJsonString());

        await using var after = f.Source(stateFile: state);
        await after.RefreshAsync();

        Assert.Equal("still running", after.Get(C)!.Activity.UserMessage);
        Assert.DoesNotContain("elsewhere", File.ReadAllText(state), StringComparison.Ordinal);
    }

    // Found in review: a host that has just started knew no session's process, so the runtime of a
    // resumed Copilot session (no lock) whose command line could not be read held nothing.
    [Fact]
    public async Task A_restart_keeps_a_session_whose_runtime_cannot_be_read_on_the_first_pass()
    {
        using var f = new Fixture();
        var state = Path.Combine(f.Root, "profile", "sessions-listed.json");
        var events = f.CopilotSession(A, lockPid: null);
        Fixture.Append(events, Copilot.User("resumed", 1));
        f.Processes.Run(AgentKind.Copilot, 300, $"node C:/npm/node_modules/@github/copilot/index.js --session-id {A}");
        await using (var before = f.Source(stateFile: state))
        {
            await before.RefreshAsync();
            Assert.Single(before.Snapshot());
        }

        f.Processes.Unidentify(AgentKind.Copilot, 300);
        await using var after = f.Source(stateFile: state);
        await after.RefreshAsync();

        Assert.Equal(A, Assert.Single(after.Snapshot()).Id);
        Assert.Equal("a copilot process that could not be read (pid 300)", after.Held?.Reason);
    }

    [Fact]
    public async Task A_restart_does_not_bring_back_a_session_whose_registration_says_it_ended_though_its_runtime_cannot_be_read()
    {
        using var f = new Fixture();
        var state = await ListedThenKeptOnlyByAHold(f);

        f.CodexRegistration(C, 950, f.CodexTranscript(C), status: "ended", ended: true, updated: Fixture.T0.AddMinutes(5));
        f.Processes.Unidentify(AgentKind.Codex, 950);
        await using var after = f.Source(stateFile: state);
        await after.RefreshAsync();

        Assert.NotNull(after.Held);
        Assert.Empty(after.Snapshot());
    }

    // Found in review: only the current registration says a session ended, not an older copy of it
    // in agent-ha-bridge's other (legacy temp) root.
    [Fact]
    public async Task An_older_ended_registration_in_another_root_does_not_keep_a_restart_from_bringing_back_a_live_session()
    {
        using var f = new Fixture();
        var state = await ListedThenKeptOnlyByAHold(f);
        var legacy = Path.Combine(f.Root, "temp", "agent-bridge-codex");
        Directory.CreateDirectory(legacy);
        f.CodexRegistration(C, 950, f.CodexTranscript(C), status: "ended", ended: true, updated: Fixture.T0.AddMinutes(5));
        File.Move(Path.Combine(f.CodexRegistrations, C + ".json"), Path.Combine(legacy, C + ".json"));
        f.CodexRegistration(C, 950, f.CodexTranscript(C), updated: Fixture.T0.AddMinutes(10));
        f.Processes.Unidentify(AgentKind.Codex, 950);

        await using var after = f.Source(stateFile: state, codexRoots: [f.CodexRegistrations, legacy]);
        await after.RefreshAsync();

        Assert.NotNull(after.Held);
        Assert.Equal(C, Assert.Single(after.Snapshot()).Id);
    }
}
