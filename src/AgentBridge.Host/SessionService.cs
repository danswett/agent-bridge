using System.Collections.Concurrent;
using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// Protocol v1's sessions ops, served on every channel a client opens to this host. Reads
// only: nothing a peer sends can change a session or reach the machine.
public sealed class SessionService
{
    readonly ISessionSource source;
    readonly MachineInfo machine;
    readonly Action<string> log;
    readonly ConcurrentDictionary<SecureChannel, Feed> feeds = new();
    readonly ConcurrentDictionary<SecureChannel, SemaphoreSlim> subscribeLocks = new();

    public SessionService(ISessionSource source, MachineInfo machine, Action<string>? log = null)
    {
        this.source = source;
        this.machine = machine;
        this.log = log ?? (_ => { });
    }

    // Channels with a subscription running; tests and the host's log read it.
    public int Subscribers => feeds.Count;

    public async Task<object?> HandleAsync(SecureChannel channel, string op, JsonElement data)
    {
        switch (op)
        {
            case Ops.Hello:
                return new HelloResult(1, machine, [Capabilities.SessionsRead]);
            case Ops.SessionsList:
                return source.List(String(data, "cursor"));
            case Ops.SessionGet:
                {
                    var id = String(data, "id") ?? throw new ArgumentException("session.get needs an id");
                    return (object?)source.Get(id) ?? new { missing = true };
                }
            case Ops.SessionsSubscribe:
                return await SubscribeAsync(channel);
            default:
                return null;
        }
    }

    // A channel has one subscription. A second subscribe (a client recovering from a gap)
    // stops the first before answering, so nothing from the old one can follow the new
    // response, and the new snapshot covers whatever the old one never sent.
    async Task<object> SubscribeAsync(SecureChannel channel)
    {
        // ChannelHub serves each request as it arrives, so two subscribes on one channel can
        // run at once; the lock keeps the old feed's stop and the new feed's start in order.
        var gate = subscribeLocks.GetOrAdd(channel, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            if (feeds.TryRemove(channel, out var old)) { await old.StopAsync(); }
            if (!channel.IsOpen) { throw new IOException("the channel is closed"); }
            var feed = new Feed(source.Subscribe());
            feeds[channel] = feed;
            // Tied to the channel rather than to the feed's run: when the response cannot be
            // sent, ChannelHub never starts the run, and the subscription would otherwise
            // stay registered with the source for as long as the host runs.
            _ = channel.Closed.ContinueWith(_ => End(channel, feed), TaskScheduler.Default);
            // The snapshot waits until the response has gone (ChannelReply), so the client
            // has {revision, count} before the first upsert.
            return new ChannelReply(feed.Subscription.Result, ch => feed.RunAsync(ch, log));
        }
        finally { gate.Release(); }
    }

    async Task End(SecureChannel channel, Feed feed)
    {
        if (feeds.TryRemove(new KeyValuePair<SecureChannel, Feed>(channel, feed))) { await feed.StopAsync(); }
        // The lock is left for the GC rather than disposed: a subscribe already waiting on
        // it would otherwise throw instead of finding the channel closed.
        subscribeLocks.TryRemove(channel, out _);
    }

    static string? String(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    sealed class Feed(SessionSubscription subscription)
    {
        readonly CancellationTokenSource stop = new();
        readonly TaskCompletionSource done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int started;

        public SessionSubscription Subscription => subscription;

        // Sends the subscription's events, in order, until the channel ends or it is
        // replaced. The channel numbers them: seq continues across subscriptions.
        public async Task RunAsync(SecureChannel channel, Action<string> log)
        {
            if (Interlocked.Exchange(ref started, 1) == 1) { return; }
            try
            {
                await foreach (var ev in subscription.Events.ReadAllAsync(stop.Token))
                {
                    try { await channel.SendEventAsync(ev.Name, ev.Data, stop.Token); }
                    catch (MessageTooLargeException e)
                    {
                        // Every session has been through SessionBudget.Fit, so this is a bug.
                        // The size is checked before encrypting, so the channel is intact and
                        // the session's next change can still get through.
                        log($"skipped a {ev.Name} too large to send ({e.Size} bytes)");
                    }
                }
            }
            catch (OperationCanceledException) { }
            // The channel has gone; End, waiting on Closed, removes the feed.
            catch (IOException) { }
            finally
            {
                subscription.Dispose();
                done.TrySetResult();
            }
        }

        public async Task StopAsync()
        {
            stop.Cancel();
            // Replaced before its snapshot started (the response never left): nothing runs.
            if (Interlocked.Exchange(ref started, 1) == 0)
            {
                subscription.Dispose();
                done.TrySetResult();
            }
            await done.Task;
        }
    }
}
