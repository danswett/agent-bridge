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
        "\u2502                                                     \u2502",
        "\u2502   1. Yes                                            \u2502",
        "\u2502 \u276F 2. Yes, and remember these paths for this session \u2502",
        "\u2502   3. Yes, allow file-tool read-only access to the listed directory \u2502",
        "\u2502   4. No (Esc)                                       \u2502",
    ];

    [Fact]
    public void Approve_is_the_digit_of_the_one_row_reading_exactly_Yes_and_the_key_waits_on_that_row_being_there()
    {
        var (choice, problem) = CopilotPermissionScreen.Locate(Shell, approve: true);

        Assert.Null(problem);
        Assert.Equal("1", choice!.Key);
        Assert.Equal(6, choice.Row);
        // Every option row, the No included, is held: a change of highlight anywhere stops the key.
        Assert.Equal(Enumerable.Range(6, 3).Select(r => new ScreenRow(r, Shell[r])), choice.Hold);
    }

    [Fact]
    public void A_list_opening_on_a_persistent_grant_is_still_approved_once_by_the_digit_of_its_plain_Yes()
    {
        var (choice, _) = CopilotPermissionScreen.Locate(Path, approve: true);

        Assert.Equal("1", choice!.Key);
        Assert.Equal(2, choice.Row);
    }

    [Fact]
    public void The_plain_Yes_is_found_wherever_the_list_puts_it_never_by_its_position()
    {
        string[] screen = ["  1. Yes, and approve all file operations for the rest of the running session", "\u276F 2. Yes", "  3. No (Esc)"];

        Assert.Equal("2", CopilotPermissionScreen.Locate(screen, approve: true).Choice!.Key);
    }

    [Theory]
    [InlineData("  1. Yes, and don't ask again for `git push` in this directory", "  2. No, and tell Copilot what to do differently (Esc to stop)")]
    [InlineData("  1. Run this command outside of the sandbox", "  2. No, and tell Copilot what to do differently (Esc to stop)")]
    public void A_prompt_without_a_plain_Yes_is_never_approved(string first, string last)
    {
        var (choice, problem) = CopilotPermissionScreen.Locate(["\u276F" + first[1..], last], approve: true);

        Assert.Null(choice);
        Assert.Contains("no single plain Yes", problem);
    }

    [Fact]
    public void A_Yes_that_wraps_onto_the_next_row_is_a_longer_label_and_not_approve_once()
    {
        string[] screen = ["\u276F 1. Yes", "     (always)", "  2. No (Esc)"];

        Assert.Contains("no single plain Yes", CopilotPermissionScreen.Locate(screen, approve: true).Problem);
    }

    [Fact]
    public void Deny_is_Escape_only_when_the_last_option_is_a_No_labelled_as_Escapes()
    {
        var (shell, _) = CopilotPermissionScreen.Locate(Shell, approve: false);
        var (path, _) = CopilotPermissionScreen.Locate(Path, approve: false);
        var (none, problem) = CopilotPermissionScreen.Locate(["\u276F 1. Yes", "  2. No"], approve: false);

        Assert.Equal((KeyPlan.Escape, 8), (shell!.Key, shell.Row));
        Assert.Equal((KeyPlan.Escape, 5), (path!.Key, path.Row));
        Assert.Null(none);
        Assert.Contains("Escape", problem);
    }

    [Fact]
    public void A_No_label_wrapped_onto_the_next_row_still_shows_it_takes_Escape()
    {
        string[] screen = ["\u276F 1. Yes", "  2. No, and tell Copilot what to do differently", "     (Esc to stop)"];

        Assert.Equal(KeyPlan.Escape, CopilotPermissionScreen.Locate(screen, approve: false).Choice!.Key);
    }

    [Fact]
    public void Nothing_is_chosen_while_someone_at_the_machine_has_the_No_and_its_message_box_selected()
    {
        string[] screen = ["  1. Yes", "  2. Yes, and don't ask again", "\u276F 3. No, and tell Copilot what to do differently (Esc to stop)"];

        Assert.Contains("someone at the machine", CopilotPermissionScreen.Locate(screen, approve: true).Problem);
        Assert.Contains("someone at the machine", CopilotPermissionScreen.Locate(screen, approve: false).Problem);
    }

    [Fact]
    public void Numbered_lists_earlier_on_screen_are_not_the_prompt()
    {
        string[] screen = ["1. Yes", "2. No (Esc)", "", "Here is the plan:", "1. Build", "2. Test", "", .. Shell];

        var (choice, _) = CopilotPermissionScreen.Locate(screen, approve: true);

        Assert.Equal(7 + 6, choice!.Row);
    }

    [Theory]
    [InlineData(new[] { "Working on it..." }, "no permission prompt")]
    [InlineData(new[] { "  2. Yes", "  3. No (Esc)" }, "could not be read")]
    [InlineData(new[] { "  1. Yes", "", "  2. No (Esc)" }, "could not be read")]
    [InlineData(new[] { "\u276F 1. Yes", "  2. Maybe later" }, "does not end with a No")]
    public void A_screen_that_does_not_show_a_whole_prompt_chooses_nothing(string[] screen, string why)
    {
        var (choice, problem) = CopilotPermissionScreen.Locate(screen, approve: true);

        Assert.Null(choice);
        Assert.Contains(why, problem);
    }

    [Fact]
    public void A_prompt_with_more_options_than_digits_chooses_nothing()
    {
        var screen = Enumerable.Range(1, 10).Select(i => i == 1 ? "\u276F 1. Yes" : i == 10 ? "  10. No (Esc)" : $"  {i}. Yes, and option {i}").ToArray();

        Assert.Contains("more options", CopilotPermissionScreen.Locate(screen, approve: true).Problem);
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

        Fixture.Append(events, Copilot.PermissionDone("r1", "c1", "approved", 4));
        Assert.Null(CopilotPermissionTranscript.Before(events, "r1").Request);
        Assert.False(CopilotPermissionTranscript.Before(events + ".missing", "r1").Readable);
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
