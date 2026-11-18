using System.Net;
using System.Text.Json;
using AgentBridge.Host;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;
using Xunit;

public sealed class WorkStateClearanceTests
{
    [Fact]
    public void Clean_pushed_session_has_a_reasoned_clearance_for_each_safeguard()
    {
        using var repo = new Repository();
        using var client = repo.Client();
        var result = new WorkStateClearanceProvider(repo.Source, client).Read(repo.Session.Id, repo.Process);
        Assert.All(new[] { result.OpenOwnedPr, result.UnpushedWork, result.Held, result.Private },
            check => { Assert.Equal(WorkState.Cleared, check.State); Assert.False(string.IsNullOrWhiteSpace(check.Reason)); });
    }

    [Fact]
    public void Open_pull_requests_unpushed_commits_and_worktree_markers_refuse_clearance()
    {
        using var repo = new Repository();
        repo.Commit("new work");
        using var client = repo.Client(openPullRequest: true, aheadBy: 1);
        var result = new WorkStateClearanceProvider(repo.Source, client).Read(repo.Session.Id, repo.Process);
        Assert.Equal(WorkState.Refused, result.OpenOwnedPr.State);
        Assert.Equal(WorkState.Refused, result.UnpushedWork.State);
        File.WriteAllText(Path.Combine(repo.GitDirectory, WorkStateClearanceProvider.HeldMarker), "");
        File.WriteAllText(Path.Combine(repo.GitDirectory, WorkStateClearanceProvider.PrivateMarker), "");
        using var markerClient = repo.Client();
        result = new WorkStateClearanceProvider(repo.Source, markerClient).Read(repo.Session.Id, repo.Process);
        Assert.Equal(WorkState.Refused, result.Held.State);
        Assert.Equal(WorkState.Refused, result.Private.State);
    }

    [Fact]
    public void Missing_process_dirty_work_and_unavailable_github_data_never_clear()
    {
        using var repo = new Repository();
        using var client = new HttpClient(new ApiHandler(_ => new(HttpStatusCode.Forbidden)));
        var missing = new WorkStateClearanceProvider(repo.Source, client).Read("missing", repo.Process);
        Assert.All(new[] { missing.OpenOwnedPr, missing.UnpushedWork, missing.Held, missing.Private },
            check => Assert.Equal(WorkState.Unknown, check.State));
        var unavailable = new WorkStateClearanceProvider(repo.Source, client).Read(repo.Session.Id, repo.Process);
        Assert.Equal(WorkState.Unknown, unavailable.OpenOwnedPr.State);
        Assert.Equal(WorkState.Unknown, unavailable.UnpushedWork.State);
        Assert.Equal(WorkState.Unknown, new WorkStateClearanceProvider(repo.Source, client)
            .Read(repo.Session.Id, repo.Process with { Id = 5678 }).Held.State);
        File.WriteAllText(Path.Combine(repo.Root, "untracked.txt"), "work");
        using var ready = repo.Client();
        Assert.Equal(WorkState.Refused, new WorkStateClearanceProvider(repo.Source, ready)
            .Read(repo.Session.Id, repo.Process).UnpushedWork.State);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"fork":true,"private":false}""")]
    [InlineData("""{"fork":false,"private":true}""")]
    [InlineData("""{"fork":"false","private":false}""")]
    [InlineData("""{"fork":false,"private":false}""")]
    [InlineData("""{"fork":false,"private":false,"full_name":"octo/renamed"}""")]
    [InlineData("""{"fork":false,"private":false,"full_name":123}""")]
    public void Fork_private_or_unproven_repository_metadata_never_clears(string metadata)
    {
        using var repo = new Repository();
        using var client = repo.Client(metadata: metadata);
        Assert.Equal(WorkState.Unknown, new WorkStateClearanceProvider(repo.Source, client)
            .Read(repo.Session.Id, repo.Process).OpenOwnedPr.State);
    }

    [Fact]
    public void Reviewer_transferred_repository_must_not_clear_the_old_owner_pr_filter()
    {
        using var repo = new Repository();
        var head = Git.Run(repo.Root, ["rev-parse", "HEAD"]).Output.Trim();
        using var client = new HttpClient(new ApiHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/repos/octo/demo")
            { return Json("""{"fork":false,"private":false,"full_name":"new-owner/demo","owner":{"login":"new-owner"}}"""); }
            if (path.EndsWith("/pulls", StringComparison.Ordinal))
            {
                var canonical = Uri.UnescapeDataString(request.RequestUri.Query).Contains("head=new-owner:main", StringComparison.Ordinal);
                return Json(canonical ? """[{"number":42}]""" : "[]");
            }
            return Json(JsonSerializer.Serialize(new { commit = new { sha = head } }));
        }));
        var result = new WorkStateClearanceProvider(repo.Source, client).Read(repo.Session.Id, repo.Process);
        Assert.NotEqual(WorkState.Cleared, result.OpenOwnedPr.State);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Reviewer_wrong_json_field_types_must_return_unknown(bool invalidSha)
    {
        using var repo = new Repository();
        using var client = new HttpClient(new ApiHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/repos/octo/demo")
            { return Json("""{"fork":false,"private":false,"full_name":"octo/demo"}"""); }
            if (path.EndsWith("/pulls", StringComparison.Ordinal)) { return Json("[]"); }
            if (path.EndsWith("/branches/main", StringComparison.Ordinal))
            { return Json(invalidSha ? """{"commit":{"sha":123}}""" : """{"commit":{"sha":"different"}}"""); }
            return Json("""{"ahead_by":"unknown"}""");
        }));
        var result = new WorkStateClearanceProvider(repo.Source, client).Read(repo.Session.Id, repo.Process);
        Assert.Equal(WorkState.Unknown, result.UnpushedWork.State);
    }

    sealed class Repository : IDisposable
    {
        public readonly string Root = Directory.CreateTempSubdirectory("ab-work-state-").FullName;
        readonly string initialHead;
        public readonly SessionProcess Process = new(1234, DateTimeOffset.UtcNow);
        public Session Session { get; }
        public string GitDirectory { get; }
        public Source Source { get; }

        public Repository()
        {
            Run(["init", "-b", "main"]);
            Run(["config", "user.name", "Test"]);
            Run(["config", "user.email", "test@example.invalid"]);
            Commit("base");
            initialHead = Run(["rev-parse", "HEAD"]);
            Run(["remote", "add", "origin", "https://github.com/octo/demo.git"]);
            GitDirectory = Run(["rev-parse", "--path-format=absolute", "--git-dir"]);
            var now = DateTimeOffset.UtcNow;
            Session = new Session { Id = "12345678-1234-4234-8234-123456789012", Agent = AgentKind.Copilot,
                Cwd = Root, Repository = "octo/demo", Branch = "main", Status = SessionStatus.Idle,
                StatusSince = now, UpdatedAt = now, Activity = new Activity() };
            Source = new Source(Session, Process);
        }

        public HttpClient Client(bool openPullRequest = false, int aheadBy = 0,
            string metadata = """{"fork":false,"private":false,"full_name":"OcTo/DeMo"}""") =>
            new(new ApiHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/repos/octo/demo") { return Json(metadata); }
                if (path.EndsWith("/pulls", StringComparison.Ordinal)) { return Json(openPullRequest ? """[{"number":42}]""" : "[]"); }
                if (path.EndsWith("/branches/main", StringComparison.Ordinal))
                { return Json(JsonSerializer.Serialize(new { commit = new { sha = initialHead } })); }
                if (path.Contains("/compare/", StringComparison.Ordinal))
                { return Json(JsonSerializer.Serialize(new { ahead_by = aheadBy })); }
                return new(HttpStatusCode.NotFound);
            }));

        public void Commit(string content)
        {
            File.WriteAllText(Path.Combine(Root, "README.md"), content);
            Run(["add", "README.md"]);
            Run(["commit", "-m", content]);
        }
        string Run(string[] arguments)
        {
            var result = Git.Run(Root, arguments);
            Assert.True(result.Ok, result.Said);
            return result.Output.Trim();
        }
        public void Dispose()
        {
            foreach (var entry in new DirectoryInfo(Root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                entry.Attributes &= ~FileAttributes.ReadOnly;
            Directory.Delete(Root, recursive: true);
        }
    }
    sealed class Source(Session session, SessionProcess process) : ISessionSource
    {
        public long Revision => 0;
        public event EventHandler<SessionChange>? Changed { add { } remove { } }
        public IReadOnlyList<Session> Snapshot() => [session];
        public SessionList List(string? cursor = null) => throw new NotSupportedException();
        public Session? Get(string id) => id == session.Id ? session : null;
        public SessionProcess? ProcessOf(string id) => id == session.Id ? process : null;
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    sealed class ApiHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
    static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, null, "application/json") };
}
