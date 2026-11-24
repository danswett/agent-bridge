using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using AgentBridge.Hooks;
using Xunit;

namespace AgentBridge.Hooks.Tests;

// #1003 PR C, the hook's end of approval.wait (docs/design/claude-approval-channel.md): over the host's local pipe (4-byte little-endian length, then
// UTF-8 JSON) the hook says hello, sends the Claude PermissionRequest event with approval.wait, and takes the verdict from the one reply. Anything
// but exactly allow or deny is none. It talks only to a server it has verified is the host, and sends nothing before that. Real pipes, no host.
public sealed class ApprovalClientTests
{
    const string Event = """{"session_id":"s-1","hook_event_name":"PermissionRequest","tool_name":"Bash","tool_input":{"command":"echo hello"}}""";
    static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    static string NewName() => "ab-appr-" + Guid.NewGuid().ToString("N")[..12];

    static async Task<string?> ReadFrame(Stream s)
    {
        var head = new byte[4];
        if (!await ReadExactly(s, head)) { return null; }
        var body = new byte[BinaryPrimitives.ReadInt32LittleEndian(head)];
        return await ReadExactly(s, body) ? Encoding.UTF8.GetString(body) : null;
    }

    static async Task<bool> ReadExactly(Stream s, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read));
            if (n == 0) { return false; }
            read += n;
        }
        return true;
    }

    static async Task Take(Stream s, List<string> got) { if (await ReadFrame(s) is { } frame) { got.Add(frame); } }

    static async Task WriteFrame(Stream s, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var head = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(head, body.Length);
        await s.WriteAsync(head);
        await s.WriteAsync(body);
        await s.FlushAsync();
    }

    // A host: serves one connection with `script`, which is given what each frame it reads said. Returns the frames received.
    sealed class FakeHost(Func<Stream, List<string>, Task> script) : IAsyncDisposable
    {
        public string Name { get; } = NewName();
        public List<string> Received { get; } = [];
        Task? serving;
        NamedPipeServerStream? server;

        public void Start()
        {
            server = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            serving = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync();
                await script(server, Received);
            });
        }

        public async ValueTask DisposeAsync()
        {
            server?.Dispose();
            if (serving is not null) { try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception e) when (e is IOException or ObjectDisposedException or TimeoutException or OperationCanceledException) { } }
        }
    }

    // The well-behaved host: hello, then the one answer given for approval.wait.
    static Func<Stream, List<string>, Task> Answers(string waitReply, string? eventBeforeAnswer = null) => async (s, got) =>
    {
        await Take(s, got);
        await WriteFrame(s, """{"re":1,"ok":true,"result":{}}""");
        await Take(s, got);
        if (eventBeforeAnswer is not null) { await WriteFrame(s, eventBeforeAnswer); }
        await WriteFrame(s, waitReply);
    };

    static Task<string?> Ask(FakeHost host, string eventText = Event, Func<NamedPipeClientStream, bool>? verify = null, TimeSpan? wait = null) =>
        ApprovalClient.AskHostAsync(host.Name, verify ?? (_ => true), eventText, wait ?? Wait, TimeSpan.FromSeconds(5));

    [Theory]
    [InlineData("allow")]
    [InlineData("deny")]
    public async Task The_hook_says_hello_sends_the_event_with_approval_wait_and_takes_the_hosts_verdict(string verdict)
    {
        await using var host = new FakeHost(Answers("{\"re\":2,\"ok\":true,\"result\":{\"verdict\":\"" + verdict + "\"}}", """{"ev":"session.upsert","seq":1,"data":{}}"""));
        host.Start();
        Assert.Equal(verdict, await Ask(host));
        Assert.Equal("""{"id":1,"op":"hello","data":{}}""", host.Received[0]);
        using var wait = JsonDocument.Parse(host.Received[1]);
        Assert.Equal((2, "approval.wait", Event), (wait.RootElement.GetProperty("id").GetInt32(), wait.RootElement.GetProperty("op").GetString(), wait.RootElement.GetProperty("data").GetProperty("event").GetString()));
    }

    [Theory]
    [InlineData("""{"re":2,"ok":true,"result":{"verdict":"none","reason":"off"}}""")]
    [InlineData("""{"re":2,"ok":true,"result":{"verdict":"ALLOW"}}""")]
    [InlineData("""{"re":2,"ok":true,"result":{"verdict":"allow "}}""")]
    [InlineData("""{"re":2,"ok":true,"result":{"verdict":true}}""")]
    [InlineData("""{"re":2,"ok":true,"result":{}}""")]
    [InlineData("""{"re":2,"ok":true,"result":"allow"}""")]
    [InlineData("""{"re":2,"ok":true}""")]
    [InlineData("""{"re":2,"ok":false,"error":"refused: no"}""")]
    [InlineData("""{"re":2,"ok":false,"result":{"verdict":"allow"}}""")]
    [InlineData("""{"re":99,"ok":true,"result":{"verdict":"allow"}}""")]
    [InlineData("""{"ok":true,"result":{"verdict":"allow"}}""")]
    [InlineData("not json")]
    public async Task Anything_but_exactly_allow_or_deny_in_a_successful_reply_to_this_request_is_none(string reply)
    {
        await using var host = new FakeHost(Answers(reply));
        host.Start();
        Assert.Null(await Ask(host, wait: TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task A_host_that_refuses_hello_is_not_sent_the_event()
    {
        await using var host = new FakeHost(async (s, got) => { await Take(s, got); await WriteFrame(s, """{"re":1,"ok":false,"error":"refused"}"""); });
        host.Start();
        Assert.Null(await Ask(host));
        Assert.Single(host.Received);
    }

    [Fact]
    public async Task A_host_that_hangs_up_or_never_answers_is_none_and_the_wait_is_bounded()
    {
        await using var hangs = new FakeHost(async (s, got) => { await Take(s, got); s.Dispose(); });
        hangs.Start();
        Assert.Null(await Ask(hangs));
        await using var silent = new FakeHost(async (s, got) =>
        {
            await Take(s, got);
            await WriteFrame(s, """{"re":1,"ok":true,"result":{}}""");
            await Take(s, got);
            await Task.Delay(TimeSpan.FromSeconds(5));
        });
        silent.Start();
        var started = DateTime.UtcNow;
        Assert.Null(await Ask(silent, wait: TimeSpan.FromMilliseconds(400)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task A_frame_the_host_claims_is_larger_than_the_pipe_allows_is_none()
    {
        await using var host = new FakeHost(async (s, got) =>
        {
            await Take(s, got);
            var head = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(head, 65520);
            await s.WriteAsync(head);
            await s.FlushAsync();
        });
        host.Start();
        Assert.Null(await Ask(host));
    }

    [Fact]
    public async Task A_server_that_is_not_verified_as_the_host_is_sent_nothing()
    {
        await using var host = new FakeHost(Answers("""{"re":2,"ok":true,"result":{"verdict":"allow"}}"""));
        host.Start();
        Assert.Null(await Ask(host, verify: _ => false));
        Assert.Empty(host.Received);
    }

    [Fact]
    public async Task An_event_too_large_to_send_is_none_and_nothing_is_sent_or_even_connected()
    {
        await using var host = new FakeHost(Answers("""{"re":2,"ok":true,"result":{"verdict":"allow"}}"""));
        host.Start();
        var big = Event.Replace("echo hello", new string('x', ApprovalClient.MaxEventBytes));
        Assert.Null(await Ask(host, eventText: big));
        Assert.Empty(host.Received);
    }

    [Fact]
    public async Task With_no_host_listening_the_answer_is_none_without_a_long_wait()
    {
        var started = DateTime.UtcNow;
        Assert.Null(await ApprovalClient.AskHostAsync(NewName(), _ => true, Event, Wait, TimeSpan.FromMilliseconds(300)));
        Assert.True(DateTime.UtcNow - started < TimeSpan.FromSeconds(3));
    }

    [Fact]
    public void The_pipe_name_is_the_hosts_own_derivation_from_the_users_sid_and_the_profile_folder_lower_cased()
    {
        Assert.Equal("AgentBridge.Host.1e659cb0b7056f9a", ApprovalClient.PipeName("S-1-5-21-1-2-3-1001", @"C:\Users\A\AppData\Local\AgentBridge\Default"));
        Assert.NotEqual(ApprovalClient.PipeName("S-1-5-21-1-2-3-1001", @"C:\Users\A\AppData\Local\AgentBridge\Default"), ApprovalClient.PipeName("S-1-5-21-1-2-3-1002", @"C:\Users\A\AppData\Local\AgentBridge\Default"));
    }

    [Fact]
    public void The_host_check_compares_the_servers_program_with_the_expected_host_program_exactly_and_never_on_a_missing_one()
    {
        Assert.True(ApprovalClient.IsHostImage(@"C:\Program Files\Slipgates\agent-bridge-host.exe", @"c:\program files\slipgates\agent-bridge-host.exe"));
        Assert.False(ApprovalClient.IsHostImage(@"C:\Users\A\Temp\agent-bridge-host.exe", @"C:\Program Files\Slipgates\agent-bridge-host.exe"));
        Assert.False(ApprovalClient.IsHostImage(null, @"C:\Program Files\Slipgates\agent-bridge-host.exe"));
        Assert.False(ApprovalClient.IsHostImage(@"C:\x\agent-bridge-host.exe", null));
        Assert.False(ApprovalClient.IsHostImage("", ""));
    }
}
