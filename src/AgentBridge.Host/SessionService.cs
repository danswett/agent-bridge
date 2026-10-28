using System.Collections.Concurrent;
using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// Protocol v1's sessions ops, served on every channel a client opens to this host. Reads,
// and the three ops that reach the machine, each only through its own service:
// session.answer (the answerer types an answer to a pending question), session.reply (the
// replier types a prompt into a session's input, with any files sent first by attachment.*,
// which go to the AttachmentStore) and session.stop (the stopper ends a session). The
// push.* ops, which register a device for notifications, go to the
// PushNotifier, and usage.* to the UsageService.
public sealed class SessionService
{
    readonly ISessionSource source;
    readonly MachineInfo machine;
    readonly Action<string> log;
    readonly ISessionAnswerer? answers;
    readonly ISessionReplier? replies;
    readonly PushNotifier? push;
    readonly AttachmentStore? attachments;
    readonly ISessionStopper? stops;
    readonly UsageService? usage;
    readonly ConcurrentDictionary<SecureChannel, Subscriber> subscribers = new();

    public SessionService(ISessionSource source, MachineInfo machine, Action<string>? log = null, ISessionAnswerer? answers = null,
        ISessionReplier? replies = null, PushNotifier? push = null, AttachmentStore? attachments = null, ISessionStopper? stops = null,
        UsageService? usage = null)
    {
        this.source = source;
        this.machine = machine;
        this.log = log ?? (_ => { });
        this.answers = answers;
        this.replies = replies;
        this.push = push;
        this.attachments = attachments;
        this.stops = stops;
        this.usage = usage;
    }

    // Channels with a subscription running; tests and the host's log read it.
    public int Subscribers => subscribers.Values.Count(s => s.Feed is not null);

    // Each feed as it is made, so tests can check a replaced one is let go.
    internal event Action<object>? FeedCreated;

    public async Task<object?> HandleAsync(SecureChannel channel, string op, JsonElement data)
    {
        switch (op)
        {
            case Ops.Hello:
                // Computed for each hello, so it says what is true now: agent-ha-bridge
                // starting for this user takes sessions.answer and sessions.reply away until
                // it stops.
                return new HelloResult(1, machine with { Sessions = source.Snapshot().Count }, Offered());
            case Ops.SessionAnswer:
                return await AnswerAsync(channel, data);
            case Ops.SessionReply:
                return await ReplyAsync(channel, data);
            case Ops.AttachmentStart or Ops.AttachmentChunk or Ops.AttachmentDiscard when attachments is not null:
                return Attach(channel, op, data);
            case Ops.SessionStop:
                return await StopAsync(data);
            case Ops.PushSubscribe or Ops.PushUnsubscribe or Ops.PushList when push is not null:
                return await push.HandleAsync(channel, op, data);
            case UsageProtocol.Get or UsageProtocol.Subscribe when usage is not null:
                return await usage.HandleAsync(channel, op, data);
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
        var state = subscribers.GetOrAdd(channel, _ => new Subscriber());
        // Cleanup is registered once per channel and finds whatever feed is current then.
        // One registration per subscribe would keep every replaced feed (and anything still
        // queued in it) reachable from the channel until it closes, so a client that keeps
        // recovering on one long-lived channel would grow the host without bound. Tied to the
        // channel rather than to the feed's run: when the response cannot be sent, ChannelHub
        // never starts the run, and the subscription would stay with the source forever.
        if (Interlocked.Exchange(ref state.Registered, 1) == 0)
        {
            _ = channel.Closed.ContinueWith(_ => EndAsync(channel, state), TaskScheduler.Default);
        }
        // ChannelHub serves each request as it arrives, so two subscribes on one channel can
        // run at once; the lock keeps the old feed's stop and the new feed's start in order.
        await state.Gate.WaitAsync();
        try
        {
            if (state.Feed is { } old)
            {
                state.Feed = null;
                await old.StopAsync();
            }
            if (!channel.IsOpen) { throw new IOException("the channel is closed"); }
            var feed = new Feed(source.Subscribe());
            state.Feed = feed;
            FeedCreated?.Invoke(feed);
            // The snapshot waits until the response has gone (ChannelReply), so the client
            // has {revision, count} before the first upsert.
            return new ChannelReply(feed.Subscription.Result, ch => feed.RunAsync(ch, log));
        }
        finally { state.Gate.Release(); }
    }

    async Task EndAsync(SecureChannel channel, Subscriber state)
    {
        subscribers.TryRemove(new KeyValuePair<SecureChannel, Subscriber>(channel, state));
        // Under the lock, so a subscribe already running finishes first and its feed is the
        // one stopped here. The lock itself is left for the GC rather than disposed: a
        // subscribe still waiting on it would otherwise throw instead of finding the channel
        // closed.
        await state.Gate.WaitAsync();
        try
        {
            if (state.Feed is { } feed)
            {
                state.Feed = null;
                await feed.StopAsync();
            }
        }
        finally { state.Gate.Release(); }
    }

    sealed class Subscriber
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public Feed? Feed;
        public int Registered;
    }

    async Task<AnswerResult> AnswerAsync(SecureChannel channel, JsonElement data)
    {
        // A request without an id, question or request UUID fails as a request; anything
        // that can be read gets an outcome.
        if (data.ValueKind != JsonValueKind.Object) { throw new ArgumentException("session.answer needs an id, a question and a request"); }
        var request = data.Deserialize<AnswerRequest>(ProtocolJson.Options) ?? throw new ArgumentException("session.answer needs an id, a question and a request");
        if (answers is null) { return new AnswerResult(AnswerOutcome.Refused, "this host does not take answers; answer at the machine"); }
        var result = await answers.AnswerAsync(request);
        // Typed from this device: it needs no notification that the question was answered.
        if (result.Outcome is AnswerOutcome.Delivered or AnswerOutcome.Unconfirmed) { push?.Answered(channel.PeerId, request.Id, request.Question); }
        return result;
    }

    List<string> Offered()
    {
        List<string> offered = [Capabilities.SessionsRead];
        if (answers?.Available == true) { offered.Add(Capabilities.SessionsAnswer); }
        if (replies?.Available == true) { offered.Add(Capabilities.SessionsReply); }
        if (attachments is not null && replies?.TakesAttachments == true) { offered.Add(Capabilities.SessionsAttach); }
        if (stops?.Available == true) { offered.Add(Capabilities.SessionsStop); }
        if (push is not null) { offered.Add(Capabilities.Push); }
        if (usage is not null) { offered.Add(UsageProtocol.Capability); }
        return offered;
    }

    async Task<ReplyResult> ReplyAsync(SecureChannel channel, JsonElement data)
    {
        // As for answers: a request without an id or request UUID fails as a request, and
        // anything that can be read gets an outcome.
        if (data.ValueKind != JsonValueKind.Object) { throw new ArgumentException("session.reply needs an id, a request and text"); }
        var request = data.Deserialize<ReplyRequest>(ProtocolJson.Options) ?? throw new ArgumentException("session.reply needs an id, a request and text");
        if (replies is null) { return new ReplyResult(AnswerOutcome.Refused, "this host does not take replies; reply at the machine"); }
        return await replies.ReplyAsync(request, channel.PeerId);
    }

    // attachment.*: the files a reply will carry, held for the device that sends them. A check
    // that fails fails the request, with the reason.
    object Attach(SecureChannel channel, string op, JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) { throw new ArgumentException($"{op} needs an attachment"); }
        return op switch
        {
            Ops.AttachmentStart => attachments!.Start(channel.PeerId, data.Deserialize<AttachmentStart>(ProtocolJson.Options)!),
            Ops.AttachmentChunk => attachments!.Chunk(channel.PeerId, data.Deserialize<AttachmentChunk>(ProtocolJson.Options)!),
            _ => new AttachmentDiscarded(attachments!.Discard(channel.PeerId, data.Deserialize<AttachmentDiscard>(ProtocolJson.Options)!.Attachment)),
        };
    }

    async Task<StopResult> StopAsync(JsonElement data)
    {
        // As for answers: a request without an id or request UUID fails as a request, and
        // anything that can be read gets an outcome.
        if (data.ValueKind != JsonValueKind.Object) { throw new ArgumentException("session.stop needs an id and a request"); }
        var request = data.Deserialize<StopRequest>(ProtocolJson.Options) ?? throw new ArgumentException("session.stop needs an id and a request");
        if (stops is null) { return new StopResult(StopOutcome.Refused, "this host does not end sessions; end it at the machine"); }
        return await stops.StopAsync(request);
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
