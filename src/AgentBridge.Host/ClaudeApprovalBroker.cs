using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentBridge.Host;

// #1003, the authenticated channel's host side (docs/design/claude-approval-channel.md). A Claude permission prompt is held here IN MEMORY by the
// host; there is no file an agent could write or replay. Only a person's answer that came by the relay-authenticated sessions.answer path ("person:"
// and the device's 32-hex id, nothing after it) resolves it; the local pipe's "person:local", any agent and anything malformed is refused and the
// prompt keeps waiting. An answer is used once; it carries the DIGEST of the command the person was shown and is refused for any other (#958); a live
// prompt is never replaced by a second registration; there is no verdict after the deadline (checked here, not left to the timer); and a command that
// would be shown cut (over PreviewMax) is never answerable. The text passed in is the exact text SHOWN, after any escaping: the broker does no escaping.
// Resolve trusts its CALLER for the principal (the sessions.answer path must pass only the authenticated one) and uses the wall clock (TimeProvider) for the deadline.
// Nothing constructs this yet: no pipe op, no hook, no registration, and the setting stays off.
internal sealed partial class ClaudeApprovalBroker
{
    public const int PreviewMax = 300;
    // What may be held at once (each for up to the wait): a flood of distinct prompts is refused, not held.
    public const int MaxPerSession = 4;
    public const int MaxTotal = 256;
    public enum Result { Accepted, Principal, Unknown, Shown, Truncated, Verdict }

    sealed class Pending
    {
        public required TaskCompletionSource<string?> Verdict { get; init; }
        public required bool Answerable { get; init; }
        public required string Shown { get; init; }
        public required DateTimeOffset Deadline { get; init; }
        public ITimer? Timer { get; set; }
    }

    [GeneratedRegex(@"^person:[0-9a-f]{32}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RelayPerson();

    // The digest an answer must carry: lower-case SHA-256 hex of the exact text the person was shown.
    // Text that is not well-formed UTF-16 (a lone surrogate) is refused, never silently changed to U+FFFD, so two different texts cannot share a digest.
    static readonly UTF8Encoding Strict = new(false, true);
    public static string Digest(string shown)
    {
        try { return Convert.ToHexString(SHA256.HashData(Strict.GetBytes(shown))).ToLowerInvariant(); }
        catch (EncoderFallbackException) { throw new ArgumentException("the text is not well-formed", nameof(shown)); }
    }

    readonly TimeProvider time;
    readonly TimeSpan wait;
    readonly object gate = new();
    readonly Dictionary<(string Session, string Request), Pending> pending = [];

    public ClaudeApprovalBroker(TimeProvider? time = null, TimeSpan? wait = null)
    {
        this.time = time ?? TimeProvider.System;
        this.wait = wait ?? TimeSpan.FromSeconds(100);
        if (this.wait <= TimeSpan.Zero || this.wait == Timeout.InfiniteTimeSpan) { throw new ArgumentOutOfRangeException(nameof(wait), "the wait must be a finite, positive time"); }
    }

    // The verdict ("allow" or "deny"), or null with no answer by the deadline or when the prompt was withdrawn. Null itself (not a task) when a prompt for
    // this session and request is already waiting (never replaced, so the command the person is looking at cannot be swapped under them) or too many are held.
    // A command over PreviewMax is never hashed (it can never be answered). Throws ArgumentException for text that is not well-formed.
    public Task<string?>? Register(string session, string request, string command)
    {
        ArgumentNullException.ThrowIfNull(command);
        var key = (session, request);
        var entry = new Pending
        {
            Verdict = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously),
            Answerable = command.Length <= PreviewMax, Shown = command.Length <= PreviewMax ? Digest(command) : "", Deadline = time.GetUtcNow() + wait,
        };
        lock (gate)
        {
            if (pending.TryGetValue(key, out var live) && time.GetUtcNow() < live.Deadline) { return null; }
            if (pending.Remove(key, out var expired)) { End(expired, null); }
            if (pending.Count >= MaxTotal || pending.Keys.Count(k => k.Session == session) >= MaxPerSession) { return null; }
            entry.Timer = time.CreateTimer(_ => { lock (gate) { if (pending.TryGetValue(key, out var held) && held == entry) { pending.Remove(key); } } End(entry, null); },
                null, wait, Timeout.InfiniteTimeSpan);
            pending[key] = entry;
        }
        return entry.Verdict.Task;
    }

    // `shown` is the digest of the command the person was shown; an answer for any other is refused and does not settle the prompt.
    public Result Resolve(string principal, string session, string request, string shown, string verdict)
    {
        if (!RelayPerson().IsMatch(principal)) { return Result.Principal; }
        lock (gate)
        {
            if (!pending.TryGetValue((session, request), out var entry)) { return Result.Unknown; }
            if (time.GetUtcNow() >= entry.Deadline) { pending.Remove((session, request)); End(entry, null); return Result.Unknown; }
            if (!entry.Answerable) { return Result.Truncated; }
            if (!string.Equals(shown, entry.Shown, StringComparison.Ordinal)) { return Result.Shown; }
            if (verdict is not ("allow" or "deny")) { return Result.Verdict; }
            pending.Remove((session, request));
            End(entry, verdict);
            return Result.Accepted;
        }
    }

    public void Withdraw(string session)
    {
        lock (gate)
        {
            foreach (var key in pending.Keys.Where(k => k.Session == session).ToList())
            {
                pending.Remove(key, out var entry);
                End(entry!, null);
            }
        }
    }

    static void End(Pending entry, string? verdict)
    {
        entry.Timer?.Dispose();
        entry.Verdict.TrySetResult(verdict);
    }
}
