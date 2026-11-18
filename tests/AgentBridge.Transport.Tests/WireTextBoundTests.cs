using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Xunit;

namespace AgentBridge.Transport.Tests;

// The relay's roster reply is one text frame that can pass the ordinary frame bound; a reply past
// even its own bound must not cost the connection.
// Keep both peers alive until the receive finishes: finalizing an unrooted sender can reset TCP
// before the client consumes its queued frame, masking the frame-bound exception.
public class WireTextBoundTests
{
    static async Task<(WebSocket Server, WebSocket Client)> PairAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var accept = listener.AcceptTcpClientAsync();
        var c = new TcpClient();
        await c.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)listener.LocalEndpoint).Port);
        var s = await accept;
        listener.Stop();
        return (WebSocket.CreateFromStream(s.GetStream(), isServer: true, null, TimeSpan.Zero),
                WebSocket.CreateFromStream(c.GetStream(), isServer: false, null, TimeSpan.Zero));
    }

    const string RosterHead = "{\"t\":\"roster\",";

    static Task SendRosterAsync(WebSocket ws, int size) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(RosterHead + new string('a', size - RosterHead.Length)), WebSocketMessageType.Text, true, default);

    static Task SendTextAsync(WebSocket ws, int size, char fill) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(new string(fill, size)), WebSocketMessageType.Text, true, default);

    [Fact]
    public async Task A_text_frame_past_the_ordinary_bound_but_within_the_roster_bound_is_read_whole()
    {
        var pair = await PairAsync();
        using var server = pair.Server;
        using var client = pair.Client;
        var size = Wire.MaxFrame + 1000;
        var send = SendRosterAsync(server, size);

        var frame = await Wire.ReceiveAsync(client, Wire.MaxFrame, Wire.MaxRosterFrame, default, discardOversizeText: true);
        await send;

        Assert.Equal(size, frame!.Value.Data.Length);
    }

    [Fact]
    public async Task A_text_frame_past_the_roster_bound_is_dropped_and_the_next_frame_still_arrives()
    {
        var pair = await PairAsync();
        using var server = pair.Server;
        using var client = pair.Client;
        var send = Task.Run(async () =>
        {
            await SendRosterAsync(server, Wire.MaxRosterFrame + 1);
            await SendTextAsync(server, 5, 'b');
        });

        var dropped = await Wire.ReceiveAsync(client, Wire.MaxFrame, Wire.MaxRosterFrame, default, discardOversizeText: true);
        var next = await Wire.ReceiveAsync(client, Wire.MaxFrame, Wire.MaxRosterFrame, default, discardOversizeText: true);

        await send;
        Assert.Empty(dropped!.Value.Data);
        Assert.Equal("bbbbb", Encoding.UTF8.GetString(next!.Value.Data));
    }

    [Fact]
    public async Task A_binary_frame_past_the_ordinary_bound_is_still_refused()
    {
        var pair = await PairAsync();
        using var server = pair.Server;
        using var client = pair.Client;
        var send = server.SendAsync(new byte[Wire.MaxFrame + 1], WebSocketMessageType.Binary, true, default);

        await Assert.ThrowsAsync<FrameTooLargeException>(() => Wire.ReceiveAsync(client, Wire.MaxFrame, Wire.MaxRosterFrame, default, discardOversizeText: true));
        await send;
    }

    [Fact]
    public async Task The_plain_overload_still_refuses_a_text_frame_past_its_bound()
    {
        var pair = await PairAsync();
        using var server = pair.Server;
        using var client = pair.Client;
        var send = SendTextAsync(server, Wire.MaxFrame + 1, 'a');

        await Assert.ThrowsAsync<FrameTooLargeException>(() => Wire.ReceiveAsync(client, Wire.MaxFrame, default));
        await send;
    }

    [Fact]
    public async Task An_oversized_text_frame_that_is_not_a_roster_reply_is_refused_not_drained()
    {
        var pair = await PairAsync();
        using var server = pair.Server;
        using var client = pair.Client;
        var send = SendTextAsync(server, Wire.MaxFrame + 1, 'a');

        await Assert.ThrowsAsync<FrameTooLargeException>(() => Wire.ReceiveAsync(client, Wire.MaxFrame, Wire.MaxRosterFrame, default, discardOversizeText: true));
        await send;
    }

    [Fact]
    public async Task A_roster_reply_past_the_drain_cap_is_refused()
    {
        var pair = await PairAsync();
        using var server = pair.Server;
        using var client = pair.Client;
        var send = SendRosterAsync(server, Wire.MaxRosterDrain + 1);

        await Assert.ThrowsAsync<FrameTooLargeException>(() => Wire.ReceiveAsync(client, Wire.MaxFrame, Wire.MaxRosterFrame, default, discardOversizeText: true));
        await send;
    }

    [Fact]
    public async Task An_oversized_binary_frame_is_still_refused_after_garbage_collection()
    {
        var pair = await PairAsync();
        using var server = pair.Server;
        using var client = pair.Client;
        var send = server.SendAsync(new byte[Wire.MaxFrame + 1], WebSocketMessageType.Binary, true, default);
        await send.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Yield();

        for (var generation = 0; generation < 4; generation++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await Assert.ThrowsAsync<FrameTooLargeException>(() => Wire.ReceiveAsync(client, Wire.MaxFrame, Wire.MaxRosterFrame, timeout.Token, discardOversizeText: true));
    }
}