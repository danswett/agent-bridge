using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AgentBridge.Hooks;
using Xunit;

namespace AgentBridge.Hooks.Tests;

// #1003 PR C, the hook's end of approval.wait (docs/design/claude-approval-channel.md): over the host's pipe (4-byte little-endian length, then UTF-8 JSON) the hook
// says hello, sends the Claude PermissionRequest event with approval.wait, and takes the verdict from the one reply. Anything but exactly allow or deny is none.
// It sends nothing before it has verified the server is the host. Real pipes, no host.
public sealed class ApprovalClientTests
{
    const string Event = """{"session_id":"s-1","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"echo <hello> & \"x\" é"}}""";
    const string Allow = """{"re":2,"ok":true,"result":{"verdict":"allow"}}""";
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    static async Task<bool> Fill(Stream s, byte[] buffer)
    {
        for (var read = 0; read < buffer.Length;)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read));
            if (n == 0) { return false; }
            read += n;
        }
        return true;
    }

    // Reads one frame into `got`; nothing is added when the client hangs up first.
    static async Task Take(Stream s, List<string> got)
    {
        var head = new byte[4];
        if (!await Fill(s, head)) { return; }
        var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(head)];
        if (await Fill(s, body)) { got.Add(Encoding.UTF8.GetString(body)); }
    }

    static async Task Send(Stream s, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var head = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(head, body.Length);
        await s.WriteAsync(head);
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    // A host serving one connection with `script`; Received holds the frames it read.
    sealed class FakeHost : IAsyncDisposable
    {
        public string Name { get; } = "ab-appr-" + Guid.NewGuid().ToString("N")[..12];
        public List<string> Received { get; } = [];
        readonly NamedPipeServerStream server;
        readonly Task serving;

        public FakeHost(Func<Stream, List<string>, Task> script)
        {
            server = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            serving = Task.Run(async () => { await server.WaitForConnectionAsync(); await script(server, Received); });
        }

        public async ValueTask DisposeAsync()
        {
            server.Dispose();
            try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception e) when (e is IOException or ObjectDisposedException or TimeoutException) { }
        }
    }

    // Hello, then the one reply given for approval.wait (after an unrelated event frame).
    static FakeHost Host(string waitReply) => new(async (s, got) =>
    {
        await Take(s, got);
        await Send(s, """{"re":1,"ok":true,"result":{}}""");
        await Take(s, got);
        await Send(s, """{"ev":"session.upsert","seq":1,"data":{}}""");
        await Send(s, waitReply);
    });

    static Task<string?> Ask(FakeHost host, string eventText = Event, Func<NamedPipeClientStream, bool>? verify = null, TimeSpan? wait = null) =>
        ApprovalClient.AskHostAsync(host.Name, verify ?? (_ => true), eventText, wait ?? Wait, TimeSpan.FromSeconds(5));

    [Theory]
    [InlineData("allow")]
    [InlineData("deny")]
    public async Task The_hook_says_hello_sends_the_event_with_approval_wait_and_takes_the_hosts_verdict(string verdict)
    {
        await using var host = Host(Allow.Replace("allow", verdict));
        Assert.Equal(verdict, await Ask(host));
        Assert.Equal("""{"id":1,"op":"hello","data":{}}""", host.Received[0]);
        using var wait = JsonDocument.Parse(host.Received[1]);
        Assert.Equal((2, "approval.wait", Event), (wait.RootElement.GetProperty("id").GetInt32(), wait.RootElement.GetProperty("op").GetString(), wait.RootElement.GetProperty("data").GetProperty("event").GetString()));
    }

    [Theory]
    [InlineData("""{"re":2,"ok":true,"result":{"verdict":"none","reason":"off"}}""")]
    [InlineData("""{"re":2,"ok":true,"result":{"verdict":"ALLOW"}}""")]
    [InlineData("""{"re":2,"ok":true,"result":{"verdict":true}}""")]
    [InlineData("""{"re":2,"ok":true,"result":"allow"}""")]
    [InlineData("""{"re":2,"ok":true}""")]
    [InlineData("""{"re":2,"ok":false,"result":{"verdict":"allow"}}""")]
    [InlineData("""{"re":99,"ok":true,"result":{"verdict":"allow"}}""")]
    [InlineData("not json")]
    public async Task Anything_but_exactly_allow_or_deny_in_a_successful_reply_to_this_request_is_none(string reply)
    {
        await using var host = Host(reply);
        Assert.Null(await Ask(host, wait: TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task A_host_that_refuses_hello_is_not_sent_the_event()
    {
        await using var host = new FakeHost(async (s, got) => { await Take(s, got); await Send(s, """{"re":1,"ok":false,"error":"refused"}"""); await Take(s, got); });
        Assert.Null(await Ask(host));
        Assert.Single(host.Received);
    }

    [Fact]
    public async Task A_host_that_hangs_up_or_never_answers_or_sends_an_oversize_frame_is_none_and_the_wait_is_bounded()
    {
        await using var hangs = new FakeHost(async (s, got) => { await Take(s, got); s.Dispose(); });
        Assert.Null(await Ask(hangs));
        await using var big = new FakeHost(async (s, got) =>
        {
            await Take(s, got);
            var head = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(head, 65520);
            await s.WriteAsync(head);
            await s.FlushAsync();
        });
        Assert.Null(await Ask(big));
        await using var silent = new FakeHost(async (s, got) => { await Take(s, got); await Send(s, """{"re":1,"ok":true,"result":{}}"""); await Take(s, got); await Task.Delay(TimeSpan.FromSeconds(5)); });
        var started = DateTime.UtcNow;
        Assert.Null(await Ask(silent, wait: TimeSpan.FromMilliseconds(400)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task A_server_not_verified_as_the_host_and_an_oversize_event_are_sent_nothing()
    {
        await using var host = Host(Allow);
        Assert.Null(await Ask(host, verify: _ => false));
        Assert.Empty(host.Received);
        await using var other = Host(Allow);
        Assert.Null(await Ask(other, eventText: Event.Replace("echo", new string('x', ApprovalClient.MaxEventBytes))));
        Assert.Empty(other.Received);
    }

    [Fact]
    public async Task With_no_host_listening_the_answer_is_none_without_a_long_wait()
    {
        var started = DateTime.UtcNow;
        Assert.Null(await ApprovalClient.AskHostAsync("ab-appr-none-" + Guid.NewGuid().ToString("N")[..8], _ => true, Event, Wait, TimeSpan.FromMilliseconds(300)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void The_pipe_name_is_the_hosts_own_derivation_and_the_host_check_is_an_exact_path_never_on_a_missing_one()
    {
        Assert.Equal("AgentBridge.Host.1e659cb0b7056f9a", ApprovalClient.PipeName("S-1-5-21-1-2-3-1001", @"C:\Users\A\AppData\Local\AgentBridge\Default"));
        Assert.True(ApprovalClient.IsHostImage(@"C:\Program Files\Slipgates\host.exe", @"c:\program files\slipgates\host.exe"));
        Assert.False(ApprovalClient.IsHostImage(@"C:\Users\A\Temp\host.exe", @"C:\Program Files\Slipgates\host.exe"));
        Assert.False(ApprovalClient.IsHostImage(null, @"C:\x\host.exe"));
        Assert.False(ApprovalClient.IsHostImage(@"C:\x\host.exe", null));
        Assert.False(ApprovalClient.IsHostImage("", ""));
    }
}
