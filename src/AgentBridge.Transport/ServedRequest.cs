namespace AgentBridge.Transport;

// The request a handler is serving, on a relay channel or the local pipe, for an op that says
// how far it has got before its response: {"re": id, "progress": {...}} frames (v1-sessions.md,
// Channel framing). A reply to a working session can take a minute to settle, and a client that
// heard nothing until then showed "sending" all that time (#598).
//
// Found through Current, set for the handler's call alone, so a service reached through the
// wrappers in front of it (remote authorization, the pipe's caller guard) needs nothing passed
// down to it. Only progress frames for this request's id can be sent through it.
public sealed class ServedRequest
{
    static readonly AsyncLocal<ServedRequest?> current = new();

    readonly long id;
    readonly Lock gate = new();
    Func<object, byte[]>? serialize;
    Func<byte[], Task>? send;
    Task chain = Task.CompletedTask;
    bool closed;
    Action<bool>? responded;

    public static ServedRequest? Current => current.Value;

    internal ServedRequest(long id, Func<object, byte[]> serialize, Func<byte[], Task> send)
    {
        this.id = id;
        this.serialize = serialize;
        this.send = send;
    }

    // Set by the channel for the handler's call; cleared before anything that follows the
    // response (ChannelReply.Then) runs.
    internal static void Enter(ServedRequest? request) => current.Value = request;

    // Queues a progress frame. Frames leave in the order given and always ahead of the response;
    // one given once the response is on its way is dropped, so nothing a delivery reports late can
    // follow it. Never waits for the send: a delivery is not held up by a slow or closed channel,
    // and a frame that cannot be sent is lost, as the response would be.
    public void Progress(object progress)
    {
        lock (gate)
        {
            if (closed || serialize is null || send is null) { return; }
            var frame = serialize(new { re = id, progress });
            var to = send;
            chain = chain.ContinueWith(_ => Quietly(to, frame), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
        }
    }

    // Called once, after the response has been sent (true) or could not be (false): the host's
    // log says when a result went out.
    public void OnResponded(Action<bool> callback)
    {
        lock (gate) { responded += callback; }
    }

    static async Task Quietly(Func<byte[], Task> send, byte[] frame)
    {
        try { await send(frame); }
        catch (Exception) { }
    }

    // No more progress; waits for what was queued to have gone, so the response follows it.
    internal async Task CloseAsync()
    {
        Task last;
        lock (gate)
        {
            closed = true;
            last = chain;
        }
        await last;
    }

    internal void Responded(bool sent)
    {
        Action<bool>? callbacks;
        lock (gate)
        {
            closed = true;
            callbacks = responded;
            responded = null;
            // Work the handler started can outlive the request with this in its context (a
            // delivery confirming for a minute); it must not keep the channel alive.
            serialize = null;
            send = null;
        }
        try { callbacks?.Invoke(sent); }
        catch (Exception) { }
    }
}
