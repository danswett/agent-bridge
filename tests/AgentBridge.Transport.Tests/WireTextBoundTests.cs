using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Xunit;

namespace AgentBridge.Transport.Tests;

// The relay's roster reply is one text frame that can pass the ordinary frame bound; a reply past
// even its own bound must not cost the connection.
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

    static Task SendTextAsync(WebSocket ws, int size, char fill) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(new string(fill, size)), WebSocketMessageType.Text, true, default);

    [Fact]
    public async Task A_text_frame_past_the_ordinary_bound_but_within_the_roster_bound_is_read_whole()
    {
        var (server, client) = await PairAsync();
        var size = Wire.MaxFrame + 1000;
        var send = SendTextAsync(server, size, 'a');

        var frame = await Wire.ReceiveAsync(client, Wire.MaxFrame, Wire.MaxRosterFrame, default);
        await send;

        Assert.Equal(size, frame!.Value.Data.Length);
    }

    [Fact]
    public async Task A_text_frame_past_the_roster_bound_is_dropped_and_the_next_frame_still_arrives()
    {
        var (server, client) = await PairAsync();
        var send = Task.Run(async () =>
        {
            await SendTextAsync(server, Wire.MaxRosterFrame + 1, 'a');
            await SendTextAsync(server, 5, 'b');
        });

        var dropped = await Wire.ReceiveAsync(client, Wire.MaxFrame, Wire.MaxRosterFrame, default);
        var next = await Wire.ReceiveAsync(client, Wire.MaxFrame, Wire.MaxRosterFrame, default);

        await send;
        Assert.Empty(dropped!.Value.Data);
        Assert.Equal("bbbbb", Encoding.UTF8.GetString(next!.Value.Data));
    }

    [Fact]
    public async Task A_binary_frame_past_the_ordinary_bound_is_still_refused()
    {
        var (server, client) = await PairAsync();
        var send = server.SendAsync(new byte[Wire.MaxFrame + 1], WebSocketMessageType.Binary, true, default);

        await Assert.ThrowsAsync<FrameTooLargeException>(() => Wire.ReceiveAsync(client, Wire.MaxFrame, Wire.MaxRosterFrame, default));
        _ = send;
    }
}
