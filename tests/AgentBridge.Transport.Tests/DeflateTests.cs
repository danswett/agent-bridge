using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AgentBridge.Transport.Tests;

// Per-message deflate inside a channel (#603).
public class DeflateTests
{
    [Theory]
    [InlineData(149)]
    [InlineData(150)]
    [InlineData(511)]
    public async Task Pwa_boundary_messages_round_trip_through_the_host_codec(int bytes)
    {
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { text = new string('x', bytes - 11) }));
        Assert.Equal(bytes, plain.Length);
        var packed = await PwaCodec("pack", plain);
        Assert.Equal(bytes < 150 ? (byte)'{' : MessageDeflate.Marker, packed[0]);
        var received = MessageDeflate.Unpack(packed, SecureChannel.MaxMessageBytes);
        Assert.Equal(plain, received);
        Assert.Equal(plain, await PwaCodec("unpack", MessageDeflate.Pack(received)));
    }

    static async Task<byte[]> PwaCodec(string mode, byte[] input)
    {
        DirectoryInfo? root = new(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "pwa", "js", "channels.js"))) { root = root.Parent; }
        Assert.NotNull(root);
        var module = new Uri(Path.Combine(root.FullName, "pwa", "js", "channels.js")).AbsoluteUri;
        var start = new ProcessStartInfo("node")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            UseShellExecute = false
        };
        start.ArgumentList.Add("--input-type=module");
        start.ArgumentList.Add("--eval");
        start.ArgumentList.Add("""
            const codec = await import(process.argv[1]);
            let input = ''; for await (const chunk of process.stdin) { input += chunk; }
            process.stdout.write(Buffer.from(await codec[process.argv[2]](Buffer.from(input, 'base64'))).toString('base64'));
            """);
        start.ArgumentList.Add(module);
        start.ArgumentList.Add(mode);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("The PWA codec could not start.");
        await process.StandardInput.WriteAsync(Convert.ToBase64String(input));
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); } }
        Assert.True(process.ExitCode == 0, await error);
        return Convert.FromBase64String(await output);
    }

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

    [Theory]
    [InlineData(150)]
    [InlineData(240)]
    [InlineData(511)]
    public void A_patch_sized_message_of_150_to_511_bytes_deflates_when_it_shrinks_and_inflates_back(int bytes)
    {
        const string patch = """{"ev":"session.patch","seq":1,"data":{"append":{"activity":{"response":""}}}}""";
        var plain = Encoding.UTF8.GetBytes(patch.Replace("\"\"", JsonSerializer.Serialize(new string('x', bytes - patch.Length))));

        var packed = MessageDeflate.Pack(plain);

        Assert.Equal(bytes, plain.Length);
        Assert.Equal(MessageDeflate.Marker, packed[0]);
        Assert.True(packed.Length < plain.Length, $"{plain.Length} bytes packed to {packed.Length}");
        Assert.Equal(plain, MessageDeflate.Unpack(packed, SecureChannel.MaxMessageBytes));
    }

    [Theory]
    [InlineData(150)]
    [InlineData(511)]
    [InlineData(4096)]
    public void A_small_message_or_one_that_does_not_shrink_goes_as_it_is(int bytes)
    {
        var small = Encoding.UTF8.GetBytes("""{"id":1,"op":"hello","data":{}}""");
        var belowFloor = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { text = new string('x', 138) }));
        var noise = new byte[bytes];
        new Random(7).NextBytes(noise);
        noise[0] = (byte)'{';

        Assert.Equal(149, belowFloor.Length);
        Assert.Same(belowFloor, MessageDeflate.Pack(belowFloor));
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
            app.Hub.SaysInflates = false;
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
