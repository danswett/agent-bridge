using System.Runtime.CompilerServices;
using Azure;
using Azure.Storage.Blobs;

namespace AgentBridge.FeedbackInbox;

public interface IFeedbackProcessedReceipts
{
    Task<bool> ContainsAsync(string bundleName, CancellationToken cancellationToken);
    Task RecordAsync(string bundleName, CancellationToken cancellationToken);
}

public interface IFeedbackBlobReader
{
    IAsyncEnumerable<string> ListAsync(CancellationToken cancellationToken);
    Task<long?> GetSizeAsync(string bundleName, CancellationToken cancellationToken);
    Task<ReadOnlyMemory<byte>?> ReadAsync(string bundleName, long maximumBytes, CancellationToken cancellationToken);
}

public sealed class FeedbackBlobSource(IFeedbackBlobReader blobs, IFeedbackProcessedReceipts receipts)
    : IEncryptedFeedbackBundleSource
{
    public async IAsyncEnumerable<string> ListPendingAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var name in blobs.ListAsync(cancellationToken))
        {
            if (!await receipts.ContainsAsync(name, cancellationToken)) { yield return name; }
        }
    }

    public Task<long?> GetSizeAsync(string bundleName, CancellationToken cancellationToken) =>
        blobs.GetSizeAsync(bundleName, cancellationToken);

    public Task<ReadOnlyMemory<byte>?> ReadAsync(
        string bundleName, long maximumBytes, CancellationToken cancellationToken) =>
        blobs.ReadAsync(bundleName, maximumBytes, cancellationToken);

    public Task MarkProcessedAsync(string bundleName, CancellationToken cancellationToken) =>
        receipts.RecordAsync(bundleName, cancellationToken);
}

public sealed class AzureFeedbackBlobReader(BlobContainerClient container) : IFeedbackBlobReader
{
    const int MaxStoredBytes = 8 * 1024 * 1024;

    public async IAsyncEnumerable<string> ListAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await using var enumerator = container.GetBlobsAsync(cancellationToken: cancellationToken)
            .GetAsyncEnumerator(cancellationToken);
        while (true)
        {
            bool next;
            try { next = await enumerator.MoveNextAsync(); }
            catch (RequestFailedException e) when (IsTransient(e))
            {
                throw new FeedbackInboxTransientException("Feedback blob listing is temporarily unavailable.", e);
            }
            catch (IOException e)
            {
                throw new FeedbackInboxTransientException("Feedback blob listing is temporarily unavailable.", e);
            }
            if (!next) { yield break; }
            yield return enumerator.Current.Name;
        }
    }

    public async Task<long?> GetSizeAsync(string bundleName, CancellationToken cancellationToken)
    {
        try
        {
            var response = await container.GetBlobClient(bundleName).GetPropertiesAsync(cancellationToken: cancellationToken);
            return response.Value.ContentLength;
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
        catch (RequestFailedException e) when (IsTransient(e))
        {
            throw new FeedbackInboxTransientException("Feedback blob properties are temporarily unavailable.", e);
        }
        catch (IOException e)
        {
            throw new FeedbackInboxTransientException("Feedback blob properties are temporarily unavailable.", e);
        }
    }

    public async Task<ReadOnlyMemory<byte>?> ReadAsync(
        string bundleName, long maximumBytes, CancellationToken cancellationToken)
    {
        if (maximumBytes is <= 0 or > MaxStoredBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        }
        try
        {
            var response = await container.GetBlobClient(bundleName).DownloadStreamingAsync(cancellationToken: cancellationToken);
            await using var input = response.Value.Content;
            using var output = new MemoryStream();
            var buffer = new byte[81920];
            while (output.Length <= maximumBytes)
            {
                var want = (int)Math.Min(buffer.Length, maximumBytes + 1 - output.Length);
                var read = await input.ReadAsync(buffer.AsMemory(0, want), cancellationToken);
                if (read == 0) { break; }
                output.Write(buffer, 0, read);
            }
            return output.ToArray();
        }
        catch (RequestFailedException e) when (e.Status == 404) { return null; }
        catch (RequestFailedException e) when (IsTransient(e))
        {
            throw new FeedbackInboxTransientException("Feedback blob download is temporarily unavailable.", e);
        }
        catch (IOException e)
        {
            throw new FeedbackInboxTransientException("Feedback blob download is temporarily unavailable.", e);
        }
    }

    static bool IsTransient(RequestFailedException exception) =>
        exception.Status is 0 or 408 or 429 || exception.Status >= 500;
}
