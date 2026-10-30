namespace AgentBridge.App.Core;

// Acts on a toast click, whether the app was running or the click is what started it: opens
// the session's card, or sends the answer through the host exactly as the card would (the
// same delivery, so the card shows its progress too), then replaces the toast with what
// became of it. A stale click is the host's to refuse: it answers stale for a question that
// has gone, and the toast says so.
public sealed class ToastResponder
{
    readonly Func<string?, Deliveries?> deliveriesFor;
    readonly IToastSink sink;
    readonly Action<string?, string?> open;
    // The outcome toasts shown for each other machine, which go when it does.
    readonly Dictionary<string, HashSet<string>> shownFor = new(StringComparer.Ordinal);

    public ToastResponder(Deliveries deliveries, IToastSink sink, Action<string?> open)
        : this(_ => deliveries, sink, (_, session) => open(session)) { }

    // deliveriesFor(machine): that machine's deliveries (machine null for this one), or null
    // when it is no longer there. open(machine, session).
    public ToastResponder(Func<string?, Deliveries?> deliveriesFor, IToastSink sink, Action<string?, string?> open)
    {
        this.deliveriesFor = deliveriesFor;
        this.sink = sink;
        this.open = open;
    }

    public TimeSpan OutcomeWait { get; init; } = TimeSpan.FromSeconds(90);

    // A click that started the app can come before the relay has said which machines are on:
    // another machine's answer waits this long for it to turn up, rather than calling a machine
    // that is online gone and dropping the answer.
    public TimeSpan MachineWait { get; init; } = TimeSpan.FromSeconds(15);

    async Task<Deliveries?> FindAsync(string? machine)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            if (deliveriesFor(machine) is { } d) { return d; }
            if (machine is null || waited.Elapsed >= MachineWait) { return null; }
            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }
    }

    // The machine left: the outcomes of answers sent to it go with its other toasts.
    public void MachineGone(string machine)
    {
        HashSet<string>? tags;
        lock (shownFor) { shownFor.Remove(machine, out tags); }
        foreach (var tag in tags ?? []) { sink.Withdraw(tag); }
    }

    void Show(string? machine, string tag, string xml)
    {
        if (machine is not null)
        {
            lock (shownFor)
            {
                if (!shownFor.TryGetValue(machine, out var tags)) { shownFor[machine] = tags = new(StringComparer.Ordinal); }
                tags.Add(tag);
            }
        }
        sink.Show(new ToastNotice(tag, xml, Quiet: true));
    }

    public async Task HandleAsync(string? arguments, IReadOnlyDictionary<string, string> inputs, string title = "Agent Bridge")
    {
        var action = ToastAction.Parse(arguments);
        if (action.Action != "answer" || action.Session is null || action.Question is null)
        {
            open(action.Machine, action.Session);
            return;
        }
        var (answer, problem) = action.Answer(inputs, "00000000-0000-4000-8000-000000000000");
        var tag = ToastPlanner.OutcomeTag(action.Session, action.Machine);
        if (answer is null)
        {
            // Nothing to send (an empty box): the card is where to answer.
            Show(action.Machine, tag, ToastXml.Outcome(action.Session, title, $"Not sent: {problem}. Open the session to answer.", action.Machine));
            return;
        }
        if (await FindAsync(action.Machine) is not { } deliveries)
        {
            Show(action.Machine, tag, ToastXml.Outcome(action.Session, title, "Not sent: that machine is not connected to this app now. Open the session to answer.", action.Machine));
            return;
        }
        // The machine left while the answer was on its way (its toasts have gone, MachineGone),
        // or left and came back with deliveries of its own: nothing more is said about this one.
        bool Gone() => action.Machine is not null && !ReferenceEquals(deliveriesFor(action.Machine), deliveries);
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
                if (Gone()) { return; }
                Show(action.Machine, tag, ToastXml.Outcome(action.Session, title, "Still sending your answer. Open the session to see what became of it.", action.Machine));
                return;
            }
            if (Gone()) { return; }
            var (text, _) = QuestionViewModel.Describe(result, "");
            Show(action.Machine, tag, ToastXml.Outcome(action.Session, title, text, action.Machine));
        }
        finally { deliveries.Changed -= Watch; }
    }
}
