using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgentBridge.Relay;
using Azure.Storage.Blobs;
using Azure.Storage.Sas;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Transport.Tests;

// The blob store's sealed format (docs/protocol/blobs.md): what the relay and storage hold,
// and how a receiver refuses anything but the blob that was sealed for it.
public class BlobFormatTests
{
    const long Mib = 1024 * 1024;

    static byte[] Id() => RandomNumberGenerator.GetBytes(BlobFormat.IdBytes);

    static async Task<(byte[] File, byte[] Hash)> OpenAsync(byte[] stored, long size, byte[] key, byte[] id)
    {
        var output = new MemoryStream();
        var hash = await BlobFormat.OpenAsync(new MemoryStream(stored), size, key, id, output);
        return (output.ToArray(), hash);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(BlobFormat.Record)]
    [InlineData(BlobFormat.Record + 1)]
    [InlineData(3 * 1024 * 1024 + 5)]
    public async Task A_sealed_blob_opens_to_the_same_bytes_and_their_hash(int size)
    {
        var file = RandomNumberGenerator.GetBytes(size);
        var key = BlobFormat.NewKey();
        var id = Id();

        var stored = BlobFormat.Seal(file, key, id);
        var (opened, hash) = await OpenAsync(stored, size, key, id);

        Assert.Equal(BlobFormat.StoredSize(size), stored.Length);
        Assert.Equal(file, opened);
        Assert.Equal(SHA256.HashData(file), hash);
    }

    [Fact]
    public async Task Sealing_a_stream_gives_the_same_bytes_as_sealing_it_whole()
    {
        var file = RandomNumberGenerator.GetBytes(200_000);
        var key = BlobFormat.NewKey();
        var id = Id();
        var streamed = new MemoryStream();

        await BlobFormat.SealAsync(new MemoryStream(file), file.Length, key, id, streamed);

        Assert.Equal(BlobFormat.Seal(file, key, id), streamed.ToArray());
    }

    // The same vector is in pwa/test/unit/blobs.test.mjs: the PWA seals exactly what the host
    // opens.
    [Fact]
    public void The_format_is_byte_for_byte_the_one_the_PWA_seals()
    {
        var key = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var id = Enumerable.Range(0, 16).Select(i => (byte)(0xA0 + i)).ToArray();
        var file = Enumerable.Range(0, 70_000).Select(i => (byte)(i % 251)).ToArray();

        var stored = BlobFormat.Seal(file, key, id);

        Assert.Equal(131_072, stored.Length);
        Assert.Equal("3e6ef14bdfc54c85c017cf13d7013ecb53c98890cc29d2e1fa247217db3a3a8c", Convert.ToHexString(SHA256.HashData(stored)).ToLowerInvariant());
    }

    [Fact]
    public void Stored_sizes_are_padded_to_buckets_so_they_say_little_about_the_file()
    {
        Assert.Equal(4096, BlobFormat.StoredSize(1));
        Assert.Equal(4096, BlobFormat.StoredSize(4000));
        Assert.Equal(8192, BlobFormat.StoredSize(5000));
        // Sixteen full records and their tags are exactly 1 MiB; one byte more is 2 MiB.
        Assert.Equal(Mib, BlobFormat.StoredSize(16 * BlobFormat.Record - 16 * BlobFormat.Tag));
        Assert.Equal(2 * Mib, BlobFormat.StoredSize(Mib));
        Assert.Equal(5 * Mib, BlobFormat.StoredSize(4 * Mib + 1));
        Assert.All(new long[] { 1, 100, 70_000, 3_000_000, 100 * Mib }, n => Assert.True(BlobFormat.IsBucket(BlobFormat.StoredSize(n))));
        Assert.False(BlobFormat.IsBucket(5000));
        Assert.False(BlobFormat.IsBucket(3 * Mib + 1));
    }

    [Fact]
    public void The_largest_file_for_a_stored_limit_is_the_last_that_fits()
    {
        var max = new RelayOptions().Blobs.MaxBytes;
        var plain = BlobFormat.MaxPlain(max);

        Assert.True(BlobFormat.StoredSize(plain) <= max);
        Assert.True(BlobFormat.StoredSize(plain + 1) > max);
        // The default holds a 200 MiB session transfer (design §9).
        Assert.True(plain >= 200 * Mib);
    }

    [Fact]
    public async Task A_changed_byte_is_refused()
    {
        var key = BlobFormat.NewKey();
        var id = Id();
        var stored = BlobFormat.Seal(RandomNumberGenerator.GetBytes(100_000), key, id);
        stored[70_000] ^= 1;

        await Assert.ThrowsAsync<BlobIntegrityException>(() => OpenAsync(stored, 100_000, key, id));
    }

    [Fact]
    public async Task Records_swapped_or_cut_off_are_refused()
    {
        var key = BlobFormat.NewKey();
        var id = Id();
        var size = 3 * BlobFormat.Record;
        var stored = BlobFormat.Seal(RandomNumberGenerator.GetBytes(size), key, id);
        var record = BlobFormat.Record + BlobFormat.Tag;
        var swapped = stored.ToArray();
        stored.AsSpan(0, record).CopyTo(swapped.AsSpan(record));
        stored.AsSpan(record, record).CopyTo(swapped.AsSpan(0));
        // The first two records, padded as if they were the whole blob: the second is not
        // marked final, so this is not a shorter file that happens to verify.
        var cut = new byte[BlobFormat.StoredSize(size)];
        stored.AsSpan(0, 2 * record).CopyTo(cut);

        await Assert.ThrowsAsync<BlobIntegrityException>(() => OpenAsync(swapped, size, key, id));
        await Assert.ThrowsAsync<BlobIntegrityException>(() => OpenAsync(cut, size, key, id));
        await Assert.ThrowsAsync<BlobIntegrityException>(() => OpenAsync(stored[..(2 * record)], size, key, id));
        await Assert.ThrowsAsync<BlobIntegrityException>(() => OpenAsync(stored, 2 * BlobFormat.Record, key, id));
    }

    [Fact]
    public async Task A_blob_opened_as_another_blob_or_with_another_key_is_refused()
    {
        var key = BlobFormat.NewKey();
        var stored = BlobFormat.Seal(RandomNumberGenerator.GetBytes(1000), key, Id());

        await Assert.ThrowsAsync<BlobIntegrityException>(() => OpenAsync(stored, 1000, key, Id()));
        await Assert.ThrowsAsync<BlobIntegrityException>(() => OpenAsync(stored, 1000, BlobFormat.NewKey(), Id()));
    }

    [Fact]
    public async Task Padding_that_is_not_zeros_or_runs_past_the_bucket_is_refused()
    {
        var key = BlobFormat.NewKey();
        var id = Id();
        var stored = BlobFormat.Seal(RandomNumberGenerator.GetBytes(1000), key, id);
        var dirty = stored.ToArray();
        dirty[^1] = 7;

        await Assert.ThrowsAsync<BlobIntegrityException>(() => OpenAsync(dirty, 1000, key, id));
        await Assert.ThrowsAsync<BlobIntegrityException>(() => OpenAsync([.. stored, 0], 1000, key, id));
        await Assert.ThrowsAsync<BlobIntegrityException>(() => OpenAsync(stored[..^1], 1000, key, id));
    }

    [Fact]
    public void A_URL_from_the_relay_is_only_followed_to_Azure_Storage_or_back_to_the_relay()
    {
        var relay = new Uri("https://relay.example");

        Assert.Equal("https://acct.blob.core.windows.net/blobs/x?sig=1", BlobHttp.Resolve(relay, "https://acct.blob.core.windows.net/blobs/x?sig=1")?.ToString());
        Assert.Equal("https://relay.example/v1/dev-blobs/x?sig=1", BlobHttp.Resolve(relay, "/v1/dev-blobs/x?sig=1")?.ToString());
        Assert.Null(BlobHttp.Resolve(relay, "http://acct.blob.core.windows.net/blobs/x"));
        Assert.Null(BlobHttp.Resolve(relay, "https://acct.blob.core.windows.net:8443/blobs/x"));
        Assert.Null(BlobHttp.Resolve(relay, "https://evil.example/blob.core.windows.net"));
        Assert.Null(BlobHttp.Resolve(relay, "https://169.254.169.254/metadata"));
        Assert.Null(BlobHttp.Resolve(relay, "https://user@acct.blob.core.windows.net/blobs/x"));
    }
}

// The relay's blob store (Blobs.cs) against the in-process local backend, which serves the
// same Put Blob and Get Blob a client uses with Azure: who can read and delete a blob, how long
// a ticket and a URL last, the size limits and quotas, and that only ciphertext is stored.
public class BlobStoreTests
{
    const long Mib = 1024 * 1024;

    static Task<TestRelay> StartAsync(Action<RelayOptions>? more = null) => TestRelay.StartAsync(o =>
    {
        o.Blobs.Backend = "local";
        more?.Invoke(o);
    });

    static LocalBlobBackend Store(TestRelay relay) => (LocalBlobBackend)relay.App.Services.GetRequiredService<BlobService>().Backend!;

    static async Task<string> RefusedAsync(Func<Task> request) => (await Assert.ThrowsAsync<BlobRefusedException>(request)).Reason;

    static async Task<HttpStatusCode> PutAsync(HttpClient http, TestRelay relay, string url, byte[] body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Put, BlobHttp.Resolve(relay.Url, url)) { Content = new ByteArrayContent(body) };
        req.Headers.TryAddWithoutValidation("x-ms-blob-type", "BlockBlob");
        using var r = await http.SendAsync(req);
        return r.StatusCode;
    }

    [Fact]
    public async Task A_multi_megabyte_file_reaches_another_device_of_the_account_and_storage_only_ever_holds_ciphertext()
    {
        await using var relay = await StartAsync();
        var token = relay.TokenFor(1, "octo");
        var phone = await relay.ConnectAsync(token, "phone", "pwa");
        var host = await relay.ConnectAsync(token, "host", "host");
        var marker = "MARKER-" + RandomNumberGenerator.GetHexString(16);
        var file = Encoding.ASCII.GetBytes(marker).Concat(RandomNumberGenerator.GetBytes((int)(5 * Mib))).Concat(Encoding.ASCII.GetBytes(marker)).ToArray();
        using var http = BlobHttp.CreateClient();

        Assert.True(phone.Relay.BlobMaxBytes >= BlobFormat.StoredSize(100 * Mib));
        var sent = await BlobTransfer.SendAsync(phone.Relay, http, host.Id, file);
        var (name, stored) = Assert.Single(Store(relay).Stored);
        var received = new MemoryStream();
        var hash = await BlobTransfer.ReceiveAsync(host.Relay, http, sent.Blob, sent.Key, file.Length, received);

        Assert.Equal(file, received.ToArray());
        Assert.Equal(SHA256.HashData(file), hash);
        Assert.Equal(BlobFormat.StoredSize(file.Length), stored.Length);
        Assert.Equal(-1, stored.AsSpan().IndexOf(Encoding.ASCII.GetBytes(marker)));
        var seen = string.Join("\n", relay.Logs) + "\n" + name + "\n" + sent.Blob;
        foreach (var secret in new[] { marker, BlobFormat.Base64Url(sent.Key), BlobFormat.Base64Url(sent.Sha256), Convert.ToHexString(sent.Sha256), phone.Id, host.Id })
        {
            Assert.DoesNotContain(secret, seen, StringComparison.OrdinalIgnoreCase);
        }

        Assert.True(await host.Relay.BlobDeleteAsync(sent.Blob));
        Assert.Empty(Store(relay).Stored);
        Assert.Equal("not-found", await RefusedAsync(() => host.Relay.BlobGetAsync(sent.Blob)));
    }

    [Fact]
    public async Task Another_account_or_another_device_can_neither_read_nor_delete_a_blob_but_the_sender_can_withdraw_it()
    {
        await using var relay = await StartAsync();
        var token = relay.TokenFor(1, "octo");
        var phone = await relay.ConnectAsync(token, "phone", "pwa");
        var host = await relay.ConnectAsync(token, "host", "host");
        var laptop = await relay.ConnectAsync(token, "laptop", "host");
        var stranger = await relay.ConnectAsync(relay.TokenFor(2, "mallory"), "stranger", "host");
        using var http = BlobHttp.CreateClient();
        var sent = await BlobTransfer.SendAsync(phone.Relay, http, host.Id, RandomNumberGenerator.GetBytes(10_000));

        Assert.Equal("not-found", await RefusedAsync(() => laptop.Relay.BlobGetAsync(sent.Blob)));
        Assert.Equal("not-found", await RefusedAsync(() => stranger.Relay.BlobGetAsync(sent.Blob)));
        Assert.Equal("not-found", await RefusedAsync(() => laptop.Relay.BlobDeleteAsync(sent.Blob)));
        Assert.Equal("not-found", await RefusedAsync(() => stranger.Relay.BlobDeleteAsync(sent.Blob)));
        // Nor can the sender read back what it sent: only the device it was for.
        Assert.Equal("not-found", await RefusedAsync(() => phone.Relay.BlobGetAsync(sent.Blob)));
        Assert.Single(Store(relay).Stored);

        Assert.True(await phone.Relay.BlobDeleteAsync(sent.Blob));
        Assert.Empty(Store(relay).Stored);
    }

    [Fact]
    public async Task A_ticket_altered_to_name_another_device_or_moved_to_another_account_is_refused()
    {
        await using var relay = await StartAsync();
        var token = relay.TokenFor(1, "octo");
        var phone = await relay.ConnectAsync(token, "phone", "pwa");
        var host = await relay.ConnectAsync(token, "host", "host");
        var laptop = await relay.ConnectAsync(token, "laptop", "host");
        var slot = await phone.Relay.BlobPutAsync(host.Id, 4096);
        var parts = slot.Blob.Split('.');
        var body = Encoding.UTF8.GetString(BlobFormat.FromBase64Url(parts[1])!).Replace(host.Id, laptop.Id);
        var forged = $"{parts[0]}.{BlobFormat.Base64Url(Encoding.UTF8.GetBytes(body))}.{parts[2]}";
        Assert.Equal(201, (int)await PutAsync(relay.Http, relay, slot.Url, new byte[4096]));

        Assert.Equal("not-found", await RefusedAsync(() => laptop.Relay.BlobGetAsync(forged)));
        Assert.Equal("not-found", await RefusedAsync(() => host.Relay.BlobGetAsync(forged)));
        // The same ticket from the same device id, signed in to another account.
        var elsewhere = await relay.ConnectAsync(relay.TokenFor(2, "mallory"), "host again", "host", key: host.Key);
        Assert.Equal("not-found", await RefusedAsync(() => elsewhere.Relay.BlobGetAsync(slot.Blob)));
    }

    [Fact]
    public async Task URLs_last_minutes_and_tickets_a_day_after_which_a_blob_can_no_longer_be_read()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var relay = await StartAsync(o => o.Blobs.Time = time);
        var token = relay.TokenFor(1, "octo");
        var phone = await relay.ConnectAsync(token, "phone", "pwa");
        var host = await relay.ConnectAsync(token, "host", "host");
        var slot = await phone.Relay.BlobPutAsync(host.Id, 4096);
        Assert.InRange(slot.Expires - time.GetUtcNow(), TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));

        time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.Forbidden, await PutAsync(relay.Http, relay, slot.Url, new byte[4096]));
        var late = await phone.Relay.BlobPutAsync(host.Id, 4096);
        Assert.Equal(HttpStatusCode.Created, await PutAsync(relay.Http, relay, late.Url, new byte[4096]));
        var read = await host.Relay.BlobGetAsync(late.Blob);
        time.Advance(TimeSpan.FromMinutes(31));
        Assert.Equal(HttpStatusCode.Forbidden, (await relay.Http.GetAsync(BlobHttp.Resolve(relay.Url, read.Url))).StatusCode);

        time.Advance(TimeSpan.FromHours(24));
        Assert.Equal("expired", await RefusedAsync(() => host.Relay.BlobGetAsync(late.Blob)));
    }

    [Fact]
    public async Task Sizes_are_capped_padded_and_checked_against_what_was_actually_uploaded()
    {
        await using var relay = await StartAsync(o => o.Blobs.MaxBytes = 8 * Mib);
        var token = relay.TokenFor(1, "octo");
        var phone = await relay.ConnectAsync(token, "phone", "pwa");
        var host = await relay.ConnectAsync(token, "host", "host");

        Assert.Equal(8 * Mib, phone.Relay.BlobMaxBytes);
        Assert.Equal("too-large", await RefusedAsync(() => phone.Relay.BlobPutAsync(host.Id, 9 * Mib)));
        Assert.Equal("bad-size", await RefusedAsync(() => phone.Relay.BlobPutAsync(host.Id, 5000)));
        Assert.Equal("bad-request", await RefusedAsync(() => phone.Relay.BlobPutAsync(host.Id, 0)));
        Assert.Equal("bad-request", await RefusedAsync(() => phone.Relay.BlobPutAsync("not-a-device", 4096)));

        // A write URL cannot limit what is sent through it, so the relay checks at the read.
        var slot = await phone.Relay.BlobPutAsync(host.Id, 4096);
        Assert.Equal(HttpStatusCode.Created, await PutAsync(relay.Http, relay, slot.Url, new byte[8192]));
        Assert.Equal("size-mismatch", await RefusedAsync(() => host.Relay.BlobGetAsync(slot.Blob)));
        Assert.Empty(Store(relay).Stored);
    }

    [Fact]
    public async Task A_write_URL_creates_its_blob_once_and_cannot_read_and_a_read_URL_cannot_write()
    {
        await using var relay = await StartAsync();
        var token = relay.TokenFor(1, "octo");
        var phone = await relay.ConnectAsync(token, "phone", "pwa");
        var host = await relay.ConnectAsync(token, "host", "host");
        var slot = await phone.Relay.BlobPutAsync(host.Id, 4096);

        Assert.Equal(HttpStatusCode.Created, await PutAsync(relay.Http, relay, slot.Url, new byte[4096]));
        Assert.Equal(HttpStatusCode.Conflict, await PutAsync(relay.Http, relay, slot.Url, new byte[4096]));
        Assert.Equal(HttpStatusCode.Forbidden, (await relay.Http.GetAsync(BlobHttp.Resolve(relay.Url, slot.Url))).StatusCode);
        var read = await host.Relay.BlobGetAsync(slot.Blob);
        Assert.Equal(HttpStatusCode.Forbidden, await PutAsync(relay.Http, relay, read.Url, new byte[4096]));
    }

    [Fact]
    public async Task An_accounts_bytes_per_day_are_limited_and_other_accounts_are_unaffected()
    {
        await using var relay = await StartAsync(o => o.Blobs.MaxBytesPerAccountPerDay = 3 * 4096);
        var phone = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "phone", "pwa");
        var other = await relay.ConnectAsync(relay.TokenFor(2, "hubot"), "other", "pwa");
        var to = RandomNumberGenerator.GetHexString(32, lowercase: true);

        for (var i = 0; i < 3; i++) { await phone.Relay.BlobPutAsync(to, 4096); }
        Assert.Equal("quota", await RefusedAsync(() => phone.Relay.BlobPutAsync(to, 4096)));
        await other.Relay.BlobPutAsync(to, 4096);
    }

    [Fact]
    public async Task An_accounts_puts_per_minute_are_limited()
    {
        await using var relay = await StartAsync(o => o.Blobs.MaxPutsPerMinute = 2);
        var phone = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "phone", "pwa");
        var to = RandomNumberGenerator.GetHexString(32, lowercase: true);

        await phone.Relay.BlobPutAsync(to, 4096);
        await phone.Relay.BlobPutAsync(to, 4096);
        Assert.Equal("rate-limited", await RefusedAsync(() => phone.Relay.BlobPutAsync(to, 4096)));
    }

    [Fact]
    public async Task A_relay_without_a_blob_store_says_so_at_admission_and_refuses_blob_requests()
    {
        await using var relay = await TestRelay.StartAsync();
        var phone = await relay.ConnectAsync(relay.TokenFor(1, "octo"), "phone", "pwa");

        Assert.Equal(0, phone.Relay.BlobMaxBytes);
        Assert.Equal("disabled", await RefusedAsync(() => phone.Relay.BlobPutAsync(phone.Id, 4096)));
    }

    [Fact]
    public void Azure_URLs_are_user_delegation_SAS_for_one_blob_one_permission_over_HTTPS_for_minutes()
    {
        var now = DateTimeOffset.UtcNow;
        var key = Azure.Storage.Blobs.Models.BlobsModelFactory.UserDelegationKey("object-id", "tenant-id", now, now.AddDays(1), "b", "2025-11-05", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var blob = new Uri("https://abblobs.blob.core.windows.net/blobs/abcdefghijklmnopqrstuv");

        var write = new Uri(AzureBlobBackend.Sas(blob, "blobs", "abcdefghijklmnopqrstuv", BlobSasPermissions.Create, now.AddMinutes(30), now, key, "abblobs"));
        var read = new Uri(AzureBlobBackend.Sas(blob, "blobs", "abcdefghijklmnopqrstuv", BlobSasPermissions.Read, now.AddMinutes(30), now, key, "abblobs"));

        foreach (var (url, permission) in new[] { (write, "c"), (read, "r") })
        {
            var q = QueryHelpers.ParseQuery(url.Query);
            Assert.Equal(blob.AbsolutePath, url.AbsolutePath);
            Assert.Equal(permission, q["sp"].ToString());
            Assert.Equal("b", q["sr"].ToString());
            Assert.Equal("https", q["spr"].ToString());
            // Signed with a delegation key from Entra (skoid), not an account key.
            Assert.Equal("object-id", q["skoid"].ToString());
            Assert.InRange(DateTimeOffset.Parse(q["se"].ToString()) - now, TimeSpan.FromMinutes(29), TimeSpan.FromMinutes(31));
        }
    }
}
