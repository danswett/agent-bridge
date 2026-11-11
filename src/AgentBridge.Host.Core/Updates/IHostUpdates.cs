namespace AgentBridge.Host.Core.Updates;

// The handle belongs to the updater that found the release: callers pass it back unchanged so
// a later check cannot substitute another release or feed for the one being installed.
public sealed record UpdateRelease(string Version, string? Notes, string? Url)
{
    public object? Handle { get; init; }
}

public interface IHostUpdates
{
    // Null when this copy is not one its updater can update.
    string? Installed { get; }
    string? ReleasesUrl { get; }
    // The newest release when it is newer than the one installed; null when current.
    Task<UpdateRelease?> CheckAsync(CancellationToken cancellationToken);
    Task DownloadAsync(UpdateRelease release, Action<int> progress, CancellationToken cancellationToken);
    // Hands the downloaded release to the updater, which applies it once this process has exited,
    // and has the host exit. Throws when it cannot be handed over.
    void ApplyAndRestart(UpdateRelease release);
    Task ApplyAndRestartAsync(UpdateRelease release)
    {
        ApplyAndRestart(release);
        return Task.CompletedTask;
    }
    // Before handing over: everything the update would end is safe to end, or this throws why
    // not. A usage probe whose input goes with a host must not be cut mid-refresh.
    Task ReadyAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
