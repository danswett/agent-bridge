using AgentBridge.Relay;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
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

        public async Task<JsonElement> ChangedAsync(Func<JsonElement, bool> match) =>
            (await UntilAsync(f => f.GetProperty("t").GetString() == "registry-changed" && match(f)))[^1];

        // Registry-changed frames received so far: what a device that never asked must not see.
        public List<JsonElement> Drain()
        {
            var got = new List<JsonElement>();
            while (frames.TryDequeue(out var f)) { if (f.GetProperty("t").GetString() == "registry-changed") { got.Add(f); } }
            return got;
        }

        public async Task<List<JsonElement>> GetAsync(Device d)
        {
            await d.Relay.SendRawAsync(JsonSerializer.SerializeToUtf8Bytes(new { t = "registry-get", id = 2 }), System.Net.WebSockets.WebSocketMessageType.Text);
            return await UntilAsync(f => f.TryGetProperty("known", out _) || f.TryGetProperty("error", out _));
        }
    }

    [Fact]
    public async Task Registry_replies_keep_sixty_four_maximum_plus_names_below_the_actual_reply_byte_budget()
    {
        await using var relay = await TestRelay.StartAsync();
        await using var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "publisher", "host");
        var record = Record(host.Id, name: new string('+', 64));
        var sessions = record["sessions"]!.AsArray();
        var first = sessions[0]!.DeepClone();
        for (var i = 1; i < 64; i++) { sessions.Add(first.DeepClone()); }
        var json = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        await host.Relay.RegistryPutAsync(JsonSerializer.SerializeToElement(record, json));
        var frames = await new Wirer(host).GetAsync(host);
        Assert.All(frames, frame =>
        {
            // GetRawText preserves the received frame's escapes, unlike reserialization.
            var bytes = System.Text.Encoding.UTF8.GetByteCount(frame.GetRawText());
            Assert.True(bytes <= AgentBridge.Relay.RosterFields.MaxFrameBytes, $"received {bytes} bytes");
        });
        Assert.Equal(64, Assert.Single(frames, f => f.TryGetProperty("record", out _)).GetProperty("record").GetProperty("sessions").GetArrayLength());
    }

    [Fact]
    public async Task RegistryPutAsync_publishes_its_hosts_record_and_refuses_a_different_machine()
    {
        await using var relay = await TestRelay.StartAsync();
        await using var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "publisher", "host");
        await host.Relay.RegistryPutAsync(JsonSerializer.SerializeToElement(Record(host.Id)));
        var records = await host.Relay.RegistryGetAsync();
        Assert.Equal(host.Id, Assert.Single(records).GetProperty("machine").GetString());
        var wrong = JsonSerializer.SerializeToElement(Record(new string('f', 32)));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Relay.RegistryPutAsync(wrong));
        Assert.Contains("bad-record", error.Message);
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.Relay.RegistryPutAsync(wrong, stop.Token));
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
    public async Task Every_allow_listed_field_is_accepted_and_returned_and_an_unlisted_one_at_any_level_is_refused()
    {
        await using var relay = await TestRelay.StartAsync();
        await using var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "dev-box", "host");
        var w = new Wirer(host);
        var rec = Record(host.Id);
        rec["truncated"] = true; rec["total"] = 65; rec["sig"] = "abc-_123";

        Assert.Null(await w.PutAsync(host, rec));
        var part = (await w.GetAsync(host)).Single(f => f.TryGetProperty("machine", out _));
        Assert.Equal(["at", "epoch", "machine", "seq", "sessions", "sig", "total", "truncated", "v"], part.GetProperty("record").EnumerateObject().Select(p => p.Name).Order());

        var top = Record(host.Id, seq: 2); top["path"] = "x";
        Assert.Equal("bad-record", await w.PutAsync(host, top));
        var nested = Record(host.Id, seq: 3); nested["sessions"]![0]!["state"] = new JsonObject { ["prompt"] = "x" };
        Assert.Equal("bad-record", await w.PutAsync(host, nested));
    }

    [Theory]
    [InlineData("v", "\"1\"")]
    [InlineData("machine", "7")]
    [InlineData("epoch", "5")]
    [InlineData("seq", "\"5\"")]
    [InlineData("at", "5")]
    [InlineData("truncated", "\"no\"")]
    [InlineData("sessions", "\"x\"")]
    [InlineData("total", "\"x\"")]
    [InlineData("sig", "5")]
    public async Task A_wrongly_typed_record_member_is_refused_and_the_socket_stays_open(string member, string raw)
    {
        await using var relay = await TestRelay.StartAsync();
        await using var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "dev-box", "host");
        var w = new Wirer(host);
        var rec = Record(host.Id);
        if (member is "total" or "sig") { rec["truncated"] = true; }
        rec[member] = JsonNode.Parse(raw);

        Assert.Equal("bad-record", await w.PutAsync(host, rec));
        Assert.Null(await w.PutAsync(host, Record(host.Id, seq: 2)));
    }

    [Theory]
    [InlineData("id", "5")]
    [InlineData("node", "5")]
    [InlineData("state", "5")]
    [InlineData("state", "\"sleeping\"")]
    [InlineData("since", "5")]
    [InlineData("name", "5")]
    [InlineData("name", "\"line\\nbreak\"")]
    public async Task A_wrongly_typed_or_invalid_session_member_is_refused_and_the_socket_stays_open(string member, string raw)
    {
        await using var relay = await TestRelay.StartAsync();
        await using var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "dev-box", "host");
        var w = new Wirer(host);
        var rec = Record(host.Id);
        rec["sessions"]![0]![member] = JsonNode.Parse(raw);

        Assert.Equal("bad-record", await w.PutAsync(host, rec));
        Assert.Null(await w.PutAsync(host, Record(host.Id, seq: 2)));
    }

    [Fact]
    public async Task More_than_the_session_limit_is_refused_and_known_lists_online_machines_too()
    {
        await using var relay = await TestRelay.StartAsync();
        await using var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "dev-box", "host");
        var w = new Wirer(host);
        var many = Record(host.Id);
        var s = many["sessions"]!.AsArray();
        var first = s[0]!.ToJsonString();
        for (var i = 0; i < 64; i++) { s.Add(JsonNode.Parse(first)); }
        Assert.Equal("bad-record", await w.PutAsync(host, many));

        Assert.Null(await w.PutAsync(host, Record(host.Id, seq: 2)));
        var known = (await w.GetAsync(host)).Single(f => f.TryGetProperty("known", out _)).GetProperty("known");
        Assert.Contains(known.EnumerateArray(), k => k.GetProperty("machine").GetString() == host.Id);
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
    public async Task RegistryGetAsync_returns_the_machines_then_a_read_past_the_socket_burst_is_refused_readably()
    {
        await using var relay = await TestRelay.StartAsync(o => o.Roster.Time = new FakeTimeProvider(DateTimeOffset.UtcNow));
        await using var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "dev-box", "host");
        Assert.Null(await new Wirer(host).PutAsync(host, Record(host.Id)));
        var machines = await host.Relay.RegistryGetAsync(CancellationToken.None);
        Assert.Equal("dev-box", Assert.Single(machines).GetProperty("name").GetString());
        for (var i = 1; i < RegistryReadLimit.SocketBurst; i++) { await host.Relay.RegistryGetAsync(CancellationToken.None); }
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Relay.RegistryGetAsync(CancellationToken.None));
        Assert.StartsWith("refused:", e.Message);
        Assert.Contains("the relay allows one registry read every", e.Message); // the fleet tools match this text
        Assert.Contains("try again in a few seconds", e.Message);
    }

    // #1396: the socket may burst, then reads at a steady rate; one account's sockets share a ceiling.
    static async Task<int> ReadsAllowedAsync(Wirer w, Device d, int tries)
    {
        var allowed = 0;
        for (var i = 0; i < tries; i++) { if (!(await w.GetAsync(d)).Any(f => f.TryGetProperty("error", out _))) { allowed++; } }
        return allowed;
    }

    [Fact]
    public async Task A_socket_may_read_its_burst_at_once_and_then_one_token_per_refill_interval()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var relay = await TestRelay.StartAsync(o => o.Roster.Time = time);
        await using var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "dev-box", "host");
        var w = new Wirer(host);
        Assert.Equal(RegistryReadLimit.SocketBurst, await ReadsAllowedAsync(w, host, RegistryReadLimit.SocketBurst + 5));
        time.Advance(TimeSpan.FromSeconds(1.0 / RegistryReadLimit.SocketRefillPerSecond));
        Assert.Equal(1, await ReadsAllowedAsync(w, host, 3));
    }

    [Fact]
    public async Task An_idle_socket_never_banks_more_than_its_burst()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var relay = await TestRelay.StartAsync(o => o.Roster.Time = time);
        await using var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "dev-box", "host");
        time.Advance(TimeSpan.FromHours(1));
        Assert.Equal(RegistryReadLimit.SocketBurst, await ReadsAllowedAsync(new Wirer(host), host, RegistryReadLimit.SocketBurst + 5));
    }

    [Fact]
    public async Task One_accounts_sockets_share_the_account_ceiling_and_another_account_is_untouched()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var relay = await TestRelay.StartAsync(o => o.Roster.Time = time);
        var perSocket = RegistryReadLimit.SocketBurst;
        var sockets = new List<Device>();
        for (var i = 0; i < 4; i++) { sockets.Add(await relay.ConnectAsync(relay.TokenFor(1, "octo"), "box-" + i, "host")); }
        var total = 0;
        foreach (var d in sockets) { total += await ReadsAllowedAsync(new Wirer(d), d, perSocket); }
        Assert.Equal(RegistryReadLimit.AccountBurst, total);
        await using var other = await relay.ConnectAsync(relay.TokenFor(2, "mona"), "other-box", "host");
        Assert.Equal(perSocket, await ReadsAllowedAsync(new Wirer(other), other, perSocket));
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(await ReadsAllowedAsync(new Wirer(sockets[3]), sockets[3], perSocket) > 0);
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

    // Slice 2b (design "Relay frames"): a device opts in with registry-subscribe. It sends the get
    // after, so the reply proves the subscription was already in place on that socket.
    static async Task<Wirer> SubscribedAsync(Device d)
    {
        var w = new Wirer(d);
        await d.Relay.SendRawAsync(JsonSerializer.SerializeToUtf8Bytes(new { t = "registry-subscribe" }), System.Net.WebSockets.WebSocketMessageType.Text);
        await w.GetAsync(d);
        return w;
    }

    [Fact]
    public async Task A_subscribed_device_hears_each_accepted_snapshot_in_order_and_nobody_else_does()
    {
        await using var relay = await TestRelay.StartAsync();
        await using var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "dev-box", "host");
        await using var watcher = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "win", "app");
        await using var plain = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "phone", "pwa");
        await using var stranger = await relay.ConnectAsync(relay.TokenFor(2, "mallory"), "win", "app");
        var heard = await SubscribedAsync(watcher);
        var plainWire = new Wirer(plain);
        var strangerWire = await SubscribedAsync(stranger);
        var put = new Wirer(host);

        Assert.Null(await put.PutAsync(host, Record(host.Id, seq: 1, name: "first")));
        Assert.Equal("stale", await put.PutAsync(host, Record(host.Id, seq: 1, name: "refused")));
        Assert.Null(await put.PutAsync(host, Record(host.Id, seq: 2, name: "second")));

        var first = await heard.ChangedAsync(_ => true);
        var second = await heard.ChangedAsync(_ => true);
        Assert.Equal(host.Id, first.GetProperty("machine").GetString());
        Assert.True(first.GetProperty("online").GetBoolean());
        Assert.Equal(["first", "second"], new[] { first, second }.Select(f => f.GetProperty("record").GetProperty("sessions")[0].GetProperty("name").GetString()));
        await Task.Delay(300);
        Assert.Empty(plainWire.Drain());
        Assert.Empty(strangerWire.Drain());
        Assert.Empty(heard.Drain());
    }

    [Fact]
    public async Task A_subscriber_hears_a_machine_offline_when_it_leaves_and_when_it_goes_quiet_for_a_minute_without_asking()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var relay = await TestRelay.StartAsync(o => o.Roster.Time = time);
        await using var watcher = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "win", "app");
        await using var quiet = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "quiet", "host");
        var gone = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "gone", "host");
        var heard = await SubscribedAsync(watcher);
        Assert.Null(await new Wirer(quiet).PutAsync(quiet, Record(quiet.Id)));
        Assert.Null(await new Wirer(gone).PutAsync(gone, Record(gone.Id)));
        // In the order they were put: waiting skips the frames it passes.
        await heard.ChangedAsync(f => f.GetProperty("machine").GetString() == quiet.Id);
        await heard.ChangedAsync(f => f.GetProperty("machine").GetString() == gone.Id);

        await gone.DisposeAsync();
        var left = await heard.ChangedAsync(f => f.GetProperty("machine").GetString() == gone.Id);
        time.Advance(TimeSpan.FromSeconds(61));
        var silent = await heard.ChangedAsync(f => f.GetProperty("machine").GetString() == quiet.Id);

        Assert.All(new[] { left, silent }, f =>
        {
            Assert.False(f.GetProperty("online").GetBoolean());
            Assert.Equal(JsonValueKind.Null, f.GetProperty("record").ValueKind);
        });
    }

    // Counts the relay's one-second sweeper timers; every other call goes to the wrapped clock.
    sealed class CountingTime(TimeProvider inner) : TimeProvider
    {
        public int Sweepers;
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp() => inner.GetTimestamp();
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => inner.LocalTimeZone;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            if (dueTime != TimeSpan.FromSeconds(1) || period != TimeSpan.FromSeconds(1)) { return timer; }
            Interlocked.Increment(ref Sweepers);
            return new Counted(timer, () => Interlocked.Increment(ref Disposed));
        }

        public int Disposed;

        sealed class Counted(ITimer timer, Action disposed) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => timer.Change(dueTime, period);
            public void Dispose() { disposed(); timer.Dispose(); }
            public ValueTask DisposeAsync() { disposed(); return timer.DisposeAsync(); }
        }
    }

    [Fact]
    public async Task However_many_devices_subscribe_the_relay_runs_one_sweeper_none_before_the_first_and_drain_disposes_it()
    {
        var time = new CountingTime(new FakeTimeProvider(DateTimeOffset.UtcNow));
        await using var relay = await TestRelay.StartAsync(o => o.Roster.Time = time);
        Assert.Equal(0, time.Sweepers);
        var devices = new List<Device>();
        try
        {
            for (var i = 0; i < 6; i++)
            {
                var d = await relay.ConnectAsync(relay.TokenFor(1, "octo"), $"win{i}", "app");
                devices.Add(d);
                await SubscribedAsync(d);
            }
            Assert.Equal(1, time.Sweepers);
            Assert.Equal(0, time.Disposed);
            await relay.App.Services.GetRequiredService<AgentBridge.Relay.RelayHub>().DrainAsync();
            Assert.Equal(1, time.Disposed);
        }
        finally { foreach (var d in devices) { await d.DisposeAsync(); } }
    }

    [Fact]
    public async Task A_machine_that_went_quiet_is_announced_offline_once_even_if_it_then_disconnects()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var relay = await TestRelay.StartAsync(o => o.Roster.Time = time);
        await using var watcher = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "win", "app");
        var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "quiet", "host");
        var heard = await SubscribedAsync(watcher);
        Assert.Null(await new Wirer(host).PutAsync(host, Record(host.Id)));
        await heard.ChangedAsync(_ => true);

        time.Advance(TimeSpan.FromSeconds(61));
        await heard.ChangedAsync(f => !f.GetProperty("online").GetBoolean());
        await host.DisposeAsync();
        await Task.Delay(500);

        Assert.Empty(heard.Drain());
    }

    [Fact]
    public async Task A_machine_that_went_quiet_and_publishes_again_is_announced_offline_then_online_in_that_order()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var relay = await TestRelay.StartAsync(o => o.Roster.Time = time);
        await using var watcher = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "win", "app");
        await using var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "quiet", "host");
        var heard = await SubscribedAsync(watcher);
        var put = new Wirer(host);
        Assert.Null(await put.PutAsync(host, Record(host.Id, seq: 1)));
        await heard.ChangedAsync(_ => true);

        time.Advance(TimeSpan.FromSeconds(61));
        await heard.ChangedAsync(f => !f.GetProperty("online").GetBoolean());
        Assert.Null(await put.PutAsync(host, Record(host.Id, seq: 2, at: time.GetUtcNow())));

        var back = await heard.ChangedAsync(_ => true);
        Assert.True(back.GetProperty("online").GetBoolean());
        Assert.Equal(2, back.GetProperty("record").GetProperty("seq").GetInt64());
    }

    [Fact]
    public async Task A_forgotten_machine_is_announced_removed_to_the_subscribers_of_its_own_account_only()
    {
        await using var relay = await TestRelay.StartAsync();
        await using var person = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "win", "app");
        await using var stranger = await relay.ConnectAsync(relay.TokenFor(2, "mallory"), "win", "app");
        var host = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "dev-box", "host");
        var heard = await SubscribedAsync(person);
        var strangerWire = await SubscribedAsync(stranger);
        Assert.Null(await new Wirer(host).PutAsync(host, Record(host.Id)));
        await host.DisposeAsync();
        await Wait.UntilAsync(() => person.Relay.Known?.Any(k => k.Id == host.Id) == true, "the host listed as offline");

        Assert.Null(await person.Relay.ForgetAsync(host.Id));

        var removed = await heard.ChangedAsync(f => f.TryGetProperty("removed", out _));
        Assert.Equal(host.Id, removed.GetProperty("machine").GetString());
        Assert.True(removed.GetProperty("removed").GetBoolean());
        await Task.Delay(300);
        Assert.Empty(strangerWire.Drain());
    }
}
