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
//
// With peers (usage.shared, #172), a request that asks for { shared: true } also gets the readings
// this host keeps from the account's other machines, after its own; one that does not gets exactly
// the host's own report, as before.
public sealed class UsageService
{
    // A change in other machines' readings is sent at most this often to each subscriber.
    public static readonly TimeSpan PeerPace = TimeSpan.FromSeconds(15);

    readonly UsageMonitor monitor;
    readonly PeerUsage? peers;
    readonly Action<string> log;
    readonly TimeProvider time;
    readonly ConcurrentDictionary<IProtocolChannel, Subscriber> subscribers = new();
    readonly object pace = new();
    DateTimeOffset peersSentAt = DateTimeOffset.MinValue;
    bool peersPending;

    public UsageService(UsageMonitor monitor, Action<string>? log = null, PeerUsage? peers = null, TimeProvider? time = null)
    {
        this.monitor = monitor;
        this.peers = peers;
        this.log = log ?? (_ => { });
        this.time = time ?? TimeProvider.System;
        monitor.Changed += (_, _) => PushAll(sharedOnly: false);
        if (peers is not null) { peers.Changed += OnPeersChanged; }
    }

    public int Subscribers => subscribers.Count;

    // hello's usage.shared: other machines' readings are served to whoever asks for them.
    public bool Shares => peers is not null;

    public Task<object?> HandleAsync(IProtocolChannel channel, string op, JsonElement data) => op switch
    {
        UsageProtocol.Get => Task.FromResult<object?>(Current(Shared(data)).Report),
        UsageProtocol.Subscribe => SubscribeAsync(channel, Shared(data)),
        _ => Task.FromResult<object?>(null),
    };

    bool Shared(JsonElement data)
    {
        if (peers is null || data.ValueKind != JsonValueKind.Object) { return false; }
        return data.TryGetProperty("shared", out var s) && s.ValueKind == JsonValueKind.True;
    }

    // The report a subscriber is sent and its version: both versions only go up, so their sum does.
    (UsageReport Report, long Version) Current(bool shared)
    {
        var (own, version) = monitor.Current;
        if (!shared || peers is null) { return (own, version); }
        var peerVersion = peers.Version;
        return (UsageShare.Serve(own, peers.Entries), version + peerVersion);
    }

    void PushAll(bool sharedOnly)
    {
        foreach (var (channel, subscriber) in subscribers)
        {
            if (sharedOnly && !subscriber.Shared) { continue; }
            _ = PushAsync(channel, subscriber);
        }
    }

    // Under the existing pacing: other machines' readings arrive a few at a time, minutes apart,
    // and are sent at most every PeerPace, the latest always.
    void OnPeersChanged()
    {
        TimeSpan wait;
        lock (pace)
        {
            if (peersPending) { return; }
            wait = peersSentAt + PeerPace - time.GetUtcNow();
            if (wait <= TimeSpan.Zero)
            {
                peersSentAt = time.GetUtcNow();
                wait = TimeSpan.Zero;
            }
            else { peersPending = true; }
        }
        if (wait == TimeSpan.Zero) { PushAll(sharedOnly: true); return; }
        _ = Task.Delay(wait, time).ContinueWith(_ =>
        {
            lock (pace)
            {
                peersPending = false;
                peersSentAt = time.GetUtcNow();
            }
            PushAll(sharedOnly: true);
        }, TaskScheduler.Default);
    }

    // A second subscribe on a channel replaces the first rather than running beside it: the
    // client has the report it was just given, and later changes arrive once.
    //
    // The subscriber is held from taking the report until the response has gone. Otherwise a
    // change announced in between could be sent as an update ahead of the response, and the
    // client, taking the response as current, would keep the older report over it.
    async Task<object?> SubscribeAsync(IProtocolChannel channel, bool shared)
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
            (report, version) = Current(shared);
            lock (current) { current.Sent = version; current.Open = false; current.Shared = shared; }
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
            bool shared;
            lock (subscriber) { shared = subscriber.Shared; }
            var (report, version) = Current(shared);
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
        // Whether it asked for other machines' readings too (usage.shared).
        public bool Shared;
    }
}
