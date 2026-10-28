using System.Threading.Channels;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core;

// The live agent sessions on this machine, read-only. Every Session it hands out is
// already within the contract's field limits and SessionBudget.
public interface ISessionSource : IAsyncDisposable
{
    // Goes up by one for every change to any session (an upsert or a removal).
    long Revision { get; }

    // Raised once per coalesced upsert (at most one per session per CoalesceInterval,
    // carrying the latest state) and once per removal. Sessions are in list form, without
    // history. Raised on a background thread, in order.
    event EventHandler<SessionChange>? Changed;

    // All live sessions in list form (no history), most recently updated first.
    IReadOnlyList<Session> Snapshot();

    // One sessions.list page: { sessions, revision, next? }. Pass the previous page's
    // Next to continue; null for the first page.
    SessionList List(string? cursor = null);

    // session.get: the full session with its tool history, or null once it has gone.
    Session? Get(string id);

    // sessions.subscribe: the result to send, then on Events one session.upsert per live
    // session, sessions.synced, and every live change after that, in order. Dispose the
    // subscription when the channel closes. A second sessions.subscribe on a channel
    // replaces the first: dispose the old subscription and call this again, which sends
    // the whole snapshot and sessions.synced afresh. The channel, not this, numbers
    // events with its own continuing seq.
    SessionSubscription Subscribe();

    // One discovery and tailing pass, now. The CLI's `sessions` command and tests use it;
    // StartAsync runs it in a loop.
    Task RefreshAsync(CancellationToken cancellationToken = default);

    // Starts watching: a pass now, then on every file change and every PollInterval.
    Task StartAsync(CancellationToken cancellationToken = default);

    // Shows a session as ending (status Ending) while the host ends it, or no longer, if
    // ending it failed. A session that has gone is removed as usual; a source that cannot
    // show it does nothing.
    Task SetEndingAsync(string id, bool ending) => Task.CompletedTask;

    // The process discovery bound a session to: its id, and its start time, which is what
    // tells it from a later process given the same id before the next discovery pass
    // notices. Null when the session has gone or the source cannot say.
    SessionProcess? ProcessOf(string id) => null;
}

public sealed record SessionProcess(int Id, DateTimeOffset? StartedAt);

public enum SessionChangeKind { Upserted, Removed }

public sealed record SessionChange(SessionChangeKind Kind, string Id, Session? Session, long Revision);

// One event for a subscriber, named as on the wire (Events.SessionUpsert and so on), with
// its data record: Session, SessionRemoved or SessionsSynced.
public sealed record SessionEvent(string Name, object Data);

public sealed class SessionSubscription : IDisposable
{
    readonly Action<SessionSubscription> _onDispose;
    internal readonly Channel<SessionEvent> Channel = System.Threading.Channels.Channel.CreateUnbounded<SessionEvent>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    internal SessionSubscription(SubscribeResult result, Action<SessionSubscription> onDispose)
    {
        Result = result;
        _onDispose = onDispose;
    }

    public SubscribeResult Result { get; }

    public ChannelReader<SessionEvent> Events => Channel.Reader;

    public void Dispose()
    {
        _onDispose(this);
        Channel.Writer.TryComplete();
    }
}
