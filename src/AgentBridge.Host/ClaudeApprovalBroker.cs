using System.Text.RegularExpressions;

namespace AgentBridge.Host;

// #1003, the authenticated channel's host side (docs/design/claude-approval-channel.md). A Claude permission prompt is held here IN MEMORY by the
// host; there is no file an agent could write or replay. Only a person's answer that came by the relay-authenticated sessions.answer path ("person:"
// and the device's 32-hex id) resolves it; the local pipe's "person:local", any agent and anything malformed is refused and the prompt keeps waiting.
// An answer is used once, there is no verdict after the deadline, and a command that would be shown cut (over PreviewMax) is never answerable.
// Nothing constructs this yet: no pipe op, no hook, no registration, and the setting stays off.
internal sealed partial class ClaudeApprovalBroker
{
    public const int PreviewMax = 300;
    public enum Result { Accepted, Principal, Unknown, Truncated, Verdict }

    sealed class Pending
    {
        public required TaskCompletionSource<string?> Verdict { get; init; }
        public required bool Answerable { get; init; }
        public ITimer? Timer { get; set; }
    }

    [GeneratedRegex("^person:[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex RelayPerson();

    readonly TimeProvider time;
    readonly TimeSpan wait;
    readonly object gate = new();
    readonly Dictionary<(string Session, string Request), Pending> pending = [];

    public ClaudeApprovalBroker(TimeProvider? time = null, TimeSpan? wait = null)
    {
        this.time = time ?? TimeProvider.System;
        this.wait = wait ?? TimeSpan.FromSeconds(100);
    }

    // The verdict ("allow" or "deny"), or null with no answer by the deadline or when the prompt was withdrawn or registered again.
    public Task<string?> Register(string session, string request, string command)
    {
        var entry = new Pending { Verdict = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously), Answerable = command.Length <= PreviewMax };
        var key = (session, request);
        lock (gate)
        {
            if (pending.Remove(key, out var earlier)) { End(earlier, null); }
            pending[key] = entry;
            entry.Timer = time.CreateTimer(_ => { lock (gate) { if (pending.TryGetValue(key, out var live) && live == entry) { pending.Remove(key); } } End(entry, null); },
                null, wait, Timeout.InfiniteTimeSpan);
        }
        return entry.Verdict.Task;
    }

    public Result Resolve(string principal, string session, string request, string verdict)
    {
        if (!RelayPerson().IsMatch(principal)) { return Result.Principal; }
        lock (gate)
        {
            if (!pending.TryGetValue((session, request), out var entry)) { return Result.Unknown; }
            if (!entry.Answerable) { return Result.Truncated; }
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
