using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core.Updates;

namespace AgentBridge.App;

// The bad-release policy the app's own updater goes by (#3307): the host's verified reader
// (GitHubHaltListSource) over the machine's shared copy, renewed within that copy's shared hourly
// budget. Before VelopackUpdater read it, its HaltPolicy was null, which AutoUpdater reads as
// "holds nothing", so an unasked install went ahead over protection nobody had checked. Until a
// read succeeds, and whenever one fails, the policy is unknown (no verified policy, not fresh),
// never an empty list, and halts known before are kept: an install nobody asked for holds
// (AutoUpdater.PolicyHold) while a person's press still installs, as the owner decided (Nov 19).
// Freshness is judged when read, so a policy past its six hours holds even between checks.
sealed class UpdaterHaltPolicy(GitHubHaltListSource source, Action<string>? log = null, TimeProvider? time = null)
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public UpdaterHaltPolicy(ReleaseDiscovery discovery, Action<string>? log = null) : this(new GitHubHaltListSource(Http, discovery), log) { }

    HaltPolicySnapshot read = new(null, null, false);

    public HaltPolicySnapshot Snapshot => read with { Fresh = read is { Fresh: true, At: { } at }
        && (time ?? TimeProvider.System).GetUtcNow() - at < ReleaseDiscovery.GithubRevalidateEvery };

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try { read = await source.ReadSnapshotAsync(cancellationToken); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException
            or JsonException or InvalidOperationException or ReleaseDiscoveryException
            || e is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            log?.Invoke($"update policy: protection could not be read ({e.GetType().Name}); unasked installs hold");
            read = read with { Fresh = false };
        }
    }
}
