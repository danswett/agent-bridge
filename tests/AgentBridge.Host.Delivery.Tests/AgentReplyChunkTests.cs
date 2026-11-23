using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.AgentReplies;
using Xunit;

// A long reply typed into Claude Code in one burst loses its head: Claude takes a burst as a paste and folds or
// drops what it cannot type (probed in a disposable session of the builder's own: one burst of 1000 characters
// arrives as about 300 typed and a [Pasted text] block; sends of 100 characters 40 ms apart arrive whole, 4500
// of them in 3 s). So the reply is typed as paced chunks; Codex's plan and short Claude replies are unchanged.
public sealed class AgentReplyChunkTests
{
    static string Words(int length) => string.Concat(Enumerable.Range(1, length).Select(i => $"w{i:000} ")).Substring(0, length).TrimEnd() + "!";

    [Fact]
    public void A_long_claude_reply_is_typed_as_sends_of_at_most_100_characters_that_say_exactly_the_same()
    {
        var text = Words(450);

        var reply = AgentReply.Claude(text, [])!;

        Assert.True(reply.Sends.Count >= 5);
        Assert.All(reply.Sends, s => Assert.True(s.Text.Length <= AgentReply.ChunkUnits));
        Assert.Equal(reply.Words, string.Concat(reply.Sends.Select(s => s.Text)));
        Assert.Equal(reply.Words, reply.Composed);
    }

    [Fact]
    public void Only_the_last_chunk_presses_Enter_and_nothing_waits_for_the_box_between_them()
    {
        var reply = AgentReply.Claude(Words(450), [])!;

        Assert.All(reply.Sends.SkipLast(1), s => Assert.False(s.Enter));
        Assert.True(reply.Sends[^1].Enter);
        Assert.All(reply.Sends, s => Assert.Null(s.After));
    }

    [Fact]
    public void A_surrogate_pair_is_never_split_between_two_chunks()
    {
        // 99 characters, then an emoji (two UTF-16 units) straddling the 100-unit mark, then more.
        var text = new string('a', AgentReply.ChunkUnits - 1) + "\U0001F680" + new string('b', 30);

        var reply = AgentReply.Claude(text, [])!;

        Assert.Equal(reply.Words, string.Concat(reply.Sends.Select(s => s.Text)));
        Assert.All(reply.Sends, s =>
        {
            Assert.False(s.Text.Length > 0 && char.IsHighSurrogate(s.Text[^1]), "a chunk ends inside a pair");
            Assert.False(s.Text.Length > 0 && char.IsLowSurrogate(s.Text[0]), "a chunk starts inside a pair");
        });
    }

    [Fact]
    public void A_short_claude_reply_is_still_one_send_with_Enter()
    {
        var reply = AgentReply.Claude("Ship it", [])!;

        var send = Assert.Single(reply.Sends);
        Assert.Equal(("Ship it", true), (send.Text, send.Enter));
    }

    [Fact]
    public void Each_chunk_but_the_last_pauses_40_ms_so_claude_does_not_take_the_burst_for_a_paste()
    {
        var reply = AgentReply.Claude(Words(450), [])!;
        var box = new[] { new ScreenRow(4, "x") };

        var keys = reply.Sends.Select(s => AgentReply.Key(s, box)).ToList();

        Assert.All(keys.SkipLast(1), k => Assert.Equal(AgentReply.ChunkPauseMs, k.PauseAfterMs));
        Assert.Equal(40, AgentReply.ChunkPauseMs);
        Assert.Equal(100, AgentReply.ChunkUnits);
    }

    [Fact]
    public void A_codex_reply_is_not_chunked()
    {
        var reply = AgentReply.Codex(Words(450), [])!;

        Assert.Single(reply.Sends);
    }
}
