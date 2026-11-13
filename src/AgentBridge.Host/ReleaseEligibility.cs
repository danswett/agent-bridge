using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Core.Updates;
using AgentBridge.Protocol;

namespace AgentBridge.Host;

public enum ReleaseBlockedReason { HaltListUnavailable, Halted, NotNewer, InvalidVersion }
public sealed record HaltCheck(bool? Halted, ReleaseBlockedReason? Reason = null);
public sealed record ReleaseEligibilityResult(bool Allowed, ReleaseBlockedReason? Reason = null);

public interface IHaltListSource
{
    // Bytes must come from the single verified release-policy asset, never a package's
    // cached copy or a missing-file-as-empty fallback.
    Task<byte[]> ReadAsync(CancellationToken cancellationToken);
    IReadOnlyCollection<string> KnownHalts => [];
}

public sealed class ReleaseEligibility(IHaltListSource source)
{
    public async Task<HaltCheck> IsHalted(string version, CancellationToken cancellationToken = default)
    {
        if (!TryRun(version, out _)) { return new(null, ReleaseBlockedReason.InvalidVersion); }
        try
        {
            var halted = ReadHalted(await source.ReadAsync(cancellationToken).ConfigureAwait(false));
            return halted is null ? KnownOrUnavailable(version) : new(halted.Contains(version, StringComparer.Ordinal));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return KnownOrUnavailable(version); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or HttpRequestException or JsonException or InvalidOperationException or KeyNotFoundException or FormatException or CryptographicException or ReleaseDiscoveryException)
        {
            return KnownOrUnavailable(version);
        }
    }

    HaltCheck KnownOrUnavailable(string version) => source.KnownHalts.Contains(version, StringComparer.Ordinal) ? new(true) : Unavailable();

    internal static string[]? ReadHalted(byte[] bytes)
    {
        if (bytes.Length is 0 or > 65_536) { return null; }
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) { return null; }
        var names = root.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != 2 || names.Distinct(StringComparer.Ordinal).Count() != 2
            || !root.TryGetProperty("v", out var v) || !v.TryGetInt32(out var schema) || schema != 1
            || !root.TryGetProperty("halted", out var halted) || halted.ValueKind != JsonValueKind.Array
            || halted.GetArrayLength() > 1024) { return null; }
        List<string> found = [];
        foreach (var item in halted.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } entry || !TryRun(entry, out _)) { return null; }
            found.Add(entry);
        }
        return [.. found];
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

    internal static bool TryRun(string? version, out long run) => ReleaseVersions.TryRun(version, out run);

    static HaltCheck Unavailable() => new(null, ReleaseBlockedReason.HaltListUnavailable);
}

// Verification uses GitHub's HTTPS repository metadata and SHA-256 asset digest, not a new
// signing key or trust in a caller-supplied feed/URL. No log or exception body is exported.
public sealed class GitHubHaltListSource(HttpClient http, ReleaseDiscovery discovery) : IHaltListSource
{
    const string Repository = "danswett/agent-bridge";
    public const string Releases = ReleaseDiscovery.HaltPolicyList;
    public HaltPolicySnapshot Snapshot => discovery.HaltPolicy;
    public IReadOnlyCollection<string> KnownHalts => Snapshot.Policy?.Halted ?? [];

    public async Task<byte[]> ReadAsync(CancellationToken cancellationToken)
    {
        var snapshot = await discovery.ReadHaltPolicyAsync(Verify, cancellationToken).ConfigureAwait(false);
        if (!snapshot.Fresh || snapshot.Policy is null) { throw new IOException("halt policy stale or unavailable"); }
        return Encoding.UTF8.GetBytes(snapshot.Policy.Body);
    }

    async Task<VerifiedHaltPolicy> Verify(string metadata, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        using var doc = JsonDocument.Parse(metadata);
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
        var halted = ReleaseEligibility.ReadHalted(body) ?? throw new IOException("halt policy malformed");
        return new(highest, Encoding.UTF8.GetString(body), halted);
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
