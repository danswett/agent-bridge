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

    // The same session before SessionBudget.Fit, within the field limits only, or null once it
    // has gone. For a consumer that sends it on under limits of its own (the HA connector): Fit
    // trims the fields older clients ignore first, activity.own among them, so the budgeted form
    // can have lost the main agent's own reply while keeping a delegate's (#175). Never for a
    // channel to a client, which the budget is for.
    Session? GetUnbudgeted(string id) => Get(id);

    // session.get { id, agent }: one of the session's sub-agents with its own response,
    // reasoning and tool history, within the session budget. Null when the session has gone
    // or has no sub-agent by that id, or from a source that lists none.
    Task<Subagent?> GetAgentAsync(string id, string agent, CancellationToken cancellationToken = default) => Task.FromResult<Subagent?>(null);

    // Where a live session's files are, for a host that types into it and then reads what
    // the agent recorded: its transcript and, for Claude and Codex, agent-ha-bridge's hook
    // registration. Null once it has gone, or from a source that does not know.
    SessionFiles? Files(string id) => null;

    // sessions.subscribe: the result to send, then on Events one session.upsert per live
    // session, sessions.synced (with what holds discovery, if anything does), and every live
    // change after that, discovery.changed included, in order. Dispose the
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

    // Why the last discovery pass could not account for everything, and for which agents, or
    // null when it could. While an agent is held, its sessions are not removed for being
    // missing, and nothing may be decided from a session of it being absent: no resume of it is
    // offered or started, and no worktree is reclaimed (§6.3 invariants 6 and 10).
    DiscoveryHeld? Held => null;

    // The process discovery bound a session to: its id, and its start time, which is what
    // tells it from a later process given the same id before the next discovery pass
    // notices. Null when the session has gone or the source cannot say.
    SessionProcess? ProcessOf(string id) => null;
}

public sealed record SessionProcess(int Id, DateTimeOffset? StartedAt);

public enum SessionChangeKind { Upserted, Removed }

public sealed record SessionChange(SessionChangeKind Kind, string Id, Session? Session, long Revision);

public sealed record SessionFiles(AgentKind Agent, string? Transcript, string? Registration);

// One event for a subscriber, named as on the wire (Events.SessionUpsert and so on), with
// its data record: Session, SessionRemoved, SessionsSynced or DiscoveryChanged.
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
