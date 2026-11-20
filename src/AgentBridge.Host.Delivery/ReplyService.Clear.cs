using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Copilot;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery;

public sealed partial class ReplyService
{
    async Task<(CopilotInputBox? Box, string? Problem, Func<string?>? Fence)> ClearComposerAsync(
        Session session, int pid, SessionProcess? process, CopilotInput input, ReplyRequest request,
        CopilotInputBoxState? state, Action<ClearedComposer> remember, Func<string?> freshness)
    {
        const string Unsafe = "the composer cannot be cleared safely; nothing was sent";
        const string Changed = "the session or person confirmation changed; nothing more was typed";
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
                || (clearing && request.ClearDraftSource?.Invoke() != draft) || !StillIdle(session, pid, process, input))
                return Changed;
            return request.ClearConfirmation(digest) ?? freshness();
        }
        if (Fence() is { } refused) return (null, refused, null);
        InjectionResult result;
        try { result = await injector.InjectAsync(pid, ClearKeys(state, Fence)); }
        catch (Exception e)
        {
            remember(saved);
            return (null, Logged("the injector failed while clearing the composer; nothing was sent", e), null);
        }
        if (result.Status == InjectionStatus.NothingTyped) return (null, "the composer changed before clearing; nothing was sent", null);
        clearing = false;
        remember(saved);
        if (result.Status != InjectionStatus.Typed) return (null, "the composer did not finish clearing; nothing was sent", null);
        var (box, problem) = await AwaitEmptyAsync(pid, Fence);
        return (box, problem, box is null ? null : Fence);
    }

    // Whether the session is still the idle, unasking Copilot process the box was read from, with
    // nothing (G5, elevation, a launch) now holding its keys.
    bool StillIdle(Session session, int pid, SessionProcess? process, CopilotInput input)
    {
        if (probe.Active() is not null || guard.Refuse(pid) is not null || LaunchHeld?.Invoke(session.Id) is not null) return false;
        if (source.Get(session.Id) is not { Agent: AgentKind.Copilot, Status: SessionStatus.Idle, Question: null } live
            || live.ProcessId != pid || process is not { StartedAt: not null } || source.ProcessOf(session.Id) != process
            || !processes.GetAgentProcesses(AgentKind.Copilot).Any(p => p.Id == pid && p.StartedAt == process.StartedAt))
            return false;
        var latest = input.Read();
        return latest.Readable && !latest.Asking && !latest.Resumed;
    }

    // The clear (Ctrl+End, Ctrl+U), held to the box's rows as read and to a safe footer under them.
    static KeyPlan ClearKeys(CopilotInputBoxState state, Func<string?> fence)
    {
        var footer = state.Rows[^1].Row + 1;
        ScreenRowCheck[] checks =
        [
            new(footer, ScreenCheck.WithAny, CopilotInputBoxReader.FooterSigns),
            new(footer, ScreenCheck.WithoutParts, CopilotInputBoxReader.UnsafeFooter),
            new(footer, ScreenCheck.WithNone, CopilotInputBoxReader.TurnSigns),
            new(footer + 1, ScreenCheck.BlankFrom, []),
        ];
        return new KeyPlan([new("\u001b[1;5F\u0015", false, 0, 0, state.Rows, checks)]) { Proceed = fence };
    }

    // The box once two reads in a row, within two seconds of the clear, show it empty.
    async Task<(CopilotInputBox? Box, string? Problem)> AwaitEmptyAsync(int pid, Func<string?> fence)
    {
        const string NotEmpty = "the composer did not read empty after clearing; nothing was sent";
        var started = options.Time.GetTimestamp();
        var empty = 0;
        while (options.Time.GetElapsedTime(started) < TimeSpan.FromSeconds(2))
        {
            if (fence() is { } changed) return (null, changed);
            CopilotInputBox? box;
            var remaining = TimeSpan.FromSeconds(2) - options.Time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) break;
            try { (box, _, _, _) = await InputBoxAsync(pid).WaitAsync(remaining, options.Time); }
            catch (TimeoutException) { return (null, NotEmpty); }
            empty = box is { Empty: true } ? empty + 1 : 0;
            if (empty == 2) return (box, null);
            await Task.Delay(TimeSpan.FromMilliseconds(50), options.Time);
        }
        return (null, NotEmpty);
    }
}
