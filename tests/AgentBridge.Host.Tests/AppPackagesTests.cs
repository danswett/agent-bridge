using System.Security.Cryptography;
using AgentBridge.App;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;
using Xunit;

// The app's updater against Velopack itself (AppPackages): a release published with a delta from
// the installed one is still downloaded as its full package, checked against the feed's checksum.
public sealed class AppPackagesTests : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "abapp-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(root)) { Directory.Delete(root, true); }
    }

    // Engineering's Claude-family review of #719 (6677216492), HIGH: built from the delta, the
    // package was never the full package's bytes, so the app's own check (DownloadMark) failed
    // every such update.
    [Theory]
    [InlineData("Slipgates")]
    [InlineData("AgentBridgeApp")]
    public async Task A_release_with_a_delta_from_the_installed_one_is_downloaded_as_its_full_package_matching_the_feeds_checksum(string downloadName)
    {
        var feed = Directory.CreateDirectory(Path.Combine(root, "feed")).FullName;
        var packages = Directory.CreateDirectory(Path.Combine(root, "packages")).FullName;
        var full = RandomNumberGenerator.GetBytes(4096);
        var delta = RandomNumberGenerator.GetBytes(64);
        File.WriteAllBytes(Path.Combine(feed, "AgentBridgeApp-0.1.601-full.nupkg"), full);
        File.WriteAllBytes(Path.Combine(feed, "AgentBridgeApp-0.1.601-delta.nupkg"), delta);
        File.WriteAllBytes(Path.Combine(feed, $"{downloadName}-win-Setup.exe"), RandomNumberGenerator.GetBytes(64));
        File.WriteAllBytes(Path.Combine(feed, $"{downloadName}-win-Portable.zip"), RandomNumberGenerator.GetBytes(64));
        // The installed version's full package, the base a delta applies to.
        File.WriteAllBytes(Path.Combine(packages, "AgentBridgeApp-0.1.600-full.nupkg"), RandomNumberGenerator.GetBytes(4096));
        var assets = VelopackAssetFeed.FromJson($$"""
            {"Assets":[
            {{Asset("0.1.600", "Full", "AgentBridgeApp-0.1.600-full.nupkg", RandomNumberGenerator.GetBytes(4096))}},
            {{Asset("0.1.601", "Full", "AgentBridgeApp-0.1.601-full.nupkg", full)}},
            {{Asset("0.1.601", "Delta", "AgentBridgeApp-0.1.601-delta.nupkg", delta)}}]}
            """).Assets;
        var target = assets.Single(a => a.Type == VelopackAssetType.Full && a.Version.ToString() == "0.1.601");
        var update = new UpdateInfo(target, false,
            assets.Single(a => a.Version.ToString() == "0.1.600"), assets.Where(a => a.Type == VelopackAssetType.Delta).ToArray());
        var source = new Recording(new SimpleFileSource(new DirectoryInfo(feed)));
        // An installer that is there (Velopack looks for it before it takes a delta), but is no
        // program: applying a delta with it fails, and Velopack then downloads the full package.
        var installer = Path.Combine(root, "Update.exe");
        File.WriteAllText(installer, "not a program");
        var copy = new TestVelopackLocator("AgentBridgeApp", "0.1.600", packages, null, null, installer, "win");

        await new UpdateManager(source, AppPackages.Options, copy).DownloadUpdatesAsync(update);

        Assert.Equal(["AgentBridgeApp-0.1.601-full.nupkg"], source.Downloaded);
        Assert.Equal(target.SHA256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(packages, target.FileName)))), ignoreCase: true);
    }

    static string Asset(string version, string type, string file, byte[] bytes) =>
        $$"""{"PackageId":"AgentBridgeApp","Version":"{{version}}","Type":"{{type}}","FileName":"{{file}}","SHA1":"{{Convert.ToHexString(SHA1.HashData(bytes))}}","SHA256":"{{Convert.ToHexString(SHA256.HashData(bytes))}}","Size":{{bytes.Length}}}""";

    sealed class Recording(IUpdateSource inner) : IUpdateSource
    {
        public List<string> Downloaded { get; } = [];
        public Task<VelopackAssetFeed> GetReleaseFeed(IVelopackLogger logger, string? appId, string channel, Guid? stagingId = null, VelopackAsset? latestLocalRelease = null) =>
            inner.GetReleaseFeed(logger, appId, channel, stagingId, latestLocalRelease);
        public Task DownloadReleaseEntry(IVelopackLogger logger, VelopackAsset releaseEntry, string localFile, Action<int> progress, CancellationToken cancelToken = default)
        {
            Downloaded.Add(releaseEntry.FileName);
            return inner.DownloadReleaseEntry(logger, releaseEntry, localFile, progress, cancelToken);
        }
    }
}
