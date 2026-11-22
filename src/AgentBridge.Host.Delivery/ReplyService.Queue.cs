using AgentBridge.Host.Delivery.Copilot;

namespace AgentBridge.Host.Delivery;

public sealed partial class ReplyService
{
    async Task<InjectionResult> FinishQueueAsync(int pid, string words, Func<Task<string?>> fence, DateTimeOffset? created)
    {
        async Task<CopilotInputBox?> OwnedAsync()
        {
            var screen = await injector.ReadScreenAsync(pid);
            return screen.Rows is { } rows ? CopilotInputBoxReader.Owned(rows, words) : null;
        }
        KeyPlan Action(CopilotInputBox box, SubmitKey action)
        {
            var footer = box.Rows[^1].Row + 1;
            return new KeyPlan([new KeySend("", false, 0, CopilotPlanner.ReplyEnterDelayMs, box.Rows,
            [
                new(footer, ScreenCheck.WithAny, CopilotInputBoxReader.FooterSigns),
                new(footer, ScreenCheck.WithoutParts, CopilotInputBoxReader.UnsafeFooter),
                new(footer + 1, ScreenCheck.BlankFrom, []),
            ]) { Submit = action }]) { ProceedAsync = fence, TargetCreatedAt = created };
        }
        if (await OwnedAsync() is not { } owned) { return Failed("the composer changed after typing; no queue key or cleanup was sent"); }
        var submit = await injector.InjectAsync(pid, Action(owned, SubmitKey.Queue));
        if ((await InputBoxAsync(pid)).Box is { Empty: true })
        { return submit.Status == InjectionStatus.Typed ? submit : Failed("queue submission was not confirmed; the composer is empty"); }
        if (await OwnedAsync() is not { } retained)
        { return Failed("queue submission was not confirmed and the composer changed; owner input was left untouched"); }
        var cleanup = await injector.InjectAsync(pid, Action(retained, SubmitKey.Clear));
        return Failed(cleanup.Status == InjectionStatus.Typed && (await InputBoxAsync(pid)).Box is { Empty: true }
            ? "queue submission failed; the owned composer text was cleared"
            : "queue submission failed; composer cleanup could not be confirmed; inspect it at the machine");
        static InjectionResult Failed(string reason) => new(InjectionStatus.Interrupted, reason);
    }
}
