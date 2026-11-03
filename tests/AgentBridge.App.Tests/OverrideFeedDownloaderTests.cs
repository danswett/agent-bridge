using AgentBridge.App;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
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
        var downloader = new OverrideFeedDownloader();
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
}
