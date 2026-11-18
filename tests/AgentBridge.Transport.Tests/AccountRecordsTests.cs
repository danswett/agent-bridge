using System.Buffers.Binary;
using AgentBridge.Relay;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentBridge.Transport.Tests;

public class AccountRecordsTests
{
    static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeMilliseconds(123456789);
    static AccountRecordService Make(IRosterStore store, IRecordAdmission? admission = null) =>
        new(store, admission ?? new Allow(), NullLogger<AccountRecordService>.Instance, new FakeTimeProvider(Now));

    [Theory]
    [InlineData("order")]
    [InlineData("marker")]
    [InlineData("keywrap")]
    [InlineData("manifest")]
    [InlineData("fleet")]
    public async Task Creating_then_reading_preserves_opaque_bytes_and_metadata_across_service_instances(string kind)
    {
        var store = new MemoryRosterStore();
        byte[] body = [0, 255, 13, 10, 128];
        var put = await Make(store).PutAsync("7", "device", kind, "a-1", 0, body);
        Assert.Null(put.Error);
        Assert.Equal(1, put.Version);
        Assert.Equal($"records/7/{kind}/a-1", Assert.Single(store.Names));
        body[0] = 9;
        var get = await Make(store).GetAsync("7", "device", kind, "a-1");
        Assert.Null(get.Error);
        Assert.Equal(1, get.Version);
        Assert.Equal(Now, get.Record!.Time);
        Assert.Equal(5, get.Record.Size);
        Assert.Equal(new byte[] { 0, 255, 13, 10, 128 }, get.Record.Body);
        get.Record.Body[0] = 8;
        Assert.Equal(0, (await Make(store).GetAsync("7", "device", kind, "a-1")).Record!.Body[0]);
        var next = await Make(store).PutAsync("7", "device", kind, "a-1", 1, []);
        Assert.Null(next.Error);
        Assert.Equal(2, next.Version);
        Assert.Empty((await Make(store).GetAsync("7", "device", kind, "a-1")).Record!.Body);
        Assert.Equal("not-found", (await Make(store).GetAsync("8", "device", kind, "a-1")).Error);
    }

    [Fact]
    public async Task A_stale_or_future_version_conflicts_without_changing_the_record()
    {
        var store = new MemoryRosterStore();
        var service = Make(store);
        await service.PutAsync("7", "device", "order", "a", 0, [1]);
        foreach (var version in new long[] { 0, 2 })
        {
            var result = await service.PutAsync("7", "device", "order", "a", version, [2]);
            Assert.Equal("conflict", result.Error);
            Assert.Equal(1, result.Version);
        }
        Assert.Equal(new byte[] { 1 }, (await service.GetAsync("7", "device", "order", "a")).Record!.Body);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task The_ciphertext_limit_is_inclusive_and_an_oversize_put_changes_nothing()
    {
        var store = new MemoryRosterStore();
        var service = Make(store);
        Assert.Null((await service.PutAsync("7", "device", "order", "a", 0, new byte[65536])).Error);
        Assert.Equal("too-large", (await service.PutAsync("7", "device", "order", "a", 1, new byte[65537])).Error);
        Assert.Equal(65536, (await service.GetAsync("7", "device", "order", "a")).Record!.Size);
        Assert.Equal(1, store.Writes);
    }

    [Theory]
    [InlineData("7", "unknown", "a")]
    [InlineData("7", "Order", "a")]
    [InlineData("7", "order", "../a")]
    [InlineData("7", "order", "a/b")]
    [InlineData("7", "order", "a\\b")]
    [InlineData("7", "order", "a\n")]
    [InlineData("7", "order", "")]
    [InlineData("7", "order", "abcdefghijklmnopqrstuvwxyz1234567")]
    [InlineData("../7", "order", "a")]
    public async Task Invalid_names_are_refused_for_reads_and_writes_without_touching_storage(string account, string kind, string id)
    {
        var store = new MemoryRosterStore();
        var service = Make(store);
        Assert.Equal("bad-request", (await service.GetAsync(account, "device", kind, id)).Error);
        Assert.Equal("bad-request", (await service.PutAsync(account, "device", kind, id, 0, [])).Error);
        Assert.Empty(store.Names);
    }

    [Fact]
    public async Task Two_services_racing_at_one_version_have_exactly_one_winner()
    {
        var store = new RaceStore();
        var results = await Task.WhenAll(
            Make(store).PutAsync("7", "device", "order", "a", 0, [1]),
            Make(store).PutAsync("7", "device", "order", "a", 0, [2]));
        var winner = Assert.Single(results, r => r.Error is null);
        var loser = Assert.Single(results, r => r.Error == "conflict");
        Assert.Equal(1, winner.Version);
        Assert.Equal(winner.Version, loser.Version);
        Assert.Equal(1, store.Inner.Writes);
    }

    [Fact]
    public async Task Default_admission_refuses_reads_and_writes_including_existing_records()
    {
        var store = new MemoryRosterStore();
        await Make(store).PutAsync("7", "device", "order", "a", 0, [1]);
        var service = Make(store, new DenyRecordAdmission());
        Assert.Equal("needs-device-approval", (await service.GetAsync("7", "device", "order", "a")).Error);
        Assert.Equal("needs-device-approval", (await service.PutAsync("7", "device", "order", "a", 1, [2])).Error);
        Assert.Equal("needs-device-approval", (await Make(store).GetAsync("7", "other-device", "order", "a")).Error);
        Assert.Equal(1, store.Writes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_store_failure_returns_unavailable_without_changing_the_existing_record(bool reading)
    {
        var store = new FaultStore();
        await Make(store).PutAsync("7", "device", "order", "a", 0, [1]);
        store.FailRead = reading;
        store.FailWrite = !reading;
        var result = await Make(store).PutAsync("7", "device", "order", "a", 1, [2]);
        Assert.Equal("unavailable", result.Error);
        Assert.Null(result.Record);
        if (reading) { Assert.Equal("unavailable", (await Make(store).GetAsync("7", "device", "order", "a")).Error); }
        store.FailRead = store.FailWrite = false;
        Assert.Equal(new byte[] { 1 }, (await Make(store).GetAsync("7", "device", "order", "a")).Record!.Body);
        Assert.Equal(1, store.Inner.Writes);
    }

    [Fact]
    public async Task Corrupt_metadata_is_unavailable_and_is_never_overwritten_as_a_new_record()
    {
        var store = new MemoryRosterStore();
        await store.WriteAsync("records/7/order/a", [0, 255], null, default);
        Assert.Equal("unavailable", (await Make(store).GetAsync("7", "device", "order", "a")).Error);
        Assert.Equal("unavailable", (await Make(store).PutAsync("7", "device", "order", "a", 0, [])).Error);
        Assert.Equal(1, store.Writes);
    }

    [Fact]
    public async Task Missing_records_start_at_zero_and_cannot_be_created_with_another_version()
    {
        var store = new MemoryRosterStore();
        var service = Make(store);
        const string id = "abcdefghijklmnopqrstuvwxyz123456";
        var missing = await service.GetAsync("7", "device", "order", id);
        Assert.Equal("not-found", missing.Error);
        Assert.Equal(0, missing.Version);
        var conflict = await service.PutAsync("7", "device", "order", id, 1, []);
        Assert.Equal("conflict", conflict.Error);
        Assert.Equal(0, conflict.Version);
        Assert.Equal("bad-request", (await service.PutAsync("7", "device", "order", id, -1, [])).Error);
        Assert.Empty(store.Names);
        Assert.Null((await service.PutAsync("7", "device", "order", id, 0, [])).Error);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(long.MaxValue)]
    public async Task Invalid_or_exhausted_versions_are_never_reset_or_wrapped(long version)
    {
        var store = new MemoryRosterStore();
        await Make(store).PutAsync("7", "device", "order", "a", 0, []);
        var name = Assert.Single(store.Names);
        var stored = (await store.ReadAsync(name, default))!;
        var bytes = stored.Body.ToArray();
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(4), version);
        await store.WriteAsync(name, bytes, stored.ETag, default);
        Assert.Equal("unavailable", (await Make(store).PutAsync("7", "device", "order", "a", version, [])).Error);
        Assert.Equal(2, store.Writes);
    }

    sealed class Allow : IRecordAdmission
    {
        public Task<bool> AdmitAsync(string accountId, string deviceId, CancellationToken ct) =>
            Task.FromResult(accountId is "7" or "8" && deviceId == "device");
    }

    class FaultStore : IRosterStore
    {
        public readonly MemoryRosterStore Inner = new();
        public bool FailRead, FailWrite;
        public virtual Task<StoredRoster?> ReadAsync(string name, CancellationToken ct) =>
            FailRead ? throw new IOException("private storage path") : Inner.ReadAsync(name, ct);
        public Task<string?> WriteAsync(string name, byte[] body, string? etag, CancellationToken ct) =>
            FailWrite ? throw new IOException("private storage path") : Inner.WriteAsync(name, body, etag, ct);
        public Task<bool> DeleteAsync(string name, string etag, CancellationToken ct) => Inner.DeleteAsync(name, etag, ct);
    }

    sealed class RaceStore : FaultStore
    {
        readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int reads;
        public override async Task<StoredRoster?> ReadAsync(string name, CancellationToken ct)
        {
            var snapshot = await base.ReadAsync(name, ct);
            if (Interlocked.Increment(ref reads) == 2) { release.SetResult(); }
            await release.Task.WaitAsync(ct);
            return snapshot;
        }
    }
}
