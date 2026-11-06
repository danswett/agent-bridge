using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Copilot;
using Xunit;

// Finding a Copilot permission prompt's answers on the session's screen, and reading its own
// record of the request. The screens are drawn the way Copilot CLI 1.0.10x draws its two
// option lists: the shell prompt (a "No" with a message box, "(Esc to stop)") and the path
// prompt (a plain "No (Esc)", opening on its second option).
public sealed class CopilotPermissionTests : IDisposable
{
    readonly Fixture f = new();

    public void Dispose() => f.Dispose();

    static readonly string[] Shell =
    [
        " Run command",
        "",
        " git push origin main --force-with-lease",
        "",
        " Do you want to run this command?",
        "",
        " \u276F 1. Yes",
        "   2. Yes, and don't ask again for `git push` in this directory",
        "   3. No, and tell Copilot what to do differently (Esc to stop)",
        "",
        " \u2191\u2193 to navigate \u00B7 Enter to select \u00B7 Esc to cancel",
    ];

    static readonly string[] Path =
    [
        "\u2502 Allow path access                                   \u2502",
        "\u2502   C:\\elsewhere\\notes.txt                            \u2502",
        "\u2502   1. Yes                                            \u2502",
        "\u2502 \u276F 2. Yes, and remember these paths for this session \u2502",
        "\u2502   3. Yes, allow file-tool read-only access to the listed directory \u2502",
        "\u2502   4. No (Esc)                                       \u2502",
    ];

    static readonly string[] ShellProbes = ["git push origin main --force-with-lease"];
    static readonly string[] PathProbes = ["notes.txt", "more"];

    // A short screen with only its options, under a row saying what it is for.
    static (CopilotPermissionScreen.Choice? Choice, string? Problem) Synthetic(IReadOnlyList<string> rows, bool approve) =>
        CopilotPermissionScreen.Locate(["Run deploy.ps1", .. rows], approve, ["deploy.ps1"]);

    [Fact]
    public void Approve_is_the_digit_of_the_one_row_reading_exactly_Yes_and_the_key_waits_on_that_row_being_there()
    {
        var (choice, problem) = CopilotPermissionScreen.Locate(Shell, approve: true, ShellProbes);

        Assert.Null(problem);
        Assert.Equal("1", choice!.Key);
        Assert.Equal(6, choice.Row);
        // Every option row, the No included, is held: a change of highlight anywhere stops the key.
        Assert.Equal(Enumerable.Range(2, 7).Select(r => new ScreenRow(r, Shell[r])), choice.Hold);
    }

    [Fact]
    public void A_list_opening_on_a_persistent_grant_is_still_approved_once_by_the_digit_of_its_plain_Yes()
    {
        var (choice, _) = CopilotPermissionScreen.Locate(Path, approve: true, PathProbes);

        Assert.Equal("1", choice!.Key);
        Assert.Equal(2, choice.Row);
    }

    [Fact]
    public void The_plain_Yes_is_found_wherever_the_list_puts_it_never_by_its_position()
    {
        string[] screen = ["  1. Yes, and approve all file operations for the rest of the running session", "\u276F 2. Yes", "  3. No (Esc)"];

        Assert.Equal("2", Synthetic(screen, approve: true).Choice!.Key);
    }

    [Theory]
    [InlineData("  1. Yes, and don't ask again for `git push` in this directory", "  2. No, and tell Copilot what to do differently (Esc to stop)")]
    [InlineData("  1. Run this command outside of the sandbox", "  2. No, and tell Copilot what to do differently (Esc to stop)")]
    public void A_prompt_without_a_plain_Yes_is_never_approved(string first, string last)
    {
        var (choice, problem) = Synthetic(["\u276F" + first[1..], last], approve: true);

        Assert.Null(choice);
        Assert.Contains("no single plain Yes", problem);
    }

    [Fact]
    public void A_Yes_that_wraps_onto_the_next_row_is_a_longer_label_and_not_approve_once()
    {
        string[] screen = ["\u276F 1. Yes", "     (always)", "  2. No (Esc)"];

        Assert.Contains("no single plain Yes", Synthetic(screen, approve: true).Problem);
    }

    [Fact]
    public void A_prompt_not_showing_what_it_is_for_chooses_nothing_and_what_it_shows_is_held_with_the_options()
    {
        Assert.Contains("does not show what this permission prompt is for", CopilotPermissionScreen.Locate(Path, approve: true, ["other.txt"]).Problem);

        var (choice, _) = CopilotPermissionScreen.Locate(Path, approve: true, PathProbes);

        // The next path request draws the same options; its paths row is what tells them apart.
        Assert.Equal(new ScreenRow(1, Path[1]), choice!.Hold[0]);
    }

    [Fact]
    public void Deny_is_Escape_only_when_the_last_option_is_a_No_labelled_as_Escapes()
    {
        var (shell, _) = CopilotPermissionScreen.Locate(Shell, approve: false, ShellProbes);
        var (path, _) = CopilotPermissionScreen.Locate(Path, approve: false, PathProbes);
        var (none, problem) = Synthetic(["\u276F 1. Yes", "  2. No"], approve: false);

        Assert.Equal((KeyPlan.Escape, 8), (shell!.Key, shell.Row));
        Assert.Equal((KeyPlan.Escape, 5), (path!.Key, path.Row));
        Assert.Null(none);
        Assert.Contains("Escape", problem);
    }

    [Fact]
    public void A_No_label_wrapped_onto_the_next_row_still_shows_it_takes_Escape()
    {
        string[] screen = ["\u276F 1. Yes", "  2. No, and tell Copilot what to do differently", "     (Esc to stop)"];

        Assert.Equal(KeyPlan.Escape, Synthetic(screen, approve: false).Choice!.Key);
    }

    [Fact]
    public void Nothing_is_chosen_while_someone_at_the_machine_has_the_No_and_its_message_box_selected()
    {
        string[] screen = ["  1. Yes", "  2. Yes, and don't ask again", "\u276F 3. No, and tell Copilot what to do differently (Esc to stop)"];

        Assert.Contains("someone at the machine", Synthetic(screen, approve: true).Problem);
        Assert.Contains("someone at the machine", Synthetic(screen, approve: false).Problem);
    }

    [Fact]
    public void Numbered_lists_earlier_on_screen_are_not_the_prompt()
    {
        string[] screen = ["1. Yes", "2. No (Esc)", "", "Here is the plan:", "1. Build", "2. Test", "", .. Shell];

        var (choice, _) = CopilotPermissionScreen.Locate(screen, approve: true, ShellProbes);

        Assert.Equal(7 + 6, choice!.Row);
    }

    [Theory]
    [InlineData(new[] { "Working on it..." }, "no permission prompt")]
    [InlineData(new[] { "  2. Yes", "  3. No (Esc)" }, "could not be read")]
    [InlineData(new[] { "  1. Yes", "", "  2. No (Esc)" }, "could not be read")]
    [InlineData(new[] { "\u276F 1. Yes", "  2. Maybe later" }, "does not end with a No")]
    public void A_screen_that_does_not_show_a_whole_prompt_chooses_nothing(string[] screen, string why)
    {
        var (choice, problem) = Synthetic(screen, approve: true);

        Assert.Null(choice);
        Assert.Contains(why, problem);
    }

    [Fact]
    public void A_prompt_with_more_options_than_digits_chooses_nothing()
    {
        var screen = Enumerable.Range(1, 10).Select(i => i == 1 ? "\u276F 1. Yes" : i == 10 ? "  10. No (Esc)" : $"  {i}. Yes, and option {i}").ToArray();

        Assert.Contains("more options", Synthetic(screen, approve: true).Problem);
    }

    [Fact]
    public void A_send_that_waits_on_rows_carries_each_to_the_worker()
    {
        var script = new KeyPlan([new KeySend("1", false, 0, 0, [new ScreenRow(6, " \u276F 1. Yes"), new ScreenRow(7, "   2. No (Esc)")])]).ToWorkerScript();

        var line = script.Split('\n')[1].Split(' ');
        Assert.Equal(9, line.Length);
        Assert.Equal(("6", "7"), (line[5], line[7]));
        Assert.Equal(" \u276F 1. Yes", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line[6])));
        Assert.Equal("   2. No (Esc)", System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(line[8])));
    }

    [Fact]
    public void The_transcript_says_whether_the_request_is_still_waiting_and_what_else_is()
    {
        var events = f.CopilotSession("11111111-1111-4111-8111-111111111111", 100);
        Fixture.Append(events, Copilot.Permission("r1", "c1", Copilot.Prompts["commands"], 1));

        var waiting = CopilotPermissionTranscript.Before(events, "r1");
        Assert.True(waiting.Readable);
        Assert.Equal("r1", waiting.Request?.RequestId);
        Assert.Equal((1, 0), (waiting.PendingPermissions, waiting.PendingAsks));

        Fixture.Append(events, Copilot.Permission("r2", "c2", Copilot.Prompts["read"], 2), Copilot.ToolStart("q1", "ask_user", new { message = "?" }, 3, agentId: "sub"));
        var crowded = CopilotPermissionTranscript.Before(events, "r1");
        Assert.Equal((2, 1), (crowded.PendingPermissions, crowded.PendingAsks));
        Assert.True(crowded.First);
        Assert.Equal("r2", Assert.Single(crowded.Others).Prompt?.RequestId);
        Assert.False(CopilotPermissionTranscript.Before(events, "r2").First);

        Fixture.Append(events, Copilot.PermissionDone("r1", "c1", "approved", 4));
        Assert.Null(CopilotPermissionTranscript.Before(events, "r1").Request);
        Assert.True(CopilotPermissionTranscript.Before(events, "r2").First);
        Assert.False(CopilotPermissionTranscript.Before(events + ".missing", "r1").Readable);
    }

    // Copilot asks a permission for each of a batch of parallel tool calls and draws one of them
    // (#690). With others waiting, the dialog is identified positively: inside one frame whose top
    // edge is on screen, every row above the options is this prompt's dialog as Copilot draws it.
    [Fact]
    public void With_other_requests_waiting_a_prompt_is_chosen_only_when_its_frame_holds_exactly_its_dialog()
    {
        var mine = new AgentBridge.Host.Core.Parsing.CopilotDialog("Push the branch", [new(true, ["git push origin main --force-with-lease"])], "Do you want to run this command?");
        var theirs = new AgentBridge.Host.Core.Parsing.CopilotDialog("Run it", [new(true, ["cd sub", "git push origin main --force-with-lease"])], "Do you want to run this command?");
        CopilotPermissionScreen.Crowd Crowd(string[] drawn, AgentBridge.Host.Core.Parsing.CopilotDialog? dialog = null) =>
            new(mine, ["git push origin main --force-with-lease", "Push the branch"], [new CopilotPermissionScreen.Other("shell, request r2", drawn, dialog ?? theirs)]);
        var waiting = Crowd(["cd sub\ngit push origin main --force-with-lease", "Run it"]);
        var shown = CopilotScreen.Shell("Push the branch", "git push origin main --force-with-lease");

        // Under the conversation, which may show anything.
        var (choice, problem) = CopilotPermissionScreen.Locate(["\u25CF git push origin main --force-with-lease", .. shown], approve: true, ShellProbes, waiting);
        Assert.Null(problem);
        Assert.Equal(("1", new ScreenRow(1, shown[0])), (choice!.Key, choice.Hold[0]));

        Assert.Equal("the machine is showing a different permission (shell, request r2)",
            CopilotPermissionScreen.Locate(CopilotScreen.Shell("Run it", "cd sub", "git push origin main --force-with-lease"), approve: true, ShellProbes, waiting).Problem);
        // The same, drawn with one more row, or a title of its own, is not this prompt's.
        Assert.Contains("does not show this permission as Copilot draws it",
            CopilotPermissionScreen.Locate(CopilotScreen.Shell("Push the branch", "git push origin main --force-with-lease", "x"), approve: true, ShellProbes, waiting).Problem);
        Assert.Contains("does not show this permission as Copilot draws it",
            CopilotPermissionScreen.Locate(CopilotScreen.Shell("Push the tags", "git push origin main --force-with-lease"), approve: true, ShellProbes, waiting).Problem);
        // No frame (the screen-reader layout), or its top edge scrolled off.
        Assert.Contains("whole frame", CopilotPermissionScreen.Locate(Shell, approve: true, ShellProbes, waiting).Problem);
        Assert.Contains("whole frame", CopilotPermissionScreen.Locate(shown[1..], approve: true, ShellProbes, waiting).Problem);
        // Text holding a line-drawing character, a tab or a hidden character, in any waiting request,
        // this one's own included.
        foreach (var bad in (string[])["x\n\u256D", "\u2502", "a\tb", "\u200Bx", "x\u202E"])
        {
            Assert.Contains("could be drawn as part of Copilot's own dialog", CopilotPermissionScreen.Locate(shown, approve: true, ShellProbes, Crowd([bad])).Problem);
            Assert.Contains("could be drawn as part of Copilot's own dialog", CopilotPermissionScreen.Locate(shown, approve: true, ShellProbes, waiting with { Drawn = [bad] }).Problem);
        }
        Assert.Contains("cannot be checked against the session's screen", CopilotPermissionScreen.Locate(shown, approve: true, ShellProbes, Crowd([])).Problem);
        Assert.Contains("cannot tell this kind", CopilotPermissionScreen.Locate(shown, approve: true, ShellProbes, waiting with { Dialog = null }).Problem);
        // Another that would be drawn the same: the same command wrapped where this one breaks a
        // line, or with white space that is not drawn.
        foreach (var twin in (string[][])[["git push origin", "main --force-with-lease"], ["git push origin main --force-with-lease  "]])
        {
            var alike = new AgentBridge.Host.Core.Parsing.CopilotDialog("Push the branch", [new(true, twin)], "Do you want to run this command?");
            Assert.Contains("would be drawn the same", CopilotPermissionScreen.Locate(shown, approve: true, ShellProbes, Crowd(["x"], alike)).Problem);
        }
    }

    // Two requests that draw the same are never told apart by the screen, whichever is drawn: a
    // warning of white space alone draws only blank rows, which the screen read skips, and a
    // warning's blank line reads as the gap between two parts. Compared as written, either pair
    // compared unlike, and the screen drawing one was read as the other's, so answering one approved
    // the other (Copilot review 5781252068 of #769).
    [Fact]
    public void Requests_that_draw_the_same_are_never_told_apart_by_the_screen_whichever_is_drawn()
    {
        const string Q = "Do you want to run this command?";
        CopilotPermissionScreen.Crowd Crowd(AgentBridge.Host.Core.Parsing.CopilotDialog mine, AgentBridge.Host.Core.Parsing.CopilotDialog theirs) =>
            new(mine, ["npm test"], [new CopilotPermissionScreen.Other("shell, request r2", ["npm test"], theirs)]);
        var plain = new AgentBridge.Host.Core.Parsing.CopilotDialog("Run the tests", [new(true, ["npm test"])], Q);
        foreach (var warning in (string[])[" ", "\n", "\u00A0", "  \n \n  "])
        {
            var warned = new AgentBridge.Host.Core.Parsing.CopilotDialog("Run the tests", [new(false, [warning]), new(true, ["npm test  "])], Q);
            var drawn = CopilotScreen.Dialog("Run the tests", [(false, [" "]), (true, ["npm test  "])], Q, CopilotScreen.ShellOptions);
            Assert.True(AgentBridge.Host.Core.Parsing.CopilotDialogs.Alike(plain, warned));
            Assert.Contains("would be drawn the same", CopilotPermissionScreen.Locate(drawn, approve: true, ["npm test"], Crowd(plain, warned)).Problem);
            Assert.Contains("would be drawn the same", CopilotPermissionScreen.Locate(CopilotScreen.Shell("Run the tests", "npm test"), approve: true, ["npm test"], Crowd(warned, plain)).Problem);
        }

        // A warning with a blank line in it reads as two parts.
        var split = new AgentBridge.Host.Core.Parsing.CopilotDialog("Run the tests", [new(false, ["Heads up"]), new(false, ["it runs"]), new(true, ["npm test"])], Q);
        var joined = new AgentBridge.Host.Core.Parsing.CopilotDialog("Run the tests", [new(false, ["Heads up\n\nit runs"]), new(true, ["npm test"])], Q);
        Assert.True(AgentBridge.Host.Core.Parsing.CopilotDialogs.Alike(split, joined));
        var screen = CopilotScreen.Dialog("Run the tests", [(false, ["Heads up"]), (false, ["it runs"]), (true, ["npm test"])], Q, CopilotScreen.ShellOptions);
        Assert.Contains("would be drawn the same", CopilotPermissionScreen.Locate(screen, approve: true, ["npm test"], Crowd(split, joined)).Problem);
        Assert.Contains("would be drawn the same", CopilotPermissionScreen.Locate(screen, approve: true, ["npm test"], Crowd(joined, split)).Problem);

        // Beside one drawn otherwise, the one with a warning of white space is found on its own screen.
        var warnedAlone = new AgentBridge.Host.Core.Parsing.CopilotDialog("Run the tests", [new(false, [" "]), new(true, ["npm test"])], Q);
        var other = new AgentBridge.Host.Core.Parsing.CopilotDialog("Run the linter", [new(true, ["npm run lint"])], Q);
        var (choice, problem) = CopilotPermissionScreen.Locate(CopilotScreen.Dialog("Run the tests", [(false, [" "]), (true, ["npm test"])], Q, CopilotScreen.ShellOptions), approve: true, ["npm test"], Crowd(warnedAlone, other));
        Assert.Null(problem);
        Assert.Equal("1", choice!.Key);
    }

    // Copilot wraps a warning without trimming it, so a run of spaces as wide as the dialog draws a
    // row of nothing: "x", 250 spaces and "y" draws as "x", a blank row and "y" do. Compared part by
    // part, the two were unlike, and the screen drawing either was read as the other's (Trust review
    // of #769). One such warning is never answered from the screen: it could not be found there.
    [Fact]
    public void A_warning_whose_spaces_wrap_into_a_blank_row_is_never_told_from_one_with_a_blank_line_nor_answered_from_the_screen()
    {
        const string Q = "Do you want to run this command?";
        CopilotPermissionScreen.Crowd Crowd(AgentBridge.Host.Core.Parsing.CopilotDialog mine, AgentBridge.Host.Core.Parsing.CopilotDialog theirs) =>
            new(mine, ["npm test"], [new CopilotPermissionScreen.Other("shell, request r2", ["npm test"], theirs)]);
        var wide = new AgentBridge.Host.Core.Parsing.CopilotDialog("Run the tests", [new(false, ["x" + new string(' ', 250) + "y"]), new(true, ["npm test"])], Q);
        var blank = new AgentBridge.Host.Core.Parsing.CopilotDialog("Run the tests", [new(false, ["x\n\ny"]), new(true, ["npm test"])], Q);
        var screen = CopilotScreen.Dialog("Run the tests", [(false, ["x"]), (false, ["y"]), (true, ["npm test"])], Q, CopilotScreen.ShellOptions);

        Assert.True(AgentBridge.Host.Core.Parsing.CopilotDialogs.Alike(wide, blank));
        Assert.Contains("would be drawn the same", CopilotPermissionScreen.Locate(screen, approve: true, ["npm test"], Crowd(blank, wide)).Problem);
        var (choice, problem) = CopilotPermissionScreen.Locate(screen, approve: true, ["npm test"], Crowd(wide, blank));
        Assert.Null(choice);
        Assert.NotNull(problem);

        // Beside one drawn otherwise, too: the screen cannot show where its text breaks.
        var other = new AgentBridge.Host.Core.Parsing.CopilotDialog("Run the linter", [new(true, ["npm run lint"])], Q);
        Assert.Contains("run of spaces", CopilotPermissionScreen.Locate(screen, approve: true, ["npm test"], Crowd(wide, other)).Problem);
    }

    // The shape seen in #690: a read of one file outside the session's paths, offering read-only
    // access to its directory, described from its request and found in Copilot's dialog for it.
    [Fact]
    public void A_path_request_offering_its_directory_is_described_as_Copilot_draws_it_and_found_among_others()
    {
        var prompt = new System.Text.Json.Nodes.JsonObject
        {
            ["kind"] = "path", ["accessKind"] = "read", ["paths"] = new System.Text.Json.Nodes.JsonArray("C:\\notes\\a.md"),
            ["readOnlyDirectories"] = new System.Text.Json.Nodes.JsonArray("C:\\notes"),
        };
        var line = System.Text.Json.JsonDocument.Parse(Copilot.Permission("r1", "c1", prompt, 1)).RootElement.GetProperty("data");
        var described = AgentBridge.Host.Core.Parsing.CopilotPermission.Describe(line)!;
        string[] screen = CopilotScreen.Dialog("Allow path access",
        [
            (false, ["This action may read the following path outside the paths currently allowed for this session."]),
            (true, ["C:\\notes\\a.md"]),
            (false, ["File-tool read-only access covers this directory and its descendants:"]),
            (true, ["C:\\notes"]),
        ], "Do you want to allow this?", ["  1. Yes", "\u276F 2. Yes, and remember this path for this session", "  3. Yes, allow file-tool read-only access to the listed directory for this session", "  4. No (Esc)"]);
        var crowd = new CopilotPermissionScreen.Crowd(described.Dialog, described.Drawn, [new CopilotPermissionScreen.Other("path access, request r2", ["C:\\notes\\b.md"])]);

        var (choice, problem) = CopilotPermissionScreen.Locate(screen, approve: true, described.Probes, crowd);

        Assert.Null(problem);
        Assert.Equal("1", choice!.Key);

        // Beside an MCP request whose argument name, nested in a list, holds a frame or hidden
        // character, as read from the transcript: delivery refuses as the listing holds it (#769).
        foreach (var name in (string[])["\u256D\u2500", "a\u202Eb", "\u200Bx"])
        {
            var tool = Copilot.Prompts["mcp"].DeepClone().AsObject();
            tool["args"] = new System.Text.Json.Nodes.JsonObject { ["items"] = new System.Text.Json.Nodes.JsonArray(new System.Text.Json.Nodes.JsonObject { [name] = "x" }) };
            var mcp = AgentBridge.Host.Core.Parsing.CopilotPermission.Describe(System.Text.Json.JsonDocument.Parse(Copilot.Permission("r2", "c2", tool, 2)).RootElement.GetProperty("data"))!;
            var beside = crowd with { Others = [new CopilotPermissionScreen.Other("MCP tool, request r2", mcp.Drawn, mcp.Dialog)] };
            Assert.Contains("could be drawn as part of Copilot's own dialog", CopilotPermissionScreen.Locate(screen, approve: true, described.Probes, beside).Problem);
            // The message shows the arguments with their names, so a hidden or right-to-left one is
            // one of the host's escapes there too, and the approval carries escaped (#836).
            if (name != "\u256D\u2500") { Assert.NotEmpty(mcp.Marks); }
        }
        // Bidi text under an argument sharing a name the request itself uses: checked as any value
        // is, never exempted by its name (Engineering's GPT delta of #769).
        foreach (var name in (string[])["kind", "toolCallId", "accessKind"])
        {
            var tool = Copilot.Prompts["mcp"].DeepClone().AsObject();
            tool["args"] = new System.Text.Json.Nodes.JsonObject { ["filter"] = new System.Text.Json.Nodes.JsonObject { [name] = "a\u202Eb" } };
            var mcp = AgentBridge.Host.Core.Parsing.CopilotPermission.Describe(System.Text.Json.JsonDocument.Parse(Copilot.Permission("r2", "c2", tool, 2)).RootElement.GetProperty("data"))!;
            var beside = crowd with { Others = [new CopilotPermissionScreen.Other("MCP tool, request r2", mcp.Drawn, mcp.Dialog)] };
            Assert.Contains("could be drawn as part of Copilot's own dialog", CopilotPermissionScreen.Locate(screen, approve: true, described.Probes, beside).Problem);
        }
    }

    [Fact]
    public void The_answer_is_read_from_the_requests_own_completion_after_the_point_read_before_typing()
    {
        var events = f.CopilotSession("11111111-1111-4111-8111-111111111111", 100);
        Fixture.Append(events, Copilot.Permission("r1", "c1", Copilot.Prompts["commands"], 1), Copilot.Permission("r2", "c2", Copilot.Prompts["read"], 2));
        var from = CopilotPermissionTranscript.Before(events, "r1").ScannedTo;
        Fixture.Append(events, Copilot.PermissionDone("r2", "c2", "approved", 3));

        Assert.Null(CopilotPermissionTranscript.Since(events, "r1", from).Kind);

        Fixture.Append(events, Copilot.PermissionDone("r1", "c1", "denied-interactively-by-user", 4, feedback: "not that"));
        var denied = CopilotPermissionTranscript.Since(events, "r1", from);
        Assert.Equal(("denied-interactively-by-user", true), (denied.Kind, denied.Feedback));
    }
}
