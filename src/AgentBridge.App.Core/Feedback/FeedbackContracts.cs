namespace AgentBridge.App.Core.Feedback;

// The shared interface of the Windows "Send feedback" flow (#738). S1 (gather and redact) owns it;
// S2 (seal and upload) and S3/S4 (screens, failure states) code against it with fakes.

public static class FeedbackLimits
{
    public const string Schema = "slipgates-feedback.v1";
    public const int MaxNoteChars = 1000;
    // The canonical bytes are what is previewed, sealed and sent. Well under the relay's 8 MiB.
    public const int MaxBundleBytes = 2 * 1024 * 1024;
    public const int MaxScreenshotBytes = 1024 * 1024;
    public const int MaxJournalLines = 200;
    public const int MaxCrashRecords = 5;
    public const int MaxSessionTurns = 20;
    public const int MaxTurnChars = 2000;
    public static readonly TimeSpan GatherTimeout = TimeSpan.FromSeconds(10);
}

// One row of the review screen's "What's included" list, in the order it appears in the bundle.
public sealed record FeedbackItem(string Id, string Label, string Summary, long Bytes, bool Optional);

// The exact bundle. CanonicalBytes is UTF-8 JSON without a byte order mark, two-space indented,
// properties in a fixed order, LF line ends; the preview shows Text and S2 seals CanonicalBytes,
// so what the person read is byte for byte what is sent.
public sealed class FeedbackBundle
{
    readonly byte[] bytes;

    public FeedbackBundle(byte[] canonicalBytes, byte[] bundleId, DateTimeOffset createdAt, IReadOnlyList<FeedbackItem> items)
    {
        if (bundleId.Length != 16) { throw new ArgumentException("A bundle id is 16 bytes.", nameof(bundleId)); }
        bytes = (byte[])canonicalBytes.Clone();
        BundleId = (byte[])bundleId.Clone();
        CreatedAt = createdAt;
        Items = items;
    }

    // The 16 raw random bytes the envelope binds as associated data; base64url in the JSON.
    public byte[] BundleId { get; }
    public DateTimeOffset CreatedAt { get; }
    public IReadOnlyList<FeedbackItem> Items { get; }
    public int Length => bytes.Length;
    public ReadOnlyMemory<byte> CanonicalBytes => bytes;
    public string Text => System.Text.Encoding.UTF8.GetString(bytes);
}

// Typed progress for the gathering state; the screen shows these and nothing else.
public enum FeedbackGatherStage { Versions, Agents, Config, Journal, Crashes, Screenshot, SessionTurns, Done }

// What the person chose on the review screen. Everything optional is off by default.
public sealed record FeedbackRequest
{
    public string Note { get; init; } = "";
    // PNG bytes the person attached; null for none.
    public byte[]? ScreenshotPng { get; init; }
    // The one session whose last turns are attached; null (the default) sends none.
    public string? SessionTurnsFor { get; init; }
}

public enum FeedbackRefusalReason
{
    NoteTooLong,
    ScreenshotTooLarge,
    ScreenshotNotPng,
    SessionNotFound,
    BundleTooLarge,
    // The session source failed (as opposed to the session not existing); appended so existing values keep their order.
    SessionUnavailable,
}

// A source that cannot be read leaves its item out and the bundle says "unavailable", so gathering
// does not fail for it. Cancellation and the overall timeout are the only exceptions the caller sees.
public abstract record FeedbackGatherResult
{
    public sealed record Ready(FeedbackBundle Bundle) : FeedbackGatherResult;
    public sealed record Refused(FeedbackRefusalReason Reason) : FeedbackGatherResult;
}

public interface IFeedbackBundleSource
{
    // Bounded by FeedbackLimits.GatherTimeout (TimeoutException) and FeedbackLimits.MaxBundleBytes (Refused.BundleTooLarge).
    Task<FeedbackGatherResult> GatherAsync(FeedbackRequest request, IProgress<FeedbackGatherStage>? progress, CancellationToken cancellationToken);
}

public enum FeedbackFailureReason
{
    Offline,
    Unreachable,
    RateLimited,
    Quota,
    Unavailable,
    Disabled,
    BadRequest,
    UploadRejected,
    KeyUnavailable,
    Cancelled,
}

public enum FeedbackRetry { Now, AfterDelay, WhenOnline, Never }

public enum FeedbackSendRefusal { NotSignedIn, NoKey, Empty }

public abstract record FeedbackSendResult
{
    public sealed record Sent(DateTimeOffset At) : FeedbackSendResult;
    // Retry says what the Retry button does; After is set only for AfterDelay.
    public sealed record Failed(FeedbackFailureReason Reason, FeedbackRetry Retry, TimeSpan? After = null) : FeedbackSendResult;
    public sealed record TooLarge(long Bytes, long Max) : FeedbackSendResult;
    public sealed record Refused(FeedbackSendRefusal Reason) : FeedbackSendResult;
}

// S2 implements this: seal CanonicalBytes (v1 envelope) and upload write-only to the relay.
public interface IFeedbackSender
{
    Task<FeedbackSendResult> SendAsync(FeedbackBundle bundle, CancellationToken cancellationToken);
}

// What the gatherer reads. Small seams, so the gatherer is testable and the app wires the real
// app info, host config and agents, journal, crash files and sessions.
public sealed record FeedbackEnvironment(
    string AppVersion,
    string Channel,
    string OsDescription,
    string Architecture,
    int ProtocolVersion,
    IReadOnlyList<string> HostVersions);

public sealed record FeedbackAgent(string Name, string? Version);

public sealed record FeedbackSessionTurn(string Role, string Text, DateTimeOffset At);

public interface IFeedbackSources
{
    FeedbackEnvironment Environment();
    Task<IReadOnlyList<FeedbackAgent>> DetectAgentsAsync(CancellationToken cancellationToken);
    // Raw config key/value pairs; the gatherer allow-lists and redacts.
    Task<IReadOnlyDictionary<string, string>> ConfigAsync(CancellationToken cancellationToken);
    // Newest last. Raw lines; the gatherer redacts and bounds them.
    Task<IReadOnlyList<string>> JournalTailAsync(int maxLines, CancellationToken cancellationToken);
    // Newest first. Raw crash text; the gatherer redacts and bounds it.
    Task<IReadOnlyList<string>> CrashRecordsAsync(int max, CancellationToken cancellationToken);
    // Last turns of one session, newest last; null when the session is unknown.
    Task<IReadOnlyList<FeedbackSessionTurn>?> SessionTurnsAsync(string sessionId, int max, CancellationToken cancellationToken);
}
