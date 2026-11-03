using Velopack.Sources;

namespace AgentBridge.App;

// An override is a pinned destination. A redirect must not escape its transport policy;
// this applies to both the feed and packages fetched by SimpleWebSource.
sealed class OverrideFeedDownloader : HttpClientFileDownloader
{
    protected override HttpClientHandler CreateHttpClientHandler()
    {
        var handler = base.CreateHttpClientHandler();
        handler.AllowAutoRedirect = false;
        return handler;
    }
}
