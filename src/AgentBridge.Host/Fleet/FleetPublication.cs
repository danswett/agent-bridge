using System.Net.WebSockets;
using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Local;

namespace AgentBridge.Host.Fleet;

// One publisher for the host's lifetime; each connection owns and awaits its loop.
public sealed class FleetPublication
{
    readonly ISessionSource source;
    readonly bool share;
    readonly TimeProvider clock;
    readonly FleetPublisher publisher;
    readonly Action<string>? log;
    long? failedAt;
    TimeSpan retry;
    FleetPublicationStatus status = new("not-started");
    public event Action? Changed;

    public FleetPublication(ISessionSource source, string machine, bool share, Func<string, string?>? name = null,
        TimeProvider? time = null, Action<string>? log = null)
    {
        this.source = source; this.share = share; this.log = log;
        clock = time ?? TimeProvider.System;
        publisher = new FleetPublisher(source, machine, name, clock);
    }

    public FleetPublicationStatus Status => Volatile.Read(ref status);

    public async Task<bool> PollAsync(Func<JsonElement, CancellationToken, Task> put, bool force = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (failedAt is { } at && clock.GetElapsedTime(at) < retry) { return false; }
        try
        {
            var sent = await publisher.PublishAsync(put, share: share, force: force, ct: ct);
            if (sent)
            {
                failedAt = null;
                Set(new(share ? "published" : "sharing-off", publisher.SkippedInvalidIds));
            }
            else if (share && source.Held is not null) { Set(Status with { Result = "discovery-held", NextRetry = null }); }
            else { Set(Status with { SkippedInvalidIds = publisher.SkippedInvalidIds }); }
            return sent;
        }
        catch (Exception e) when (e is InvalidDataException or IOException or InvalidOperationException or TimeoutException or WebSocketException or JsonException
            || e is OperationCanceledException && !ct.IsCancellationRequested)
        {
            var failures = Status.Failures < int.MaxValue ? Status.Failures + 1 : int.MaxValue;
            retry = TimeSpan.FromSeconds(Math.Min(30, Math.Pow(2, Math.Min(failures, 5))));
            failedAt = clock.GetTimestamp();
            var failure = e switch
            {
                InvalidDataException or JsonException => "invalid-snapshot", ObjectDisposedException => "transport",
                InvalidOperationException => "refused", TimeoutException => "timeout", _ => "transport",
            };
            Set(new("failed", publisher.SkippedInvalidIds, failures, clock.GetUtcNow() + retry, failure));
            return false;
        }
    }

    void Set(FleetPublicationStatus next)
    {
        if (Status == next) { return; }
        Volatile.Write(ref status, next);
        log?.Invoke($"fleet registry: {next.Result}; skipped invalid ids {next.SkippedInvalidIds}; failures {next.Failures}; {next.Failure}");
        Changed?.Invoke();
    }

    public async Task RunAsync(Func<JsonElement, CancellationToken, Task> put, Task disconnected, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (disconnected.IsCompleted) { await disconnected; return; }
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loop = LoopAsync(stop.Token);
        try { await await Task.WhenAny(loop, disconnected).WaitAsync(ct); }
        finally
        {
            await stop.CancelAsync();
            try { await loop; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }

        async Task LoopAsync(CancellationToken token)
        {
            var force = true;
            while (true)
            {
                if (await PollAsync(put, force, token)) { force = false; }
                await Task.Delay(TimeSpan.FromSeconds(2), clock, token);
            }
        }
    }
}
