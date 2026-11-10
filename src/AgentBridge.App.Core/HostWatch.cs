using AgentBridge.Local;

namespace AgentBridge.App.Core;

// How the watch ended. Connected: the host serves and reached the relay. Quiet: nothing to blame
// on the host (no relay to reach, or one that does not answer). Failed: Reason says why.
public enum HostWatchEnd { Connected, Quiet, Failed }

public sealed record HostWatchResult(HostWatchEnd End, string? Reason = null);

// After an update the app watches the new host (#581, #1308). It fails when the host cannot start
// three times, or has not reached the relay within Window while the relay's /healthz answers. A
// relay that is down, or a host with no account, is not the host's fault and ends it quietly:
// blaming the host then would only send the owner after a good build. Every input is a seam
// (relay state, start failures, the probe) so tests need no host; the clock is the injected one.
public sealed class HostWatch(Func<RelayState?> relay, Func<int> startFailures, Func<CancellationToken, Task<bool>> reachable, TimeProvider? time = null)
{
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    public const int FailedStarts = 3;

    public TimeSpan Poll { get; init; } = TimeSpan.FromSeconds(5);

    readonly TimeProvider clock = time ?? TimeProvider.System;

    // relay: the host's relay state, or null while its status cannot be read (not serving).
    public async Task<HostWatchResult> RunAsync(CancellationToken ct = default)
    {
        var began = clock.GetUtcNow();
        var baseline = startFailures();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            // A start that worked resets the count: the failures are then counted again from there.
            var failed = startFailures();
            if (failed < baseline) { baseline = failed; }
            if (failed - baseline >= FailedStarts) { return new(HostWatchEnd.Failed, $"the host failed to start {FailedStarts} times after the update"); }
            switch (relay())
            {
                case RelayState.Connected: return new(HostWatchEnd.Connected);
                case RelayState.SignedOut or RelayState.Off: return new(HostWatchEnd.Quiet);
            }
            if (clock.GetUtcNow() - began >= Window)
            {
                return await Reachable(ct)
                    ? new(HostWatchEnd.Failed, $"the host did not connect to the relay within {Window.TotalMinutes:0} minutes of the update, though the relay answers")
                    : new(HostWatchEnd.Quiet);
            }
            await Task.Delay(Poll, clock, ct);
        }
    }

    async Task<bool> Reachable(CancellationToken ct)
    {
        try { return await reachable(ct); }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException or StackOverflowException)) { return false; }
    }

    // GET {relay}/healthz answering 2xx: the real probe; the relay's address is the host's status's.
    public static Func<CancellationToken, Task<bool>> Healthz(Func<string?> relayUrl, HttpClient http) => async ct =>
    {
        if (!Uri.TryCreate(relayUrl(), UriKind.Absolute, out var url)) { return false; }
        var scheme = url.Scheme is "wss" ? "https" : url.Scheme is "ws" ? "http" : url.Scheme;
        using var response = await http.GetAsync(new UriBuilder(url) { Scheme = scheme, Port = url.IsDefaultPort ? -1 : url.Port, Path = "/healthz", Query = "" }.Uri, ct);
        return response.IsSuccessStatusCode;
    };
}
