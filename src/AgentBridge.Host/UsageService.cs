using System.Collections.Concurrent;
using System.Text.Json;
using AgentBridge.Host.Core.Usage;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// Protocol v1's usage ops (docs/protocol/v1-usage.md): usage.get, and usage.subscribe, which
// answers with the current report and then sends usage.update on that channel whenever the
// monitor announces a change. Usage moves slowly, so this is never more than one small event
// per change, and nothing at all while it holds still.
public sealed class UsageService
{
    readonly UsageMonitor monitor;
    readonly Action<string> log;
    readonly ConcurrentDictionary<IProtocolChannel, Subscriber> subscribers = new();

    public UsageService(UsageMonitor monitor, Action<string>? log = null)
    {
        this.monitor = monitor;
        this.log = log ?? (_ => { });
        monitor.Changed += (_, _) => { foreach (var (channel, subscriber) in subscribers) { _ = PushAsync(channel, subscriber); } };
    }

    public int Subscribers => subscribers.Count;

    public Task<object?> HandleAsync(IProtocolChannel channel, string op, JsonElement data) => op switch
    {
        UsageProtocol.Get => Task.FromResult<object?>(monitor.Current.Report),
        UsageProtocol.Subscribe => SubscribeAsync(channel),
        _ => Task.FromResult<object?>(null),
    };

    // A second subscribe on a channel replaces the first rather than running beside it: the
    // client has the report it was just given, and later changes arrive once.
    //
    // The subscriber is held from taking the report until the response has gone. Otherwise a
    // change announced in between could be sent as an update ahead of the response, and the
    // client, taking the response as current, would keep the older report over it.
    async Task<object?> SubscribeAsync(IProtocolChannel channel)
    {
        var added = false;
        var current = subscribers.AddOrUpdate(channel, _ => { added = true; return new Subscriber(); }, (_, existing) => existing);
        if (added) { _ = channel.Closed.ContinueWith(_ => subscribers.TryRemove(channel, out Subscriber? _), TaskScheduler.Default); }
        // Under the gate, so an update already being sent finishes first and the report taken
        // here is at least as new as anything the client has had.
        await current.Gate.WaitAsync();
        UsageReport report;
        try
        {
            long version;
            (report, version) = monitor.Current;
            lock (current) { current.Sent = version; current.Open = false; }
        }
        finally { current.Gate.Release(); }
        // After the response has gone: anything announced since the report was taken follows it.
        return new ChannelReply(report, ch =>
        {
            lock (current) { current.Open = true; }
            return PushAsync(ch, current);
        });
    }

    async Task PushAsync(IProtocolChannel channel, Subscriber subscriber)
    {
        await subscriber.Gate.WaitAsync();
        try
        {
            var (report, version) = monitor.Current;
            lock (subscriber) { if (!subscriber.Open || version <= subscriber.Sent) { return; } }
            if (!channel.IsOpen) { return; }
            await channel.SendEventAsync(UsageProtocol.Update, report);
            lock (subscriber) { subscriber.Sent = version; }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException) { }
        catch (MessageTooLargeException e) { log($"skipped a {UsageProtocol.Update} too large to send ({e.Size} bytes)"); }
        finally { subscriber.Gate.Release(); }
    }

    sealed class Subscriber
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public long Sent;
        // Whether updates may go: not between taking a subscribe's report and sending its response.
        public bool Open;
    }
}
