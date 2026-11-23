using AgentBridge.Relay;
using Xunit;

namespace AgentBridge.Transport.Tests;

// Slice 2b review: the per-subscriber queue is bounded, so a subscriber that stops reading costs the
// relay a fixed amount of memory and then its socket, never unbounded growth.
public sealed class RegistryOutboxTests
{
    static byte[] Frame(int size = 10) => new byte[size];

    // A send that stays pending until released, so frames pile up behind it.
    sealed class Stalled
    {
        readonly TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Sent;
        public int Overflows;
        public Task SendAsync(byte[] _) { Interlocked.Increment(ref Sent); return gate.Task; }
        public void Overflow() => Interlocked.Increment(ref Overflows);
        public void Release() => gate.SetResult();
    }

    [Fact]
    public async Task Frames_leave_in_the_order_they_were_queued()
    {
        var seen = new List<int>();
        var outbox = new RegistryOutbox(f => { lock (seen) { seen.Add(f.Length); } return Task.CompletedTask; }, () => { });
        for (var i = 1; i <= 20; i++) { outbox.Enqueue(Frame(i)); }
        await Wait.UntilAsync(() => { lock (seen) { return seen.Count == 20; } }, "all frames sent");
        Assert.Equal(Enumerable.Range(1, 20), seen);
    }

    [Fact]
    public async Task A_subscriber_that_stops_reading_overflows_once_at_the_frame_cap_and_keeps_nothing_queued()
    {
        var stall = new Stalled();
        var outbox = new RegistryOutbox(stall.SendAsync, stall.Overflow);
        outbox.Enqueue(Frame());
        await Wait.UntilAsync(() => stall.Sent == 1, "first frame in flight");

        for (var i = 0; i < RegistryOutbox.MaxFrames; i++) { outbox.Enqueue(Frame()); }
        Assert.Equal(0, stall.Overflows);
        outbox.Enqueue(Frame());
        outbox.Enqueue(Frame());

        Assert.Equal(1, stall.Overflows);
        Assert.True(outbox.Closed);
        Assert.Equal(0, outbox.QueuedBytes);
        stall.Release();
        await Task.Delay(100);
        Assert.Equal(1, stall.Sent);
    }

    [Fact]
    public async Task Sixteen_kilobyte_frames_hit_the_byte_cap_and_queued_bytes_never_pass_it()
    {
        var stall = new Stalled();
        var outbox = new RegistryOutbox(stall.SendAsync, stall.Overflow);
        outbox.Enqueue(Frame(16 * 1024));
        await Wait.UntilAsync(() => stall.Sent == 1, "first frame in flight");

        var peak = 0L;
        for (var i = 0; i < 40; i++)
        {
            outbox.Enqueue(Frame(16 * 1024));
            peak = Math.Max(peak, outbox.QueuedBytes);
        }

        Assert.True(peak <= RegistryOutbox.MaxBytes, $"queued {peak} bytes");
        Assert.Equal(1, stall.Overflows);
        stall.Release();
    }
}
