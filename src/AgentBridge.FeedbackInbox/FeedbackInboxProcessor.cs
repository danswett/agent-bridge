using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentBridge.FeedbackInbox;

public sealed class FeedbackInboxOptions
{
    public bool Enabled { get; set; }
    public long MaxBundleBytes { get; set; } = 8L * 1024 * 1024;
    public int MaxNewIssuesPerHour { get; set; } = 10;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMinutes(5);
    public List<string> PublicErrorTypes { get; set; } = [];
    public List<string> PublicStages { get; set; } = [];
}

public sealed record FeedbackMetadata(
    string ErrorType,
    string Stage,
    string? AppVersion,
    string? HostVersion,
    string OperatingSystem,
    string Architecture,
    bool Actionable);

public sealed record DecryptedFeedback(FeedbackMetadata? Metadata, string PrivateContent);

public sealed record FeedbackPrivateRecord(
    string BundleName,
    DateTimeOffset ReceivedAt,
    string? Fingerprint,
    string Summary,
    string? PrivateContent,
    string? OpaqueCiphertextBase64,
    string? UnclassifiedReason);

public sealed record PublicFeedbackIssue(
    string Fingerprint,
    string Title,
    string Body,
    string ErrorType,
    string Stage,
    string? AppVersion,
    string? HostVersion,
    string OperatingSystem,
    string Architecture,
    int? TesterCount,
    IReadOnlyList<string> Labels);

public sealed record PublicFeedbackIssueReference(int Number, string Url);

public interface IEncryptedFeedbackBundleSource
{
    IAsyncEnumerable<string> ListPendingAsync(CancellationToken cancellationToken);
    Task<long?> GetSizeAsync(string bundleName, CancellationToken cancellationToken);
    Task<ReadOnlyMemory<byte>?> ReadAsync(string bundleName, long maximumBytes, CancellationToken cancellationToken);
    Task DeleteAsync(string bundleName, CancellationToken cancellationToken);
}

public interface IFeedbackPrivateKeyProvider
{
    Task<FeedbackPrivateKey> GetAsync(string keyId, CancellationToken cancellationToken);
}

public sealed class FeedbackPrivateKey(byte[] bytes) : IDisposable
{
    readonly byte[] key = bytes;

    public ReadOnlySpan<byte> Bytes => key;

    public void Dispose() => CryptographicOperations.ZeroMemory(key);
}

public interface IFeedbackEnvelopeDecryptor
{
    Task<DecryptedFeedback> DecryptAsync(
        ReadOnlyMemory<byte> ciphertext,
        IFeedbackPrivateKeyProvider keys,
        CancellationToken cancellationToken);
}

public sealed class FeedbackEnvelopeException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

public sealed class FeedbackInboxTransientException(string message, Exception? innerException = null)
    : Exception(message, innerException)
{
}

public interface IFeedbackPrivateStore
{
    // True only when the configured destination is explicitly confirmed private; false or null fails closed.
    Task<bool?> ConfirmRepositoryPrivateAsync(CancellationToken cancellationToken);
    Task UpsertClassifiedAsync(string fingerprint, FeedbackPrivateRecord record, CancellationToken cancellationToken);
    Task StoreUnclassifiedAsync(FeedbackPrivateRecord record, CancellationToken cancellationToken);
    Task LinkPublicIssueAsync(string fingerprint, PublicFeedbackIssueReference issue, CancellationToken cancellationToken);
}

public interface IPublicFeedbackIssueStore
{
    // Returns null when the hourly creation cap is reached; updates do not consume the cap.
    Task<PublicFeedbackIssueReference?> UpsertAsync(
        PublicFeedbackIssue issue,
        int maxNewIssuesPerHour,
        CancellationToken cancellationToken);
}

public sealed class FeedbackInboxProcessor(
    FeedbackInboxOptions options,
    IEncryptedFeedbackBundleSource source,
    IFeedbackPrivateKeyProvider keys,
    IFeedbackEnvelopeDecryptor decryptor,
    IFeedbackPrivateStore privateStore,
    IPublicFeedbackIssueStore publicIssues,
    TimeProvider? timeProvider = null)
{
    static readonly Regex Code = new("^[a-z0-9][a-z0-9._-]{0,47}$", RegexOptions.CultureInvariant);
    static readonly Regex Version = new(
        @"^[0-9]{1,3}(?:\.[0-9]{1,3}){1,3}(?:-(?:alpha|beta|preview)(?:\.[0-9]{1,3})?)?$",
        RegexOptions.CultureInvariant);
    static readonly HashSet<string> OperatingSystems = ["windows", "macos", "ios", "android", "linux"];
    static readonly HashSet<string> Architectures = ["x64", "arm64", "x86", "arm", "wasm"];
    readonly TimeProvider time = timeProvider ?? TimeProvider.System;

    public async Task ProcessAsync(CancellationToken cancellationToken = default)
    {
        if (!options.Enabled) { return; }
        if (options.MaxBundleBytes <= 0 || options.MaxNewIssuesPerHour <= 0 || options.PollInterval <= TimeSpan.Zero)
        {
            throw new InvalidOperationException("Feedback inbox limits must be positive.");
        }
        if (await privateStore.ConfirmRepositoryPrivateAsync(cancellationToken) is not true)
        {
            throw new InvalidOperationException("The private feedback repository is not confirmed private.");
        }

        await foreach (var name in source.ListPendingAsync(cancellationToken))
        {
            var size = await source.GetSizeAsync(name, cancellationToken);
            if (size is null) { continue; }
            if (size <= 0 || size > options.MaxBundleBytes)
            {
                await SaveUnclassifiedAsync(name, "size-limit", null, null, cancellationToken);
                await source.DeleteAsync(name, cancellationToken);
                continue;
            }

            var ciphertext = await source.ReadAsync(name, options.MaxBundleBytes, cancellationToken);
            if (ciphertext is null) { continue; }
            if (ciphertext.Value.Length == 0 || ciphertext.Value.Length > options.MaxBundleBytes)
            {
                await SaveUnclassifiedAsync(name, "size-limit", null, null, cancellationToken);
                await source.DeleteAsync(name, cancellationToken);
                continue;
            }
            if (ciphertext.Value.Length != size)
            {
                await SaveUnclassifiedAsync(name, "size-mismatch", null, ciphertext.Value, cancellationToken);
                await source.DeleteAsync(name, cancellationToken);
                continue;
            }

            DecryptedFeedback decoded;
            try
            {
                decoded = await decryptor.DecryptAsync(ciphertext.Value, keys, cancellationToken);
            }
            catch (Exception exception) when (exception is FeedbackEnvelopeException or CryptographicException)
            {
                await SaveUnclassifiedAsync(name, "invalid-envelope", null, ciphertext.Value, cancellationToken);
                await source.DeleteAsync(name, cancellationToken);
                continue;
            }

            if (!TryNormalize(decoded.Metadata, out var metadata))
            {
                await SaveUnclassifiedAsync(name, "unclassified", decoded.PrivateContent, null, cancellationToken);
                await source.DeleteAsync(name, cancellationToken);
                continue;
            }

            var fingerprint = Fingerprint(metadata);
            var receivedAt = time.GetUtcNow();
            var summary = Summary(metadata);
            var record = new FeedbackPrivateRecord(name, receivedAt, fingerprint, summary, decoded.PrivateContent, null, null);
            await privateStore.UpsertClassifiedAsync(fingerprint, record, cancellationToken);

            if (metadata.Actionable)
            {
                var issue = CreatePublicIssue(metadata, fingerprint);
                var reference = await publicIssues.UpsertAsync(issue, options.MaxNewIssuesPerHour, cancellationToken);
                if (reference is null) { continue; }
                await privateStore.LinkPublicIssueAsync(fingerprint, reference, cancellationToken);
            }

            await source.DeleteAsync(name, cancellationToken);
        }
    }

    async Task SaveUnclassifiedAsync(
        string name,
        string reason,
        string? privateContent,
        ReadOnlyMemory<byte>? ciphertext,
        CancellationToken cancellationToken)
    {
        var record = new FeedbackPrivateRecord(
            name,
            time.GetUtcNow(),
            null,
            "Unclassified beta feedback; public fields could not be safely derived.",
            privateContent,
            ciphertext is null ? null : Convert.ToBase64String(ciphertext.Value.Span),
            reason);
        await privateStore.StoreUnclassifiedAsync(record, cancellationToken);
    }

    bool TryNormalize(FeedbackMetadata? input, out FeedbackMetadata normalized)
    {
        normalized = input!;
        if (input is null) { return false; }
        if (string.IsNullOrWhiteSpace(input.ErrorType) || string.IsNullOrWhiteSpace(input.Stage)
            || string.IsNullOrWhiteSpace(input.OperatingSystem) || string.IsNullOrWhiteSpace(input.Architecture))
        {
            return false;
        }

        var errorType = input.ErrorType.Trim().ToLowerInvariant();
        var stage = input.Stage.Trim().ToLowerInvariant();
        var os = input.OperatingSystem.Trim().ToLowerInvariant();
        var architecture = input.Architecture.Trim().ToLowerInvariant();
        var appVersion = NormalizeVersion(input.AppVersion);
        var hostVersion = NormalizeVersion(input.HostVersion);

        if (!Code.IsMatch(errorType) || !Code.IsMatch(stage)
            || !options.PublicErrorTypes.Contains(errorType, StringComparer.Ordinal)
            || !options.PublicStages.Contains(stage, StringComparer.Ordinal)
            || !OperatingSystems.Contains(os) || !Architectures.Contains(architecture)
            || (appVersion is null && hostVersion is null))
        {
            return false;
        }

        normalized = new FeedbackMetadata(errorType, stage, appVersion, hostVersion, os, architecture, input.Actionable);
        return true;
    }

    static string? NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) { return null; }
        var normalized = version.Trim();
        return Version.IsMatch(normalized) ? normalized : null;
    }

    static string Fingerprint(FeedbackMetadata m)
    {
        var canonical = string.Join('\n', m.ErrorType, m.Stage, m.AppVersion ?? "", m.HostVersion ?? "", m.OperatingSystem, m.Architecture);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    static string Summary(FeedbackMetadata m) =>
        $"Beta feedback reports {m.ErrorType} at {m.Stage} ({m.OperatingSystem}/{m.Architecture}).";

    static PublicFeedbackIssue CreatePublicIssue(FeedbackMetadata m, string fingerprint)
    {
        var prefix = fingerprint[..8];
        var title = $"[beta-feedback] {m.ErrorType} at {m.Stage} ({prefix})";
        var body = string.Join('\n',
            "Automatically filed beta feedback. Private details are stored separately.",
            "",
            $"- Error type: `{m.ErrorType}`",
            $"- Stage: `{m.Stage}`",
            $"- App version: `{m.AppVersion ?? "unknown"}`",
            $"- Host version: `{m.HostVersion ?? "unknown"}`",
            $"- OS: `{m.OperatingSystem}`",
            $"- Architecture: `{m.Architecture}`",
            "- Testers affected: unknown",
            $"- Fingerprint: `{fingerprint}`");
        return new PublicFeedbackIssue(
            fingerprint,
            title,
            body,
            m.ErrorType,
            m.Stage,
            m.AppVersion,
            m.HostVersion,
            m.OperatingSystem,
            m.Architecture,
            null,
            ["beta-feedback", $"fb-kind:{m.ErrorType}"]);
    }
}
