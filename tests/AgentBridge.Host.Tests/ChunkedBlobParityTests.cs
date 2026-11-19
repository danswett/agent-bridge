using System.Security.Cryptography;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;
using AgentBridge.Transport;
using Xunit;

public sealed class ChunkedBlobParityTests
{
    const string Peer = "synthetic-sender";
    static readonly byte[] Key = Enumerable.Range(0, BlobFormat.KeyBytes).Select(i => (byte)i).ToArray();
    static readonly byte[] Id = Enumerable.Range(0, BlobFormat.IdBytes).Select(i => (byte)i).ToArray();

    sealed class Source(Func<Stream, CancellationToken, Task> fetch) : IBlobSource
    {
        public long MaxBytes { get; init; } = AttachmentLimits.BlobBytes;
        public TaskCompletionSource Forgotten { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task FetchAsync(BlobRef blob, long size, Stream output, Action<long> progress, CancellationToken cancellationToken)
        {
            try { await fetch(output, cancellationToken); }
            catch (BlobIntegrityException e) { throw new InvalidDataException(e.Message, e); }
        }
        public Task ForgetAsync(BlobRef blob) { Forgotten.SetResult(); return Task.CompletedTask; }
    }

    static byte[] FileBytes()
    {
        var bytes = new byte[5 * BlobFormat.Record + 13];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }.CopyTo(bytes, 0);
        return bytes;
    }

    static AttachmentStart Start(byte[] bytes, string name = "image.png") => new()
    {
        Attachment = Guid.NewGuid().ToString(),
        Name = name,
        Size = bytes.Length,
        Sha256 = Attachments.Base64Url(SHA256.HashData(bytes)),
        Blob = new BlobRef { Id = BlobFormat.Base64Url(Id) + ".synthetic.ticket", Key = BlobFormat.Base64Url(Key) },
    };

    static AttachmentStore Store(Fixture f, Source source) =>
        new(new AttachmentOptions { StagingRoot = Path.Combine(f.Root, "attachments") }, sweep: false, blobs: source);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_oversize_blob_write_is_refused_before_any_excess_byte_reaches_disk(bool synchronous)
    {
        var bytes = FileBytes();
        var excessWritten = false;
        var source = new Source(async (output, ct) =>
        {
            await output.WriteAsync(bytes, ct);
            if (synchronous) { output.WriteByte(1); }
            else { await output.WriteAsync(new byte[] { 1 }, ct); }
            excessWritten = true;
        });
        using var f = new Fixture();
        using var store = Store(f, source);
        var start = Start(bytes);
        store.Start(Peer, start);

        await source.Forgotten.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.False(excessWritten);
        Assert.Throws<InvalidOperationException>(() => store.Start(Peer, start));
        Assert.Empty(Directory.GetFiles(store.IncomingRoot));
        Assert.Null(store.Claim(Peer, [new AttachmentRef { Id = start.Attachment }]).Claim);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_tampered_or_missing_blob_chunk_leaves_nothing_claimable(bool missing)
    {
        var bytes = FileBytes();
        var stored = BlobFormat.Seal(bytes, Key, Id);
        var second = BlobFormat.Record + BlobFormat.Tag;
        if (missing) { stored = stored[..second]; }
        else { stored[second] ^= 1; }
        var source = new Source(async (output, ct) =>
        {
            await BlobFormat.OpenAsync(new MemoryStream(stored), bytes.Length, Key, Id, output, ct: ct);
        });
        using var f = new Fixture();
        using var store = Store(f, source);
        var start = Start(bytes);
        store.Start(Peer, start);

        await source.Forgotten.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Throws<InvalidOperationException>(() => store.Start(Peer, start));
        Assert.Equal(0, store.Count);
        Assert.Empty(Directory.GetFiles(store.IncomingRoot));
        Assert.Null(store.Claim(Peer, [new AttachmentRef { Id = start.Attachment }]).Claim);
    }

    [Fact]
    public void An_over_limit_blob_is_refused_before_fetching()
    {
        var bytes = FileBytes();
        var source = new Source((_, _) => throw new InvalidOperationException("must not fetch"))
        {
            MaxBytes = bytes.Length - 1,
        };
        using var f = new Fixture();
        using var store = Store(f, source);

        Assert.Throws<ArgumentException>(() => store.Start(Peer, Start(bytes)));
        Assert.Equal(0, store.Count);
        Assert.False(Directory.Exists(store.StagingRoot));
    }

    [Theory]
    [InlineData("image.png", false)]
    [InlineData("report.bin", false)]
    [InlineData("image.png", true)]
    public async Task Only_a_complete_blob_with_the_announced_hash_can_be_staged(string name, bool wrongHash)
    {
        var bytes = FileBytes();
        var stored = BlobFormat.Seal(bytes, Key, Id);
        var source = new Source(async (output, ct) =>
        {
            await BlobFormat.OpenAsync(new MemoryStream(stored), bytes.Length, Key, Id, output, ct: ct);
        })
        {
            MaxBytes = bytes.Length,
        };
        using var f = new Fixture();
        using var store = Store(f, source);
        var start = Start(bytes, name);
        if (wrongHash) { start = start with { Sha256 = Attachments.Base64Url(new byte[32]) }; }
        store.Start(Peer, start);

        await source.Forgotten.Task.WaitAsync(TimeSpan.FromSeconds(10));

        if (wrongHash)
        {
            Assert.Throws<InvalidOperationException>(() => store.Start(Peer, start));
            Assert.Empty(Directory.GetFiles(store.IncomingRoot));
            Assert.Null(store.Claim(Peer, [new AttachmentRef { Id = start.Attachment }]).Claim);
            return;
        }
        Assert.True(store.Start(Peer, start).Complete);
        var (claim, problem) = store.Claim(Peer, [new AttachmentRef { Id = start.Attachment }]);
        Assert.Null(problem);
        var path = Assert.Single(store.Stage(claim!));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path));
        store.Consume(claim!);
        Assert.Empty(Directory.GetFiles(store.IncomingRoot));
    }
}
