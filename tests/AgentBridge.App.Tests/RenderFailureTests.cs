using AgentBridge.App.Core;
using Xunit;

public class RenderFailureTests
{
    [Fact]
    public void A_failed_message_is_reported_without_its_text_and_does_not_stop_the_next_message()
    {
        var lines = new List<string>();
        var error = new InvalidOperationException("private transcript text");
        Assert.False(MessageRender.TryDraw(() => throw error,
            e => lines.Add(MessageRender.Failure("synthetic-session", 7, 15360, e))));
        Assert.True(MessageRender.TryDraw(() => lines.Add("next message drawn"), _ => Assert.Fail("Unexpected render failure")));
        Assert.Contains("session=synthetic-session item=7 bytes=15360", lines[0]);
        Assert.Contains("exception=System.InvalidOperationException", lines[0]);
        Assert.DoesNotContain("private transcript text", lines[0]);
        Assert.Equal("next message drawn", lines[1]);
    }

    [Fact]
    public void A_diagnostic_failure_is_not_swallowed()
    {
        Assert.Throws<IOException>(() => MessageRender.TryDraw(
            () => throw new InvalidOperationException(), _ => throw new IOException("log failed")));
    }
}
