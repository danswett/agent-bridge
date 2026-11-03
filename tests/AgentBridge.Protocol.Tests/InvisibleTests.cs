using System.Text.Json;
using AgentBridge.Protocol;
using Xunit;

// What an agent's input passes over, drops or draws as nothing (Invisible): a reply, launch
// prompt or answer is judged by its first visible base character, never by what hides in front
// of it (#448). pwa/test/unit/invisible.test.mjs holds the PWA to the same cases.
public class InvisibleTests
{
    // Each shows as nothing. Copilot CLI trims U+FEFF; Claude Code strips the zero-width, bidi,
    // filler, variation-selector and tag characters before it sends; any agent may count a mark
    // without a base, or a character its Unicode has but the host's lacks, as nothing too.
    public static readonly TheoryData<string> LeadIns =
    [
        "\uFEFF", "\u200B", "\u2060", "\u200C\u200D \uFEFF", "\U000E0001",
        "\u3164", "\u115F", "\uFFA0", "\u034F", "\uFE0F", "\u2065", "\U000E0000", "\U000E0100",
        "\u0301", "\u20DD", "\u2800", "\u0378", "\u3164 \u034F\uFE0F",
    ];

    // What people write, which none of this may turn into nothing.
    public static readonly TheoryData<string> Words =
    [
        "👍", "👨‍👩‍👧", "❤️", "1️⃣", "नमस्ते", "안녕하세요", "สวัสดี", "مرحبا", "ship it", "\u3164ship it",
    ];

    static ReplyRequest Reply(string text) => new() { Id = "s1", Request = "6f1c2a9e-4b7d-4f0a-9b8e-2d1c3e4f5a6b", Text = text };

    static LaunchRequest Launch(string prompt) => new() { Request = "6f1c2a9e-4b7d-4f0a-9b8e-2d1c3e4f5a6b", Prompt = prompt };

    static readonly Question Plain = new() { Id = "q1", Kind = QuestionKind.Form, Message = "Pick", Fields = [], AskedAt = DateTimeOffset.UnixEpoch };

    static readonly Question WithOther = Plain with
    {
        Fields = [new Field { Name = "answer", Type = FieldType.Choice, Options = [new("a")], AllowOther = true, Required = true }],
    };

    static string? Text(string text) => Answers.Check(Plain, new AnswerRequest { Id = "s1", Question = "q1", Request = "r1", Text = text });

    static string? Other(string text) => Answers.Check(WithOther, new AnswerRequest
    {
        Id = "s1",
        Question = "q1",
        Request = "r1",
        Fields = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new { answer = new { other = text } })),
    });

    [Theory]
    [MemberData(nameof(LeadIns))]
    public void A_reply_or_prompt_starting_a_command_behind_invisible_characters_is_refused(string lead)
    {
        Assert.StartsWith("a reply cannot start with /", Replies.Check(Reply(lead + "/clear")));
        Assert.StartsWith("a reply cannot start with !", Replies.Check(Reply(lead + "!dir")));
        Assert.StartsWith("a prompt cannot start with /", LaunchProtocol.Check(Launch(lead + "/clear")));
        Assert.StartsWith("a prompt cannot start with !", LaunchProtocol.Check(Launch(lead + "!rm -rf .")));
        // A lone / is refused too: an agent that drops the rest leaves it in its box for the
        // next reply's Enter.
        Assert.StartsWith("a reply cannot start with /", Replies.Check(Reply(lead + "/")));
    }

    [Theory]
    [MemberData(nameof(LeadIns))]
    public void A_reply_or_answer_of_nothing_but_invisible_characters_is_empty(string lead)
    {
        Assert.Equal("the reply is empty", Replies.Check(Reply(lead)));
        Assert.Equal("the answer is empty", Text(lead));
        Assert.Equal("\"answer\": the other answer is empty", Other(lead));
    }

    [Theory]
    [MemberData(nameof(Words))]
    public void Emoji_joined_emoji_and_words_in_any_script_are_still_said(string words)
    {
        Assert.Null(Replies.Check(Reply(words)));
        Assert.Null(LaunchProtocol.Check(Launch(words)));
        Assert.Null(Text(words));
        Assert.Null(Other(words));
        Assert.Null(Replies.Check(Reply("\uFEFF" + words)));
    }
}
