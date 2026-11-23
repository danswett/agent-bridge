namespace AgentBridge.Relay;

// The registry-changed frames waiting to go to one subscriber, sent in order by one drain loop.
// Bounded: a subscriber that stops reading holds at most MaxFrames / MaxBytes here (plus the one
// frame in flight), and past that it is cut off (the owner closes its socket) rather than growing
// the relay's memory. It resyncs with registry-get when it reconnects.
public sealed class RegistryOutbox(Func<byte[], Task> send, Action overflow)
{
    public const int MaxFrames = 32;
    public const int MaxBytes = 256 * 1024;
    readonly object gate = new();
    readonly Queue<byte[]> queue = new();
    long bytes;
    bool draining;
    bool closed;

    public long QueuedBytes { get { lock (gate) { return bytes; } } }

    // True once it overflowed: nothing more is accepted, ever.
    public bool Closed { get { lock (gate) { return closed; } } }

    public void Enqueue(byte[] data)
    {
        bool start = false, over = false;
        lock (gate)
        {
            if (closed) { return; }
            if (queue.Count >= MaxFrames || bytes + data.Length > MaxBytes)
            {
                // Drop what is queued: the subscriber is being closed and will resync.
                closed = true;
                over = true;
                queue.Clear();
                bytes = 0;
            }
            else
            {
                queue.Enqueue(data);
                bytes += data.Length;
                start = !draining;
                draining = true;
            }
        }
        if (over) { overflow(); }
        if (start) { _ = Task.Run(DrainAsync); }
    }

    async Task DrainAsync()
    {
        while (true)
        {
            byte[] next;
            lock (gate)
            {
                if (!queue.TryDequeue(out next!)) { draining = false; return; }
                bytes -= next.Length;
            }
            try { await send(next); } catch (Exception) { }
        }
    }
}
