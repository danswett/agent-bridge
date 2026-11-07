using Velopack;

namespace AgentBridge.App;

// How the app's updater downloads a release (VelopackUpdater). Linked into the host's tests, which
// try it against Velopack itself (AppPackagesTests).
static class AppPackages
{
    // Full packages only (Engineering's Claude-family review of #719, 6677216492). Velopack
    // (1.2.161) rebuilds a release from deltas into a zip that is never the published full
    // package's bytes, and moves it into place unchecked, so every such update failed the check
    // against the feed's checksum (DownloadMark), dropped its base and was held before a full
    // download got through. A full package Velopack checks itself against that same checksum
    // (size, and SHA-256, else SHA-1) before DownloadMark checks it again. Any delta is more than
    // none, so none is used.
    public static UpdateOptions Options => new() { MaximumDeltasBeforeFallback = 0 };
}
