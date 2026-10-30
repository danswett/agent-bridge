// Bridge Protocol v1, session transfer: resuming on one machine a session that ended on
// another. The contract is docs/protocol/v1-transfer.md; these records mirror it and serialize
// to exactly that JSON with ProtocolJson.Options. The signed command's cryptography is in
// AgentBridge.Host (SignedCommands), since it needs the device keys.
using System.Text;
using System.Text.RegularExpressions;

namespace AgentBridge.Protocol;

public static partial class TransferProtocol
{
    // Offered on a channel through the relay by a host that brings sessions in from the
    // account's other machines (session.launch takes a transfer): it has a relay with a blob
    // store, launching is on, and launch.transfer.receive allows it.
    public const string Capability = "sessions.transfer";
    // Offered, on the same terms, by a host that hands its ended sessions to the account's other
    // machines (session.transfer; launch.transfer.serve). A client lists another machine's
    // sessions for a transfer only where it says so.
    public const string ServeCapability = "sessions.transfer.serve";
    // Host to host: one machine asking another for a session's transcript, carrying the
    // command a person's device signed for exactly that.
    public const string Op = "session.transfer";
    public const string Action = "session.transfer";

    // A transcript bundle, compressed, as agent-ha-bridge caps it (newSession.transferMaxBytes):
    // 25 MB by default, and never more than 200 MB (design §9).
    public const long DefaultMaxBytes = 25L * 1024 * 1024;
    public const long MostBytes = 200L * 1024 * 1024;

    // How long a signed command may be carried out for, and how far a clock may be ahead.
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan Skew = TimeSpan.FromMinutes(2);

    // A command's parts are base64url; these bound what a host reads before checking them.
    public const int MaxBody = 2048;
    public const int MaxKey = 96;
    public const int MaxMac = 64;

    public static bool IsDeviceId(string? id) => id is { Length: 32 } && DeviceId().IsMatch(id);

    // Null when a launch's transfer can be acted on; otherwise why not.
    public static string? Check(LaunchTransfer t)
    {
        if (!IsDeviceId(t.From)) { return "transfer.from must be the device id of the machine the session is on"; }
        if (!Guid.TryParseExact(t.Session, "D", out _)) { return "transfer.session must be the UUID of a session on that machine"; }
        return t.Command is null ? "transfer.command is missing: a transfer needs a command signed by the device asking for it" : Check(t.Command);
    }

    public static string? Check(SignedCommand c)
    {
        static bool Part(string? s, int max) => s is { Length: > 0 } && s.Length <= max && s.All(x => char.IsAsciiLetterOrDigit(x) || x is '-' or '_');
        if (!Part(c.Body, MaxBody) || !Part(c.By, MaxKey) || !Part(c.Mac, MaxMac)) { return "the signed command is malformed"; }
        return null;
    }

    [GeneratedRegex("^[0-9a-f]{32}$")]
    private static partial Regex DeviceId();
}

// session.launch's transfer: instead of starting a new session or resuming one of this
// machine's, bring an ended session from another machine of the account and resume it here as
// a session of its own. Workspace names where it opens; the command is what the device asking
// signed for the machine the session is on, carried there unchanged.
public sealed record LaunchTransfer
{
    public required string From { get; init; }
    public required string Session { get; init; }
    public SignedCommand? Command { get; init; }
}

// A command signed by a person's device for one machine (design §5.5): the body's exact bytes,
// base64url, the signing device's public key, and a MAC only that device and that machine can
// make (docs/protocol/v1-transfer.md, "The signed command").
public sealed record SignedCommand
{
    public required string Body { get; init; }
    public required string By { get; init; }
    public required string Mac { get; init; }
}

// What a signed command says, once its MAC checks out.
public sealed record CommandBody
{
    public required int V { get; init; }
    public required string Action { get; init; }
    public required string Request { get; init; }
    public required CommandPrincipal Principal { get; init; }
    public required CommandTarget Target { get; init; }
    // The machine the command is carried out for: here, the one the session goes to.
    public required string To { get; init; }
    public required long IssuedAt { get; init; }
    public required long ExpiresAt { get; init; }
}

public sealed record CommandPrincipal
{
    // "user": a person's device. Agents are a later principal (design §5.6).
    public required string Kind { get; init; }
    public required string Device { get; init; }
}

public sealed record CommandTarget
{
    public required string Machine { get; init; }
    public string? Session { get; init; }
}

// session.transfer's request: the command, nothing else. Everything the source acts on is in it.
public sealed record TransferRequest
{
    public required SignedCommand Command { get; init; }
}

// session.transfer's result: the bundle, sealed into the relay's blob store for the asking
// machine alone (docs/protocol/blobs.md), and what it holds.
public sealed record TransferResult
{
    public required BlobRef Blob { get; init; }
    // The bundle's bytes and SHA-256 (base64url), as for an attachment.
    public required long Size { get; init; }
    public required string Sha256 { get; init; }
    public required AgentKind Agent { get; init; }
    // The launcher that ran it on its machine: Agency's sessions resume in Agency.
    public required string Launcher { get; init; }
    public required string Title { get; init; }
    public required int Files { get; init; }
    // The agent's version there, when it said: Codex has changed how it stores sessions.
    public string? AgentVersion { get; init; }
}

public static class TransferLimits
{
    // Files in a bundle, and the bytes they unpack to, whatever the sender says.
    public const int MaxFiles = 20_000;

    public static string? CheckResult(TransferResult r, long maxBytes)
    {
        if (r.Size <= 0) { return "the bundle is empty"; }
        if (r.Blob is null || r.Blob.Id is not { Length: > 0 and <= AttachmentLimits.BlobId } || r.Blob.Key is null) { return "the bundle names no blob"; }
        if (r.Size > maxBytes) { return $"the bundle is {Mib(r.Size)}, over the {Mib(maxBytes)} this machine takes"; }
        if (Attachments.Sha256(r.Sha256) is null) { return "the bundle's sha256 is malformed"; }
        if (r.Files is <= 0 or > MaxFiles) { return "the bundle's file count is out of range"; }
        if (!LaunchProtocol.Agents.Contains(r.Launcher, StringComparer.Ordinal)) { return "the bundle names an unknown launcher"; }
        if (r.Title is null || Encoding.UTF8.GetByteCount(r.Title) > LaunchProtocol.MaxLabel * 4) { return "the bundle's title is malformed"; }
        return null;
    }

    public static string Mib(long bytes) => bytes >= 1024 * 1024
        ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.#} MiB")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Math.Ceiling(bytes / 1024.0)} KiB");
}
