using System.Collections.Concurrent;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Lite;

// What the lane knows of a session's screen: a bounded-age snapshot, because the scheduler asks for it under
// its own lock and must not read a console there. RefreshAsync reads the screen (a pump calls it before it asks
// the scheduler to deliver); Get answers from the last read, or "unknown" (null, null) when there is none, the
// read failed, or it is older than MaxAge. Age is measured on the monotonic TimeProvider clock, so a wall-clock
// step cannot make an old snapshot fresh. Unknown never admits a delivery.
internal sealed class LaneScreens
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(3);

    readonly Func<string, CancellationToken, Task<(bool? Idle, bool? Empty)>> read;
    readonly TimeProvider time;
    readonly TimeSpan maxAge;
    readonly ConcurrentDictionary<string, Snapshot> snapshots = new(StringComparer.Ordinal);

    sealed record Snapshot(bool? Idle, bool? Empty, long At);

    public LaneScreens(Func<string, CancellationToken, Task<(bool? Idle, bool? Empty)>> read, TimeProvider time, TimeSpan? maxAge = null)
    {
        this.read = read;
        this.time = time;
        this.maxAge = maxAge ?? MaxAge;
    }

    public static LaneScreens Create(ISessionSource source, IConsoleInjector injector, TimeProvider time) =>
        new((session, cancellation) => ReadAsync(source, injector, session, cancellation), time);

    // The snapshot is dated when its read began, so it is never younger than what it saw. A read that fails
    // leaves "unknown"; a cancelled one leaves the earlier snapshot to age out.
    public async Task RefreshAsync(string session, CancellationToken cancellationToken = default)
    {
        var at = time.GetTimestamp();
        (bool? Idle, bool? Empty) value;
        try { value = await read(session, cancellationToken); }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException) { value = (null, null); }
        snapshots.AddOrUpdate(session, new Snapshot(value.Idle, value.Empty, at), (_, old) => old.At > at ? old : new Snapshot(value.Idle, value.Empty, at));
    }

    public (bool? Idle, bool? Empty) Get(string session)
    {
        if (!snapshots.TryGetValue(session, out var snapshot)) { return (null, null); }
        var age = time.GetElapsedTime(snapshot.At);
        return age >= TimeSpan.Zero && age <= maxAge ? (snapshot.Idle, snapshot.Empty) : (null, null);
    }

    static async Task<(bool? Idle, bool? Empty)> ReadAsync(ISessionSource source, IConsoleInjector injector, string session, CancellationToken cancellationToken)
    {
        if (source.GetForDelivery(session) is not { } found || source.ProcessOf(session) is not { } process) { return (null, null); }
        var screen = await injector.ReadScreenAsync(process.Id, cancellationToken);
        return screen.Rows is { } rows ? Map(found.Status == SessionStatus.Idle && found.Question is null, rows) : (null, null);
    }

    // Empty only when the reader found the main box empty, outside shell and vim normal mode; a draft, a shell
    // box or a box it cannot place is not empty. Idle also needs the screen to show no turn running. A screen
    // with no single box is unknown.
    internal static (bool? Idle, bool? Empty) Map(bool sessionIdle, IReadOnlyList<string> rows)
    {
        var box = CopilotInputBoxReader.Read(rows);
        return box.State is { } state ? (sessionIdle && !state.Working, box.Box is not null) : (null, null);
    }
}
