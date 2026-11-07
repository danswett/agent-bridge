using System.Text.Json;
using AgentBridge.Host.Core.Updates;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;

namespace AgentBridge.Host;

// Velopack's GithubSource with its release list from the machine's shared ReleaseDiscovery
// (#530), rather than a request to the API of its own at every check, and from the CDN's copy of
// that list first while ReleaseDiscovery.ReadCdnFirst is on (#927). The rest is Velopack's: each
// release's releases.<channel>.json, and the packages, from their browser download URLs on
// GitHub, which the API's limit does not count. The app compiles this same file (linked from
// AgentBridge.App.csproj), so the app and the host read releases one way.
// downloader: Velopack's own unless a test hands in one that serves the releases' feeds.
// installed: the version installed here, which a list from the CDN may not be older than
// (ReleaseDiscovery.CdnProblem).
sealed class DiscoveredGithubSource(string repository, bool prereleases, ReleaseDiscovery discovery, IFileDownloader? downloader = null, Func<string?>? installed = null)
    : GithubSource(repository, null, prereleases, downloader)
{
    // The channel of the check under way (the installed package's, `win` or `win-arm64`), which a
    // list from the CDN is judged by: Velopack names it to GetReleaseFeed, not to GetReleases.
    readonly AsyncLocal<string?> channel = new();

    public override Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null)
    {
        this.channel.Value = channel;
        return base.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
    }

    protected override async Task<GithubRelease[]> GetReleases(bool includePrereleases)
    {
        // Read through discovery, so a list this can't read is its failure, backed off, never kept.
        var releases = await discovery.ReleasesAsync(ReleaseDiscovery.ReleasesUrl(RepoUri.ToString()),
            json => JsonSerializer.Deserialize<GithubRelease?[]>(json) ?? [], CancellationToken.None, installed?.Invoke(), channel.Value, includePrereleases);
        // As GithubSource orders and filters its own.
        return [.. releases.OfType<GithubRelease>().OrderByDescending(r => r.PublishedAt).Where(r => includePrereleases || !r.Prerelease)];
    }
}
