// Bridge Protocol v1, launching: launch.options and session.launch. The contract is
// docs/protocol/v1-launch.md; these records mirror it and serialize to exactly that JSON with
// ProtocolJson.Options.
using System.Text;
using System.Text.RegularExpressions;

namespace AgentBridge.Protocol;

public static partial class LaunchProtocol
{
    public const string Capability = "sessions.launch";
    public const string Options = "launch.options";
    public const string Launch = "session.launch";

    // The opening prompt. One request carries it whole, so it is bounded well inside a
    // message; a longer handover goes as an attachment (Copilot), as at the machine.
    public const int MaxPrompt = 16 * 1024;
    public const int MaxRequest = 20 * 1024;

    // Names a client sends back: an agent id, a workspace label, a session to resume, a
    // profile, and model, effort and context values. Each is matched against what
    // launch.options offered, so these bound only what a host has to read.
    public const int MaxName = 256;

    // What launch.options lists, and how long each entry may be.
    public const int MaxWorkspaces = 64;
    public const int MaxResumable = 50;
    public const int MaxChoices = 64;
    public const int MaxLabel = 120;
    public const int MaxOptionsBytes = 40 * 1024;

    public const string Ask = "ask";
    public const string AllowAll = "allowAll";

    // The launchers a host can start. Agency starts Copilot sessions, with an Agency profile.
    public static readonly IReadOnlyList<string> Agents = ["agency", "copilot", "claude", "codex"];

    // Null when the request can be acted on; otherwise why not, for a refused outcome. Whether
    // the values name something this machine offers is the host's (launch.options).
    public static string? Check(LaunchRequest launch)
    {
        if (launch.Agent is { } agent && !Agents.Contains(agent, StringComparer.Ordinal)) { return $"unknown agent '{Cut(agent)}'; this protocol launches {string.Join(", ", Agents)}"; }
        foreach (var (name, value) in new[] { ("workspace", launch.Workspace), ("resume", launch.Resume), ("profile", launch.Profile), ("model", launch.Model), ("effort", launch.Effort), ("context", launch.Context) })
        {
            if (value is null) { continue; }
            if (Encoding.UTF8.GetByteCount(value) > MaxName) { return $"the {name} is longer than {MaxName} bytes"; }
            if (value.Any(char.IsControl)) { return $"the {name} holds control characters"; }
        }
        // Model, effort and context reach a command line, so they are refused outright unless
        // they look like what an agent names its own: never a space, a quote or a leading dash.
        foreach (var (name, value) in new[] { ("model", launch.Model), ("effort", launch.Effort), ("context", launch.Context) })
        {
            if (value is { Length: > 0 } && !Tuning().IsMatch(value)) { return $"'{Cut(value)}' is not a {name} any agent offers"; }
        }
        if (launch.Resume is not null && launch.Workspace is not null)
        {
            return "a resumed session reopens in its own folder; name a workspace or a session to resume, not both";
        }
        if (launch.Resume is { Length: 0 }) { return "the session to resume is empty"; }
        if (launch.Transfer is { } transfer)
        {
            if (launch.Resume is not null) { return "a transferred session is brought from another machine; name a session to resume or one to transfer, not both"; }
            // No silent fallback to the default workspace: the session's own folder is on the
            // other machine, and where it opens here is the person's choice.
            if (launch.Workspace is null) { return "a session from another machine has no folder here; name the workspace it opens in"; }
            if (TransferProtocol.Check(transfer) is { } bad) { return bad; }
        }
        if (launch.Workspace is { Length: 0 }) { return "the workspace is empty"; }
        if (launch.Permissions is { } p && p is not (Ask or AllowAll)) { return $"unknown permissions '{Cut(p)}'; use '{Ask}' or '{AllowAll}'"; }

        var attachments = launch.Attachments ?? [];
        if (attachments.Count > AttachmentLimits.PerReply) { return $"a launch carries at most {AttachmentLimits.PerReply} attachments"; }
        if (attachments.Any(a => a is null || !Attachments.IsId(a.Id))) { return "each attachment must be the UUID of one sent with attachment.start"; }
        if (attachments.Select(a => a.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != attachments.Count) { return "the same attachment is in the launch twice"; }

        var prompt = launch.Prompt ?? "";
        if (Encoding.UTF8.GetByteCount(prompt) > MaxPrompt) { return $"the prompt is longer than {MaxPrompt / 1024} KiB; send the rest as an attachment or a reply once it has started"; }
        // The agent takes the prompt as its first input, so the rules for a reply hold: one
        // beginning with / or ! would run as a command on the machine.
        var first = prompt.FirstOrDefault(c => !char.IsWhiteSpace(c) && !char.IsControl(c));
        return first switch
        {
            '/' => "a prompt cannot start with /, which the agent would run as a command; type commands at the machine",
            '!' => "a prompt cannot start with !, which the agent would run as a shell command; type commands at the machine",
            _ => null,
        };
    }

    // The prompt as it reaches the agent: every control character, line breaks included, and
    // the Unicode line and paragraph separators become spaces, and the ends are trimmed. A
    // command line holds one line, and an Enter inside it would submit part of the prompt.
    public static string Flatten(string? prompt)
    {
        if (string.IsNullOrEmpty(prompt)) { return ""; }
        var b = new StringBuilder(prompt.Length);
        foreach (var c in prompt) { b.Append(char.IsControl(c) || c is '\u2028' or '\u2029' ? ' ' : c); }
        return b.ToString().Trim();
    }

    static string Cut(string value) => Limits.KeepStart(value, 64)!;

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:\[\]-]{0,127}$")]
    private static partial Regex Tuning();
}

// session.launch. Request is the client's UUID for this press, with session.answer's
// semantics. Absent (or null) settings take the machine's defaults; an empty model, effort or
// context asks for the agent's own default, over the machine's.
public sealed record LaunchRequest
{
    public required string Request { get; init; }
    public string? Agent { get; init; }
    public string? Workspace { get; init; }
    public string? Resume { get; init; }
    public string? Profile { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? Context { get; init; }
    public string? Permissions { get; init; }
    public string? Prompt { get; init; }
    public IReadOnlyList<AttachmentRef>? Attachments { get; init; }
    // With sessions.transfer: a session from another machine, resumed here as one of its own
    // (docs/protocol/v1-transfer.md). Not with resume; needs workspace.
    public LaunchTransfer? Transfer { get; init; }
}

// Session: the session's id, once known (always with started). Launched: what it was started
// with, after the machine's defaults filled what the request left out.
public sealed record LaunchResult(LaunchOutcome Outcome, string? Reason = null)
{
    public string? Session { get; init; }
    public LaunchedWith? Launched { get; init; }
    // With unconfirmed or busy because a launched agent is asking whether to trust its folder:
    // that question (launch.trust).
    public TrustQuestion? Trust { get; init; }
}

// An outcome a client does not know reads as Unconfirmed: something may have started.
[UnknownAs(Unconfirmed)]
public enum LaunchOutcome { Started, Unconfirmed, Busy, Refused, Failed }

public sealed record LaunchedWith
{
    public required string Agent { get; init; }
    public string? Workspace { get; init; }
    public string? Resume { get; init; }
    public string? Cwd { get; init; }
    public bool? Worktree { get; init; }
    public string? Profile { get; init; }
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? Context { get; init; }
    public required string Permissions { get; init; }
    // For a transferred session: where it came from. Resume is its new id here.
    public LaunchedTransfer? Transfer { get; init; }
}

public sealed record LaunchedTransfer
{
    public required string From { get; init; }
    public required string Session { get; init; }
    public required long Bytes { get; init; }
}

// launch.options' result: what session.launch can name on this machine now.
public sealed record LaunchOptions
{
    public IReadOnlyList<LaunchAgent> Agents { get; init; } = [];
    public IReadOnlyList<LaunchWorkspace> Workspaces { get; init; } = [];
    public IReadOnlyList<ResumableSession> Resumable { get; init; } = [];
    public required LaunchDefaults Defaults { get; init; }
    // Set when a launch now would be busy, saying why.
    public string? Busy { get; init; }
    // Folder-trust questions launched agents are waiting on (launch.trust). Absent when none.
    public IReadOnlyList<TrustQuestion>? Trust { get; init; }
}

// One launcher. Models, efforts and contexts are the values it takes; none of them is the
// agent's own default, which a client offers as well and sends as "".
public sealed record LaunchAgent
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required AgentKind Kind { get; init; }
    public IReadOnlyList<string> Models { get; init; } = [];
    public IReadOnlyList<string> Efforts { get; init; } = [];
    public IReadOnlyList<string> Contexts { get; init; } = [];
    public IReadOnlyList<string>? Profiles { get; init; }
    public LaunchTuning? Defaults { get; init; }
    // It takes attachments with the opening prompt (with sessions.attach).
    public bool? Attachments { get; init; }
    // It starts its session only on a first message, so a launch must carry a prompt.
    public bool? NeedsPrompt { get; init; }
}

public sealed record LaunchTuning
{
    public string? Model { get; init; }
    public string? Effort { get; init; }
    public string? Context { get; init; }
    public string? Profile { get; init; }
}

public sealed record LaunchWorkspace
{
    public required string Label { get; init; }
    public required string Path { get; init; }
    // A new session here starts in a git worktree of its own.
    public bool? Isolate { get; init; }
}

public sealed record ResumableSession
{
    public required string Id { get; init; }
    public required AgentKind Agent { get; init; }
    // The launcher that resumes it.
    public required string Launcher { get; init; }
    public required string Title { get; init; }
    public string? Cwd { get; init; }
    public string? Workspace { get; init; }
    public DateTimeOffset? UpdatedAt { get; init; }
}

public sealed record LaunchDefaults
{
    public string? Agent { get; init; }
    public string? Workspace { get; init; }
    public required string Permissions { get; init; }
}
