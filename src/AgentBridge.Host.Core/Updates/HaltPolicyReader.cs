using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Core.Updates;

public sealed record HaltPolicyRelease(string Tag, string Channel, bool Prerelease, string Feed, string Sha256, long Size);
public sealed record HaltPolicyEnvelope(long Sequence, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt,
    IReadOnlyList<string> Halted, IReadOnlyList<HaltPolicyRelease> Releases, string BodyHash)
{ public byte[] Body { get; init; } = []; }

// Every failure is unavailable protection, never an empty checked policy; the reason is for logs.
public sealed class HaltPolicyUnavailableException(string reason, Exception? inner = null) : IOException(reason, inner);

// What the platform trust check found for the catalog: on Windows, WinVerifyTrust's chain with
// revocation, the timestamp and whether the catalog lists the policy's SHA-256. A seam, so tests
// and Linux CI use fixture certificates; the pin itself is checked here, the same everywhere.
public sealed record HaltCatalogEvidence(bool ChainTrusted, bool Timestamped, bool CoversBody, X509Certificate2? Signer);
public interface IHaltCatalogVerifier { HaltCatalogEvidence Inspect(byte[] catalog, byte[] body); }

// The signed halt policy (docs/design/halt-policy-without-github-api.md): the fixed anchor
// release's JSON and its Authenticode catalog, by browser download, never api.github.com. This
// reads and verifies one pair; freshness against a clock and durable state are the caller's.
public static partial class HaltPolicyReader
{
    public const string Anchor = ReleaseDiscovery.DownloadPrefix + "halt-policy-v1/";
    public const string JsonUrl = Anchor + "halt-policy.json", CatalogUrl = Anchor + "halt-policy.cat";
    public const int JsonLargest = 64 * 1024, CatalogLargest = 256 * 1024;
    const int HaltsLargest = 1024, ReleasesLargest = 4, FeedLargest = 4 * 1024 * 1024, Hops = 3;
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10), LongestValidity = TimeSpan.FromHours(6);
    // The same pin as the app's executables (build/app-signing.ps1): Public Trust's EKU is in
    // anyone's Artifact Signing certificate; only the identity validation EKU names the owner.
    const string PublicTrustEku = "1.3.6.1.4.1.311.97.1.0", CodeSigningEku = "1.3.6.1.5.5.7.3.3";
    // A release download answers with a redirect to GitHub's asset store; nothing else is followed.
    static readonly string[] AssetHosts = ["release-assets.githubusercontent.com", "objects.githubusercontent.com"];
    static readonly string[] Channels = ["win", "win-arm64"];
    static readonly string[] Fields = ["v", "repository", "purpose", "sequence", "issuedAt", "expiresAt", "halted", "releases"];
    static readonly string[] ReleaseFields = ["tag", "channel", "prerelease", "feed", "sha256", "size"];
    static readonly HttpMessageHandler Direct = new SocketsHttpHandler { AllowAutoRedirect = false };

    public static async Task<HaltPolicyEnvelope> ReadAsync(IHaltCatalogVerifier verifier, string identityEku,
        HttpMessageHandler? handler = null, TimeProvider? time = null, CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient(handler ?? Direct, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AgentBridge-updater");
        var body = await FetchAsync(http, JsonUrl, JsonLargest, time ?? TimeProvider.System, cancellationToken).ConfigureAwait(false);
        var catalog = await FetchAsync(http, CatalogUrl, CatalogLargest, time ?? TimeProvider.System, cancellationToken).ConfigureAwait(false);
        HaltCatalogEvidence evidence;
        try { evidence = verifier.Inspect(catalog, body) ?? throw new HaltPolicyUnavailableException("catalog verifier gave no evidence"); }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested) { throw new HaltPolicyUnavailableException("halt policy catalog unverifiable", e); }
        RequirePin(evidence, identityEku);
        return Parse(body);
    }

    public static void RequirePin(HaltCatalogEvidence evidence, string identityEku)
    {
        var ekus = evidence.Signer?.Extensions.OfType<X509EnhancedKeyUsageExtension>()
            .SelectMany(e => e.EnhancedKeyUsages.Cast<Oid>()).Select(o => o.Value).ToHashSet(StringComparer.Ordinal) ?? [];
        var problem = !IdentityEku().IsMatch(identityEku ?? "") ? "no owner identity EKU is pinned"
            : !evidence.ChainTrusted ? "catalog chain untrusted"
            : !evidence.CoversBody ? "catalog does not cover the policy"
            : !evidence.Timestamped ? "catalog not timestamped"
            : !ekus.Contains(CodeSigningEku) || !ekus.Contains(PublicTrustEku) ? "catalog signer lacks the required EKUs"
            : !ekus.Contains(identityEku) ? "catalog signer is not the pinned owner" : null;
        if (problem is not null) { throw new HaltPolicyUnavailableException(problem); }
    }

    public static HaltPolicyEnvelope Parse(byte[] body)
    {
        if (body.Length is 0 or > JsonLargest) { throw new HaltPolicyUnavailableException("halt policy size out of bounds"); }
        try
        {
            using var doc = JsonDocument.Parse(body, new JsonDocumentOptions { MaxDepth = 3, AllowDuplicateProperties = false });
            var root = Exactly(doc.RootElement, Fields);
            if (root.GetProperty("v").GetInt32() != 1 || Text(root, "repository") != "danswett/agent-bridge" || Text(root, "purpose") != "update-halt-policy")
            { throw new HaltPolicyUnavailableException("not this repository's halt policy v1"); }
            var sequence = root.GetProperty("sequence").GetInt64();
            var (issued, expires) = (Time(root, "issuedAt"), Time(root, "expiresAt"));
            if (sequence < 1 || expires <= issued || expires - issued > LongestValidity)
            { throw new HaltPolicyUnavailableException("halt policy sequence or validity invalid"); }
            string[] halted = [.. Items(root, "halted", HaltsLargest).Select(h => h.GetString() is { } v && ReleaseVersions.TryRun(v, out _) ? v : throw new FormatException())];
            HaltPolicyRelease[] releases = [.. Items(root, "releases", ReleasesLargest).Select(Release)];
            if (halted.Distinct(StringComparer.Ordinal).Count() != halted.Length || releases.DistinctBy(r => (r.Channel, r.Prerelease)).Count() != releases.Length)
            { throw new HaltPolicyUnavailableException("halt policy repeats an entry"); }
            return new(sequence, issued, expires, halted, releases, Convert.ToHexStringLower(SHA256.HashData(body))) { Body = body };
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException or KeyNotFoundException)
        { throw new HaltPolicyUnavailableException("halt policy malformed", e); }
    }

    static HaltPolicyRelease Release(JsonElement e)
    {
        Exactly(e, ReleaseFields);
        var (tag, channel, feed, sha) = (Text(e, "tag"), Text(e, "channel"), Text(e, "feed"), Text(e, "sha256"));
        var size = e.GetProperty("size").GetInt64();
        // Exact equality refuses another repository, userinfo, queries and plain HTTP at once.
        if (!tag.StartsWith('v') || !ReleaseVersions.TryRun(tag[1..], out _) || !Channels.Contains(channel, StringComparer.Ordinal)
            || feed != $"{ReleaseDiscovery.DownloadPrefix}{tag}/releases.{channel}.json"
            || sha.Length != 64 || !sha.All(char.IsAsciiHexDigitLower) || size is < 1 or > FeedLargest)
        { throw new HaltPolicyUnavailableException("halt policy release descriptor invalid"); }
        return new(tag, channel, e.GetProperty("prerelease").GetBoolean(), feed, sha, size);
    }

    static JsonElement Exactly(JsonElement e, string[] fields) =>
        e.ValueKind == JsonValueKind.Object && e.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal).SequenceEqual(fields.Order(StringComparer.Ordinal))
            ? e : throw new HaltPolicyUnavailableException("halt policy fields are not v1's");

    static string Text(JsonElement e, string name) => e.GetProperty(name).GetString() ?? throw new FormatException();

    static JsonElement[] Items(JsonElement e, string name, int largest) =>
        e.GetProperty(name) is var items && items.GetArrayLength() <= largest ? [.. items.EnumerateArray()]
            : throw new HaltPolicyUnavailableException($"halt policy {name} over its limit");

    static DateTimeOffset Time(JsonElement e, string name) => DateTimeOffset.ParseExact(Text(e, name), "yyyy-MM-dd'T'HH:mm:ss'Z'",
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    static async Task<byte[]> FetchAsync(HttpClient http, string url, int largest, TimeProvider time, CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(Deadline, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        try
        {
            var uri = new Uri(url);
            for (var hop = 0; hop <= Hops; hop++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, linked.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is >= 300 and < 400 && response.Headers.Location is { } next)
                {
                    uri = next.IsAbsoluteUri ? next : new Uri(uri, next);
                    if (uri.Scheme != Uri.UriSchemeHttps || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || !AssetHosts.Contains(uri.Host, StringComparer.OrdinalIgnoreCase))
                    { throw new HaltPolicyUnavailableException("halt policy redirected off the release asset store"); }
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > largest)
                { throw new HaltPolicyUnavailableException($"halt policy asset unavailable ({(int)response.StatusCode})"); }
                using var stream = await response.Content.ReadAsStreamAsync(linked.Token).ConfigureAwait(false);
                using var output = new MemoryStream();
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer, linked.Token).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + read > largest) { throw new HaltPolicyUnavailableException("halt policy asset over its cap"); }
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
            throw new HaltPolicyUnavailableException("halt policy redirected too often");
        }
        catch (OperationCanceledException e) when (!cancellationToken.IsCancellationRequested)
        { throw new HaltPolicyUnavailableException("halt policy download passed its deadline", e); }
        catch (Exception e) when (e is not OperationCanceledException and not HaltPolicyUnavailableException)
        { throw new HaltPolicyUnavailableException("halt policy download failed", e); }
    }

    [GeneratedRegex(@"^1\.3\.6\.1\.4\.1\.311\.97(\.[0-9]+){4}$")]
    private static partial Regex IdentityEku();
}
