using System.Text.Json.Nodes;
using AgentBridge.Host.Delivery.Copilot;
using Xunit;

// Copilot's input box read from its screen before a reply is typed (#825), against screens built
// only from captured ones and the CLI's own rendering code (tests/fixtures/screens).
public class CopilotInputBoxTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (var c in Fixture()) { data.Add(c!["name"]!.GetValue<string>()); }
        return data;
    }

    static JsonArray Fixture() =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "screens", "copilot-input-box.json")))!["cases"]!.AsArray();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Each_screen_is_read_as_an_empty_prompt_box_a_draft_shell_mode_or_nothing_known(string name)
    {
        var c = Fixture().Single(x => x!["name"]!.GetValue<string>() == name)!;
        var rows = c["rows"]!.AsArray().Select(r => r!.GetValue<string>()).ToList();

        var (box, problem, seen, state) = CopilotInputBoxReader.Read(rows);

        // Only the prompt's own box, seen, may be refused for what is in it before the typing
        // turn; anything else is left to the checks in the turn, which may name a question.
        Assert.Equal(c["expect"]!.GetValue<string>() is "ok" or "draft" or "shell" or "vim", seen);
        // Whatever is refused, the box the screen shows is described as it is (for #804).
        if (seen)
        {
            Assert.True(state is { Main: true, Complete: true });
            Assert.Equal(c["expect"]!.GetValue<string>() == "shell", state!.Shell);
            if (c["content"]?.GetValue<string>() is { } content) { Assert.Equal(content, state.Content); }
        }
        switch (c["expect"]!.GetValue<string>())
        {
            case "ok":
                Assert.Null(problem);
                Assert.True(box!.Empty);
                // Held to the box's own rows, from its top to its bottom.
                Assert.All(box.Rows, r => Assert.Equal(rows[r.Row], r.Text));
                break;
            case "draft":
                Assert.Null(box);
                Assert.StartsWith("something is already typed in Copilot's input box at the machine", problem);
                var shown = c["shown"]?.GetValue<string>() ?? c["content"]!.GetValue<string>();
                Assert.Contains($"(\"{shown}\")", problem);
                break;
            case "shell":
                Assert.Null(box);
                Assert.Contains("shell mode", problem);
                break;
            case "vim":
                Assert.Null(box);
                Assert.Contains("vim normal mode", problem);
                break;
            default:
                Assert.Null(box);
                Assert.Contains("reply at the machine", problem);
                Assert.DoesNotContain("shell mode,", problem);
                break;
        }
    }

    [Fact]
    public void What_is_in_the_box_is_shown_escaped_and_no_more_than_500_characters_of_it()
    {
        var shown = CopilotInputBoxReader.Shown(new string('x', 600) + "\u202E");

        Assert.Equal(new string('x', 500) + "\u2026", shown);
        Assert.Equal("a\\u202Eb", CopilotInputBoxReader.Shown("a\u202Eb"));
    }

    [Fact]
    public void A_box_whose_draft_reads_as_the_shell_placeholder_but_without_its_footer_is_still_shell_mode()
    {
        // The placeholder alone is enough: an empty box in shell mode shows it, and a person
        // could not be typing it into a prompt box without being refused for a draft anyway.
        var (box, problem, _, _) = CopilotInputBoxReader.Read(["\u257B\u2584\u2584\u2584", "\u2503 Execute shell command", "\u2579\u2580\u2580\u2580", " \u2190 open sidebar \u00B7 / commands"]);

        Assert.Null(box);
        Assert.Contains("shell mode", problem);
    }

    static readonly string[] Rail = ["\u257B\u2584\u2584\u2584\u2584", "\u2503", "\u2579\u2580\u2580\u2580\u2580"];
    const string Idle = " \u2190 open sidebar \u00B7 Interactive \u00B7 Allow All \u00B7 / commands \u00B7 ? help";
    const string WithText = " Interactive \u00B7 Allow All \u00B7 @ files \u00B7 # issues";

    [Fact]
    public void A_draft_is_described_whole_its_lines_counted_and_its_rows_given_though_the_reply_is_refused()
    {
        string[] rows = ["\u25CF Done.", Rail[0], "\u2503 first line", "\u2503 second line", Rail[2], WithText];

        var (box, problem, seen, state) = CopilotInputBoxReader.Read(rows);

        Assert.Null(box);
        Assert.StartsWith("something is already typed", problem);
        Assert.True(seen);
        Assert.Equal(("first line\nsecond line", 2, false, true, true), (state!.Content, state.Lines, state.Shell, state.Main, state.Complete));
        Assert.Equal([1, 2, 3, 4], state.Rows.Select(r => r.Row));
    }

    [Fact]
    public void A_draft_taller_than_the_window_is_described_as_cut_off_and_refused_as_before()
    {
        // Its top edge scrolled away: the '┃' rows start at the window's first row.
        string[] rows = ["\u2503 line 7", "\u2503 line 8", Rail[2], WithText];

        var (box, problem, seen, state) = CopilotInputBoxReader.Read(rows);

        Assert.Null(box);
        Assert.StartsWith("the host could not find Copilot's input box", problem);
        Assert.False(seen);
        Assert.Equal(("line 7\nline 8", 2, true, false), (state!.Content, state.Lines, state.Main, state.Complete));
    }

    [Fact]
    public void A_box_not_at_the_bottom_over_the_footer_is_described_as_not_the_prompts_and_a_screen_of_two_is_not_described()
    {
        var (_, _, _, alone) = CopilotInputBoxReader.Read([.. Rail, Idle, "some text below"]);
        var (_, _, _, two) = CopilotInputBoxReader.Read([.. Rail, "", .. Rail, Idle]);

        Assert.False(alone!.Main);
        Assert.Null(two);
    }

    [Fact]
    public void An_empty_box_in_shell_mode_is_described_in_shell_mode_with_its_placeholder_as_drawn()
    {
        var (_, _, _, state) = CopilotInputBoxReader.Read([Rail[0], "\u2503 Execute shell command", Rail[2], " \u2190 open sidebar \u00B7 Shell \u00B7 Allow All \u00B7 / commands"]);

        Assert.Equal(("Execute shell command", true, true), (state!.Content, state.Shell, state.Main));
    }
}

