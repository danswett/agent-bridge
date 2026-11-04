namespace AgentBridge.App.Core;

// Channel selection (stable/pre-release and architecture) never relaxes transport security.
public static class UpdateFeedPolicy
{
    public const string DevelopmentVariable = "AGENT_BRIDGE_DEVELOPMENT_UPDATE_FEEDS";

    // Null means a directory path. The caller retains its normal local-file source.
    public static Uri? WebUri(string feed, bool developmentMode = false)
    {
        feed = feed.Trim();
        // Check before URI normalization: Windows accepts mixed separators and extended UNC
        // namespaces, including WebDAV shares that can silently reintroduce cleartext transport.
        if (NetworkPath(feed)) { throw new InvalidOperationException("Update feed directories must be local."); }
        if (!Uri.TryCreate(feed, UriKind.Absolute, out var uri))
        {
            if (feed.Contains("://", StringComparison.Ordinal) || feed.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || feed.StartsWith("https:", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The update feed URL is invalid.");
            }
            return null;
        }
        if (uri.IsFile)
        {
            if (uri.Host.Length != 0 || uri.IsUnc || NetworkPath(uri.LocalPath)) { throw new InvalidOperationException("Update feed directories must be local."); }
            return null;
        }
        if (uri.Scheme == Uri.UriSchemeHttps) { return uri; }
        if (developmentMode && uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback) { return uri; }
        throw new InvalidOperationException("Remote update feeds require HTTPS. Development mode permits HTTP only on loopback.");
    }
    public static string LocalDirectory(string feed)
    {
        if (WebUri(feed) is not null) { throw new InvalidOperationException("The update feed is not a local directory."); }
        return Uri.TryCreate(feed.Trim(), UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath : feed.Trim();
    }

    static bool NetworkPath(string path) => path.Replace('\\', '/').StartsWith("//", StringComparison.Ordinal);
}
