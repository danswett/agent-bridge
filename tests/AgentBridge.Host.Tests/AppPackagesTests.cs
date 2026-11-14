using System.Net;
using System.Security.Cryptography;
using AgentBridge.App;
using Velopack;
using Velopack.Locators;
using Velopack.Logging;
using Velopack.Sources;
using Microsoft.Extensions.Time.Testing;
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

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 0)]
    [InlineData(false, 100)]
    public async Task Package_download_counts_are_written_bytes_and_only_the_responses_size_is_reported(bool hasSize, int readMilliseconds)
    {
        Directory.CreateDirectory(root);
        var time = new FakeTimeProvider();
        var counts = new List<(long Bytes, long? Total, DateTimeOffset At)>();
        var requests = 0;
        var downloader = new PackageDownloader(() => new Response(hasSize, () => requests++,
            () => time.Advance(TimeSpan.FromMilliseconds(readMilliseconds))), time)
        {
            Report = (bytes, total) => counts.Add((bytes, total, time.GetUtcNow())),
        };
        var file = Path.Combine(root, "package.nupkg");

        await downloader.DownloadFile("https://packages.invalid/package.nupkg", file, _ => { }, null!, 30);

        Assert.Equal(1, requests);
        Assert.NotEmpty(counts);
        Assert.InRange(counts.Count, 2, readMilliseconds == 0 ? 2 : 68);
        if (readMilliseconds > 0) { Assert.True(counts.Count > 2); }
        for (var i = 1; i < counts.Count - 1; i++)
        {
            Assert.True(counts[i].At - counts[i - 1].At >= TimeSpan.FromMilliseconds(250));
        }
        Assert.Equal(200_000, counts[^1].Bytes);
        Assert.Equal(200_000, new FileInfo(file).Length);
        Assert.All(counts, count => Assert.Equal(hasSize ? 200_000L : (long?)null, count.Total));
        Assert.True(counts.Select(c => c.Bytes).SequenceEqual(counts.Select(c => c.Bytes).Distinct().Order()));
        counts.Clear();
        await downloader.DownloadString("https://packages.invalid/feed.json", null!, 30);
        Assert.Empty(counts);
    }

    [Fact]
    public async Task A_retry_response_cannot_relabel_the_previous_streams_buffered_bytes()
    {
        Directory.CreateDirectory(root);
        var counts = new List<(long Bytes, long? Total)>();
        var handler = new Retry();
        var downloader = new PackageDownloader(() => handler, new FakeTimeProvider())
        {
            Report = (bytes, total) => counts.Add((bytes, total)),
        };

        await Assert.ThrowsAsync<IOException>(() => downloader.DownloadFile(
            "https://packages.invalid/Package.nupkg", Path.Combine(root, "package.nupkg"), _ => { }, null!, 30));

        Assert.Equal(2, handler.Requests);
        Assert.Equal(new long[] { 1000, 3000 }, counts.Select(c => c.Bytes));
        Assert.All(counts, count => Assert.Null(count.Total));
    }

    sealed class Retry : HttpClientHandler
    {
        public int Requests;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(++Requests == 1
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new Content(false, () => { }, true) }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("failure") });
    }

    sealed class Response(bool hasSize, Action requested, Action read) : HttpClientHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new Content(hasSize, read) });
        }
    }

    sealed class Content(bool hasSize, Action read, bool fails = false) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 200_000; return hasSize; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            stream.WriteAsync(new byte[200_000]).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new Chunks(read, fails));
    }

    sealed class Chunks(Action read, bool fails) : MemoryStream(new byte[200_000])
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (fails && Position >= 3000) { throw new IOException("the package stream failed"); }
            read();
            return base.ReadAsync(buffer[..Math.Min(buffer.Length, 1000)], cancellationToken);
        }
    }

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
