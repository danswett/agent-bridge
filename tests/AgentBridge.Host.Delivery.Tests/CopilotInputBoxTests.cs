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

        var (box, problem, seen) = CopilotInputBoxReader.Read(rows);

        // Only the prompt's own box, seen, may be refused for what is in it before the typing
        // turn; anything else is left to the checks in the turn, which may name a question.
        Assert.Equal(c["expect"]!.GetValue<string>() is "ok" or "draft" or "shell" or "vim", seen);
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
        var (box, problem, _) = CopilotInputBoxReader.Read(["\u257B\u2584\u2584\u2584", "\u2503 Execute shell command", "\u2579\u2580\u2580\u2580", " \u2190 open sidebar \u00B7 / commands"]);

        Assert.Null(box);
        Assert.Contains("shell mode", problem);
    }
}
