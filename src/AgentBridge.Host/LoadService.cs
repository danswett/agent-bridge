using System.Collections.Concurrent;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Load;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

public sealed class LoadService
{
    readonly Func<ProcessLoadReport?> current;
    readonly Func<bool> enabled;
    readonly Action<string>? log;
    readonly TimeProvider clock;
    readonly ConcurrentDictionary<IProtocolChannel, Subscription> subscriptions = new();
    public int Subscribers => subscriptions.Values.Count(s => s.Active);

    public LoadService(ISessionSource source, Func<ProcessLoadReport?> current, Func<bool> enabled, Action<string>? log = null, TimeProvider? time = null)
    {
        this.current = current; this.enabled = enabled; this.log = log;
        clock = time ?? TimeProvider.System;
        source.Changed += (_, _) => Publish();
    }

    AgentBridge.Protocol.MachineLoad Current()
    {
        var record = MachineLoadMapper.Map(current(), clock.GetUtcNow(), TimeSpan.FromSeconds(15));
        // The record's 64 KiB budget must also leave room for the Noise response/event envelope.
        while (ProtocolJson.ByteCount(new { re = long.MaxValue, ok = true, result = record }) > SecureChannel.MaxMessageBytes
            || ProtocolJson.ByteCount(new { ev = "load.update", seq = long.MaxValue, data = record }) > SecureChannel.MaxMessageBytes)
        {
            record = record with { Sessions = record.Sessions.SkipLast(1).ToArray(),
                State = record.State == MachineLoadState.Fresh ? MachineLoadState.Partial : record.State };
        }
        return record;
    }

    public async Task<object?> HandleAsync(IProtocolChannel channel, string op)
    {
        if (op == "load.get") { return Current(); }
        var state = subscriptions.GetOrAdd(channel, _ => new Subscription());
        if (Interlocked.Exchange(ref state.Registered, 1) == 0)
        { _ = channel.Closed.ContinueWith(_ => subscriptions.TryRemove(channel, out Subscription? _), TaskScheduler.Default); }
        await state.Gate.WaitAsync();
        try
        {
            state.Active = false;
            var generation = ++state.Generation;
            if (op == "load.unsubscribe") { return new { }; }
            if (!channel.IsOpen) { throw new IOException("the channel is closed"); }
            var record = Current();
            state.SentAt = clock.GetTimestamp();
            // A replaced response's callback cannot reactivate it after unsubscribe or resubscribe.
            return new ChannelReply(record, async _ =>
            {
                await state.Gate.WaitAsync();
                try { if (state.Generation == generation && channel.IsOpen) { state.Active = true; } }
                finally { state.Gate.Release(); }
            });
        }
        finally { state.Gate.Release(); }
    }

    public void Publish()
    {
        foreach (var (channel, state) in subscriptions) { _ = PushAsync(channel, state); }
    }

    async Task PushAsync(IProtocolChannel channel, Subscription state)
    {
        // A slow connection holds at most one send; sample events never build a waiting queue.
        if (!await state.Gate.WaitAsync(0)) { return; }
        try
        {
            if (!state.Active || !enabled() || !channel.IsOpen
                || clock.GetElapsedTime(state.SentAt) < TimeSpan.FromSeconds(1)) { return; }
            await channel.SendEventAsync("load.update", Current());
            state.SentAt = clock.GetTimestamp();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException or MessageTooLargeException)
        {
            state.Active = false;
            log?.Invoke($"load.update stopped: {e.Message}");
        }
        finally { state.Gate.Release(); }
    }

    sealed class Subscription
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public volatile bool Active;
        public int Registered;
        public long Generation, SentAt;
    }
}
