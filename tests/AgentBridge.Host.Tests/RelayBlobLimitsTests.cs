using AgentBridge.Host;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Protocol;
using AgentBridge.Relay;
using AgentBridge.Transport;
using AgentBridge.Transport.Tests;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

[Trait("Category", "Network")]
public sealed class RelayBlobLimitsTests
{
    static Task<TestRelay> StartAsync(bool smallerRelay = false) => TestRelay.StartAsync(o =>
    {
        o.Blobs.Backend = "local";
        if (smallerRelay) { o.Blobs.MaxBytes = 128 * 1024; }
    });

    static async Task<(Device Host, Device Phone, RelayBlobs Blobs)> ConnectAsync(TestRelay relay)
    {
        var token = relay.TokenFor(1, "octo");
        var host = await relay.ConnectAsync(token, "host", "host");
        var phone = await relay.ConnectAsync(token, "phone", "pwa");
        var blobs = new RelayBlobs();
        blobs.Attach(host.Relay);
        return (host, phone, blobs);
    }

    static LocalBlobBackend Stored(TestRelay relay) =>
        (LocalBlobBackend)relay.App.Services.GetRequiredService<BlobService>().Backend!;

    static AttachmentStart Attachment(BlobTransfer.Sent sent, string name = "image.png") => new()
    {
        Attachment = Guid.NewGuid().ToString(),
        Name = name,
        Size = sent.Size,
        Sha256 = Attachments.Base64Url(sent.Sha256),
        Blob = new BlobRef { Id = sent.Blob, Key = Attachments.Base64Url(sent.Key) },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_over_limit_blob_is_refused_before_the_host_fetches_or_writes_it(bool smallerRelay)
    {
        await using var relay = await StartAsync(smallerRelay);
        var (host, phone, blobs) = await ConnectAsync(relay);
        using var receiver = blobs;
        using var output = new MemoryStream();
        using var http = BlobHttp.CreateClient();
        var sent = await BlobTransfer.SendAsync(phone.Relay, http, host.Id, [1]);
        var max = Math.Min(AttachmentLimits.BlobBytes, BlobFormat.MaxPlain(phone.Relay.BlobMaxBytes));
        var blob = Attachment(sent).Blob!;

        var refusal = await Assert.ThrowsAsync<BlobRefusedException>(() =>
            receiver.FetchAsync(blob, max + 1, output, _ => { }, CancellationToken.None));

        Assert.Equal("too-large", refusal.Reason);
        Assert.Equal(max, receiver.MaxBytes);
        Assert.Equal(0, output.Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(BlobFormat.Record + BlobFormat.Tag)]
    [InlineData(2 * (BlobFormat.Record + BlobFormat.Tag))]
    public async Task A_tampered_chunk_is_refused_and_leaves_no_attachment_on_the_host(int offset)
    {
        await using var relay = await StartAsync();
        var (host, phone, blobs) = await ConnectAsync(relay);
        using var receiver = blobs;
        using var f = new Fixture();
        using var store = new AttachmentStore(new AttachmentOptions
        {
            StagingRoot = Path.Combine(f.Root, "attachments"),
        }, sweep: false, blobs: receiver);
        using var http = BlobHttp.CreateClient();
        var sent = await BlobTransfer.SendAsync(phone.Relay, http, host.Id, new byte[3 * BlobFormat.Record]);
        var start = Attachment(sent);
        // Change just one authenticated record, including one after valid records were written.
        Assert.Single(Stored(relay).Stored).Value[offset] ^= 1;
        store.Start(phone.Id, start);
        string? refusal = null;

        await HostWait.UntilAsync(() =>
        {
            try { Assert.False(store.Start(phone.Id, start).Complete); }
            catch (InvalidOperationException e) { refusal = e.Message; return true; }
            return false;
        }, "the host to refuse the changed chunk");

        Assert.Equal("the attachment in the blob store is not what was sent, so it was dropped; send it again", refusal);
        Assert.Equal(0, store.Count);
        Assert.Empty(Directory.GetFiles(store.StagingRoot, "*", SearchOption.AllDirectories));
        Assert.Null(store.Claim(phone.Id, [new AttachmentRef { Id = start.Attachment }]).Claim);
        await HostWait.UntilAsync(() => Stored(relay).Stored.Count == 0, "the unusable blob to be deleted");
    }

    [Fact]
    public async Task A_valid_attachment_at_the_relay_limit_is_stored_without_Home_Assistant()
    {
        await using var relay = await StartAsync(smallerRelay: true);
        var (host, phone, blobs) = await ConnectAsync(relay);
        using var receiver = blobs;
        using var f = new Fixture();
        using var store = new AttachmentStore(new AttachmentOptions
        {
            StagingRoot = Path.Combine(f.Root, "attachments"),
        }, sweep: false, blobs: receiver);
        using var http = BlobHttp.CreateClient();
        var bytes = new byte[receiver.MaxBytes];
        "an attachment at the relay limit"u8.CopyTo(bytes);
        var sent = await BlobTransfer.SendAsync(phone.Relay, http, host.Id, bytes);
        var start = Attachment(sent, "report.txt");
        store.Start(phone.Id, start);

        await HostWait.UntilAsync(() => store.Start(phone.Id, start).Complete, "the valid blob to arrive");

        Assert.Equal(bytes, File.ReadAllBytes(Assert.Single(Directory.GetFiles(store.IncomingRoot))));
        Assert.NotNull(store.Claim(phone.Id, [new AttachmentRef { Id = start.Attachment }]).Claim);
        await HostWait.UntilAsync(() => Stored(relay).Stored.Count == 0, "the received blob to be deleted");
    }
}
