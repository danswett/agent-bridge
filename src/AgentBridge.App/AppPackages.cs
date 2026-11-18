using Velopack;
using Velopack.Sources;

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

// Keep Velopack's downloader and handler settings; observe only its package file writes.
class PackageDownloader(Func<HttpClientHandler>? handler = null, TimeProvider? time = null) : HttpClientFileDownloader
{
    public Action<long, long?>? Report { get; set; }
    readonly AsyncLocal<Action<long, long?>?> reporting = new();
    readonly AsyncLocal<Observation?> observation = new();
    sealed class Observation(Action<long, long?> report, TimeProvider time)
    {
        public long? Total;
        long written, sent;
        long? at;
        public void Written(long bytes)
        {
            written = bytes;
            // Before the UI callback: neither disk-backed source checks nor state sends run per chunk.
            if (at is null || time.GetElapsedTime(at.Value) >= TimeSpan.FromMilliseconds(250)) { Flush(); }
        }
        public void Flush()
        {
            if (written == sent) { return; }
            sent = written;
            at = time.GetTimestamp();
            report(written, Total);
        }
    }

    public override async Task DownloadFile(string url, string targetFile, Action<int> progress,
        IDictionary<string, string>? headers, double timeout, CancellationToken cancelToken = default)
    {
        reporting.Value = Report;
        try { await base.DownloadFile(url, targetFile, progress, headers, timeout, cancelToken); }
        finally { reporting.Value = null; }
    }

    protected override HttpClientHandler CreateHttpClientHandler() =>
        new ResponseHandler(handler?.Invoke() ?? base.CreateHttpClientHandler(), total =>
        {
            if (observation.Value is { } current) { current.Total = total; }
        });

    protected override async Task DownloadToStreamInternal(HttpClient client, string url, Stream targetStream,
        Action<int>? progress, CancellationToken cancelToken)
    {
        var current = reporting.Value is { } report ? new Observation(report, time ?? TimeProvider.System) : null;
        observation.Value = current;
        try
        {
            await base.DownloadToStreamInternal(client, url,
                current is not null ? new WrittenStream(targetStream, current.Written) : targetStream, progress, cancelToken);
        }
        finally { observation.Value = null; current?.Flush(); }
    }

    sealed class ResponseHandler(HttpClientHandler inner, Action<long?> received) : HttpClientHandler
    {
        readonly HttpMessageInvoker sender = new(inner);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await sender.SendAsync(request, cancellationToken);
            received(response.Content.Headers.ContentLength);
            return response;
        }
        protected override void Dispose(bool disposing) { if (disposing) { sender.Dispose(); } base.Dispose(disposing); }
    }

    sealed class WrittenStream(Stream inner, Action<long> report) : Stream
    {
        long written;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => inner.SetLength(value);
        public override void Write(byte[] buffer, int offset, int count)
        {
            inner.Write(buffer, offset, count);
            report(written += count);
        }
        public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken);
            report(written += count);
        }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await inner.WriteAsync(buffer, cancellationToken);
            report(written += buffer.Length);
        }
    }
}
