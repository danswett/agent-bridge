using System.Net;
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
    public void Missing_session_evidence_and_unavailable_github_data_remain_unknown()
    {
        using var repo = new Repository();
        using var client = new HttpClient(new ApiHandler(_ => new(HttpStatusCode.Forbidden)));
        var missing = new WorkStateClearanceProvider(repo.Source, client).Read("missing", repo.Process);
        Assert.All(new[] { missing.OpenOwnedPr, missing.UnpushedWork, missing.Held, missing.Private },
            check => Assert.Equal(WorkState.Unknown, check.State));

        var unavailable = new WorkStateClearanceProvider(repo.Source, client).Read(repo.Session.Id, repo.Process);
        Assert.Equal(WorkState.Unknown, unavailable.OpenOwnedPr.State);
        Assert.Equal(WorkState.Unknown, unavailable.UnpushedWork.State);
        using var forkClient = repo.Client(fork: true);
        var fork = new WorkStateClearanceProvider(repo.Source, forkClient).Read(repo.Session.Id, repo.Process);
        Assert.Equal(WorkState.Unknown, fork.OpenOwnedPr.State);
    }

    sealed class Repository : IDisposable
    {
        readonly string root = Path.Combine(Path.GetTempPath(), "agent-bridge-work-state-" + Guid.NewGuid().ToString("N"));
        readonly string initialHead;
        public readonly SessionProcess Process = new(1234, DateTimeOffset.UtcNow);
        public Session Session { get; }
        public string GitDirectory { get; }
        public Source Source { get; }

        public Repository()
        {
            Directory.CreateDirectory(root);
            Run(root, ["init", "-b", "main"]);
            Run(root, ["config", "user.name", "Test"]);
            Run(root, ["config", "user.email", "test@example.invalid"]);
            File.WriteAllText(Path.Combine(root, "README.md"), "base");
            Run(root, ["add", "README.md"]);
            Run(root, ["commit", "-m", "base"]);
            initialHead = Run(root, ["rev-parse", "HEAD"]);
            Run(root, ["remote", "add", "origin", "https://github.com/octo/demo.git"]);
            GitDirectory = Run(root, ["rev-parse", "--path-format=absolute", "--git-dir"]);
            var now = DateTimeOffset.UtcNow;
            Session = new Session
            {
                Id = "12345678-1234-4234-8234-123456789012", Agent = AgentKind.Copilot,
                Cwd = root, Repository = "octo/demo", Branch = "main", Status = SessionStatus.Idle,
                StatusSince = now, UpdatedAt = now, Activity = new Activity(),
            };
            Source = new Source(Session, Process);
        }

        public HttpClient Client(bool openPullRequest = false, int aheadBy = 0, bool fork = false) =>
            new(new ApiHandler(request =>
            {
                var path = request.RequestUri!.AbsolutePath;
                if (path == "/repos/octo/demo") { return Json(System.Text.Json.JsonSerializer.Serialize(new { fork })); }
                if (path.EndsWith("/pulls", StringComparison.Ordinal))
                {
                    return Json(openPullRequest ? """[{"number":42}]""" : "[]");
                }
                if (path.EndsWith("/branches/main", StringComparison.Ordinal))
                {
                    return Json(System.Text.Json.JsonSerializer.Serialize(new { commit = new { sha = initialHead } }));
                }
                if (path.Contains("/compare/", StringComparison.Ordinal))
                {
                    return Json(System.Text.Json.JsonSerializer.Serialize(new { ahead_by = aheadBy }));
                }
                return new(HttpStatusCode.NotFound);
            }));

        public void Commit(string content)
        {
            File.WriteAllText(Path.Combine(root, "README.md"), content);
            Run(root, ["add", "README.md"]);
            Run(root, ["commit", "-m", content]);
        }

        static string Run(string directory, string[] arguments)
        {
            var result = Git.Run(directory, arguments);
            Assert.True(result.Ok, result.Said);
            return result.Output.Trim();
        }

        public void Dispose()
        {
            foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos("*", SearchOption.AllDirectories))
                entry.Attributes &= ~FileAttributes.ReadOnly;
            Directory.Delete(root, recursive: true);
        }
    }

    sealed class Source(Session session, SessionProcess process) : ISessionSource {
        public long Revision => 0;
        public event EventHandler<SessionChange>? Changed { add { } remove { } }
        public IReadOnlyList<Session> Snapshot() => [session];
        public SessionList List(string? cursor = null) => throw new NotSupportedException();
        public Session? Get(string id) => id == session.Id ? session : null; public SessionProcess? ProcessOf(string id) => id == session.Id ? process : null;
        public SessionSubscription Subscribe() => throw new NotSupportedException();
        public Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StartAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    sealed class ApiHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }

    static HttpResponseMessage Json(string content) => new(HttpStatusCode.OK) { Content = new StringContent(content, null, "application/json") };
}
