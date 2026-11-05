using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace AgentBridge.Host;

public enum ReleaseBlockedReason { HaltListUnavailable, Halted, NotNewer, InvalidVersion }
public sealed record HaltCheck(bool? Halted, ReleaseBlockedReason? Reason = null);
public sealed record ReleaseEligibilityResult(bool Allowed, ReleaseBlockedReason? Reason = null);

public interface IHaltListSource
{
    // Bytes must come from the single verified release-policy asset, never a package's
    // cached copy or a missing-file-as-empty fallback.
    Task<byte[]> ReadAsync(CancellationToken cancellationToken);
}

public sealed class ReleaseEligibility(IHaltListSource source)
{
    public async Task<HaltCheck> IsHalted(string version, CancellationToken cancellationToken = default)
    {
        if (!TryRun(version, out _)) { return new(null, ReleaseBlockedReason.InvalidVersion); }
        try
        {
            var bytes = await source.ReadAsync(cancellationToken).ConfigureAwait(false);
            if (bytes.Length is 0 or > 65_536) { return Unavailable(); }
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return Unavailable(); }
            var names = root.EnumerateObject().Select(p => p.Name).ToArray();
            if (names.Length != 2 || names.Distinct(StringComparer.Ordinal).Count() != 2
                || !root.TryGetProperty("v", out var v) || !v.TryGetInt32(out var schema) || schema != 1
                || !root.TryGetProperty("halted", out var halted) || halted.ValueKind != JsonValueKind.Array
                || halted.GetArrayLength() > 1024) { return Unavailable(); }
            var found = false;
            foreach (var item in halted.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } entry || !TryRun(entry, out _)) { return Unavailable(); }
                found |= entry == version;
            }
            return new(found);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or CryptographicException)
        {
            return Unavailable();
        }
    }

    public async Task<ReleaseEligibilityResult> Evaluate(string target, string installed, CancellationToken cancellationToken = default)
    {
        var halt = await IsHalted(target, cancellationToken).ConfigureAwait(false);
        if (halt.Reason is { } why) { return new(false, why); }
        if (halt.Halted == true) { return new(false, ReleaseBlockedReason.Halted); }
        if (!TryRun(installed, out _)) { return new(false, ReleaseBlockedReason.InvalidVersion); }
        return IsNewer(target, installed) ? new(true) : new(false, ReleaseBlockedReason.NotNewer);
    }

    public static bool IsNewer(string target, string installed) =>
        TryRun(target, out var next) && TryRun(installed, out var current) && next > current;

    internal static bool TryRun(string? version, out long run)
    {
        run = 0;
        if (version is null || !version.StartsWith("0.1.", StringComparison.Ordinal)) { return false; }
        var number = version.AsSpan(4);
        return number.Length is > 0 and <= 19 && long.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out run)
            && number.SequenceEqual(run.ToString(CultureInfo.InvariantCulture));
    }

    static HaltCheck Unavailable() => new(null, ReleaseBlockedReason.HaltListUnavailable);
}

// Verification uses GitHub's HTTPS repository metadata and SHA-256 asset digest, not a new
// signing key or trust in a caller-supplied feed/URL. No log or exception body is exported.
public sealed class GitHubHaltListSource(HttpClient http) : IHaltListSource
{
    const string Repository = "danswett/agent-bridge";
    public const string Releases = "https://api.github.com/repos/" + Repository + "/releases?per_page=100";

    public async Task<byte[]> ReadAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var doc = JsonDocument.Parse(await ReadBounded(new Uri(Releases), 1_048_576, timeout.Token).ConfigureAwait(false));
        if (doc.RootElement.ValueKind != JsonValueKind.Array) { throw new IOException("halt metadata unavailable"); }
        JsonElement? latest = null;
        long highest = -1;
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (!release.TryGetProperty("draft", out var draft) || draft.ValueKind != JsonValueKind.False
                || !release.TryGetProperty("tag_name", out var tag) || tag.ValueKind != JsonValueKind.String
                || tag.GetString() is not { } text || !text.StartsWith('v')
                || !ReleaseEligibility.TryRun(text[1..], out var run) || run <= highest) { continue; }
            highest = run;
            latest = release;
        }
        if (latest is not { } current || !current.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        { throw new IOException("halt asset unavailable"); }
        var matching = assets.EnumerateArray().Where(a => a.TryGetProperty("name", out var n) && n.GetString() == "halts.json").ToArray();
        if (matching.Length != 1) { throw new IOException("halt asset unavailable"); }
        var asset = matching[0];
        var expectedUrl = $"https://github.com/{Repository}/releases/download/{current.GetProperty("tag_name").GetString()}/halts.json";
        if (asset.GetProperty("browser_download_url").GetString() != expectedUrl
            || !asset.GetProperty("size").TryGetInt64(out var size) || size is <= 0 or > 65_536
            || asset.GetProperty("digest").GetString() is not { } digest
            || !digest.StartsWith("sha256:", StringComparison.Ordinal) || digest.Length != 71)
        { throw new IOException("halt asset verification unavailable"); }
        var expectedHash = Convert.FromHexString(digest[7..]);
        var body = await ReadBounded(new Uri(expectedUrl), 65_536, timeout.Token).ConfigureAwait(false);
        if (body.Length != size || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(body), expectedHash))
        { throw new IOException("halt asset verification failed"); }
        return body;
    }

    async Task<byte[]> ReadBounded(Uri uri, int maximum, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("AgentBridge", "1.0"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximum) { throw new IOException("halt source too large"); }
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > maximum) { throw new IOException("halt source too large"); }
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }
}
