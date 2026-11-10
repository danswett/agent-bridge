using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// Acts on a toast click, whether the app was running or the click is what started it: opens
// the session's card, or sends the answer through the host exactly as the card would (the
// same delivery, so the card shows its progress too), then replaces the toast with what
// became of it. A stale click is the host's to refuse: it answers stale for a question that
// has gone, and the toast says so. An approval is never answered from a toast (#372).
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
    // another machine's answer waits this long for it to turn up, and for its sessions to be
    // confirmed (Machine.Current, #269), rather than calling a machine that is online gone and
    // dropping the answer. One still away then is not answered: Deliveries holds it.
    public TimeSpan MachineWait { get; init; } = TimeSpan.FromSeconds(15);

    // The session as the app knows it now (machine null for this one's), or null when it doesn't
    // yet. Only a second line of defence: no toast this build draws answers an approval. A session
    // asking for approval now takes no answer from a toast whatever question the click names: a
    // button for an earlier question is stale anyway, and opening shows what is asked now.
    public Func<string?, string, AgentBridge.Protocol.Session?> SessionOf { get; init; } = (_, _) => null;

    bool AsksApproval(ToastAction action) =>
        action.Session is { } session && SessionOf(action.Machine, session)?.Question is { Kind: AgentBridge.Protocol.QuestionKind.Approval };

    // The gates on an answer from a toast, beyond its being one: it carries this build's mark (an
    // older build's button may be Approve once), and its question is not known to be an approval.
    bool MayAnswer(ToastAction action)
    {
        if (!action.Current) { return false; }
        return !AsksApproval(action);
    }

    async Task<Deliveries?> FindAsync(string? machine)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            var d = deliveriesFor(machine);
            if (d is not null && !d.Holds(AgentBridge.Protocol.Ops.SessionAnswer)) { return d; }
            if (machine is null || waited.Elapsed >= MachineWait) { return d; }
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

    // The outcome toasts that say why an answer could not go: their cause ends with the question.
    readonly HashSet<string> causeBound = new(StringComparer.Ordinal);

    // The session is known and no longer asks the question the click named: a cause-bound outcome
    // arriving now has nothing left to say, and QuestionClosed has already come and gone.
    bool Closed(ToastAction action) =>
        action.Session is { } session && SessionOf(action.Machine, session) is { } s && s.Question?.Id != action.Question;

    // The question closed (answered elsewhere, cancelled, its session ended): a toast saying an
    // answer to it did not go has nothing left to say, and goes. What an answer came to stays (#1193).
    public void QuestionClosed(string? machine, string session)
    {
        var tag = ToastPlanner.OutcomeTag(session, machine);
        bool bound;
        lock (shownFor)
        {
            bound = causeBound.Remove(tag);
            if (bound && machine is not null && shownFor.TryGetValue(machine, out var tags)) { tags.Remove(tag); }
        }
        if (bound) { sink.Withdraw(tag); }
    }

    void Show(string? machine, string tag, string xml, bool outlives = false)
    {
        lock (shownFor)
        {
            if (machine is not null)
            {
                if (!shownFor.TryGetValue(machine, out var tags)) { shownFor[machine] = tags = new(StringComparer.Ordinal); }
                tags.Add(tag);
            }
            if (outlives) { causeBound.Remove(tag); } else { causeBound.Add(tag); }
        }
        sink.Show(new ToastNotice(tag, xml, Quiet: true));
    }

    public async Task HandleAsync(string? arguments, IReadOnlyDictionary<string, string> inputs, string title = Product.Name)
    {
        var action = ToastAction.Parse(arguments);
        if (action.Action != "answer" || action.Session is null || action.Question is null || !MayAnswer(action))
        {
            // A click that answers nothing, and any that would answer an approval: an older build's
            // toast, still in Notification Center, offered Approve once (#372). It opens the
            // question, where the person approves seeing the whole command.
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
        // The words chosen (Delivery.Chosen), as the app knows the question at the click: taken here,
        // before anything is waited for. Taken after FindAsync, an option relabelled while the machine
        // was coming back gave its new label (#625 cr2-625d, 6676595964). A question the app doesn't
        // know yet at the click gives none ("You answered"), and none is looked up later.
        var asked = SessionOf(action.Machine, action.Session)?.Question is { } q && q.Id == action.Question ? q : null;
        var chosen = ReceiptWords.AnsweredValue(asked, answer);
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
            var entry = deliveries.Submit(key, AgentBridge.Protocol.Ops.SessionAnswer, request => answer with { Request = request }, outcome => outcome is not ("busy" or "refused"), payload: answer, chosen: chosen);
            if (entry.State is DeliveryState.Done or DeliveryState.Failed) { done.TrySetResult(entry); }
            Delivery result;
            try { result = await done.Task.WaitAsync(OutcomeWait); }
            catch (TimeoutException)
            {
                // The send is still unresolved, so this notice does not depend on the question staying open.
                if (Gone()) { return; }
                Show(action.Machine, tag, ToastXml.Outcome(action.Session, title, "Still sending your answer. Open the session to see what became of it.", action.Machine), outlives: true);
                return;
            }
            if (Gone() || (Closed(action) && !QuestionViewModel.OutlivesQuestion(result))) { return; }
            var text = QuestionViewModel.Describe(result, null).Whole;
            Show(action.Machine, tag, ToastXml.Outcome(action.Session, title, text, action.Machine), QuestionViewModel.OutlivesQuestion(result));
        }
        finally { deliveries.Changed -= Watch; }
    }
}
