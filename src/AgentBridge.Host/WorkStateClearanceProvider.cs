using System.Net;
using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

public sealed class WorkStateClearanceProvider(ISessionSource source, HttpClient? github = null, TimeProvider? time = null)
    : IWorkStateClearance
{
    public const string HeldMarker = "agent-bridge-host-held";
    public const string PrivateMarker = "agent-bridge-host-private";
    static readonly HttpClient publicGithub = new() { Timeout = TimeSpan.FromSeconds(4), MaxResponseContentBufferSize = 1024 * 1024 };
    readonly HttpClient client = github ?? publicGithub;
    readonly TimeProvider clock = time ?? TimeProvider.System;

    public WorkStateClearance Read(string session, SessionProcess process)
    {
        var checkedAt = clock.GetUtcNow();
        var unknown = new WorkClearance(WorkState.Unknown, "session work state could not be proved");
        WorkStateClearance Result(WorkClearance? pr = null, WorkClearance? unpushed = null,
            WorkClearance? held = null, WorkClearance? privateWork = null) =>
            new(session, process, checkedAt, pr ?? unknown, unpushed ?? unknown, held ?? unknown, privateWork ?? unknown);

        if (source.Get(session) is not { } current || source.ProcessOf(session) != process
            || process.Id <= 0 || process.StartedAt is null || current.Id != session
            || current.ProcessId is { } pid && pid != process.Id)
        { return Result(); }
        if (source.Held is not null)
        { return Result(held: new(WorkState.Refused, "session discovery is held")); }
        if (current.Cwd is not { Length: > 0 } cwd || !Path.IsPathFullyQualified(cwd)
            || current.Branch is not { Length: > 0 } branch || current.Repository is not { Length: > 0 } repository
            || !Directory.Exists(cwd))
        { return Result(); }

        var top = Git.Run(cwd, ["rev-parse", "--show-toplevel"]);
        var gitDirectory = Git.Run(cwd, ["rev-parse", "--path-format=absolute", "--git-dir"]);
        var liveBranch = Git.Run(cwd, ["branch", "--show-current"]);
        var head = Git.Run(cwd, ["rev-parse", "--verify", "HEAD^{commit}"]);
        var remote = Git.Run(cwd, ["remote", "get-url", "origin"]);
        var status = Git.Run(cwd, ["--no-optional-locks", "-c", "core.fsmonitor=false", "status", "--porcelain=v1", "--untracked-files=all", "--ignore-submodules=none"]);
        var physicalTop = top.Ok ? Folders.Physical(top.Output.Trim()) : null;
        var physicalCwd = Folders.Physical(cwd);
        if (!top.Ok || !gitDirectory.Ok || !liveBranch.Ok || !head.Ok || !remote.Ok || !status.Ok
            || physicalTop is null || physicalCwd is null || !Folders.IsAtOrUnder(physicalCwd, physicalTop)
            || !string.Equals(liveBranch.Output.Trim(), branch, StringComparison.Ordinal)
            || !TryGithubRepository(remote.Output.Trim(), out var owner, out var repo)
            || !string.Equals(repository, owner + "/" + repo, StringComparison.OrdinalIgnoreCase))
        { return Result(); }

        var gitDir = Path.GetFullPath(gitDirectory.Output.Trim());
        if (Folders.Physical(gitDir) is not { } physicalGitDir || !Folders.Same(gitDir, physicalGitDir))
        { return Result(); }
        var held = Marker(gitDir, HeldMarker, "worktree lock is present");
        if (held.State == WorkState.Cleared) { held = Marker(gitDir, "locked", "worktree lock is present"); }
        var privateWork = Marker(gitDir, PrivateMarker, "private-work marker is present");
        var openPullRequest = CheckPullRequests(owner, repo, branch);
        var unpushed = status.Output.Length == 0
            ? CheckUnpushed(owner, repo, branch, head.Output.Trim(), openPullRequest.State != WorkState.Unknown)
            : new WorkClearance(WorkState.Refused, "the session worktree has uncommitted or untracked changes");
        return new(session, process, checkedAt, openPullRequest, unpushed, held, privateWork);
    }

    WorkClearance CheckPullRequests(string owner, string repository, string branch)
    {
        try
        {
            var repositoryResponse = GetJson($"/repos/{owner}/{repository}");
            using var metadata = repositoryResponse.Body;
            if (repositoryResponse.Status != HttpStatusCode.OK || metadata?.RootElement.ValueKind != JsonValueKind.Object
                || !metadata.RootElement.TryGetProperty("full_name", out var canonical) || canonical.ValueKind != JsonValueKind.String
                || !string.Equals(canonical.GetString(), owner + "/" + repository, StringComparison.OrdinalIgnoreCase)
                || !metadata.RootElement.TryGetProperty("fork", out var fork) || fork.ValueKind != JsonValueKind.False
                || !metadata.RootElement.TryGetProperty("private", out var privateRepo) || privateRepo.ValueKind != JsonValueKind.False)
            { return new(WorkState.Unknown, "public non-fork pull-request targets could not be proved"); }
            var response = GetJson($"/repos/{owner}/{repository}/pulls?state=open&head={Uri.EscapeDataString(owner + ":" + branch)}&per_page=1");
            using var body = response.Body;
            if (response.Status != HttpStatusCode.OK || body is null || body.RootElement.ValueKind != JsonValueKind.Array)
            { return new(WorkState.Unknown, "open pull requests could not be checked"); }
            return body.RootElement.GetArrayLength() == 0
                ? new(WorkState.Cleared, "no open pull request uses the session's recorded branch")
                : new(WorkState.Refused, "an open pull request uses the session's recorded branch");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or TaskCanceledException)
        { return new(WorkState.Unknown, "open pull requests could not be checked"); }
    }
    WorkClearance CheckUnpushed(string owner, string repository, string branch, string head, bool repositoryProven)
    {
        if (!repositoryProven) { return new(WorkState.Unknown, "the repository identity could not be proved"); }
        try
        {
            var branchResponse = GetJson($"/repos/{owner}/{repository}/branches/{Uri.EscapeDataString(branch)}");
            using var branchBody = branchResponse.Body;
            if (branchResponse.Status == HttpStatusCode.NotFound && repositoryProven)
            { return new(WorkState.Refused, "the session's recorded branch is not present on GitHub"); }
            if (branchResponse.Status != HttpStatusCode.OK || branchBody is null || branchBody.RootElement.ValueKind != JsonValueKind.Object
                || !branchBody.RootElement.TryGetProperty("commit", out var commit) || commit.ValueKind != JsonValueKind.Object
                || !commit.TryGetProperty("sha", out var shaValue) || shaValue.ValueKind != JsonValueKind.String
                || shaValue.GetString() is not { Length: > 0 } remoteHead)
            { return new(WorkState.Unknown, "the pushed branch head could not be checked"); }
            if (string.Equals(head, remoteHead, StringComparison.OrdinalIgnoreCase))
            { return new(WorkState.Cleared, "the session worktree head matches its pushed branch"); }

            var comparison = GetJson($"/repos/{owner}/{repository}/compare/{Uri.EscapeDataString(remoteHead)}...{Uri.EscapeDataString(head)}");
            using var compareBody = comparison.Body;
            if (comparison.Status != HttpStatusCode.OK || compareBody is null || compareBody.RootElement.ValueKind != JsonValueKind.Object
                || !compareBody.RootElement.TryGetProperty("ahead_by", out var ahead) || ahead.ValueKind != JsonValueKind.Number
                || !ahead.TryGetInt32(out var count) || count < 0)
            { return new(WorkState.Unknown, "unpushed commits could not be checked"); }
            return count == 0
                ? new(WorkState.Cleared, "all session worktree commits are on its pushed branch")
                : new(WorkState.Refused, "the session worktree has commits not present on its pushed branch");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException or TaskCanceledException)
        { return new(WorkState.Unknown, "unpushed commits could not be checked"); }
    }
    WorkClearance Marker(string gitDirectory, string name, string reason)
    {
        try
        {
            _ = File.GetAttributes(Path.Combine(gitDirectory, name));
            return new(WorkState.Refused, reason);
        }
        catch (FileNotFoundException) when (Directory.Exists(gitDirectory))
        { return new(WorkState.Cleared, $"no {name} marker is present"); }
        catch (DirectoryNotFoundException)
        { return new(WorkState.Unknown, $"{name} marker could not be checked"); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { return new(WorkState.Unknown, $"{name} marker could not be checked"); }
    }
    (HttpStatusCode Status, JsonDocument? Body) GetJson(string path)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com" + path);
        request.Headers.UserAgent.ParseAdd("AgentBridgeHost/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = client.SendAsync(request).GetAwaiter().GetResult();
        if (!response.IsSuccessStatusCode) { return (response.StatusCode, null); }
        using var stream = response.Content.ReadAsStream();
        return (response.StatusCode, JsonDocument.Parse(stream));
    }

    static bool TryGithubRepository(string remote, out string owner, out string repository)
    {
        owner = repository = "";
        string path;
        if (remote.StartsWith("git@github.com:", StringComparison.OrdinalIgnoreCase))
        { path = remote["git@github.com:".Length..]; }
        else if (Uri.TryCreate(remote, UriKind.Absolute, out var uri)
            && uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && (uri.Scheme is "https" or "ssh") && (uri.UserInfo.Length == 0 || uri.UserInfo == "git")
            && uri.IsDefaultPort && uri.Query.Length == 0 && uri.Fragment.Length == 0)
        { path = uri.AbsolutePath.Trim('/'); }
        else { return false; }
        var parts = path.Split('/');
        if (parts.Length != 2 || parts.Any(p => p.Length == 0 || p is "." or ".." || p.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.'))))
        { return false; }
        owner = parts[0];
        repository = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];
        return repository.Length > 0;
    }
}
