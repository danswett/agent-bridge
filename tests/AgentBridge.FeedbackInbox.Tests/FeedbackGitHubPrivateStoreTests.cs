using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.FeedbackInbox;
using Xunit;

namespace AgentBridge.FeedbackInbox.Tests;

public sealed class FeedbackGitHubPrivateStoreTests
{
    const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Public_constructor_disables_http_redirects()
    {
        using var store = new FeedbackGitHubPrivateStore(new FakeTokens());
        var client = Assert.IsType<HttpClient>(typeof(FeedbackGitHubPrivateStore)
            .GetField("http", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store));
        var handler = Assert.IsType<HttpClientHandler>(typeof(HttpMessageInvoker)
            .GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client));
        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public async Task Private_records_are_chunked_and_replayed_without_duplicates()
    {
        var api = new FakeGitHub();
        using var store = new FeedbackGitHubPrivateStore(new FakeTokens(), api);
        var record = Record(new string('x', 100_000));
        api.FailCommentOnceAt = 3;
        await Assert.ThrowsAsync<FeedbackInboxTransientException>(
            () => store.UpsertClassifiedAsync(Fingerprint, record, CancellationToken.None));

        await store.UpsertClassifiedAsync(Fingerprint, record with { ReceivedAt = record.ReceivedAt.AddMinutes(1) }, CancellationToken.None);
        var count = api.Comments.Count;
        await store.UpsertClassifiedAsync(Fingerprint, record with { ReceivedAt = record.ReceivedAt.AddMinutes(1) }, CancellationToken.None);

        Assert.Single(api.Issues);
        Assert.Equal(5, count);
        Assert.Equal(count, api.Comments.Count);
        Assert.All(api.Comments, comment => Assert.True(comment.Length < 65536));
        var json = string.Concat(api.Comments.Skip(1).Take(3).Select(comment => comment[(comment.IndexOf("\n\n    ", StringComparison.Ordinal) + 6)..]));
        var saved = JsonSerializer.Deserialize<FeedbackPrivateRecord>(json);
        Assert.Equal(record.PrivateContent, saved!.PrivateContent);
        Assert.Equal(record.ReceivedAt, saved.ReceivedAt);
    }

    [Fact]
    public async Task Unclassified_feedback_is_idempotent_by_bundle_name()
    {
        var api = new FakeGitHub();
        using var store = new FeedbackGitHubPrivateStore(new FakeTokens(), api);
        var record = Record("private fixture");
        await store.StoreUnclassifiedAsync(record, CancellationToken.None);
        await store.StoreUnclassifiedAsync(record, CancellationToken.None);
        Assert.Single(api.Issues);
        Assert.Equal(3, api.Comments.Count);
        Assert.DoesNotContain("private fixture", api.Issues[0].Body);
    }

    [Fact]
    public async Task Rechecks_privacy_before_writing_any_content()
    {
        var api = new FakeGitHub { PublicAtPrivacyCheck = 3 };
        using var store = new FeedbackGitHubPrivateStore(new FakeTokens(), api);
        await Assert.ThrowsAsync<FeedbackInboxRepositoryNotPrivateException>(
            () => store.StoreUnclassifiedAsync(Record("private fixture"), CancellationToken.None));
        Assert.Single(api.Issues);
        Assert.Empty(api.Comments);
    }

    [Fact]
    public async Task Confirmed_public_repository_never_accepts_feedback()
    {
        var api = new FakeGitHub { PublicAtPrivacyCheck = 1 };
        using var store = new FeedbackGitHubPrivateStore(new FakeTokens(), api);
        await Assert.ThrowsAsync<FeedbackInboxRepositoryNotPrivateException>(
            () => store.StoreUnclassifiedAsync(Record("private fixture"), CancellationToken.None));
        Assert.Empty(api.Issues);
        Assert.Empty(api.Comments);
        Assert.Equal("/repos/danswett/slipgates-feedback", Assert.Single(api.RequestPaths));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Unavailable_repository_status_fails_closed(HttpStatusCode status)
    {
        var api = new FakeGitHub { RepositoryStatus = status };
        using var store = new FeedbackGitHubPrivateStore(new FakeTokens(), api);
        Assert.Null(await store.ConfirmRepositoryPrivateAsync(CancellationToken.None));
        await Assert.ThrowsAsync<FeedbackInboxTransientException>(
            () => store.StoreUnclassifiedAsync(Record("private fixture"), CancellationToken.None));
        Assert.Empty(api.Issues);
        Assert.Empty(api.Comments);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Unavailable_issue_endpoint_is_retryable(HttpStatusCode status)
    {
        var api = new FakeGitHub { IssueStatus = status };
        using var store = new FeedbackGitHubPrivateStore(new FakeTokens(), api);
        await Assert.ThrowsAsync<FeedbackInboxTransientException>(
            () => store.StoreUnclassifiedAsync(Record("private fixture"), CancellationToken.None));
        Assert.Empty(api.Issues);
        Assert.Empty(api.Comments);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Unavailable_comment_read_is_retryable_without_writing_feedback(HttpStatusCode status)
    {
        var api = new FakeGitHub { CommentGetStatus = status };
        using var store = new FeedbackGitHubPrivateStore(new FakeTokens(), api);
        await Assert.ThrowsAsync<FeedbackInboxTransientException>(
            () => store.StoreUnclassifiedAsync(Record("private fixture"), CancellationToken.None));
        Assert.Single(api.Issues);
        Assert.Empty(api.Comments);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task Unavailable_comment_write_is_retryable_without_marking_completion(HttpStatusCode status)
    {
        var api = new FakeGitHub { CommentPostStatus = status };
        using var store = new FeedbackGitHubPrivateStore(new FakeTokens(), api);
        await Assert.ThrowsAsync<FeedbackInboxTransientException>(
            () => store.StoreUnclassifiedAsync(Record("private fixture"), CancellationToken.None));
        Assert.Single(api.Issues);
        Assert.Empty(api.Comments);
    }

    [Fact]
    public async Task Unavailable_or_mismatched_repository_never_accepts_feedback()
    {
        var api = new FakeGitHub { RepositoryName = "another/repository" };
        using var store = new FeedbackGitHubPrivateStore(new FakeTokens(), api);
        Assert.Null(await store.ConfirmRepositoryPrivateAsync(CancellationToken.None));
        await Assert.ThrowsAsync<FeedbackInboxTransientException>(
            () => store.StoreUnclassifiedAsync(Record("private fixture"), CancellationToken.None));
        Assert.Empty(api.Issues);
    }

    [Fact]
    public async Task Public_links_are_idempotent_and_do_not_use_the_supplied_url()
    {
        var api = new FakeGitHub();
        using var store = new FeedbackGitHubPrivateStore(new FakeTokens(), api);
        await store.UpsertClassifiedAsync(Fingerprint, Record("fixture"), CancellationToken.None);
        var reference = new PublicFeedbackIssueReference(42, "https://invalid.example/private");
        await store.LinkPublicIssueAsync(Fingerprint, reference, CancellationToken.None);
        await store.LinkPublicIssueAsync(Fingerprint, reference, CancellationToken.None);
        Assert.Equal(4, api.Comments.Count);
        Assert.Contains("https://github.com/danswett/agent-bridge/issues/42", api.Comments[^1]);
        Assert.DoesNotContain("invalid.example", api.Comments[^1]);
    }

    [Fact]
    public async Task Rejects_invalid_group_identifiers_before_making_requests()
    {
        var api = new FakeGitHub();
        using var store = new FeedbackGitHubPrivateStore(new FakeTokens(), api);
        await Assert.ThrowsAsync<ArgumentException>(
            () => store.UpsertClassifiedAsync(Fingerprint + "\n", Record("fixture"), CancellationToken.None));
        Assert.Empty(api.Issues);
        Assert.Empty(api.Comments);
    }

    [Fact]
    public async Task Finds_existing_completion_on_later_comment_pages()
    {
        var api = new FakeGitHub();
        using var store = new FeedbackGitHubPrivateStore(new FakeTokens(), api);
        var record = Record("fixture");
        await store.StoreUnclassifiedAsync(record, CancellationToken.None);
        api.Comments.InsertRange(0, Enumerable.Repeat("unrelated", 100));
        var before = api.Comments.Count;
        await store.StoreUnclassifiedAsync(record, CancellationToken.None);
        Assert.Equal(before, api.Comments.Count);
    }

    [Fact]
    public async Task Failed_response_body_is_retried_without_writing_feedback()
    {
        var api = new FakeGitHub { FailRepositoryBodyOnce = true };
        using var store = new FeedbackGitHubPrivateStore(new FakeTokens(), api);
        var record = Record("private fixture");
        await Assert.ThrowsAsync<FeedbackInboxTransientException>(
            () => store.StoreUnclassifiedAsync(record, CancellationToken.None));
        Assert.Empty(api.Issues);
        await store.StoreUnclassifiedAsync(record, CancellationToken.None);
        Assert.Single(api.Issues);
    }

    [Fact]
    public async Task Duplicate_group_issues_stop_without_writing_feedback()
    {
        var api = new FakeGitHub();
        using var store = new FeedbackGitHubPrivateStore(new FakeTokens(), api);
        var record = Record("private fixture");
        await store.UpsertClassifiedAsync(Fingerprint, record, CancellationToken.None);
        api.Issues.Add(new FakeGitHub.Issue(2, api.Issues[0].Body));
        var comments = api.Comments.Count;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.UpsertClassifiedAsync(Fingerprint, record, CancellationToken.None));
        Assert.Equal(comments, api.Comments.Count);
    }

    [Fact]
    public async Task Malformed_received_marker_stops_without_writing_feedback()
    {
        var api = new FakeGitHub();
        using var store = new FeedbackGitHubPrivateStore(new FakeTokens(), api);
        var record = Record("private fixture");
        await store.StoreUnclassifiedAsync(record, CancellationToken.None);
        api.Comments.Clear();
        api.Comments.Add($"<!-- slipgates-bundle-received:{BundleHash(record)}:invalid -->");
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => store.StoreUnclassifiedAsync(record, CancellationToken.None));
        Assert.Single(api.Comments);
    }

    [Fact]
    public async Task Forged_completion_and_mentions_remain_inert_during_retry()
    {
        var api = new FakeGitHub { FailCommentOnceAt = 3 };
        using var store = new FeedbackGitHubPrivateStore(new FakeTokens(), api);
        var baseRecord = Record("");
        var marker = $"<!-- slipgates-bundle-complete:{BundleHash(baseRecord)} -->";
        var record = baseRecord with { PrivateContent = $"text\n{marker}\n@synthetic-user" };
        await Assert.ThrowsAsync<FeedbackInboxTransientException>(
            () => store.StoreUnclassifiedAsync(record, CancellationToken.None));
        Assert.Equal(2, api.Comments.Count);
        Assert.DoesNotContain(marker, api.Comments[1]);
        var json = api.Comments[1][(api.Comments[1].IndexOf("\n\n    ", StringComparison.Ordinal) + 6)..];
        Assert.Equal($"<!-- slipgates-bundle-part:{BundleHash(record)}:{Hash(json)}:1:1 -->\n\n    {json}", api.Comments[1]);
        Assert.DoesNotContain('\n', json);
        Assert.DoesNotContain('\r', json);
        Assert.Contains("@synthetic-user", json);
        Assert.Equal(record.PrivateContent, JsonSerializer.Deserialize<FeedbackPrivateRecord>(json)!.PrivateContent);
        await store.StoreUnclassifiedAsync(record, CancellationToken.None);
        Assert.Equal(3, api.Comments.Count);
        Assert.Equal(marker, api.Comments[2]);
    }

    static FeedbackPrivateRecord Record(string text) => new(
        "synthetic-bundle", DateTimeOffset.UnixEpoch, Fingerprint, "fixture", text, null, null);

    static string BundleHash(FeedbackPrivateRecord record) =>
        Hash(record.BundleName);

    static string Hash(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    sealed class FakeTokens : IFeedbackGitHubTokenProvider
    {
        public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult("fixture");
    }

    sealed class FakeGitHub : HttpMessageHandler
    {
        public sealed record Issue(int Number, string Body);
        public List<Issue> Issues { get; } = [];
        public List<string> Comments { get; } = [];
        public List<string> RequestPaths { get; } = [];
        public int? FailCommentOnceAt { get; set; }
        public int? PublicAtPrivacyCheck { get; set; }
        public string RepositoryName { get; set; } = "danswett/slipgates-feedback";
        public HttpStatusCode? RepositoryStatus { get; set; }
        public HttpStatusCode? IssueStatus { get; set; }
        public HttpStatusCode? CommentGetStatus { get; set; }
        public HttpStatusCode? CommentPostStatus { get; set; }
        public bool FailRepositoryBodyOnce { get; set; }
        int privacyChecks;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            var path = request.RequestUri.AbsolutePath;
            RequestPaths.Add(path);
            if (path == "/repos/danswett/slipgates-feedback" && request.Method == HttpMethod.Get)
            {
                privacyChecks++;
                if (RepositoryStatus is { } repositoryStatus) { return new HttpResponseMessage(repositoryStatus); }
                if (FailRepositoryBodyOnce)
                {
                    FailRepositoryBodyOnce = false;
                    return new HttpResponseMessage(HttpStatusCode.OK) { Content = new FailingContent() };
                }
                return Json(new { full_name = RepositoryName, @private = privacyChecks != PublicAtPrivacyCheck });
            }
            if (path == "/repos/danswett/slipgates-feedback/issues")
            {
                if (IssueStatus is { } issueStatus) { return new HttpResponseMessage(issueStatus); }
                if (request.Method == HttpMethod.Get)
                {
                    return Json(Page(request, Issues.Select(i => new { number = i.Number, body = i.Body })));
                }
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                var issue = new Issue(Issues.Count + 1, body.RootElement.GetProperty("body").GetString()!);
                Issues.Add(issue);
                return Json(new { number = issue.Number });
            }
            if (path.EndsWith("/comments", StringComparison.Ordinal))
            {
                if (request.Method == HttpMethod.Get)
                {
                    if (CommentGetStatus is { } getStatus) { return new HttpResponseMessage(getStatus); }
                    return Json(Page(request, Comments.Select(body => new { body })));
                }
                if (CommentPostStatus is { } postStatus) { return new HttpResponseMessage(postStatus); }
                if (FailCommentOnceAt == Comments.Count + 1)
                {
                    FailCommentOnceAt = null;
                    return new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                }
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
                Comments.Add(body.RootElement.GetProperty("body").GetString()!);
                return Json(new { id = Comments.Count });
            }
            throw new InvalidOperationException("Unexpected fake GitHub request.");
        }

        static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
        };

        static IEnumerable<T> Page<T>(HttpRequestMessage request, IEnumerable<T> values)
        {
            var page = int.Parse(request.RequestUri!.Query.Split("page=")[^1]);
            return values.Skip((page - 1) * 100).Take(100);
        }
    }

    sealed class FailingContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            throw new IOException("Synthetic response-body failure.");

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
