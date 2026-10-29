using System.Security.Cryptography;
using System.Text.Json;
using AgentBridge.Protocol;
using Xunit;

public class AttachmentTests
{
    const string Id = "0b7e3c1d-5f2a-4c6e-8d9b-1a2b3c4d5e6f";
    static readonly string Hash = Attachments.Base64Url(SHA256.HashData("hello"u8));

    static AttachmentStart Start(string name = "shot.png", long size = 5, string? sha = null, string id = Id, string? type = "image/png") =>
        new() { Attachment = id, Name = name, Type = type, Size = size, Sha256 = sha ?? Hash };

    [Fact]
    public void A_start_names_one_file_of_up_to_256_KiB_by_a_UUID_with_its_sha256_in_base64url()
    {
        Assert.Null(Attachments.Check(Start()));
        Assert.Null(Attachments.Check(Start(size: AttachmentLimits.Bytes, type: null)));
        Assert.Equal("the attachment is larger than 256 KiB", Attachments.Check(Start(size: AttachmentLimits.Bytes + 1)));
        Assert.Equal("the attachment is empty", Attachments.Check(Start(size: 0)));
        Assert.Equal("attachment must be a UUID", Attachments.Check(Start(id: "7")));
        Assert.Equal("the attachment needs a name", Attachments.Check(Start(name: " ")));
        Assert.Equal("the attachment's name is longer than 255 bytes", Attachments.Check(Start(name: new string('é', 128))));
        Assert.Equal("the attachment's type is longer than 127 bytes", Attachments.Check(Start(type: new string('x', 128))));
        // Hex, padded base64 or standard base64's + and / are not the contract's form.
        Assert.NotNull(Attachments.Check(Start(sha: Convert.ToHexString(SHA256.HashData("hello"u8)))));
        Assert.NotNull(Attachments.Check(Start(sha: Hash + "=")));
        Assert.NotNull(Attachments.Check(Start(sha: "+" + Hash[1..])));
    }

    [Fact]
    public void A_start_from_the_blob_store_takes_files_of_up_to_100_MiB_with_the_relays_ticket_and_a_32_byte_key()
    {
        var blob = new BlobRef { Id = "AAAAAAAAAAAAAAAAAAAAAA.eyJmIjoiIn0.c2ln", Key = Attachments.Base64Url(new byte[32]) };
        var start = Start(size: AttachmentLimits.BlobBytes) with { Blob = blob };

        Assert.Null(Attachments.Check(start));
        Assert.Equal("the attachment is larger than 100 MiB", Attachments.Check(start with { Size = AttachmentLimits.BlobBytes + 1 }));
        Assert.Equal("blob.key must be 32 bytes, base64url without padding", Attachments.Check(start with { Blob = blob with { Key = Attachments.Base64Url(new byte[16]) } }));
        Assert.Equal("blob.id must be the relay's ticket for the blob", Attachments.Check(start with { Blob = blob with { Id = "" } }));
        Assert.Equal("blob.id must be the relay's ticket for the blob", Attachments.Check(start with { Blob = blob with { Id = "a b" } }));
        Assert.Equal("blob.id must be the relay's ticket for the blob", Attachments.Check(start with { Blob = blob with { Id = new string('a', AttachmentLimits.BlobId + 1) } }));
        Assert.Equal(start, JsonSerializer.Deserialize<AttachmentStart>(JsonSerializer.Serialize(start, ProtocolJson.Options), ProtocolJson.Options));
    }

    [Fact]
    public void A_chunk_carries_at_most_11_KiB_of_base64_so_its_request_stays_under_16_KiB_as_sent()
    {
        var full = new AttachmentChunk { Attachment = Id, Offset = AttachmentLimits.Bytes - AttachmentLimits.Chunk, Data = Convert.ToBase64String(new byte[AttachmentLimits.Chunk]) };
        Assert.Equal((AttachmentLimits.Chunk, (string?)null), (Attachments.Read(full).Bytes!.Length, Attachments.Read(full).Problem));
        var request = JsonSerializer.SerializeToUtf8Bytes(new { id = int.MaxValue, op = Ops.AttachmentChunk, data = full }, ProtocolJson.Options);
        Assert.True(request.Length <= AttachmentLimits.ChunkRequest, $"{request.Length} bytes");

        Assert.Equal("a chunk carries at most 11 KiB", Attachments.Read(full with { Data = Convert.ToBase64String(new byte[AttachmentLimits.Chunk + 1]) }).Problem);
        Assert.Equal("the chunk is empty", Attachments.Read(full with { Data = "" }).Problem);
        Assert.Equal("the chunk's data is not base64", Attachments.Read(full with { Data = "not base64!" }).Problem);
        Assert.Equal("the offset is outside the attachment", Attachments.Read(full with { Offset = -1 }).Problem);
    }

    [Fact]
    public void Attachment_records_read_from_and_write_to_the_contract_json()
    {
        var start = JsonSerializer.Deserialize<AttachmentStart>($$"""{"attachment":"{{Id}}","name":"a.png","type":"image/png","size":5,"sha256":"{{Hash}}","via":"channel"}""", ProtocolJson.Options)!;
        Assert.Equal(Start(name: "a.png"), start);
        Assert.Equal("""{"received":11264,"complete":false}""", JsonSerializer.Serialize(new AttachmentState(11264, false), ProtocolJson.Options));
        Assert.Equal("""{"discarded":true}""", JsonSerializer.Serialize(new AttachmentDiscarded(true), ProtocolJson.Options));
    }
}
