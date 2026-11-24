using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using AgentBridge.Local;
using AgentBridge.Protocol;
using Xunit;

// #2558 and the app-update lane: a host that answers a request and hangs up at once (HostControl's Stop does: its reply, then options.Stop())
// leaves the client's own write still completing. The client's read loop reads the answer and the end of the stream and closes the stream, and
// the write (its Flush) then fails with "Cannot access a closed pipe": the call threw although the answer was already in. A request with no
// answer still fails, as it must.
public sealed class LocalChannelLateWriteTests
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    // The peer's stream: it takes the request's bytes at once, and says nothing back until the client has flushed. Then (answer: true) it
    // sends the response and ends, and the flush, still pending, fails only once the client has closed the stream, as a pipe's does.
    sealed class HangsUpAfterAnswering(bool answer, bool failAtOnce = false) : Stream
    {
        readonly TaskCompletionSource flushed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        readonly TaskCompletionSource closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        byte[] pending = [];
        int offset;
        bool sent;

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

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            failAtOnce ? throw new ObjectDisposedException("pipe", "Cannot access a closed pipe.") : ValueTask.CompletedTask;

        public override async Task FlushAsync(CancellationToken ct)
        {
            flushed.TrySetResult();
            await closed.Task.WaitAsync(Patience, ct);
            throw new ObjectDisposedException("pipe", "Cannot access a closed pipe.");
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            await flushed.Task.WaitAsync(ct);
            if (!sent) { sent = true; pending = answer ? Frame("""{"re":1,"ok":true,"result":{"stopping":true}}""") : []; }
            if (offset >= pending.Length) { return 0; }
            var n = Math.Min(buffer.Length, pending.Length - offset);
            pending.AsMemory(offset, n).CopyTo(buffer);
            offset += n;
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            closed.TrySetResult();
            base.Dispose(disposing);
        }
    }

    static LocalChannel Client(Stream stream)
    {
        var channel = new LocalChannel(stream, "host", "host", "host", null, ProtocolJson.Options);
        channel.Start();
        return channel;
    }

    [Fact]
    public async Task A_late_write_failure_after_the_answer_and_the_end_of_the_stream_does_not_fail_the_call()
    {
        // Deterministic, and many times over for a run under load.
        for (var round = 0; round < 200; round++)
        {
            await using var channel = Client(new HangsUpAfterAnswering(answer: true));

            var answer = await channel.RequestAsync("host.stop", timeout: Patience);

            Assert.True(answer.GetProperty("ok").GetBoolean(), $"round {round}");
            Assert.True(answer.GetProperty("result").GetProperty("stopping").GetBoolean(), $"round {round}");
        }
    }

    [Fact]
    public async Task A_write_that_fails_with_no_answer_in_still_fails_the_call()
    {
        // The host hangs up without answering: the failed write is the real failure and is not swallowed.
        await using var channel = Client(new HangsUpAfterAnswering(answer: false));

        await Assert.ThrowsAsync<IOException>(() => channel.RequestAsync("host.stop", timeout: Patience));
    }

    [Fact]
    public async Task A_write_that_fails_at_once_still_fails_the_call()
    {
        await using var channel = Client(new HangsUpAfterAnswering(answer: false, failAtOnce: true));

        await Assert.ThrowsAsync<IOException>(() => channel.RequestAsync("host.stop", timeout: Patience));
    }
}
