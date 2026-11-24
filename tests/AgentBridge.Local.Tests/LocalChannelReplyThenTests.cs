using System.Buffers.Binary;
using System.Text;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

// #4134 (#2558): a ChannelReply's `then` hook (HostControl's Stop and Restart, ManageService's Leave, the subscribe snapshot) runs once the reply has been
// written. The reply's send is a write and then a flush; a client that reads the reply and hangs up at once can make the flush throw, and the hook was
// skipped though the client had its answer: the host said "stopping" and did not stop (an App.Tests run hung on it). A reply that was never written is
// still a failed send, and its hook still does not run.
public sealed class LocalChannelReplyThenTests
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // The client's end as the server channel sees it: it hands over one request, takes the reply's write, and then (flushFails) its flush
    // fails as a pipe's does when the reader has gone; or (writeFails) the write itself fails.
    sealed class ClientThatHungUp(bool flushFails, bool writeFails = false) : Stream
    {
        readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly byte[] request = Frame("""{"id":1,"op":"host.stop"}""");
        int offset;

        static byte[] Frame(string json)
        {
            var body = Encoding.UTF8.GetBytes(json);
            var frame = new byte[4 + body.Length];
            BinaryPrimitives.WriteUInt32LittleEndian(frame, (uint)body.Length);
            body.CopyTo(frame, 4);
            return frame;
        }

        public override bool CanRead => true;
        public override bool CanWrite => true;
        public override bool CanSeek => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (offset < request.Length)
            {
                var n = Math.Min(buffer.Length, request.Length - offset);
                request.AsMemory(offset, n).CopyTo(buffer);
                offset += n;
                return n;
            }
            await closed.Task.WaitAsync(ct);
            return 0;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            writeFails ? throw new IOException("the pipe is broken") : ValueTask.CompletedTask;

        public override Task FlushAsync(CancellationToken ct) =>
            flushFails ? throw new ObjectDisposedException("pipe", "Cannot access a closed pipe.") : Task.CompletedTask;

        protected override void Dispose(bool disposing)
        {
            closed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    static LocalChannel Server(Stream stream, TaskCompletionSource ran, bool runsIfUnsent = true, List<string>? log = null)
    {
        var channel = new LocalChannel(stream, "app", "app", "local",
            (_, _, _) => Task.FromResult<object?>(new ChannelReply(new { stopping = true }, _ => { ran.TrySetResult(); return Task.CompletedTask; }, runsIfUnsent)),
            ProtocolJson.Options) { Log = log is null ? null : m => { lock (log) { log.Add(m); } } };
        channel.Start();
        return channel;
    }

    [Fact]
    public async Task An_action_runs_when_the_reply_was_written_and_only_its_flush_failed_because_the_client_had_gone()
    {
        for (var round = 0; round < 200; round++)
        {
            var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var channel = Server(new ClientThatHungUp(flushFails: true), ran);

            await ran.Task.WaitAsync(Patience);
        }
    }

    [Fact]
    public async Task An_action_runs_when_the_reply_could_not_be_written_at_all_and_the_failure_is_logged()
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new List<string>();
        await using var channel = Server(new ClientThatHungUp(flushFails: false, writeFails: true), ran, log: log);

        await ran.Task.WaitAsync(Patience);

        lock (log) { Assert.Contains(log, l => l.Contains("host.stop", StringComparison.Ordinal) && l.Contains("could not be sent", StringComparison.Ordinal)); }
    }

    [Fact]
    public async Task A_follow_up_that_is_not_an_action_still_runs_only_after_its_reply_was_sent()
    {
        var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var channel = Server(new ClientThatHungUp(flushFails: false, writeFails: true), ran, runsIfUnsent: false);

        // The failed write closes the channel; the serving of the request ends with it.
        await channel.Closed.WaitAsync(Patience);
        await Task.Yield();
        Assert.False(ran.Task.IsCompleted, "a follow-up ran for a reply that was never written");
    }

    [Fact]
    public async Task A_client_that_reads_the_reply_and_hangs_up_at_once_never_leaves_the_action_unrun_over_a_real_pipe()
    {
        const int Rounds = 3000;
        for (var round = 0; round < Rounds; round++)
        {
            var folder = Path.Combine(Path.GetTempPath(), "abt-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(folder);
            try
            {
                var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                await using var server = LocalServer.Start(LocalEndpoint.For(folder), (_, _, _) =>
                    Task.FromResult<object?>(new ChannelReply(new { stopping = true }, _ => { ran.TrySetResult(); return Task.CompletedTask; }, RunsIfUnsent: true)));
                var client = await LocalClient.ConnectAsync(LocalEndpoint.For(folder), Patience);
                try { await client.CallAsync("host.stop"); } catch (IOException) { }
                await client.DisposeAsync();

                await ran.Task.WaitAsync(Patience);
            }
            finally { try { Directory.Delete(folder, true); } catch (IOException) { } }
        }
    }
}
