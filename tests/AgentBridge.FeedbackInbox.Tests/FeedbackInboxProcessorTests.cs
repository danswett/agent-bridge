using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using AgentBridge.FeedbackInbox;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentBridge.FeedbackInbox.Tests;

public sealed class FeedbackInboxProcessorTests
{
    static readonly FeedbackMetadata Classified = new("startup-failure", "launch", "1.2.3", "1.2.3", "windows", "x64", true);
    const string PrivateMarker = "testerKey=forged; tester text, private log and personal path";

    static FeedbackInboxProcessor Processor(
        FakeSource source,
        FakeDecryptor decryptor,
        FakePrivateStore privateStore,
        FakePublicStore publicStore,
        FeedbackInboxOptions? options = null) =>
        new(options ?? new FeedbackInboxOptions
        {
            Enabled = true,
            PublicErrorTypes = ["startup-failure"],
            PublicStages = ["launch"]
        }, source, new FakeKeys(), decryptor, privateStore, publicStore);

    [Fact]
    public async Task Public_issues_contain_only_validated_fields_and_never_decrypted_content()
    {
        var source = new FakeSource(("random-name", [1, 2, 3]));
        var decryptor = new FakeDecryptor(new DecryptedFeedback(Classified, PrivateMarker));
        var privateStore = new FakePrivateStore();
        var publicStore = new FakePublicStore();

        await Processor(source, decryptor, privateStore, publicStore).ProcessAsync();

        var issue = Assert.Single(publicStore.Issues);
        Assert.Contains("[beta-feedback] startup-failure at launch (", issue.Title);
        Assert.Contains("Testers affected: unknown", issue.Body);
        Assert.Equal(["beta-feedback", "fb-kind:startup-failure"], issue.Labels);
        Assert.DoesNotContain(PrivateMarker, issue.Body);
        Assert.DoesNotContain("random-name", issue.Body);
        Assert.Contains(PrivateMarker, Assert.Single(privateStore.Classified).PrivateContent);
        Assert.Empty(source.Pending);
        Assert.Equal(10, publicStore.LastHourlyCap);
    }

    [Fact]
    public async Task Public_tester_count_stays_unknown_without_authenticated_identity()
    {
        var source = new FakeSource(("random-name", [1, 2, 3]));
        var decryptor = new FakeDecryptor(new DecryptedFeedback(Classified, PrivateMarker));
        var privateStore = new FakePrivateStore();
        var publicStore = new FakePublicStore();

        await Processor(source, decryptor, privateStore, publicStore).ProcessAsync();

        var issue = Assert.Single(publicStore.Issues);
        Assert.Null(issue.TesterCount);
        Assert.Contains("Testers affected: unknown", issue.Body);
    }

    [Fact]
    public async Task Unknown_error_codes_are_kept_private()
    {
        var metadata = Classified with { ErrorType = "unreviewed-tag" };
        var source = new FakeSource(("random-name", [1, 2, 3]));
        var decryptor = new FakeDecryptor(new DecryptedFeedback(metadata, PrivateMarker));
        var privateStore = new FakePrivateStore();
        var publicStore = new FakePublicStore();

        await Processor(source, decryptor, privateStore, publicStore).ProcessAsync();

        var record = Assert.Single(privateStore.Unclassified);
        Assert.Equal("unclassified", record.UnclassifiedReason);
        Assert.Equal(metadata, record.Metadata);
        Assert.Contains(PrivateMarker, record.PrivateContent);
        Assert.Empty(publicStore.Issues);
    }

    [Theory]
    [InlineData("0.1.1108")]
    [InlineData("0.1.4-preview.1108")]
    public async Task Four_digit_build_numbers_are_preserved_in_public_issues(string version)
    {
        var metadata = Classified with { AppVersion = version };
        var source = new FakeSource(("random-name", [1, 2, 3]));
        var decryptor = new FakeDecryptor(new DecryptedFeedback(metadata, PrivateMarker));
        var privateStore = new FakePrivateStore();
        var publicStore = new FakePublicStore();

        await Processor(source, decryptor, privateStore, publicStore).ProcessAsync();

        var issue = Assert.Single(publicStore.Issues);
        Assert.Contains($"App version: `{version}`", issue.Body);
    }

    [Theory]
    [InlineData("alice.smith")]
    [InlineData("0.1.11080")]
    public async Task Invalid_versions_are_omitted_from_public_issues(string version)
    {
        var metadata = Classified with { AppVersion = version };
        var source = new FakeSource(("random-name", [1, 2, 3]));
        var decryptor = new FakeDecryptor(new DecryptedFeedback(metadata, PrivateMarker));
        var privateStore = new FakePrivateStore();
        var publicStore = new FakePublicStore();

        await Processor(source, decryptor, privateStore, publicStore).ProcessAsync();

        var issue = Assert.Single(publicStore.Issues);
        Assert.DoesNotContain(version, issue.Body);
        Assert.Contains("App version: `unknown`", issue.Body);
    }

    [Fact]
    public async Task Multiple_bundles_update_one_issue_without_claiming_tester_count()
    {
        var source = new FakeSource(("first", [1, 2, 3]), ("second", [4, 5, 6]));
        var decryptor = new FakeDecryptor(new DecryptedFeedback(Classified, PrivateMarker));
        var privateStore = new FakePrivateStore();
        var publicStore = new FakePublicStore();

        await Processor(source, decryptor, privateStore, publicStore).ProcessAsync();

        var issue = Assert.Single(publicStore.Issues);
        Assert.Null(issue.TesterCount);
        Assert.Contains("Testers affected: unknown", issue.Body);
        Assert.Equal(2, privateStore.Classified.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Unconfirmed_private_destination_fails_before_reading_or_deleting(bool? confirmation)
    {
        var source = new FakeSource(("random-name", [1, 2, 3]));
        var decryptor = new FakeDecryptor(new DecryptedFeedback(Classified, PrivateMarker));
        var privateStore = new FakePrivateStore { RepositoryPrivate = confirmation };
        var publicStore = new FakePublicStore();

        var processor = Processor(source, decryptor, privateStore, publicStore);
        if (confirmation is false)
        {
            await Assert.ThrowsAsync<FeedbackInboxRepositoryNotPrivateException>(() => processor.ProcessAsync());
        }
        else
        {
            await Assert.ThrowsAsync<FeedbackInboxTransientException>(() => processor.ProcessAsync());
        }

        Assert.Equal(0, source.ListCalls);
        Assert.Equal(0, source.Reads);
        Assert.Equal(0, decryptor.Calls);
        Assert.Empty(privateStore.Classified);
        Assert.Empty(privateStore.Unclassified);
        Assert.Single(source.Pending);
        Assert.Empty(publicStore.Issues);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task Worker_retries_unconfirmed_private_destination_without_processing(bool? initialConfirmation)
    {
        var source = new FakeSource(("bundle", [1, 2, 3]));
        var decryptor = new FakeDecryptor(new DecryptedFeedback(Classified, PrivateMarker));
        var privateStore = new FakePrivateStore { RepositoryPrivate = initialConfirmation };
        var publicStore = new FakePublicStore();
        var options = new FeedbackInboxOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMilliseconds(20),
            PublicErrorTypes = ["startup-failure"],
            PublicStages = ["launch"]
        };
        var processor = Processor(source, decryptor, privateStore, publicStore, options);
        var worker = new FeedbackInboxWorker(options, processor, NullLogger<FeedbackInboxWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await privateStore.FirstConfirmationCall.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(0, source.ListCalls);
            Assert.Equal(0, decryptor.Calls);
            Assert.Single(source.Pending);
            Assert.Empty(privateStore.Classified);

            privateStore.RepositoryPrivate = true;
            await source.DeletedBundle.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.True(privateStore.ConfirmationCalls >= 2);
        Assert.Single(privateStore.Classified);
        Assert.Empty(source.Pending);
        Assert.Single(publicStore.Issues);
    }

    [Fact]
    public async Task Cryptographic_failure_is_quarantined_and_does_not_block_later_bundles()
    {
        var source = new FakeSource(("invalid", [1, 2, 3]), ("valid", [4, 5, 6]));
        var decryptor = new FakeDecryptor(new DecryptedFeedback(Classified, PrivateMarker)) { InvalidFirstByte = 1 };
        var privateStore = new FakePrivateStore();
        var publicStore = new FakePublicStore();

        await Processor(source, decryptor, privateStore, publicStore).ProcessAsync();

        var quarantined = Assert.Single(privateStore.Unclassified);
        Assert.Equal("invalid-envelope", quarantined.UnclassifiedReason);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), quarantined.OpaqueCiphertextBase64);
        Assert.Equal("valid", Assert.Single(privateStore.Classified).BundleName);
        Assert.Single(publicStore.Issues);
        Assert.Empty(source.Pending);
    }

    [Fact]
    public async Task Transient_storage_failure_is_retried_on_next_poll()
    {
        var source = new FakeSource(("bundle", [1, 2, 3])) { TransientSizeFailures = 1 };
        var decryptor = new FakeDecryptor(new DecryptedFeedback(Classified, PrivateMarker));
        var privateStore = new FakePrivateStore();
        var publicStore = new FakePublicStore();
        var options = new FeedbackInboxOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMilliseconds(20),
            PublicErrorTypes = ["startup-failure"],
            PublicStages = ["launch"]
        };
        var processor = Processor(source, decryptor, privateStore, publicStore, options);
        var worker = new FeedbackInboxWorker(options, processor, NullLogger<FeedbackInboxWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await source.SecondSizeCall.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        Assert.Single(privateStore.Classified);
        Assert.Empty(source.Pending);
    }

    [Fact]
    public async Task Unclassified_feedback_is_filed_privately_without_a_public_issue()
    {
        var source = new FakeSource(("random-name", [1, 2, 3]));
        var decryptor = new FakeDecryptor(new DecryptedFeedback(null, PrivateMarker));
        var privateStore = new FakePrivateStore();
        var publicStore = new FakePublicStore();

        await Processor(source, decryptor, privateStore, publicStore).ProcessAsync();

        var record = Assert.Single(privateStore.Unclassified);
        Assert.Equal("unclassified", record.UnclassifiedReason);
        Assert.Contains(PrivateMarker, record.PrivateContent);
        Assert.Empty(publicStore.Issues);
        Assert.Empty(source.Pending);
    }

    [Fact]
    public async Task Oversized_bundles_are_never_read_or_decrypted()
    {
        var source = new FakeSource(("random-name", new byte[9])) { ReportedSize = 9 };
        var decryptor = new FakeDecryptor(new DecryptedFeedback(Classified, PrivateMarker));
        var privateStore = new FakePrivateStore();
        var publicStore = new FakePublicStore();
        var options = new FeedbackInboxOptions { Enabled = true, MaxBundleBytes = 8 };

        await Processor(source, decryptor, privateStore, publicStore, options).ProcessAsync();

        var record = Assert.Single(privateStore.Unclassified);
        Assert.Equal("size-limit", record.UnclassifiedReason);
        Assert.Null(record.OpaqueCiphertextBase64);
        Assert.Equal(0, source.Reads);
        Assert.Equal(0, decryptor.Calls);
        Assert.Empty(publicStore.Issues);
        Assert.Empty(source.Pending);
    }

    [Fact]
    public async Task Size_mismatch_is_kept_private_without_decryption()
    {
        var source = new FakeSource(("random-name", [1, 2, 3])) { ReportedSize = 2 };
        var decryptor = new FakeDecryptor(new DecryptedFeedback(Classified, PrivateMarker));
        var privateStore = new FakePrivateStore();
        var publicStore = new FakePublicStore();

        await Processor(source, decryptor, privateStore, publicStore).ProcessAsync();

        var record = Assert.Single(privateStore.Unclassified);
        Assert.Equal("size-mismatch", record.UnclassifiedReason);
        Assert.Equal(Convert.ToBase64String([1, 2, 3]), record.OpaqueCiphertextBase64);
        Assert.Equal(0, decryptor.Calls);
        Assert.Empty(publicStore.Issues);
        Assert.Empty(source.Pending);
    }

    [Theory]
    [InlineData("launch\n@everyone")]
    [InlineData(null)]
    public async Task Invalid_public_fields_are_kept_private(string? stage)
    {
        var unsafeMetadata = Classified with { Stage = stage! };
        var source = new FakeSource(("random-name", [1, 2, 3]));
        var decryptor = new FakeDecryptor(new DecryptedFeedback(unsafeMetadata, PrivateMarker));
        var privateStore = new FakePrivateStore();
        var publicStore = new FakePublicStore();

        await Processor(source, decryptor, privateStore, publicStore).ProcessAsync();

        var record = Assert.Single(privateStore.Unclassified);
        Assert.Equal("unclassified", record.UnclassifiedReason);
        Assert.Equal(unsafeMetadata, record.Metadata);
        Assert.Empty(publicStore.Issues);
    }

    [Fact]
    public async Task Disabled_inbox_does_not_read_or_decrypt_pending_bundles()
    {
        var source = new FakeSource(("random-name", [1, 2, 3]));
        var decryptor = new FakeDecryptor(new DecryptedFeedback(Classified, PrivateMarker));
        var privateStore = new FakePrivateStore();
        var publicStore = new FakePublicStore();

        await Processor(source, decryptor, privateStore, publicStore, new FeedbackInboxOptions()).ProcessAsync();

        Assert.Equal(0, source.Reads);
        Assert.Equal(0, decryptor.Calls);
        Assert.Empty(privateStore.Classified);
        Assert.Empty(publicStore.Issues);
        Assert.Single(source.Pending);
    }

    sealed class FakeSource(params (string Name, byte[] Bytes)[] bundles) : IEncryptedFeedbackBundleSource
    {
        public readonly Dictionary<string, byte[]> Pending = bundles.ToDictionary(x => x.Name, x => x.Bytes);
        public int Reads;
        public int ListCalls;
        public long? ReportedSize { get; init; }
        public int TransientSizeFailures { get; set; }
        public TaskCompletionSource SecondSizeCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource DeletedBundle { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int sizeCalls;

        public async IAsyncEnumerable<string> ListPendingAsync([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            ListCalls++;
            foreach (var name in Pending.Keys.ToArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return name;
                await Task.Yield();
            }
        }

        public Task<long?> GetSizeAsync(string bundleName, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref sizeCalls) > 1) { SecondSizeCall.TrySetResult(); }
            if (TransientSizeFailures > 0)
            {
                TransientSizeFailures--;
                return Task.FromException<long?>(new FeedbackInboxTransientException("temporary storage failure"));
            }
            return Task.FromResult(Pending.TryGetValue(bundleName, out var bytes) ? ReportedSize ?? bytes.Length : (long?)null);
        }

        public Task<ReadOnlyMemory<byte>?> ReadAsync(string bundleName, long maximumBytes, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult(Pending.TryGetValue(bundleName, out var bytes) ? (ReadOnlyMemory<byte>?)bytes : null);
        }

        public Task DeleteAsync(string bundleName, CancellationToken cancellationToken)
        {
            Pending.Remove(bundleName);
            DeletedBundle.TrySetResult();
            return Task.CompletedTask;
        }
    }

    sealed class FakeKeys : IFeedbackPrivateKeyProvider
    {
        public Task<FeedbackPrivateKey> GetAsync(string keyId, CancellationToken cancellationToken) =>
            Task.FromResult(new FeedbackPrivateKey([1]));
    }

    sealed class FakeDecryptor(DecryptedFeedback result) : IFeedbackEnvelopeDecryptor
    {
        public int Calls;
        public byte? InvalidFirstByte { get; init; }

        public Task<DecryptedFeedback> DecryptAsync(
            ReadOnlyMemory<byte> ciphertext,
            IFeedbackPrivateKeyProvider keys,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (ciphertext.Length > 0 && ciphertext.Span[0] == InvalidFirstByte)
            {
                return Task.FromException<DecryptedFeedback>(new CryptographicException("Invalid authentication tag."));
            }
            return Task.FromResult(result);
        }
    }

    sealed class FakePrivateStore : IFeedbackPrivateStore
    {
        readonly HashSet<string> seenBundles = [];
        public bool? RepositoryPrivate { get; set; } = true;
        public int ConfirmationCalls;
        public TaskCompletionSource FirstConfirmationCall { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly List<FeedbackPrivateRecord> Classified = [];
        public readonly List<FeedbackPrivateRecord> Unclassified = [];
        public readonly List<PublicFeedbackIssueReference> Links = [];

        public Task<bool?> ConfirmRepositoryPrivateAsync(CancellationToken cancellationToken)
        {
            var result = RepositoryPrivate;
            if (Interlocked.Increment(ref ConfirmationCalls) == 1) { FirstConfirmationCall.TrySetResult(); }
            return Task.FromResult(result);
        }

        public Task UpsertClassifiedAsync(string fingerprint, FeedbackPrivateRecord record, CancellationToken cancellationToken)
        {
            if (seenBundles.Add(record.BundleName)) { Classified.Add(record); }
            return Task.CompletedTask;
        }

        public Task StoreUnclassifiedAsync(FeedbackPrivateRecord record, CancellationToken cancellationToken)
        {
            if (Unclassified.All(x => x.BundleName != record.BundleName)) { Unclassified.Add(record); }
            return Task.CompletedTask;
        }

        public Task LinkPublicIssueAsync(string fingerprint, PublicFeedbackIssueReference issue, CancellationToken cancellationToken)
        {
            Links.Add(issue);
            return Task.CompletedTask;
        }
    }

    sealed class FakePublicStore : IPublicFeedbackIssueStore
    {
        public readonly List<PublicFeedbackIssue> Issues = [];
        public int LastHourlyCap;

        public Task<PublicFeedbackIssueReference?> UpsertAsync(
            PublicFeedbackIssue issue,
            int maxNewIssuesPerHour,
            CancellationToken cancellationToken)
        {
            var existing = Issues.FindIndex(x => x.Fingerprint == issue.Fingerprint);
            if (existing < 0) { Issues.Add(issue); }
            else { Issues[existing] = issue; }
            LastHourlyCap = maxNewIssuesPerHour;
            return Task.FromResult<PublicFeedbackIssueReference?>(new PublicFeedbackIssueReference(1, "https://example.invalid/1"));
        }
    }
}
