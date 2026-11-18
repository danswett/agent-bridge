using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

public sealed partial class ReplyService
{
    async Task<(CopilotInputBox? Box, string? Problem, Func<string?>? Fence)> ClearComposerAsync(
        Session session, int pid, SessionProcess? process, CopilotInput input, ReplyRequest request,
        CopilotInputBoxState? state, Action<ClearedComposer> remember)
    {
        const string Unsafe = "the composer cannot be cleared safely; nothing was sent";
        const string Changed = "the session or person confirmation changed; nothing more was typed";
        const string NotEmpty = "the composer did not read empty after clearing; nothing was sent";
        var draft = request.ClearDraftSource?.Invoke();
        if (draft is null || state is not { Complete: true, Main: true, Shell: false, Lines: 1, Vim: CopilotVimMode.None or CopilotVimMode.Insert }
            || draft.Length == 0 || draft.Length > CopilotInputBoxReader.ShownLength
            || draft.Contains('\n') || draft.Contains('\r') || state.Content != draft.Trim(' '))
            return (null, Unsafe, null);
        var (shown, marks) = ShownText.RevealMarked(draft, rightToLeft: true);
        // Never delete words we cannot return whole. Wrapped and multi-line drafts need a
        // lossless reconstruction first; their screen rows are not their original line breaks.
        if (shown.Length > CopilotInputBoxReader.ShownLength) return (null, Unsafe, null);
        var saved = new ClearedComposer(shown, [.. marks.Select(m => (IReadOnlyList<int>)[m.Start, m.Length])]);
        var digest = EscapedApproval.Shown(shown);
        var clearing = true;
        string? Fence()
        {
            if (request.Intent != ReplyIntent.Steer || request.ClearConfirmation is null
                || (clearing && request.ClearDraftSource?.Invoke() != draft)
                || probe.Active() is not null || guard.Refuse(pid) is not null || LaunchHeld?.Invoke(session.Id) is not null)
                return Changed;
            if (source.Get(session.Id) is not { Agent: AgentKind.Copilot, Status: SessionStatus.Idle, Question: null } live
                || live.ProcessId != pid || process is not { StartedAt: not null } || source.ProcessOf(session.Id) != process
                || !processes.GetAgentProcesses(AgentKind.Copilot).Any(p => p.Id == pid && p.StartedAt == process.StartedAt))
                return Changed;
            var latest = input.Read();
            return !latest.Readable || latest.Asking || latest.Resumed ? Changed : request.ClearConfirmation(digest);
        }
        if (Fence() is { } refused) return (null, refused, null);
        var footer = state.Rows[^1].Row + 1;
        ScreenRowCheck[] checks =
        [
            new(footer, ScreenCheck.WithAny, CopilotInputBoxReader.FooterSigns),
            new(footer, ScreenCheck.WithoutParts, CopilotInputBoxReader.UnsafeFooter),
            new(footer, ScreenCheck.WithNone, CopilotInputBoxReader.TurnSigns),
            new(footer + 1, ScreenCheck.BlankFrom, []),
        ];
        var plan = new KeyPlan([new("\u001b[1;5F\u0015", false, 0, 0, state.Rows, checks)]) { Proceed = Fence };
        InjectionResult result;
        try { result = await injector.InjectAsync(pid, plan); }
        catch (Exception e)
        {
            remember(saved);
            return (null, Logged("the injector failed while clearing the composer; nothing was sent", e), null);
        }
        if (result.Status == InjectionStatus.NothingTyped) return (null, "the composer changed before clearing; nothing was sent", null);
        clearing = false;
        remember(saved);
        if (result.Status != InjectionStatus.Typed) return (null, "the composer did not finish clearing; nothing was sent", null);
        var started = options.Time.GetTimestamp();
        var empty = 0;
        while (options.Time.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
        {
            if (Fence() is { } changed) return (null, changed, null);
            CopilotInputBox? box;
            var remaining = TimeSpan.FromSeconds(2) - options.Time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) break;
            try { (box, _, _, _) = await InputBoxAsync(pid).WaitAsync(remaining, options.Time); }
            catch (TimeoutException) { return (null, NotEmpty, null); }
            empty = box is { Empty: true } ? empty + 1 : 0;
            if (empty == 2) return (box, null, Fence);
            await Task.Delay(TimeSpan.FromMilliseconds(50), options.Time);
        }
        return (null, NotEmpty, null);
    }
}
