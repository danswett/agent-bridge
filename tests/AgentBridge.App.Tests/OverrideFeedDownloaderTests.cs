using AgentBridge.App;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Logging;
using Velopack.Sources;
using Xunit;

public sealed class OverrideFeedDownloaderTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Override_feed_and_package_downloads_never_follow_a_redirect(bool package)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var sinkRequests = 0;
        app.MapGet("/feed", (HttpContext context) => Results.Redirect($"http://{context.Request.Host}/sink"));
        app.MapGet("/sink", () => { Interlocked.Increment(ref sinkRequests); return "harmless redirect sink"; });
        await app.StartAsync();
        var downloader = new OverrideFeedDownloader(developmentMode: true);
        var url = app.Urls.Single() + "/feed";
        var target = Path.Combine(Path.GetTempPath(), $"agent-bridge-redirect-{Guid.NewGuid():N}.tmp");
        try
        {
            if (package)
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadFile(url, target, _ => {}, null, 1, CancellationToken.None));
            }
            else
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => downloader.DownloadBytes(url, null, 1));
            }
            Assert.Equal(0, Volatile.Read(ref sinkRequests));
        }
        finally
        {
            File.Delete(target);
            await app.StopAsync();
        }
    }

    [Theory]
    [InlineData("http://updates.example/package.nupkg")]
    [InlineData("http://127.0.0.1/package.nupkg")]
    [InlineData("file:///tmp/package.nupkg")]
    [InlineData("/tmp/package.nupkg")]
    public async Task Every_download_entry_point_rejects_insecure_or_local_destinations(string url)
    {
        var downloader = new OverrideFeedDownloader();
        var target = Path.Combine(Path.GetTempPath(), $"agent-bridge-rejected-{Guid.NewGuid():N}.tmp");
        await Assert.ThrowsAsync<InvalidOperationException>(() => downloader.DownloadBytes(url, null, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => downloader.DownloadString(url, null, 1));
        await Assert.ThrowsAsync<InvalidOperationException>(() => downloader.DownloadFile(url, target, _ => {}, null, 1));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task An_https_feed_cannot_download_an_absolute_http_package()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        await using var app = builder.Build();
        var requests = 0;
        app.MapGet("/package.nupkg", () => { Interlocked.Increment(ref requests); return "not a package"; });
        await app.StartAsync();
        var target = Path.Combine(Path.GetTempPath(), $"agent-bridge-package-{Guid.NewGuid():N}.tmp");
        try
        {
            var source = new SimpleWebSource("https://updates.example/", new OverrideFeedDownloader());
            var asset = new VelopackAsset { FileName = app.Urls.Single() + "/package.nupkg" };
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                source.DownloadReleaseEntry(NullVelopackLogger.Instance, asset, target, _ => {}, CancellationToken.None));
            Assert.Equal(0, Volatile.Read(ref requests));
            Assert.False(File.Exists(target));
        }
        finally { File.Delete(target); await app.StopAsync(); }
    }
}
