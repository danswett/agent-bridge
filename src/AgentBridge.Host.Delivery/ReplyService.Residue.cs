using System.Collections.Concurrent;
using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

// A reply this host typed into Copilot's box but never sent (its Enter held back, or not taken)
// left every later reply refused as "something is already typed" until a person cleared it at
// the machine (#2144). Words the box holds that are exactly the host's own, typed into the same
// process, are cleared and the reply goes on; anything a person may have typed is never touched.
public sealed partial class ReplyService
{
    // What this host last typed into each Copilot session's box: the process (its pid and start
    // time, read before the keys: a later process given the same pid is not it), the words, when,
    // and whether that reply's confirmation is still running (its Enter may yet be taken). In
    // memory only, so a restarted host has none and refuses as before. The words are never logged.
    sealed record Injected(int Pid, SessionProcess? Process, string Words, DateTimeOffset At, bool Confirming);
    readonly ConcurrentDictionary<string, Injected> injected = new(StringComparer.Ordinal);

    // The words a reply typed, kept until a confirmation shows they were sent.
    Injected Remember(string sessionId, int pid, SessionProcess? process, string words, bool confirming) =>
        injected[sessionId] = new Injected(pid, process, words, options.Time.GetUtcNow(), confirming);

    async Task<ReplyResult> SettleAsync(string sessionId, Injected typed, Func<Task<ReplyResult>> confirm)
    {
        var delivered = false;
        try
        {
            var result = await confirm();
            delivered = result.Outcome == AnswerOutcome.Delivered;
            return result;
        }
        finally
        {
            if (delivered) { injected.TryRemove(KeyValuePair.Create(sessionId, typed)); }
            else { injected.TryUpdate(sessionId, typed with { Confirming = false }, typed); }
        }
    }

    // The record of what the box holds, when that is exactly this host's unsent words in this
    // process, and the box is whole, the prompt's own, and in neither shell nor vim's normal mode.
    // A process whose start time was not known, or is not the one typed into, is never this one.
    Injected? Residue(string sessionId, int pid, CopilotInputBoxState? state) =>
        injected.TryGetValue(sessionId, out var typed) && typed is { Confirming: false, Process.StartedAt: not null } && typed.Pid == pid
        && typed.Process.Id == pid && source.ProcessOf(sessionId) == typed.Process
        && state is { Complete: true, Main: true, Shell: false, Vim: CopilotVimMode.None or CopilotVimMode.Insert }
        && ReadsAs(typed.Words, state.Content) ? typed : null;

    // The reader trims each drawn row's ASCII spaces and joins rows with '\n', so words wrapped
    // across rows read as their rows with the spaces at each wrap gone. Any other difference, a
    // character added, removed or changed anywhere, is not the host's words.
    static bool ReadsAs(string words, string content)
    {
        var rows = content.Split('\n');
        var at = 0;
        for (var i = 0; i < rows.Length; i++)
        {
            if (rows[i].Length == 0 || !words.AsSpan(at).StartsWith(rows[i], StringComparison.Ordinal)) { return false; }
            at += rows[i].Length;
            if (i < rows.Length - 1) { while (at < words.Length && words[at] == ' ') { at++; } }
        }
        return at == words.Length;
    }

    // Clears the host's own unsent words with the guarded clear, while the session is still the
    // process they were typed into, idle, the retry still current and nothing changed, and logs it
    // (session, length, age; never the words). Refused before any key (a turn running, a
    // question), it gives no reason, so the reply keeps today's refusal. The fence it returns holds
    // the reply's own keys to the same, since nothing else in a plain request rechecks them.
    async Task<(CopilotInputBox? Box, string? Problem, Func<string?>? Fence)> ClearResidueAsync(Session session, int pid,
        CopilotInput input, CopilotInputBoxState state, Injected residue, Func<string?> freshness)
    {
        const string Changed = "the session changed before this host's unsent words could be cleared; nothing was typed";
        string? Idle() => freshness() ?? (StillIdle(session, pid, residue.Process, input) ? null : Changed);
        string? Fence() => Idle() ?? (injected.TryGetValue(session.Id, out var now) && now == residue ? null : Changed);
        if (Fence() is { } refused) { return (null, refused == Changed ? null : refused, null); }
        InjectionResult result;
        try { result = await injector.InjectAsync(pid, ClearKeys(state, Fence)); }
        catch (Exception e) { return (null, Logged($"the injector failed while clearing this host's unsent words from {Short(session.Id)}", e), null); }
        if (result.Status == InjectionStatus.NothingTyped) { return (null, "the composer changed before this host's unsent words were cleared; nothing was sent", null); }
        injected.TryRemove(KeyValuePair.Create(session.Id, residue));
        var age = (options.Time.GetUtcNow() - residue.At).TotalSeconds;
        log(FormattableString.Invariant($"residue clear in {Short(session.Id)} (pid {pid}): {residue.Words.Length} characters this host typed {age:0} s ago, never sent and alone in the box, cleared before a new reply -> {result.Status}"));
        if (result.Status != InjectionStatus.Typed) { return (null, "the composer did not finish clearing this host's unsent words; nothing was sent", null); }
        var (box, problem) = await AwaitEmptyAsync(pid, Idle);
        return (box, problem, box is null ? null : Idle);
    }
}
