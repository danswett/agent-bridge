using System.Runtime.CompilerServices;
using AgentBridge.FeedbackInbox;
using Xunit;

namespace AgentBridge.FeedbackInbox.Tests;

public sealed class FeedbackBlobSourceTests
{
    [Fact]
    public async Task Completed_blobs_are_not_deleted_or_reprocessed_after_restart()
    {
        var blobs = new FakeBlobs();
        var receipts = new FakeReceipts();
        var source = new FeedbackBlobSource(blobs, receipts);
        Assert.Equal(["first", "second"], await ListAsync(source));
        Assert.Equal(3, await source.GetSizeAsync("first", CancellationToken.None));
        Assert.Equal(new byte[] { 1, 2, 3 }, (await source.ReadAsync("first", 8, CancellationToken.None))!.Value.ToArray());

        await source.MarkProcessedAsync("first", CancellationToken.None);
        var restarted = new FeedbackBlobSource(blobs, receipts);
        Assert.Equal(["second"], await ListAsync(restarted));
        Assert.Equal(2, blobs.Items.Count);
    }

    [Fact]
    public async Task Failed_receipt_leaves_blob_pending()
    {
        var blobs = new FakeBlobs();
        var receipts = new FakeReceipts { FailWrites = true };
        var source = new FeedbackBlobSource(blobs, receipts);
        await Assert.ThrowsAsync<FeedbackInboxTransientException>(
            () => source.MarkProcessedAsync("first", CancellationToken.None));
        Assert.Equal(["first", "second"], await ListAsync(new FeedbackBlobSource(blobs, receipts)));
    }

    static async Task<List<string>> ListAsync(FeedbackBlobSource source)
    {
        var names = new List<string>();
        await foreach (var name in source.ListPendingAsync(CancellationToken.None)) { names.Add(name); }
        return names;
    }

    sealed class FakeBlobs : IFeedbackBlobReader
    {
        public Dictionary<string, byte[]> Items { get; } = new()
        {
            ["first"] = [1, 2, 3],
            ["second"] = [4, 5]
        };

        public async IAsyncEnumerable<string> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            foreach (var name in Items.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return name;
            }
        }

        public Task<long?> GetSizeAsync(string bundleName, CancellationToken cancellationToken) =>
            Task.FromResult<long?>(Items.TryGetValue(bundleName, out var item) ? item.Length : null);

        public Task<ReadOnlyMemory<byte>?> ReadAsync(string bundleName, long maximumBytes, CancellationToken cancellationToken) =>
            Task.FromResult<ReadOnlyMemory<byte>?>(Items.TryGetValue(bundleName, out var item) ? item : null);
    }

    sealed class FakeReceipts : IFeedbackProcessedReceipts
    {
        readonly HashSet<string> processed = [];
        public bool FailWrites { get; init; }

        public Task<bool> ContainsAsync(string bundleName, CancellationToken cancellationToken) =>
            Task.FromResult(processed.Contains(bundleName));

        public Task RecordAsync(string bundleName, CancellationToken cancellationToken)
        {
            if (FailWrites) { throw new FeedbackInboxTransientException("Receipt store unavailable."); }
            processed.Add(bundleName);
            return Task.CompletedTask;
        }
    }
}
