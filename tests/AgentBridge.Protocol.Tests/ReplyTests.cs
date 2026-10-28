using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;
using Xunit;

public class ReplyTests
{
    static ReplyRequest Reply(string? text, string? attachmentsJson = null) => new()
    {
        Id = "s1",
        Request = "6f1c2a9e-4b7d-4f0a-9b8e-2d1c3e4f5a6b",
        Text = text,
        Attachments = attachmentsJson is null ? null : JsonSerializer.Deserialize<JsonElement>(attachmentsJson),
    };

    [Fact]
    public void A_reply_must_say_something_and_control_characters_alone_say_nothing()
    {
        Assert.Null(Replies.Check(Reply("please carry on")));
        Assert.Equal("the reply is empty", Replies.Check(Reply(null)));
        Assert.Equal("the reply is empty", Replies.Check(Reply(" \t\r\n ")));
        Assert.Equal("the reply is empty", Replies.Check(Reply("\u0001\u001b\u2028")));
    }

    [Fact]
    public void The_limit_is_16_KiB_of_UTF_8_not_16_thousand_characters()
    {
        Assert.Null(Replies.Check(Reply(new string('x', Limits.ReplyText))));
        Assert.Equal("the reply is longer than 16 KiB", Replies.Check(Reply(new string('x', Limits.ReplyText + 1))));
        // 3 bytes each: 5,462 of them is 16,386 bytes, though far fewer characters.
        var cjk = new string('界', Limits.ReplyText / 3 + 1);
        Assert.True(cjk.Length < Limits.ReplyText && Encoding.UTF8.GetByteCount(cjk) > Limits.ReplyText);
        Assert.Equal("the reply is longer than 16 KiB", Replies.Check(Reply(cjk)));
    }

    [Fact]
    public void A_slash_or_bang_command_is_refused_wherever_leading_space_or_control_characters_hide_it()
    {
        Assert.Contains("cannot start with /", Replies.Check(Reply("/clear")));
        Assert.Contains("cannot start with /", Replies.Check(Reply("  \n\u0001/exit")));
        Assert.Contains("cannot start with !", Replies.Check(Reply("\t!rm -rf build")));
        // Anywhere else they are ordinary text.
        Assert.Null(Replies.Check(Reply("use /tmp and run !build later")));
    }

    [Fact]
    public void Attachments_are_refused_until_the_slice_that_carries_them_but_an_empty_list_is_none()
    {
        Assert.Contains("attachments are not supported yet", Replies.Check(Reply("see this", """[{"name":"shot.png"}]""")));
        Assert.Contains("attachments are not supported yet", Replies.Check(Reply("see this", """{"name":"shot.png"}""")));
        Assert.Null(Replies.Check(Reply("see this", "[]")));
        Assert.Null(Replies.Check(Reply("see this", "null")));
    }

    [Fact]
    public void A_reply_request_reads_from_and_writes_to_the_contract_json()
    {
        var json = """{"id":"s1","request":"6f1c2a9e-4b7d-4f0a-9b8e-2d1c3e4f5a6b","text":"go on","future":1}""";
        var read = JsonSerializer.Deserialize<ReplyRequest>(json, ProtocolJson.Options)!;
        Assert.Equal(("s1", "go on", (JsonElement?)null), (read.Id, read.Text, read.Attachments));
        Assert.Equal("""{"outcome":"busy","reason":"x"}""", JsonSerializer.Serialize(new ReplyResult(AnswerOutcome.Busy, "x"), ProtocolJson.Options));
        Assert.Equal(AnswerOutcome.Unconfirmed, JsonSerializer.Deserialize<ReplyResult>("""{"outcome":"queued"}""", ProtocolJson.Options)!.Outcome);
    }
}
