using System.Net.WebSockets;
using AgentBridge.Relay;
using Xunit;

namespace AgentBridge.Transport.Tests;

// The cause on the relay's disconnect log line (#970): enough to tell a reset from a clean close
// or a missed pong, and nothing that identifies a person or a machine.
public class DisconnectCauseTests
{
    [Fact]
    public void A_close_frame_shows_its_status_and_no_exception()
    {
        Assert.Equal("exception=none wserror=none close=1000 keepalive=no",
            DisconnectCause.Describe(null, WebSocketCloseStatus.NormalClosure));
    }

    [Fact]
    public void A_reset_without_a_close_frame_shows_the_error_and_a_null_close_status()
    {
        var reset = new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "The remote party closed the WebSocket connection without completing the close handshake.");
        Assert.Equal("exception=WebSocketException wserror=ConnectionClosedPrematurely close=none keepalive=no",
            DisconnectCause.Describe(reset, null));
    }

    [Fact]
    public void A_missed_pong_is_told_from_a_reset()
    {
        var missed = new WebSocketException(WebSocketError.Faulted, "The WebSocket didn't receive a Pong frame in response to a Ping frame within the configured KeepAliveTimeout.");
        Assert.EndsWith("keepalive=timeout", DisconnectCause.Describe(missed, null));
    }

    [Fact]
    public void An_exception_message_never_reaches_the_line()
    {
        var e = new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "peer 203.0.113.9 reset");
        Assert.DoesNotContain("203.0.113.9", DisconnectCause.Describe(e, null));
    }

    [Fact]
    public void A_cancelled_read_shows_the_exception_type()
    {
        Assert.StartsWith("exception=OperationCanceledException wserror=none close=none",
            DisconnectCause.Describe(new OperationCanceledException(), null));
    }
}
