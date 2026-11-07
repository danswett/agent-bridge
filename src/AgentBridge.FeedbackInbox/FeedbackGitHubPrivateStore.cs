using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBridge.FeedbackInbox;

public interface IFeedbackGitHubTokenProvider
{
    Task<string> GetAsync(CancellationToken cancellationToken);
}

public sealed class FeedbackGitHubPrivateStore : IFeedbackPrivateStore, IDisposable
{
    const string Repository = "danswett/slipgates-feedback";
    const string IssuePath = "repos/" + Repository + "/issues";
    const int ChunkSize = 48000;
    static readonly Regex FingerprintPattern = new(@"\A[0-9a-f]{64}\z", RegexOptions.CultureInvariant);
    readonly IFeedbackGitHubTokenProvider tokens;
    readonly HttpClient http;

    public FeedbackGitHubPrivateStore(IFeedbackGitHubTokenProvider tokens)
        : this(tokens, new HttpClientHandler { AllowAutoRedirect = false }) { }

    internal FeedbackGitHubPrivateStore(IFeedbackGitHubTokenProvider tokens, HttpMessageHandler handler)
    {
        this.tokens = tokens;
        http = new HttpClient(handler) { BaseAddress = new Uri("https://api.github.com/") };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("slipgates-feedback-inbox");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<bool?> ConfirmRepositoryPrivateAsync(CancellationToken cancellationToken)
    {
        using var response = await SendAsync(HttpMethod.Get, "repos/" + Repository, null, cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized)
        {
            return null;
        }
        await RequireSuccessAsync(response);
        using var json = await ReadJsonAsync(response, cancellationToken);
        var root = json.RootElement;
        if (!root.TryGetProperty("full_name", out var name) || name.ValueKind != JsonValueKind.String
            || !string.Equals(name.GetString(), Repository, StringComparison.OrdinalIgnoreCase)
            || !root.TryGetProperty("private", out var isPrivate)
            || isPrivate.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return null;
        }
        return isPrivate.GetBoolean();
    }

    public Task UpsertClassifiedAsync(string fingerprint, FeedbackPrivateRecord record, CancellationToken cancellationToken)
    {
        RequireFingerprint(fingerprint);
        return StoreAsync("group-" + fingerprint, record, cancellationToken);
    }

    public Task StoreUnclassifiedAsync(FeedbackPrivateRecord record, CancellationToken cancellationToken) =>
        StoreAsync("unclassified-" + Hash(record.BundleName), record, cancellationToken);

    public async Task LinkPublicIssueAsync(
        string fingerprint, PublicFeedbackIssueReference issue, CancellationToken cancellationToken)
    {
        RequireFingerprint(fingerprint);
        if (issue.Number <= 0) { throw new ArgumentOutOfRangeException(nameof(issue)); }
        await EnsurePrivateAsync(cancellationToken);
        var issueNumber = await FindIssueAsync("group-" + fingerprint, cancellationToken)
            ?? throw new FeedbackInboxTransientException("The private feedback group is unavailable.");
        var marker = $"<!-- slipgates-public-issue:{issue.Number} -->";
        var comments = await ListCommentsAsync(issueNumber, cancellationToken);
        if (comments.Any(body => body.StartsWith(marker, StringComparison.Ordinal))) { return; }
        await PostCommentAsync(issueNumber,
            $"{marker}\nPublic issue: https://github.com/danswett/agent-bridge/issues/{issue.Number}", cancellationToken);
    }

    async Task StoreAsync(string group, FeedbackPrivateRecord record, CancellationToken cancellationToken)
    {
        await EnsurePrivateAsync(cancellationToken);
        var number = await FindIssueAsync(group, cancellationToken)
            ?? await CreateIssueAsync(group, cancellationToken);
        var bundleHash = Hash(record.BundleName);
        var complete = $"<!-- slipgates-bundle-complete:{bundleHash} -->";
        var comments = await ListCommentsAsync(number, cancellationToken);
        if (comments.Any(body => body.StartsWith(complete, StringComparison.Ordinal))) { return; }

        var receivedPrefix = $"<!-- slipgates-bundle-received:{bundleHash}:";
        var received = comments.Where(body => body.StartsWith(receivedPrefix, StringComparison.Ordinal)).ToArray();
        if (received.Length > 1) { throw new InvalidOperationException("Duplicate private feedback bundle metadata requires reconciliation."); }
        long receivedTicks;
        if (received.Length == 0)
        {
            receivedTicks = record.ReceivedAt.UtcTicks;
            await PostCommentAsync(number, $"{receivedPrefix}{receivedTicks.ToString(CultureInfo.InvariantCulture)} -->", cancellationToken);
        }
        else
        {
            var marker = received[0];
            if (!marker.EndsWith(" -->", StringComparison.Ordinal)
                || !long.TryParse(marker.AsSpan(receivedPrefix.Length, marker.Length - receivedPrefix.Length - 4),
                    NumberStyles.None, CultureInfo.InvariantCulture, out receivedTicks)
                || receivedTicks < 0 || receivedTicks > DateTimeOffset.MaxValue.UtcTicks)
            {
                throw new InvalidOperationException("Private feedback bundle metadata requires reconciliation.");
            }
        }
        var json = JsonSerializer.Serialize(record with { ReceivedAt = new DateTimeOffset(receivedTicks, TimeSpan.Zero) });
        var recordHash = Hash(json);
        var chunks = (json.Length + ChunkSize - 1) / ChunkSize;
        for (var index = 0; index < chunks; index++)
        {
            var marker = $"<!-- slipgates-bundle-part:{bundleHash}:{recordHash}:{index + 1}:{chunks} -->";
            if (comments.Any(body => body.StartsWith(marker, StringComparison.Ordinal))) { continue; }
            var part = json.Substring(index * ChunkSize, Math.Min(ChunkSize, json.Length - index * ChunkSize));
            await PostCommentAsync(number, $"{marker}\n\n    {part}", cancellationToken);
        }
        await PostCommentAsync(number, complete, cancellationToken);
    }

    async Task EnsurePrivateAsync(CancellationToken cancellationToken)
    {
        var isPrivate = await ConfirmRepositoryPrivateAsync(cancellationToken);
        if (isPrivate is false) { throw new FeedbackInboxRepositoryNotPrivateException(); }
        if (isPrivate is null) { throw new FeedbackInboxTransientException("Private feedback repository status is unavailable."); }
    }

    async Task<int?> FindIssueAsync(string group, CancellationToken cancellationToken)
    {
        var marker = $"<!-- slipgates-feedback-group:{group} -->";
        int? found = null;
        for (var page = 1; ; page++)
        {
            using var response = await SendAsync(HttpMethod.Get, $"{IssuePath}?state=all&per_page=100&page={page}", null, cancellationToken);
            await RequireSuccessAsync(response);
            using var json = await ReadJsonAsync(response, cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Array) { throw InvalidResponse(); }
            var count = 0;
            foreach (var item in json.RootElement.EnumerateArray())
            {
                count++;
                if (item.TryGetProperty("pull_request", out _)) { continue; }
                if (!item.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.String
                    || !body.GetString()!.StartsWith(marker, StringComparison.Ordinal)) { continue; }
                if (!item.TryGetProperty("number", out var number) || !number.TryGetInt32(out var value) || value <= 0)
                {
                    throw InvalidResponse();
                }
                if (found is not null) { throw new InvalidOperationException("Duplicate private feedback groups require reconciliation."); }
                found = value;
            }
            if (count < 100) { return found; }
        }
    }

    async Task<int> CreateIssueAsync(string group, CancellationToken cancellationToken)
    {
        await EnsurePrivateAsync(cancellationToken);
        using var response = await SendAsync(HttpMethod.Post, IssuePath,
            new { title = "[feedback] " + group, body = $"<!-- slipgates-feedback-group:{group} -->\nPrivate feedback." },
            cancellationToken);
        await RequireSuccessAsync(response);
        using var json = await ReadJsonAsync(response, cancellationToken);
        if (!json.RootElement.TryGetProperty("number", out var number) || !number.TryGetInt32(out var value) || value <= 0)
        {
            throw InvalidResponse();
        }
        return value;
    }

    async Task<List<string>> ListCommentsAsync(int number, CancellationToken cancellationToken)
    {
        var comments = new List<string>();
        for (var page = 1; ; page++)
        {
            using var response = await SendAsync(HttpMethod.Get, $"{IssuePath}/{number}/comments?per_page=100&page={page}", null, cancellationToken);
            await RequireSuccessAsync(response);
            using var json = await ReadJsonAsync(response, cancellationToken);
            if (json.RootElement.ValueKind != JsonValueKind.Array) { throw InvalidResponse(); }
            var count = 0;
            foreach (var item in json.RootElement.EnumerateArray())
            {
                count++;
                if (!item.TryGetProperty("body", out var body) || body.ValueKind != JsonValueKind.String) { throw InvalidResponse(); }
                comments.Add(body.GetString()!);
            }
            if (count < 100) { return comments; }
        }
    }

    async Task PostCommentAsync(int number, string body, CancellationToken cancellationToken)
    {
        await EnsurePrivateAsync(cancellationToken);
        using var response = await SendAsync(HttpMethod.Post, $"{IssuePath}/{number}/comments", new { body }, cancellationToken);
        await RequireSuccessAsync(response);
    }

    async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var token = await tokens.GetAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(token)) { throw new FeedbackInboxTransientException("Private feedback credentials are unavailable."); }
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) { request.Content = JsonContent.Create(body); }
        try { return await http.SendAsync(request, cancellationToken); }
        catch (HttpRequestException) { throw new FeedbackInboxTransientException("Private feedback service is unavailable."); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new FeedbackInboxTransientException("Private feedback service timed out.");
        }
    }

    static Task RequireSuccessAsync(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) { return Task.CompletedTask; }
        if ((int)response.StatusCode is 403 or 408 or 429 || (int)response.StatusCode >= 500)
        {
            throw new FeedbackInboxTransientException("Private feedback service is unavailable.");
        }
        throw new InvalidOperationException($"Private feedback service rejected a request (HTTP {(int)response.StatusCode}).");
    }

    static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException) { throw InvalidResponse(); }
    }

    static FeedbackInboxTransientException InvalidResponse() => new("Private feedback service returned an invalid response.");
    static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static void RequireFingerprint(string fingerprint)
    {
        if (!FingerprintPattern.IsMatch(fingerprint)) { throw new ArgumentException("Invalid feedback fingerprint.", nameof(fingerprint)); }
    }

    public void Dispose() => http.Dispose();
}
