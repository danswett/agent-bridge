using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBridge.Host.Core.Updates;

// The release halt list (#580, ops/plan.md §6.4): rollout.json, which the owner publishes as the one
// asset of the repository's pinned `rollout` release (ops/tools/release-halt.ps1), and which the
// app's and the host's updaters read before they download or install anything:
//
//   { "halt": ["0.1.1200"] }
//
// A halted version is never installed, even one downloaded before it was halted. Installs never
// downgrade (#451, and the owner's decision on #580: not even from a halted build), so the list
// only ever takes a release away: a machine already on a halted version stays on it until a
// higher release, the revert or the fix, rolls it forward. A list that cannot be fetched or read
// installs nothing new, rather than being taken as "nothing halted". Other fields are ignored, so
// the document can grow; `lastKnownGood`, proposed on #580 and dropped by the owner, means nothing.
//
// Read from where the releases come from: GitHub's download URL for the `rollout` release, or,
// with a feed.txt (a test VM, a private channel), rollout.json beside that feed's
// releases.<channel>.json, so a rehearsal can halt its own releases.
public sealed partial class ReleaseRollout
{
    public const string FileName = "rollout.json";
    public const string Tag = "rollout";
    // Far more than a halt list needs; a bigger answer is not the list.
    public const int MaxBytes = 64 * 1024;

    ReleaseRollout(IReadOnlySet<string> halted) => Halted = halted;

    public IReadOnlySet<string> Halted { get; }

    public bool Halts(string version) => Normalize(version) is { } v && Halted.Contains(v);

    // Where the list for a feed is: feed is feed.txt's content, "" for GitHub's releases. Beside a
    // web feed's releases.<channel>.json, as Velopack's SimpleWebSource finds that (its query, if
    // it has one, kept).
    public static string Location(string feed, string repository)
    {
        if (feed.Length == 0) { return $"{repository.TrimEnd('/')}/releases/download/{Tag}/{FileName}"; }
        if (Uri.TryCreate(feed, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
        {
            var at = new UriBuilder(u);
            at.Path = $"{at.Path.TrimEnd('/')}/{FileName}";
            return at.Uri.AbsoluteUri;
        }
        return Path.Combine(feed, FileName);
    }

    // Where, for a log or an error: never a web feed's query, which may carry a credential.
    static string Shown(string location) =>
        Uri.TryCreate(location, UriKind.Absolute, out var u) && u.Scheme is "http" or "https" && u.Query.Length > 0
            ? u.GetLeftPart(UriPartial.Path)
            : location;

    // The list, or RolloutUnavailableException saying why there is none: nothing is assumed.
    public static ReleaseRollout Parse(string json)
    {
        JsonDocument doc;
        // A byte order mark, as Windows PowerShell writes one, is not part of the document.
        try { doc = JsonDocument.Parse(json.TrimStart('\uFEFF')); }
        catch (JsonException e) { throw new RolloutUnavailableException($"the release halt list ({FileName}) is not JSON: {e.Message}", e); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new RolloutUnavailableException($"the release halt list ({FileName}) is not a JSON object");
            }
            // Required, even empty: a document without it (`{}`, or a misspelt name) is a mistake
            // to stop on, not a list that halts nothing.
            if (!doc.RootElement.TryGetProperty("halt", out var halt) || halt.ValueKind != JsonValueKind.Array)
            {
                throw new RolloutUnavailableException($"the release halt list ({FileName}) has no \"halt\" array");
            }
            var halted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var entry in halt.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.String || Normalize(entry.GetString()!) is not { } v)
                {
                    throw new RolloutUnavailableException($"the release halt list ({FileName}) names something that is not a version: {entry.GetRawText()}");
                }
                halted.Add(v);
            }
            return new ReleaseRollout(halted);
        }
    }

    // Fetched or read now, never from a cache: a halt must reach a machine about to install.
    public static async Task<ReleaseRollout> ReadAsync(string location, HttpClient http, CancellationToken cancellationToken = default)
    {
        string text;
        try
        {
            if (Uri.TryCreate(location, UriKind.Absolute, out var u) && u.Scheme is "http" or "https")
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, u);
                request.Headers.CacheControl = new() { NoCache = true };
                request.Headers.UserAgent.ParseAdd("agent-bridge-updater");
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw new RolloutUnavailableException($"the release halt list could not be fetched from {Shown(location)}: HTTP {(int)response.StatusCode}");
                }
                if (response.Content.Headers.ContentLength > MaxBytes) { throw TooBig(location); }
                await using var body = await response.Content.ReadAsStreamAsync(cancellationToken);
                text = await ReadCappedAsync(body, location, cancellationToken);
            }
            else
            {
                await using var file = new FileStream(location, FileMode.Open, FileAccess.Read, FileShare.Read);
                text = await ReadCappedAsync(file, location, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        // Unreachable, timed out (HttpClient's timeout is a cancellation), missing, unreadable, or
        // not a path at all.
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or OperationCanceledException
            or InvalidOperationException or ArgumentException or NotSupportedException)
        {
            throw new RolloutUnavailableException($"the release halt list could not be read from {Shown(location)}: {e.Message}", e);
        }
        return Parse(text);
    }

    static async Task<string> ReadCappedAsync(Stream stream, string location, CancellationToken cancellationToken)
    {
        var buffer = new byte[MaxBytes + 1];
        var read = 0;
        int n;
        while (read < buffer.Length && (n = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken)) > 0) { read += n; }
        if (read > MaxBytes) { throw TooBig(location); }
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }

    static RolloutUnavailableException TooBig(string location) =>
        new($"the release halt list at {Shown(location)} is larger than {MaxBytes / 1024} KB, so it is not the list");

    // A version as Velopack prints one (0.1.1200), a leading v (the release's tag) allowed; null for
    // anything else.
    static string? Normalize(string version)
    {
        var v = version.Trim();
        if (v.StartsWith('v') || v.StartsWith('V')) { v = v[1..]; }
        return VersionPattern().IsMatch(v) ? v.ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"\A[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}

// The halt list could not be fetched, or was not a list: nothing new is installed (#580).
public sealed class RolloutUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

// The release about to be downloaded or installed is on the halt list (#580).
public sealed class ReleaseHaltedException(string version)
    : InvalidOperationException($"{version} has been halted (withdrawn), so it is not installed; this machine waits for a newer release")
{
    public string Version { get; } = version;
}
