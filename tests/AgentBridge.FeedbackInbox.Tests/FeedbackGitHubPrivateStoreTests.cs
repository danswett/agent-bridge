using System.Net;
using System.Text;
using System.Text.Json;
using AgentBridge.FeedbackInbox;
using Xunit;

namespace AgentBridge.FeedbackInbox.Tests;

public sealed class FeedbackGitHubPrivateStoreTests
{
    const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

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

    static FeedbackPrivateRecord Record(string text) => new(
        "synthetic-bundle", DateTimeOffset.UnixEpoch, Fingerprint, "fixture", text, null, null);

    sealed class FakeTokens : IFeedbackGitHubTokenProvider
    {
        public Task<string> GetAsync(CancellationToken cancellationToken) => Task.FromResult("fixture");
    }

    sealed class FakeGitHub : HttpMessageHandler
    {
        public sealed record Issue(int Number, string Body);
        public List<Issue> Issues { get; } = [];
        public List<string> Comments { get; } = [];
        public int? FailCommentOnceAt { get; set; }
        public int? PublicAtPrivacyCheck { get; set; }
        public string RepositoryName { get; set; } = "danswett/slipgates-feedback";
        int privacyChecks;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            var path = request.RequestUri.AbsolutePath;
            if (path == "/repos/danswett/slipgates-feedback" && request.Method == HttpMethod.Get)
            {
                privacyChecks++;
                return Json(new { full_name = RepositoryName, @private = privacyChecks != PublicAtPrivacyCheck });
            }
            if (path == "/repos/danswett/slipgates-feedback/issues")
            {
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
                if (request.Method == HttpMethod.Get) { return Json(Page(request, Comments.Select(body => new { body }))); }
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
}
