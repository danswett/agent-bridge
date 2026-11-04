using AgentBridge.App.Core;
using Velopack.Sources;

namespace AgentBridge.App;

// An override is a pinned destination. A redirect must not escape its transport policy;
// this applies to both the feed and packages fetched by SimpleWebSource.
sealed class OverrideFeedDownloader(bool developmentMode = false) : HttpClientFileDownloader
{
    public override Task DownloadFile(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers, double timeout, CancellationToken cancelToken = default)
        => base.DownloadFile(Checked(url), targetFile, progress, headers, timeout, cancelToken);

    public override Task<byte[]> DownloadBytes(string url, IDictionary<string, string>? headers, double timeout)
        => base.DownloadBytes(Checked(url), headers, timeout);

    public override Task<string> DownloadString(string url, IDictionary<string, string>? headers, double timeout)
        => base.DownloadString(Checked(url), headers, timeout);

    // A secure feed can name an absolute package URL; validate every actual request.
    string Checked(string url) => UpdateFeedPolicy.WebUri(url, developmentMode)?.AbsoluteUri
        ?? throw new InvalidOperationException("Web update downloads require an allowed HTTP or HTTPS URL.");

    protected override HttpClientHandler CreateHttpClientHandler()
    {
        var handler = base.CreateHttpClientHandler();
        handler.AllowAutoRedirect = false;
        return handler;
    }
}
