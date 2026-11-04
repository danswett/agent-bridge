using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AgentBridge.Transport.Tests;

// Per-message deflate inside a channel (#603).
public class DeflateTests
{
    // A session list as a host sends it: field names and status words over and over.
    static string Sessions(int n) => JsonSerializer.Serialize(new
    {
        re = 3,
        ok = true,
        result = new
        {
            sessions = Enumerable.Range(0, n).Select(i => new
            {
                id = $"{i:x8}-0000-4000-8000-000000000000",
                agent = "copilot",
                status = i % 3 == 0 ? "working" : "idle",
                name = $"Session {i}",
                cwd = $"C:\\Users\\someone\\repos\\project-{i % 7}",
                activity = new { response = "Done. The tests pass and the change is pushed.", tool = "powershell" },
            }),
        },
    });

    [Fact]
    public void A_message_deflates_to_less_than_half_and_inflates_back_byte_for_byte()
    {
        var plain = Encoding.UTF8.GetBytes(Sessions(80));

        var packed = MessageDeflate.Pack(plain);

        Assert.Equal(MessageDeflate.Marker, packed[0]);
        Assert.True(packed.Length * 2 < plain.Length, $"{plain.Length} bytes packed to {packed.Length}");
        Assert.Equal(plain, MessageDeflate.Unpack(packed, SecureChannel.MaxMessageBytes));
    }

    [Fact]
    public void A_small_message_or_one_that_does_not_shrink_goes_as_it_is()
    {
        var small = Encoding.UTF8.GetBytes("""{"id":1,"op":"hello","data":{}}""");
        var noise = new byte[4096];
        new Random(7).NextBytes(noise);
        noise[0] = (byte)'{';

        Assert.Same(small, MessageDeflate.Pack(small));
        Assert.Same(noise, MessageDeflate.Pack(noise));
        Assert.Same(small, MessageDeflate.Unpack(small, SecureChannel.MaxMessageBytes));
    }

    // Raw DEFLATE (RFC 1951), not zlib's wrapping of it: one stored block, as any inflater writes
    // and reads it. The PWA's unit tests read the same bytes (channels.test.mjs).
    [Fact]
    public void A_deflated_message_is_raw_DEFLATE_with_no_zlib_header()
    {
        byte[] stored = [MessageDeflate.Marker, 0x01, 0x05, 0x00, 0xfa, 0xff, .. "hello"u8];

        Assert.Equal("hello"u8.ToArray(), MessageDeflate.Unpack(stored, 100));
    }

    [Fact]
    public void A_message_that_inflates_past_what_a_sender_may_send_or_is_corrupt_is_refused()
    {
        using var bomb = new MemoryStream();
        bomb.WriteByte(MessageDeflate.Marker);
        using (var z = new DeflateStream(bomb, CompressionLevel.SmallestSize, leaveOpen: true)) { z.Write(new byte[10 * 1024 * 1024]); }
        byte[] corrupt = [MessageDeflate.Marker, 0xff, 0xff, 0xff, 0xff];

        Assert.True(bomb.Length < SecureChannel.MaxMessageBytes);
        Assert.Throws<FormatException>(() => MessageDeflate.Unpack(bomb.ToArray(), SecureChannel.MaxMessageBytes));
        Assert.Throws<FormatException>(() => MessageDeflate.Unpack(corrupt, SecureChannel.MaxMessageBytes));
        Assert.True(ChannelHub.EndsChannel(new FormatException()));
    }

    // Both ends say they inflate: a large message crosses deflated. One that does not say so (an
    // older peer) is sent JSON as it always was.
    [Theory]
    [Trait("Category", "Network")]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_large_message_crosses_the_relay_deflated_only_to_a_peer_that_said_it_inflates(bool inflates)
    {
        var sizes = new ConcurrentQueue<(string From, int Bytes)>();
        await using var relay = await TestRelay.StartAsync(o =>
        {
            var tap = o.Tap;
            o.Tap = (from, to, payload) => { tap?.Invoke(from, to, payload); sizes.Enqueue((from, payload.Length)); };
        });
        var token = relay.TokenFor(1, "octo");
        var host = await relay.ConnectAsync(token, "host-a", "host");
        var app = await relay.ConnectAsync(token, "app-b", "app");
        await app.Relay.WaitForPeerAsync(p => p.Id == host.Id, TimeSpan.FromSeconds(10));
        if (!inflates)
        {
            // As an app from before: its introduction does not say it inflates.
            typeof(ChannelHub).GetField("intro", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .SetValue(app.Hub, JsonSerializer.SerializeToUtf8Bytes(new { name = "app-b", kind = "app" }));
        }
        var ch = await app.Hub.OpenAsync(host.Id);
        var text = Sessions(60);
        sizes.Clear();

        var reply = await ch.RequestAsync("echo", new { text });

        Assert.Equal(text, reply.GetProperty("result").GetProperty("text").GetString());
        var fromHost = sizes.Where(s => s.From == host.Id).Sum(s => s.Bytes);
        var fromApp = sizes.Where(s => s.From == app.Id).Sum(s => s.Bytes);
        var json = JsonSerializer.SerializeToUtf8Bytes(new { text }).Length;
        if (inflates)
        {
            Assert.True(fromApp * 2 < json && fromHost * 2 < json, $"app sent {fromApp}, host {fromHost}, for {json} bytes of JSON");
        }
        else
        {
            // The app still sends deflated: the host said it inflates. The host does not.
            Assert.True(fromApp * 2 < json, $"app sent {fromApp} for {json} bytes of JSON");
            Assert.True(fromHost > json, $"host sent {fromHost} for {json} bytes of JSON");
        }
    }
}
