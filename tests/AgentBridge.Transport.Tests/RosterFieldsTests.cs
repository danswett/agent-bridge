using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Transport.Tests;

// #1400: the relay's session registry, the roster fields an agent reads (docs/protocol/roster.md).
[Trait("Category", "Network")]
public sealed class RosterFieldsTests
{
    const string Epoch = "0123456789abcdef";

    static JsonObject Record(string machine, long seq = 1, string name = "build", string epoch = Epoch, DateTimeOffset? at = null) => new()
    {
        ["v"] = 1, ["machine"] = machine, ["epoch"] = epoch, ["seq"] = seq,
        ["at"] = (at ?? DateTimeOffset.UtcNow).ToString("o"), ["truncated"] = false,
        ["sessions"] = new JsonArray(new JsonObject
        {
            ["id"] = "aaaaaaaaaaaaaaaa", ["node"] = "agent_bridge_aaaaaaaaaaaaaaaa",
            ["name"] = name, ["state"] = "working", ["since"] = DateTimeOffset.UtcNow.ToString("o"),
        }),
    };

    sealed class Wirer
    {
        readonly ConcurrentQueue<JsonElement> frames = new();
        public Wirer(Device d) => d.Relay.Unhandled += frames.Enqueue;

        async Task<List<JsonElement>> UntilAsync(Func<JsonElement, bool> done)
        {
            var got = new List<JsonElement>();
            var end = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < end)
            {
                while (frames.TryDequeue(out var f)) { got.Add(f); if (done(f)) { return got; } }
                await Task.Delay(20);
            }
            throw new TimeoutException();
        }

        public async Task<string?> PutAsync(Device d, JsonNode record)
        {
            await d.Relay.SendRawAsync(JsonSerializer.SerializeToUtf8Bytes(new { t = "registry-put", id = 1, record }), System.Net.WebSockets.WebSocketMessageType.Text);
            var r = (await UntilAsync(f => f.GetProperty("t").GetString() == "registry-result"))[^1];
            return r.GetProperty("error").GetString();
        }

        public async Task<List<JsonElement>> GetAsync(Device d)
        {
            await d.Relay.SendRawAsync(JsonSerializer.SerializeToUtf8Bytes(new { t = "registry-get", id = 2 }), System.Net.WebSockets.WebSocketMessageType.Text);
            return await UntilAsync(f => f.TryGetProperty("known", out _) || f.TryGetProperty("error", out _));
        }
    }

    [Fact]
    public async Task A_machine_record_carries_only_the_allow_listed_fields_and_is_read_by_the_same_account()
    {
        await using var relay = await TestRelay.StartAsync();
        await using var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "dev-box", "host");
        await using var app = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "win", "app");
        var host1 = new Wirer(host);
        var reader = new Wirer(app);

        Assert.Null(await host1.PutAsync(host, Record(host.Id)));
        var frames = await reader.GetAsync(app);

        var all = string.Join("|", frames);
        var part = frames.SingleOrDefault(f => f.TryGetProperty("machine", out _));
        Assert.True(part.ValueKind == JsonValueKind.Object, all);
        Assert.True(part.GetProperty("online").GetBoolean());
        Assert.Equal("dev-box", part.GetProperty("name").GetString());
        var s = part.GetProperty("record").GetProperty("sessions")[0];
        Assert.Equal(["id", "name", "node", "since", "state"], s.EnumerateObject().Select(p => p.Name).Order());
        Assert.Equal("build", s.GetProperty("name").GetString());
    }

    [Theory]
    [InlineData("prompt", "fix the login bug")]
    [InlineData("cwd", @"C:\work\repo")]
    [InlineData("token", "ghp_secret")]
    public async Task A_session_with_a_field_off_the_allow_list_is_refused_and_never_held_or_logged(string field, string value)
    {
        await using var relay = await TestRelay.StartAsync();
        await using var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "dev-box", "host");
        var w = new Wirer(host);
        var rec = Record(host.Id);
        rec["sessions"]![0]![field] = value;

        Assert.Equal("bad-record", await w.PutAsync(host, rec));
        Assert.DoesNotContain(await w.GetAsync(host), f => f.TryGetProperty("machine", out _));
        Assert.DoesNotContain(relay.Logs, l => l.Contains(value) || l.Contains("build"));
    }

    [Fact]
    public async Task Another_account_never_sees_the_records()
    {
        await using var relay = await TestRelay.StartAsync();
        await using var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "dev-box", "host");
        await using var other = await relay.ConnectAsync(relay.TokenFor(2, "mona"), "win", "app");
        Assert.Null(await new Wirer(host).PutAsync(host, Record(host.Id)));

        var frames = await new Wirer(other).GetAsync(other);

        Assert.DoesNotContain(frames, f => f.TryGetProperty("machine", out _));
    }

    [Fact]
    public async Task A_record_for_another_machine_an_over_size_record_and_a_stale_sequence_are_refused_with_the_socket_open()
    {
        await using var relay = await TestRelay.StartAsync();
        await using var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "dev-box", "host");
        var w = new Wirer(host);

        Assert.Equal("bad-record", await w.PutAsync(host, Record("bbbbbbbbbbbbbbbb")));
        Assert.Equal("too-large", await w.PutAsync(host, Record(host.Id, name: new string('x', 20_000))));
        Assert.Null(await w.PutAsync(host, Record(host.Id, seq: 5)));
        Assert.Equal("stale", await w.PutAsync(host, Record(host.Id, seq: 5)));
        Assert.Equal("stale", await w.PutAsync(host, Record(host.Id, seq: 2)));
        Assert.Null(await w.PutAsync(host, Record(host.Id, seq: 6)));
    }

    [Fact]
    public async Task A_machine_that_leaves_is_not_online_and_one_that_goes_quiet_for_a_minute_loses_its_record()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var relay = await TestRelay.StartAsync(o => o.Roster.Time = time);
        await using var app = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "win", "app");
        var reader = new Wirer(app);
        await using var quiet = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "quiet", "host");
        Assert.Null(await new Wirer(quiet).PutAsync(quiet, Record(quiet.Id)));
        var gone = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "gone", "host");
        Assert.Null(await new Wirer(gone).PutAsync(gone, Record(gone.Id)));

        time.Advance(TimeSpan.FromSeconds(61));
        await gone.DisposeAsync();
        await Task.Delay(300);
        var frames = await reader.GetAsync(app);

        var parts = frames.Where(f => f.TryGetProperty("machine", out _)).ToList();
        var q = Assert.Single(parts);
        Assert.False(q.GetProperty("online").GetBoolean());
        Assert.Equal(JsonValueKind.Null, q.GetProperty("record").ValueKind);
    }
}
