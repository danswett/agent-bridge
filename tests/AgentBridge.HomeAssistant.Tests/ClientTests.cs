using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using AgentBridge.HomeAssistant;
using Xunit;

// The client's wire: what goes to Home Assistant's REST and WebSocket APIs, through an in-process
// handler and an in-memory WebSocket pair. No socket is opened.
public class ClientTests
{
    const string Token = "test-token-not-a-secret";

    sealed class Handler(Func<HttpRequestMessage, string?, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Seen.Add((request, body));
            return respond(request, body);
        }
    }

    [Fact]
    public async Task A_publish_is_one_mqtt_publish_service_call_with_the_token_as_a_bearer_header()
    {
        var handler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]") });
        var client = new HomeAssistantClient(new Uri("http://ha.test:8123"), Token, new HttpClient(handler));
        await client.PublishAsync("copilot/cli/agent_bridge_x/status/state", "idle", true);
        var (request, body) = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("http://ha.test:8123/api/services/mqtt/publish", request.RequestUri!.ToString());
        Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
        Assert.Equal(Token, request.Headers.Authorization.Parameter);
        Assert.Equal("application/json", request.Content!.Headers.ContentType!.MediaType);
        Contract.AssertJson(JsonNode.Parse("""{"topic":"copilot/cli/agent_bridge_x/status/state","payload":"idle","retain":true,"qos":1}"""), JsonNode.Parse(body!));
    }

    [Fact]
    public async Task A_service_call_that_names_more_than_one_entity_is_refused_before_anything_is_sent()
    {
        var handler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK));
        var client = new HomeAssistantClient(new Uri("http://ha.test:8123"), Token, new HttpClient(handler));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallServiceAsync("button", "press", new JsonObject { ["entity_id"] = new JsonArray("button.a_stop", "button.b_stop") }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.CallServiceAsync("button", "press", new JsonObject { ["area_id"] = "office" }));
        Assert.Empty(handler.Seen);
    }

    [Fact]
    public async Task A_missing_entity_reads_as_none_and_a_failure_says_its_status_without_the_body()
    {
        var handler = new Handler((r, _) => r.RequestUri!.AbsolutePath.EndsWith("missing", StringComparison.Ordinal)
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content = new StringContent("echo of something sent") });
        var client = new HomeAssistantClient(new Uri("http://ha.test:8123/"), Token, new HttpClient(handler));
        Assert.Null(await client.GetStateAsync("sensor.missing"));
        var e = await Assert.ThrowsAsync<HttpRequestException>(() => client.GetStateAsync("sensor.other"));
        Assert.Contains("401", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("echo", e.Message, StringComparison.Ordinal);
    }

    // Two ends of an in-memory connection, as WebSockets over a pair of byte channels.
    sealed class Pipe : Stream
    {
        readonly ChannelReader<byte[]> input;
        readonly ChannelWriter<byte[]> output;
        byte[] pending = [];
        int offset;

        Pipe(ChannelReader<byte[]> input, ChannelWriter<byte[]> output) { this.input = input; this.output = output; }

        public static (Stream Client, Stream Server) Pair()
        {
            var a = Channel.CreateUnbounded<byte[]>();
            var b = Channel.CreateUnbounded<byte[]>();
            return (new Pipe(a.Reader, b.Writer), new Pipe(b.Reader, a.Writer));
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (offset >= pending.Length)
            {
                if (!await input.WaitToReadAsync(cancellationToken)) { return 0; }
                pending = await input.ReadAsync(cancellationToken);
                offset = 0;
            }
            var n = Math.Min(buffer.Length, pending.Length - offset);
            pending.AsMemory(offset, n).CopyTo(buffer);
            offset += n;
            return n;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            output.TryWrite(buffer.ToArray());
            return ValueTask.CompletedTask;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer, offset, count).GetAwaiter().GetResult();
        public override void Write(byte[] buffer, int offset, int count) => output.TryWrite(buffer.AsSpan(offset, count).ToArray());
        public override void Flush() { }
        protected override void Dispose(bool disposing) { output.TryComplete(); base.Dispose(disposing); }
        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    // A Home Assistant WebSocket endpoint: asks for auth, checks the token, answers one command.
    static (HomeAssistantClient Client, List<JsonObject> Received, List<Uri> Connected) Server(Func<JsonObject, JsonObject> answer, string accept = Token)
    {
        var received = new List<JsonObject>();
        var connected = new List<Uri>();
        var client = new HomeAssistantClient(new Uri("https://ha.test"), Token, new HttpClient(new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))), (uri, ct) =>
        {
            connected.Add(uri);
            var (c, s) = Pipe.Pair();
            var server = WebSocket.CreateFromStream(s, new WebSocketCreationOptions { IsServer = true });
            _ = Task.Run(async () =>
            {
                await Send(server, new JsonObject { ["type"] = "auth_required", ["ha_version"] = "2026.10.0" });
                var auth = await Receive(server);
                lock (received) { received.Add(auth); }
                if (auth["access_token"]?.GetValue<string>() != accept)
                {
                    await Send(server, new JsonObject { ["type"] = "auth_invalid", ["message"] = "Invalid access token or password" });
                    return;
                }
                await Send(server, new JsonObject { ["type"] = "auth_ok" });
                var command = await Receive(server);
                lock (received) { received.Add(command); }
                // Events for another subscription first: a client must wait for its own result.
                await Send(server, new JsonObject { ["id"] = 99, ["type"] = "event" });
                var reply = answer(command);
                reply["id"] = command["id"]!.GetValue<int>();
                reply["type"] = "result";
                await Send(server, reply);
            });
            return Task.FromResult(WebSocket.CreateFromStream(c, new WebSocketCreationOptions { IsServer = false }));
        });
        return (client, received, connected);

        static Task Send(WebSocket ws, JsonObject message) =>
            ws.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

        static async Task<JsonObject> Receive(WebSocket ws)
        {
            var buffer = new byte[64 * 1024];
            var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
            return JsonNode.Parse(buffer.AsSpan(0, r.Count))!.AsObject();
        }
    }

    [Fact]
    public async Task A_registry_command_authenticates_then_runs_and_reads_its_own_result()
    {
        var (client, received, connected) = Server(_ => new JsonObject
        {
            ["success"] = true,
            ["result"] = new JsonArray(
                new JsonObject { ["entity_id"] = "sensor.ai_agent_bridge_x_status", ["unique_id"] = "agent_bridge_x_status" },
                new JsonObject { ["entity_id"] = "light.kitchen" }),
        });
        var entries = await client.ListEntityRegistryAsync();
        Assert.Equal("wss://ha.test/api/websocket", Assert.Single(connected).ToString());
        Assert.Equal("auth", received[0]["type"]!.GetValue<string>());
        Assert.Equal("config/entity_registry/list", received[1]["type"]!.GetValue<string>());
        Assert.Equal([new HaRegistryEntry("sensor.ai_agent_bridge_x_status", "agent_bridge_x_status"), new HaRegistryEntry("light.kitchen", null)], entries);
    }

    [Fact]
    public void An_event_is_read_with_the_account_that_fired_it_and_anything_else_is_not_one()
    {
        static JsonElement Message(string json) => JsonDocument.Parse(json).RootElement;
        var tap = HomeAssistantClient.Event(Message("""
            {"id":1,"type":"event","event":{"event_type":"mobile_app_notification_action",
             "data":{"action":"BRIDGE_q1_0","action_data":{"bridge":"decision"}},"context":{"id":"c","user_id":"u-phone"}}}
            """), HaNotify.ActionEvent);
        Assert.NotNull(tap);
        Assert.Equal("u-phone", tap.UserId);
        Assert.Equal("BRIDGE_q1_0", tap.Data.GetProperty("action").GetString());
        Assert.Null(HomeAssistantClient.Event(Message("""{"id":1,"type":"event","event":{"event_type":"mobile_app_notification_action","data":{},"context":{"user_id":null}}}"""), HaNotify.ActionEvent)!.UserId);
        Assert.Null(HomeAssistantClient.Event(Message("""{"id":1,"type":"event","event":{"event_type":"state_changed","data":{}}}"""), HaNotify.ActionEvent));
        Assert.Null(HomeAssistantClient.Event(Message("""{"id":1,"type":"result","success":true}"""), HaNotify.ActionEvent));
        Assert.Null(HomeAssistantClient.Event(Message("""{"id":1,"type":"event","event":{"event_type":"mobile_app_notification_action","data":"x"}}"""), HaNotify.ActionEvent));
    }

    [Fact]
    public async Task A_rename_names_one_entity_and_its_new_id()
    {
        var (client, received, _) = Server(_ => new JsonObject { ["success"] = true, ["result"] = new JsonObject() });
        await client.RenameEntityAsync("sensor.ai_agent_bridge_x_status", "sensor.agent_bridge_x_status");
        Contract.AssertJson(JsonNode.Parse("""{"id":1,"type":"config/entity_registry/update","entity_id":"sensor.ai_agent_bridge_x_status","new_entity_id":"sensor.agent_bridge_x_status"}"""), received[1]);
    }

    [Fact]
    public async Task An_image_is_fetched_from_the_store_by_id_and_a_missing_or_oversized_one_is_not_taken()
    {
        var png = new byte[] { 0x89, 0x50, 0x4E, 0x47, 1, 2, 3, 4 };
        var handler = new Handler((r, _) => r.RequestUri!.AbsolutePath switch
        {
            "/api/image/serve/abc123/original" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(png) },
            "/api/image/serve/big/original" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[100]) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });
        var client = new HomeAssistantClient(new Uri("http://ha.test:8123"), Token, new HttpClient(handler));

        Assert.Equal(png, await client.DownloadImageAsync("abc123", 1024));
        Assert.Equal("Bearer", handler.Seen[0].Request.Headers.Authorization!.Scheme);
        Assert.Null(await client.DownloadImageAsync("gone", 1024));
        await Assert.ThrowsAsync<InvalidDataException>(() => client.DownloadImageAsync("big", 99));
        // An id that is not one never reaches a URL.
        var seen = handler.Seen.Count;
        await Assert.ThrowsAsync<ArgumentException>(() => client.DownloadImageAsync("../states", 1024));
        await Assert.ThrowsAsync<ArgumentException>(() => client.DeleteImageAsync("a/b"));
        Assert.Equal(seen, handler.Seen.Count);
    }

    [Fact]
    public async Task An_image_whose_body_stalls_after_its_headers_fails_within_the_clients_timeout()
    {
        var handler = new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new Stalling()) });
        var client = new HomeAssistantClient(new Uri("http://ha.test:8123"), Token, new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(300) });

        // Only a backstop: stopped by it, the fetch would end in a cancellation, not a timeout.
        using var backstop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var e = await Assert.ThrowsAsync<TimeoutException>(() => client.DownloadImageAsync("abc123", 1024, backstop.Token));
        Assert.Contains("GET image", e.Message, StringComparison.Ordinal);
    }

    // A body that sends its first bytes and then nothing more.
    sealed class Stalling : Stream
    {
        bool sent;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!sent)
            {
                sent = true;
                buffer.Span[0] = 0x89;
                return 1;
            }
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return 0;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() { }
        public override bool CanRead => true;
        public override bool CanWrite => false;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task An_image_is_deleted_from_the_store_by_its_own_command()
    {
        var (client, received, _) = Server(_ => new JsonObject { ["success"] = true, ["result"] = null });
        await client.DeleteImageAsync("abc123");
        Contract.AssertJson(JsonNode.Parse("""{"id":1,"type":"image/delete","image_id":"abc123"}"""), received[1]);
    }

    [Fact]
    public async Task A_dashboard_is_read_as_served_and_its_absence_is_an_answer()
    {
        var (found, received, _) = Server(_ => new JsonObject { ["success"] = true, ["result"] = new JsonObject { ["views"] = new JsonArray() } });
        var dashboard = await found.ReadDashboardAsync("agent-decisions");
        Assert.True(dashboard.Found);
        Assert.Equal("""{"views":[]}""", dashboard.Json);
        Assert.Equal("agent-decisions", received[1]["url_path"]!.GetValue<string>());

        var (missing, _, _) = Server(_ => new JsonObject { ["success"] = false, ["error"] = new JsonObject { ["code"] = "config_not_found", ["message"] = "Unknown config specified: agent-decisions" } });
        Assert.False((await missing.ReadDashboardAsync("agent-decisions")).Found);

        var (failing, _, _) = Server(_ => new JsonObject { ["success"] = false, ["error"] = new JsonObject { ["code"] = "unknown_error", ["message"] = "boom" } });
        await Assert.ThrowsAsync<HaCommandException>(() => failing.ReadDashboardAsync("agent-decisions"));
    }

    [Fact]
    public async Task A_retained_message_arriving_while_a_later_filter_is_subscribed_is_still_read()
    {
        var client = new HomeAssistantClient(new Uri("https://ha.test"), Token, new HttpClient(new Handler((_, _) => new HttpResponseMessage(HttpStatusCode.OK))), (uri, ct) =>
        {
            var (c, s) = Pipe.Pair();
            var server = WebSocket.CreateFromStream(s, new WebSocketCreationOptions { IsServer = true });
            _ = Task.Run(async () =>
            {
                await Send(server, new JsonObject { ["type"] = "auth_required" });
                await Receive(server);
                await Send(server, new JsonObject { ["type"] = "auth_ok" });
                var first = await Receive(server);
                await Send(server, new JsonObject { ["id"] = first["id"]!.GetValue<int>(), ["type"] = "result", ["success"] = true });
                var second = await Receive(server);
                // The first filter's retained message, before the second subscription's result.
                await Send(server, new JsonObject
                {
                    ["id"] = first["id"]!.GetValue<int>(), ["type"] = "event",
                    ["event"] = new JsonObject { ["topic"] = "copilot/cli/a/status/state", ["payload"] = "idle", ["retain"] = true },
                });
                await Send(server, new JsonObject { ["id"] = second["id"]!.GetValue<int>(), ["type"] = "result", ["success"] = true });
            });
            return Task.FromResult(WebSocket.CreateFromStream(c, new WebSocketCreationOptions { IsServer = false }));
        });

        var read = await client.ReadTopicsAsync(["copilot/cli/a/#", "copilot/cli/b/#"], TimeSpan.FromMilliseconds(300));

        Assert.Equal(new HaMessage("copilot/cli/a/status/state", "idle", true), Assert.Single(read));
    }

    static Task Send(WebSocket ws, JsonObject message) =>
        ws.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None);

    static async Task<JsonObject> Receive(WebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
        return JsonNode.Parse(buffer.AsSpan(0, r.Count))!.AsObject();
    }

    [Fact]
    public async Task A_refused_token_fails_the_command()
    {
        var (client, _, _) = Server(_ => new JsonObject { ["success"] = true }, accept: "some-other-token");
        var e = await Assert.ThrowsAsync<HaCommandException>(() => client.ListEntityRegistryAsync());
        Assert.Equal("auth_invalid", e.Code);
    }
}
