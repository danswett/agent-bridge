namespace AgentBridge.App.Core;

// Channel selection (stable/pre-release and architecture) never relaxes transport security.
public static class UpdateFeedPolicy
{
    public const string DevelopmentVariable = "AGENT_BRIDGE_DEVELOPMENT_UPDATE_FEEDS";

    // Null means a directory path. The caller retains its normal local-file source.
    public static Uri? WebUri(string feed, bool developmentMode = false)
    {
        if (!Uri.TryCreate(feed, UriKind.Absolute, out var uri))
        {
            if (feed.Contains("://", StringComparison.Ordinal) || feed.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || feed.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The update feed URL is invalid.");
            }
            return null;
        }
        if (uri.IsFile) { return null; }
        if (uri.Scheme == Uri.UriSchemeHttps) { return uri; }
        if (developmentMode && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) { return uri; }
        throw new InvalidOperationException("Remote update feeds require HTTPS. Development mode permits HTTP only on loopback.");
    }
}
