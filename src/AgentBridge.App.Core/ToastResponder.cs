namespace AgentBridge.App.Core;

// Acts on a toast click, whether the app was running or the click is what started it: opens
// the session's card, or sends the answer through the host exactly as the card would (the
// same delivery, so the card shows its progress too), then replaces the toast with what
// became of it. A stale click is the host's to refuse: it answers stale for a question that
// has gone, and the toast says so.
public sealed class ToastResponder
{
    readonly Deliveries deliveries;
    readonly IToastSink sink;
    readonly Action<string?> open;

    public ToastResponder(Deliveries deliveries, IToastSink sink, Action<string?> open)
    {
        this.deliveries = deliveries;
        this.sink = sink;
        this.open = open;
    }

    public TimeSpan OutcomeWait { get; init; } = TimeSpan.FromSeconds(90);

    public async Task HandleAsync(string? arguments, IReadOnlyDictionary<string, string> inputs, string title = "Agent Bridge")
    {
        var action = ToastAction.Parse(arguments);
        if (action.Action != "answer" || action.Session is null || action.Question is null)
        {
            open(action.Session);
            return;
        }
        var (answer, problem) = action.Answer(inputs, "00000000-0000-4000-8000-000000000000");
        var tag = ToastPlanner.OutcomeTag(action.Session);
        if (answer is null)
        {
            // Nothing to send (an empty box): the card is where to answer.
            sink.Show(new ToastNotice(tag, ToastXml.Outcome(action.Session, title, $"Not sent: {problem}. Open the session to answer."), Quiet: true));
            return;
        }
        var key = $"answer|{action.Session}|{action.Question}";
        var done = new TaskCompletionSource<Delivery>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Watch(Delivery d)
        {
            if (d.Key == key && deliveries.Get(key) is { State: DeliveryState.Done or DeliveryState.Failed } now) { done.TrySetResult(now); }
        }
        deliveries.Changed += Watch;
        try
        {
            var entry = deliveries.Submit(key, AgentBridge.Protocol.Ops.SessionAnswer, request => answer with { Request = request }, outcome => outcome is not ("busy" or "refused"), payload: answer);
            if (entry.State is DeliveryState.Done or DeliveryState.Failed) { done.TrySetResult(entry); }
            Delivery result;
            try { result = await done.Task.WaitAsync(OutcomeWait); }
            catch (TimeoutException)
            {
                sink.Show(new ToastNotice(tag, ToastXml.Outcome(action.Session, title, "Still sending your answer. Open the session to see what became of it."), Quiet: true));
                return;
            }
            var (text, _) = QuestionViewModel.Describe(result, "");
            sink.Show(new ToastNotice(tag, ToastXml.Outcome(action.Session, title, text), Quiet: true));
        }
        finally { deliveries.Changed -= Watch; }
    }
}
