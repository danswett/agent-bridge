using System.Collections.Concurrent;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

public sealed partial class ReplyService
{
    readonly ConcurrentDictionary<string, SemaphoreSlim> replyQueues = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, Task<bool>> recovering = new(StringComparer.Ordinal);

    async Task<ReplyResult?> ClaimIdleAsync(Session session, Guid key)
    {
        // A genuine draft or unsafe mode is still a distinct refusal, not an agent queue.
        if (session.ProcessId is { } pid && await InputBoxAsync(pid) is { Seen: true, Problem: { } problem })
            return Unverified(problem);
        while (true)
        {
            Stopping.ThrowIfCancellationRequested();
            if (probe.Active() is { } active) return Refused(active);
            var now = source.Get(session.Id);
            if (now is null || now.ProcessId != session.ProcessId) return Stale("the session has ended or changed process");
            if (now.Question is { } asking) return Asking(asking);
            if (LaunchHeld?.Invoke(session.Id) is { } held) return Refused(held);
            if (now.Status == SessionStatus.Idle && (now.ProcessId is not { } target || await InputBoxAsync(target) is not { State: { Working: true, Main: true } })
                && gate.TryClaim(session.Id, key)) return null;
            await Task.Delay(options.PollInterval, options.Time, Stopping);
        }
    }

    async Task<bool> RecoverCopilotAsync(Session session, AgentProcess process, string transcript, long from, string words, DeliveryTimeline timeline, bool rollback, Task? workerGone = null)
    {
        var submitted = false;
        long? emptyAt = null;
        try
        {
            if (workerGone is not null) await workerGone.WaitAsync(Stopping);
            while (!Stopping.IsCancellationRequested)
            {
                var recorded = CopilotPrompts.Since(transcript, from).Prompts.Any(p => CopilotReply.Matches(words, p.Content));
                var now = source.Get(session.Id);
                if (now is null || now.ProcessId != session.ProcessId) return recorded;
                if (!SameProcess()) return false;
                if (now.Status == SessionStatus.Idle && now.Question is null)
                {
                    await gate.WaitToTypeAsync();
                    try
                    {
                        if (Proceed() is null)
                        {
                            var (_, _, _, box) = await InputBoxAsync(process.Id);
                            if (box is { Main: true, Complete: true, Content.Length: 0 })
                            {
                                emptyAt ??= options.Time.GetTimestamp();
                                if (!rollback || options.Time.GetElapsedTime(emptyAt.Value) >= options.SubmitAfter) return recorded || !rollback;
                            }
                            else emptyAt = null;
                            if (box is { Main: true, Complete: true, Shell: false, Working: false, Content.Length: > 0 }
                                && Remainder(box.Content, words) is null)
                            { log($"reply recovery to {Short(session.Id)}: draft changed; no recovery keys"); return false; }
                            if (!recorded && box is { Main: true, Complete: true, Shell: false, Working: false, Vim: not CopilotVimMode.Normal }
                                && box.Content.Length > 0 && Remainder(box.Content, words) is { } remainder && (rollback || remainder.Length == 0))
                            {
                                var footer = box.Rows[^1].Row + 1;
                                // Replies have one logical line: Ctrl+E then Ctrl+U removes only
                                // the prefix this failed write owns, never submitting it.
                                var plan = new KeyPlan([new(rollback ? "\u0005\u0015" : "", !rollback, 0, 0, box.Rows,
                                [
                                    new(footer, ScreenCheck.WithAny, CopilotInputBoxReader.FooterSigns),
                                    new(footer, ScreenCheck.WithoutParts, CopilotInputBoxReader.UnsafeFooter),
                                    new(footer, ScreenCheck.WithNone, CopilotInputBoxReader.TurnSigns),
                                    new(footer + 1, ScreenCheck.BlankFrom, []),
                                ])]) { Proceed = Proceed };
                                var result = await injector.InjectAsync(process.Id, plan);
                                log($"reply recovery to {Short(session.Id)}: owned draft {words.Length} characters, {(rollback ? "clear" : "Enter")} {result.Status}");
                                if (result.Status == InjectionStatus.Typed) { if (!rollback) timeline.Typed(false); submitted = true; }
                            }
                            // Never erase or submit somebody else's draft, or type into a dialog.
                        }
                    }
                    finally { gate.DoneTyping(); }
                }
                await Task.Delay(submitted ? options.SubmitAfter : options.PollInterval, options.Time, Stopping);
            }
        }
        catch (OperationCanceledException) when (Stopping.IsCancellationRequested) { }
        catch (Exception e)
        {
            log($"reply recovery to {Short(session.Id)} held after failure: {e.GetType().Name}");
            try { await Task.Delay(Timeout.InfiniteTimeSpan, Stopping); }
            catch (OperationCanceledException) when (Stopping.IsCancellationRequested) { }
        }
        return false;

        bool SameProcess() => processes.GetAgentProcesses(AgentKind.Copilot).Any(p => p.Id == process.Id && p.StartedAt == process.StartedAt);
        string? Proceed() => !Stopping.IsCancellationRequested && SameProcess() && probe.Active() is null
            && LaunchHeld?.Invoke(session.Id) is null && guard.Refuse(process.Id) is null
            && source.Get(session.Id) is { Agent: AgentKind.Copilot, Status: SessionStatus.Idle, Question: null } live && live.ProcessId == process.Id
            && inputs.TryGetValue(transcript, out var input)
            && (input.Read() is { Readable: true, Asking: false, Resumed: false } || NotYetWritten(transcript, live))
                ? null : "the pending reply is not safe to submit";
    }

    static string? Remainder(string content, string words)
    {
        var remaining = InjectableText.Flatten(words);
        foreach (var line in content.Split('\n'))
        {
            var drawn = InjectableText.Flatten(line);
            if (!remaining.StartsWith(drawn, StringComparison.Ordinal)) return null;
            remaining = remaining[drawn.Length..].TrimStart(' ');
        }
        return remaining;
    }
}
