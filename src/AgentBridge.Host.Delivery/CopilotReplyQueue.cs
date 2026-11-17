using System.Text.Json;
using AgentBridge.Host.Core;
using AgentBridge.Host.Core.Processes;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

public sealed partial class ReplyService
{
    readonly Lock queueLock = new();
    readonly Dictionary<string, ReplyQueue> replyQueues = new(StringComparer.Ordinal);
    internal int QueuedSessions { get { lock (queueLock) return replyQueues.Count; } }

    sealed class ReplyQueue
    {
        public readonly SemaphoreSlim Turn = new(1, 1);
        public readonly Dictionary<string, QueueReceipt> Pending = [];
        public Func<DeliveryTimeline, CancellationToken, Task<bool>>? Cleanup;
        public int Users;
    }

    sealed class QueueReceipt
    {
        public readonly TaskCompletionSource<ReplyResult> Result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public DateTimeOffset? Expires;
    }

    async Task<ReplyResult> QueueAsync(ReplyRequest request, Guid key, string peer, DeliveryTimeline timeline, CancellationToken cancellation)
    {
        if (Replies.Check(request) is { } invalid) return Refused(invalid);
        if (request.RequireIdle || request.StillCurrent is not null || source.Get(request.Id)?.Agent != AgentKind.Copilot)
            return await DeliverAsync(request, key, peer, timeline);
        var fingerprint = InjectionResult.Fingerprint(JsonSerializer.Serialize(
            new { peer, request.Id, request.Text, request.Attachments, request.Mode }));
        ReplyQueue queue;
        QueueReceipt receipt;
        bool duplicate;
        lock (queueLock)
        {
            foreach (var pair in replyQueues.ToArray())
            {
                foreach (var entry in pair.Value.Pending.Where(p => p.Value.Expires <= options.Time.GetUtcNow()).ToArray())
                    pair.Value.Pending.Remove(entry.Key);
                if (pair.Value.Users == 0 && pair.Value.Pending.Count == 0
                    && (pair.Value.Cleanup is null || source.Get(pair.Key) is null)) replyQueues.Remove(pair.Key);
            }
            if (!replyQueues.TryGetValue(request.Id, out queue!))
            {
                if (replyQueues.Count >= options.QueueSessions) return Busy();
                replyQueues.Add(request.Id, queue = new());
            }
            duplicate = queue.Pending.TryGetValue(fingerprint, out receipt!);
            if (!duplicate)
            {
                if (queue.Users >= options.QueueCapacity || queue.Pending.Count >= options.QueueCapacity) return Busy();
                queue.Pending.Add(fingerprint, receipt = new());
                queue.Users++;
            }
        }
        log($"reply {Short(request.Request)} to {Short(request.Id)}: {(duplicate ? "deduplicated" : "queued")} host-side fingerprint {fingerprint[..12]}, {request.Text?.Length ?? 0} characters; in-memory only, lost on host restart");
        if (duplicate) return await receipt.Result.Task;
        using var expiry = new CancellationTokenSource(options.QueueLifetime, options.Time);
        using var waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellation, Stopping, expiry.Token);
        var held = false;
        ReplyResult Cancelled() => Refused($"the reply queue was {(cancellation.IsCancellationRequested || Stopping.IsCancellationRequested ? "cancelled" : "expired")} before typing; nothing was typed");
        void Release()
        {
            if (!held) return;
            held = false;
            queue.Turn.Release();
        }
        try
        {
            await queue.Turn.WaitAsync(waiting.Token);
            held = true;
            waiting.Token.ThrowIfCancellationRequested();
            if (queue.Cleanup is { } cleanup) await cleanup(timeline, waiting.Token);
            var result = await DeliverAsync(request, key, peer, timeline, waiting.Token, Release);
            if (result.Outcome == AnswerOutcome.Refused && waiting.IsCancellationRequested) result = Cancelled();
            if (result.Outcome == AnswerOutcome.Unconfirmed)
            {
                lock (queueLock)
                    receipt.Expires = options.Time.GetUtcNow() + options.QueueLifetime;
            }
            receipt.Result.SetResult(result);
            return result;
        }
        catch (OperationCanceledException) when (waiting.IsCancellationRequested)
        {
            var result = Cancelled();
            receipt.Result.SetResult(result);
            return result;
        }
        catch (Exception e) { receipt.Result.TrySetException(e); throw; }
        finally
        {
            Release();
            lock (queueLock)
            {
                if (queue.Pending.GetValueOrDefault(fingerprint) == receipt && receipt.Expires is null) queue.Pending.Remove(fingerprint);
                if (--queue.Users == 0 && queue.Pending.Count == 0 && queue.Cleanup is null) replyQueues.Remove(request.Id);
            }
        }
    }

    async Task<ReplyResult?> ClaimIdleAsync(Session session, Guid key, CancellationToken cancellation)
    {
        // A genuine draft or unsafe mode is still a distinct refusal, not an agent queue.
        if (session.ProcessId is { } pid && await InputBoxAsync(pid) is { Seen: true, Problem: { } problem })
            return Unverified(problem);
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (probe.Active() is { } active) return Refused(active);
            var now = source.Get(session.Id);
            if (now is null || now.ProcessId != session.ProcessId) return Stale("the session has ended or changed process");
            if (now.Question is { } asking) return Asking(asking);
            if (LaunchHeld?.Invoke(session.Id) is { } held) return Refused(held);
            if (now.Status == SessionStatus.Idle && (now.ProcessId is not { } target || await InputBoxAsync(target) is not { State: { Working: true, Main: true } })
                && gate.TryClaim(session.Id, key)) return null;
            await Task.Delay(options.PollInterval, options.Time, cancellation);
        }
    }

    async Task<bool> RecoverCopilotAsync(Session session, AgentProcess process, string transcript, string words, DeliveryTimeline timeline, InjectionResult write, bool clearOnly = false, CancellationToken queuedCancellation = default)
    {
        if (write.TextWritten is not { } length || length <= 0 || length > words.Length || write.EnterWritten is null) return false;
        if (write.EnterWritten == true) return true;
        var rollback = clearOnly || length != words.Length;
        var owned = words[..length];
        if (write.TextFingerprint != InjectionResult.Fingerprint(owned)) return false;
        lock (queueLock)
            if (!clearOnly && replyQueues.TryGetValue(session.Id, out var queue))
                queue.Cleanup = (t, ct) => RecoverCopilotAsync(session, process, transcript, words, t, write, clearOnly: true, queuedCancellation: ct);
        using var expiry = new CancellationTokenSource(options.SubmitPatience, options.Time);
        using var recovery = CancellationTokenSource.CreateLinkedTokenSource(Stopping, expiry.Token, queuedCancellation);
        long? emptyAt = null;
        try
        {
            while (!recovery.IsCancellationRequested)
            {
                var now = source.Get(session.Id);
                if (now is null || now.ProcessId != session.ProcessId || !SameProcess()) { Forget(); return false; }
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
                                if (options.Time.GetElapsedTime(emptyAt.Value) >= options.SubmitAfter) { Forget(); return false; }
                            }
                            else emptyAt = null;
                            if (box is { Main: true, Complete: true, Shell: false, Working: false, Content.Length: > 0 }
                                && Remainder(box.Content, owned) != "")
                            { Forget(); log($"reply recovery to {Short(session.Id)}: draft changed; no recovery keys"); return false; }
                            if (box is { Main: true, Complete: true, Shell: false, Working: false, Vim: not CopilotVimMode.Normal }
                                && box.Content.Length > 0 && Remainder(box.Content, owned) == "")
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
                                ])])
                                { Proceed = Proceed };
                                var result = await injector.InjectAsync(process.Id, plan);
                                log($"reply recovery to {Short(session.Id)}: owned prefix {length} characters fingerprint {write.TextFingerprint[..12]}, {(rollback ? "clear" : "Enter")} {result.Status}");
                                if (result.Status != InjectionStatus.Typed) { if (result.Status == InjectionStatus.Interrupted) Forget(); return false; }
                                if (!rollback) { Forget(); timeline.Typed(false); return true; }
                            }
                            // Never erase or submit somebody else's draft, or type into a dialog.
                        }
                    }
                    finally { gate.DoneTyping(); }
                }
                await Task.Delay(options.SubmitAfter, options.Time, recovery.Token);
            }
        }
        catch (OperationCanceledException) when (recovery.IsCancellationRequested) { }
        catch (Exception e)
        {
            Forget();
            log($"reply recovery to {Short(session.Id)} ended after failure: {e.GetType().Name}; delivery uncertain");
        }
        return false;

        void Forget()
        {
            lock (queueLock)
                if (replyQueues.TryGetValue(session.Id, out var queue)) queue.Cleanup = null;
        }
        bool SameProcess() => processes.GetAgentProcesses(AgentKind.Copilot).Any(p => p.Id == process.Id && p.StartedAt == process.StartedAt);
        string? Proceed() => !recovery.IsCancellationRequested && SameProcess() && probe.Active() is null
            && LaunchHeld?.Invoke(session.Id) is null && guard.Refuse(process.Id) is null
            && source.Get(session.Id) is { Agent: AgentKind.Copilot, Status: SessionStatus.Idle, Question: null } live && live.ProcessId == process.Id
            && inputs.TryGetValue(transcript, out var input)
            && (input.Read() is { Readable: true, Asking: false, Resumed: false } || NotYetWritten(transcript, live))
                ? null : "the pending reply is not safe to submit";
    }

    static string? Remainder(string content, string words)
    {
        var remaining = words.Trim(' ');
        foreach (var line in content.Split('\n'))
        {
            var drawn = line;
            if (!remaining.StartsWith(drawn, StringComparison.Ordinal)) return null;
            remaining = remaining[drawn.Length..].TrimStart(' ');
        }
        return remaining;
    }
}
