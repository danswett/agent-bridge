using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

internal sealed class AutoRetryWatch(ISessionSource source, ISessionReplier replier, Func<bool> enabled, TimeProvider time, Action<string> log,
    Func<string, bool>? launchHeld = null, Func<bool>? serving = null) : IAsyncDisposable
{
    sealed class Entry(Session expected)
    {
        public Session Expected = expected;
        public readonly Activity Before = expected.Activity;
        public DateTimeOffset? SentError;
        public RetryEpisode Episode = null!;
    }
    readonly Dictionary<string, Entry> episodes = new(StringComparer.Ordinal);
    readonly SemaphoreSlim tick = new(1, 1);
    readonly CancellationTokenSource stop = new();
    CancellationTokenRegistration stopping;
    Task? running;

    public void Start(CancellationToken cancellation)
    {
        stopping = cancellation.Register(stop.Cancel);
        running = RunAsync(stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await stop.CancelAsync();
        if (running is not null) await running;
        stopping.Dispose();
        stop.Dispose();
        tick.Dispose();
    }

    async Task RunAsync(CancellationToken cancellation)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1), time);
            while (await timer.WaitForNextTickAsync(cancellation))
            {
                try { await TickAsync(cancellation); }
                catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
                { log($"auto-retry pass failed ({e.GetType().Name})"); }
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        finally { foreach (var e in episodes.Values) e.Episode.Cancel(); }
    }

    internal async Task TickAsync(CancellationToken cancellation = default)
    {
        if (serving?.Invoke() == false) return;
        if (!await tick.WaitAsync(0, cancellation)) return;
        try
        {
            var live = source.Snapshot();
            foreach (var id in episodes.Keys.Except(live.Select(s => s.Id)).ToArray())
            {
                episodes[id].Episode.Cancel();
                episodes.Remove(id);
            }
            var pending = new List<Task>();
            foreach (var listed in live)
            {
                var s = source.GetForDelivery(listed.Id);
                if (s is null || s.Agent != AgentKind.Copilot) continue;
                episodes.TryGetValue(s.Id, out var entry);
                var reason = Reason(s);
                // An acknowledged continue briefly clears attention; that alone is not healing.
                if (reason is null && s.Status == SessionStatus.Idle && entry is not null
                    && MainResponse(s.Activity) is { } response && (response != MainResponse(entry.Before) || s.Activity.ResponseTurn != entry.Before.ResponseTurn)
                    && !response.StartsWith("\u26a0\ufe0f Turn error:", StringComparison.Ordinal))
                {
                    entry.Episode.Cancel();
                    episodes.Remove(s.Id);
                    entry = null;
                }
                if (reason == "no-response" && entry is null && enabled() && Guid.TryParse(s.Id, out var session) && session != Guid.Empty)
                {
                    entry = new(s);
                    var owned = entry;
                    entry.Episode = new(session, () => Observe(owned), replier, time, m => log($"auto-retry {s.Id} no-response: {m}"));
                    episodes.Add(s.Id, entry);
                }
                if (entry is not null)
                {
                    entry.Expected = s;
                    if (!enabled() || reason is not (null or "no-response")) entry.Episode.Cancel();
                }
                pending.Add(AttemptAsync(s, entry, reason, cancellation));
            }
            await Task.WhenAll(pending);
        }
        finally { tick.Release(); }
    }

    async Task AttemptAsync(Session s, Entry? entry, string? reason, CancellationToken cancellation)
    {
        if (entry is not null && reason == "no-response")
        {
            try
            {
                if (await entry.Episode.TryAsync(entry.Episode.NextAttempt, Guid.NewGuid(), RetryReason.Auto, cancellation,
                    ready: s.Attention is { } failure && (entry.SentError is null || failure.Since > entry.SentError.Value)))
                    entry.SentError = s.Attention?.Since;
            }
            catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
            { log($"auto-retry {s.Id}: failed ({e.GetType().Name})"); }
        }
        reason = Reason(source.GetForDelivery(s.Id) ?? s);
        var state = reason is not (null or "no-response") ? "needs-person" : !enabled() ? "off"
            : entry?.Episode.Stop != RetryStop.None ? "needs-person" : "backoff";
        await source.SetAutoRetryAsync(s.Id, reason is null && entry is null ? null
            : new(state, reason ?? "no-response", entry?.Episode.Attempts.Count ?? 0), cancellation);
    }

    RetryObservation? Observe(Entry entry)
    {
        var s = source.GetForDelivery(entry.Expected.Id);
        if (s is null || source.ProcessOf(s.Id) is not { } process || s.Attention is not { } error) return null;
        return new(default, error.Since, process,
            s.Attention.Since == entry.Expected.Attention?.Since && s.Activity == entry.Expected.Activity ? 0 : 1,
            enabled() && serving?.Invoke() != false && Reason(s) == "no-response" && s.Status == SessionStatus.Idle && s.Question is null
                && s.Activity.CurrentTool is not { Status: ToolStatus.Running } && s.Activity.Background is null
                && s.Subagents?.Any(a => !a.Finished) != true,
            error.AutoRetryable);
    }

    static string? MainResponse(Activity a) => a.Source?.Response is null ? a.Response ?? a.Own?.Response : a.Own?.Response;

    string? Reason(Session s) => launchHeld?.Invoke(s.Id) == true ? "permission"
        : s.Question is { } q ? q.Kind == QuestionKind.Form ? "question" : "permission"
        : s.Attention is { } a ? a.AutoRetryable ? "no-response"
            : a.Kind == AttentionKind.SignInNeeded || a.ErrorClass == ErrorClass.Auth ? "auth"
            : a.ErrorClass == ErrorClass.Quota ? "quota" : "other"
        : s.Status == SessionStatus.Waiting ? "question" : null;
}
