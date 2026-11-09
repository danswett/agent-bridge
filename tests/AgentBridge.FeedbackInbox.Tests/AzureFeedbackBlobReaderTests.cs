using System.Net;
using System.Net.Http.Headers;
using System.Text;
using AgentBridge.FeedbackInbox;
using Azure.Core.Pipeline;
using Azure.Storage.Blobs;
using Xunit;

namespace AgentBridge.FeedbackInbox.Tests;

public sealed class AzureFeedbackBlobReaderTests
{
    [Fact]
    public async Task Reads_the_container_shape_without_writing_or_deleting()
    {
        var handler = new StorageHandler();
        var reader = Reader(handler);
        var names = new List<string>();
        await foreach (var name in reader.ListAsync(CancellationToken.None)) { names.Add(name); }
        Assert.Equal(["fixture"], names);
        Assert.Equal(20, await reader.GetSizeAsync("fixture", CancellationToken.None));
        var limited = await reader.ReadAsync("fixture", 10, CancellationToken.None);
        Assert.Equal(11, limited!.Value.Length);
        Assert.Equal(new[] { "GET", "HEAD", "GET" }, handler.Methods);
    }

    [Fact]
    public async Task Missing_blobs_are_absent_and_transient_errors_are_retryable()
    {
        var handler = new StorageHandler();
        var reader = Reader(handler);
        Assert.Null(await reader.GetSizeAsync("missing", CancellationToken.None));
        Assert.Null(await reader.ReadAsync("missing", 10, CancellationToken.None));
        handler.Unavailable = true;
        await Assert.ThrowsAsync<FeedbackInboxTransientException>(
            () => reader.GetSizeAsync("fixture", CancellationToken.None));
        await Assert.ThrowsAsync<FeedbackInboxTransientException>(
            () => reader.ReadAsync("fixture", 10, CancellationToken.None));
    }

    static AzureFeedbackBlobReader Reader(StorageHandler handler)
    {
        var options = new BlobClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
        options.Retry.MaxRetries = 0;
        return new AzureFeedbackBlobReader(new BlobContainerClient(new Uri("https://fixture.invalid/feedback"), options));
    }

    sealed class StorageHandler : HttpMessageHandler
    {
        public List<string> Methods { get; } = [];
        public bool Unavailable { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Methods.Add(request.Method.Method);
            if (Unavailable) { return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)); }
            if (request.RequestUri!.AbsolutePath.EndsWith("/missing", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            if (request.RequestUri.Query.Contains("comp=list", StringComparison.Ordinal))
            {
                const string xml = """
                    <?xml version="1.0" encoding="utf-8"?>
                    <EnumerationResults ServiceEndpoint="https://fixture.invalid/" ContainerName="feedback">
                      <Blobs><Blob><Name>fixture</Name><Properties><Content-Length>20</Content-Length></Properties></Blob></Blobs>
                      <NextMarker />
                    </EnumerationResults>
                    """;
                response.Content = new StringContent(xml, Encoding.UTF8, "application/xml");
            }
            else
            {
                response.Content = new ByteArrayContent(new byte[20]);
                response.Content.Headers.ContentLength = 20;
                response.Content.Headers.LastModified = DateTimeOffset.UnixEpoch;
                response.Headers.ETag = new EntityTagHeaderValue("\"fixture\"");
                response.Headers.Add("x-ms-blob-type", "BlockBlob");
            }
            return Task.FromResult(response);
        }
    }
}
