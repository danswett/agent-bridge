using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;

namespace AgentBridge.HomeAssistant;

// Stage 5b: phone notifications through Home Assistant (docs/ha-connector.md), as agent-ha-bridge
// sends them:
//
//   answer buttons  a pending question on each phone named in answerButtons.services, its options
//                   as buttons and a text action, once per question (Sync-DaemonAnswerButtons);
//                   withdrawn once it is answered or gone (Clear-DaemonAnswerButtons). A tap fires
//                   mobile_app_notification_action, held for by a subscription of its own, and is
//                   answered through the same delivery as the card (Invoke-DaemonAnswerButton).
//   the hooks'      a question, a permission prompt or a blocked Claude, and a finished turn, to
//   pushes          notifications.service, as each agent's hook words them. The host owns the
//                   hooks now, so the connector sends them, from the session as the host sees it.
public sealed partial class HaConnector
{
    HaNotifySettings Notify => options.Notify;

    // Questions whose answer buttons reached a phone, so one is never sent twice however often the
    // session changes (DaemonAnswerButtonSent). Forgotten once the question is finished.
    readonly HashSet<string> buttonsSent = new(StringComparer.Ordinal);
    // Taps the listener has taken, waiting for a pass.
    readonly ConcurrentQueue<HaEvent> taps = new();
    Task tapping = Task.CompletedTask;
    // The first pass after a start: what sessions already show then was the hooks' news when it
    // happened, not now, so nothing in it is pushed (answer buttons are, as the daemon re-sends them).
    bool baselinePass;

    sealed class NotifyState
    {
        // The question whose answer buttons are on the phones now, and when sending them is next tried.
        public string? Shown;
        public DateTimeOffset RetryAt;
        // What the hooks' pushes have said already.
        public string? Question;
        public bool Waiting;
        public string? Finished;
    }

    // Holds the subscription for as long as the connector runs and is not paused, and queues what
    // arrives; the passes act on it. Started only when there is a phone to tap.
    async Task TapLoopAsync(CancellationToken token)
    {
        var backoff = TimeSpan.FromSeconds(2);
        while (!token.IsCancellationRequested)
        {
            if (!started || pausedFor is not null)
            {
                try { await Task.Delay(options.Tick, time, token); } catch (OperationCanceledException) { return; }
                continue;
            }
            using var held = CancellationTokenSource.CreateLinkedTokenSource(token);
            // Let go while agent-ha-bridge runs: its own listener takes the taps then.
            var monitor = Task.Run(async () =>
            {
                while (!held.IsCancellationRequested && started && pausedFor is null)
                {
                    try { await Task.Delay(options.Tick, time, held.Token); } catch (OperationCanceledException) { return; }
                }
                try { await held.CancelAsync(); } catch (ObjectDisposedException) { }
            }, CancellationToken.None);
            try
            {
                await foreach (var e in ha.EventsAsync(HaNotify.ActionEvent, held.Token))
                {
                    Tapped(e);
                    backoff = TimeSpan.FromSeconds(2);
                }
            }
            catch (OperationCanceledException) when (held.IsCancellationRequested) { }
            catch (Exception e)
            {
                Problem("taps", $"Home Assistant connector could not hold the notification-action subscription: {Describe(e)}; trying again in {backoff.TotalSeconds:F0}s");
                try { await Task.Delay(backoff, time, token); } catch (OperationCanceledException) { return; }
                backoff = TimeSpan.FromTicks(Math.Min(backoff.Ticks * 2, TimeSpan.FromSeconds(60).Ticks));
            }
            await held.CancelAsync();
            await monitor;
        }
    }

    internal void Tapped(HaEvent e) => taps.Enqueue(e);

    bool ServesButtons => ServesAnswers && Notify.ButtonsOn;

    // Sends the answer buttons for every armed question that has none on the phones yet.
    async Task ButtonsAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (!ServesButtons || !options.ConsumeAnswers) { return; }
        List<(string Id, Published P)> due;
        lock (gate)
        {
            due = [.. published.Where(x => x.Value.Armed is { Settled: false } a && !buttonsSent.Contains(a.Question.Id) && x.Value.Notify.RetryAt <= now)
                .Select(x => (x.Key, x.Value))];
        }
        foreach (var (id, p) in due)
        {
            var armed = p.Armed!;
            // An approval is answered by its own tap on the card; the daemon never puts one on a phone.
            if (armed.Plan.AnswerOnTap) { continue; }
            var marker = HaNotify.Marker(armed.Plan);
            var buttons = HaNotify.Options(marker);
            var actions = HaNotify.Actions(marker, armed.Question.Id, buttons, Notify.MaxButtons, textAction: TakesWords(armed.Plan));
            var payload = HaNotify.Payload(id, p.Name, marker, actions, options.DashboardUrlPath, options.MachineName,
                Notify.Icon, Notify.IconColor, Notify.Color, Notify.InterruptionLevel);
            var sent = await SendAsync(Notify.AnswerButtonServices, payload, "answer buttons", ct);
            if (sent > 0)
            {
                lock (gate) { buttonsSent.Add(armed.Question.Id); }
                p.Notify.Shown = armed.Question.Id;
                log($"Home Assistant connector sent answer buttons for {id[..Math.Min(8, id.Length)]} ({actions.Count} action(s))");
            }
            else { p.Notify.RetryAt = now + options.ButtonsRetry; }
        }
    }

    // Whether typed words can answer the question: the daemon offers the text action for anything
    // but a form or a terminal-only question, and so does this, except where the host could not
    // deliver the words (a lone choice whose prompt has no "Other"), which would be a button that
    // silently does nothing.
    static bool TakesWords(DecisionPlan plan) =>
        !plan.TerminalOnly && !plan.AnswerOnTap && (plan.Fields.Count == 0 || (plan.Fields.Count == 1 && (plan.Fields[0].IsText || plan.TakesTypedAnswer)));

    // Withdraws a question's notification from the phones; forget once the question is finished.
    async Task ClearButtonsAsync(string sessionId, NotifyState state, bool forget, CancellationToken ct)
    {
        var shown = state.Shown;
        if (shown is null || !Notify.ButtonsOn) { return; }
        try { await SendAsync(Notify.AnswerButtonServices, HaNotify.Clear(sessionId), "answer button clear", ct); }
        finally
        {
            if (forget)
            {
                lock (gate) { buttonsSent.Remove(shown); }
                state.Shown = null;
            }
        }
    }

    // Send-BridgeAnswerButtonNotification: one payload to each service, a failure at one logged and
    // the rest still sent. How many took it.
    async Task<int> SendAsync(IReadOnlyList<string> services, JsonObject payload, string what, CancellationToken ct)
    {
        var sent = 0;
        foreach (var service in services)
        {
            if (HaNotify.Split(service) is not { } target) { continue; }
            try
            {
                await ha.CallServiceAsync(target.Domain, target.Service, payload.DeepClone().AsObject(), ct);
                sent++;
            }
            catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning))
            {
                log($"Home Assistant connector {what} via {service} failed: {Describe(e)}");
            }
        }
        return sent;
    }

    // Invoke-DaemonAnswerButtonQueue: every tap taken since the last pass, answered as the card's
    // answers are.
    async Task TapsAsync(DateTimeOffset now, CancellationToken ct)
    {
        while (taps.TryDequeue(out var tap))
        {
            try { await TapAsync(tap, now, ct); }
            catch
            {
                // Tried again on the next pass; a tap already used is never delivered twice.
                taps.Enqueue(tap);
                throw;
            }
        }
    }

    async Task TapAsync(HaEvent tap, DateTimeOffset now, CancellationToken ct)
    {
        if (HaNotify.Choice(tap.Data) is not { } choice) { return; }
        Published? p;
        lock (gate) { published.TryGetValue(choice.SessionId, out p); }
        var who = choice.SessionId[..Math.Min(8, choice.SessionId.Length)];
        if (p is null) { log("Home Assistant connector: an answer button for a session that is no longer live; ignored"); return; }
        var armed = p.Armed;
        if (armed is null || armed.Question.Id != choice.DecisionId)
        {
            // The notification outlives its question: a tap on an old one never answers what is asked now.
            log($"Home Assistant connector: an answer button for {who} is for an earlier question; ignored");
            return;
        }
        if (armed.Settled)
        {
            log($"Home Assistant connector: an answer button for {who} came after the question was answered; ignored");
            await ClearButtonsAsync(choice.SessionId, p.Notify, forget: false, ct);
            return;
        }
        if (armed.Plan.TerminalOnly || armed.Plan.AnswerOnTap) { return; }
        if (PhoneAnswer(choice, armed, DriverOf(tap.UserId)) is not { } read)
        {
            log($"Home Assistant connector: an answer button for {who} names no answer this question takes; ignored");
            return;
        }
        if (read.Press == armed.UsedPress) { return; }
        var delivered = await DeliverAsync(choice.SessionId, p, armed, read, read.Request!, now, ct);
        if (delivered == AnswerOutcome.Delivered)
        {
            log($"Home Assistant connector: answered from a phone for {who}");
            await ClearButtonsAsync(choice.SessionId, p.Notify, forget: false, ct);
        }
    }

    // What a tap answers, or null: an option by its button's index, or words typed at the text action.
    Read? PhoneAnswer(TapChoice choice, Armed armed, string driver)
    {
        var plan = armed.Plan;
        var sessionId = choice.SessionId;
        // One tap, however often it arrives: the same button, or the same words.
        var press = $"phone:{choice.Index}:{choice.ReplyText}";
        if (!string.IsNullOrWhiteSpace(choice.ReplyText))
        {
            var text = choice.ReplyText.Trim();
            if (plan.Fields.Count == 0) { return Answer(sessionId, armed, null, text, text, null, press, driver); }
            if (plan.Fields.Count != 1) { return null; }
            var field = plan.Fields[0];
            var name = field.Source!.Name;
            if (field.IsText)
            {
                if (field.Source.Type != FieldType.Number) { return Answer(sessionId, armed, new Dictionary<string, JsonElement> { [name] = JsonSerializer.SerializeToElement(text) }, null, text, null, press, driver); }
                var number = ExactNumber(text) ?? (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d) ? JsonSerializer.SerializeToElement(d) : null);
                return number is { } n ? Answer(sessionId, armed, new Dictionary<string, JsonElement> { [name] = n }, null, text, null, press, driver) : null;
            }
            // Through the prompt's own "Other" entry, where it has one.
            return plan.TakesTypedAnswer
                ? Answer(sessionId, armed, new Dictionary<string, JsonElement> { [name] = JsonSerializer.SerializeToElement(new { other = text }) }, null, text, null, press, driver)
                : null;
        }
        var options = HaNotify.Options(HaNotify.Marker(plan));
        if (choice.Index < 0 || choice.Index >= options.Count || plan.Fields.Count != 1) { return null; }
        var label = options[choice.Index];
        var lone = plan.Fields[0];
        var source = lone.Source!;
        var index = lone.Options.ToList().IndexOf(label);
        if (lone.IsText || index < 0 || source.Options is not { } offered && source.Type != FieldType.Boolean) { return null; }
        JsonElement value = source.Type switch
        {
            FieldType.Boolean => JsonSerializer.SerializeToElement(index == 0),
            // One tap is one choice of a "choose any" question, as the daemon's Selections @(answer).
            _ when lone.MultiSelect => JsonSerializer.SerializeToElement(new[] { source.Options![index].Value }),
            _ => JsonSerializer.SerializeToElement(source.Options![index].Value),
        };
        return Answer(sessionId, armed, new Dictionary<string, JsonElement> { [source.Name] = value }, null, label, null, press, driver);
    }

    // The hooks' pushes for one session, from what the host now shows of it.
    async Task PushesAsync(Session full, Published p, string name, bool firstSeen, CancellationToken ct)
    {
        var state = p.Notify;
        var status = HaPayloads.Status(full);
        var question = full.Question;
        // As the hooks send it: the preview is the reply's own text, not trimmed.
        var response = full.Activity.Response;
        var idle = status is "idle" or "agents" or "shell";
        // A reply already there when the session first appears is an earlier turn's (a resumed
        // session's, or one from before this start): the hooks pushed it when it was new, if ever.
        if (firstSeen) { state.Finished = response; }
        if (firstSeen && baselinePass)
        {
            state.Question = question?.Id;
            state.Waiting = full.Agent == AgentKind.Claude && status == "waiting" && question is null;
            return;
        }
        if (!Notify.Enabled) { return; }
        var pushes = new List<(string Title, string Message)>();
        if (question is not null && question.Id != state.Question)
        {
            state.Question = question.Id;
            if (QuestionPush(full, name, question) is { } push) { pushes.Add(push); }
        }
        if (question is null) { state.Question = null; }
        // Claude blocked on the person with nothing to answer on the card (Invoke-ClaudeNotificationHook).
        var blocked = full.Agent == AgentKind.Claude && status == "waiting" && question is null;
        if (blocked && !state.Waiting) { pushes.Add(HaNotify.ClaudeWaiting(name, options.MachineName, null)); }
        state.Waiting = blocked;
        // A finished turn (the agentStop and Stop hooks): once, with a reply that is the turn's own.
        // The host keeps the previous turn's reply while the next runs, so a reply counts only once it
        // differs from the one last seen at rest.
        if (idle && !string.IsNullOrWhiteSpace(response) && response != state.Finished
            && HaNotify.Response(name, response, full.Agent == AgentKind.Copilot) is { } finished)
        {
            pushes.Add(finished);
        }
        if (idle) { state.Finished = response; }
        foreach (var (title, message) in pushes)
        {
            if (HaNotify.Split(Notify.Service) is not { } target) { continue; }
            var data = HaNotify.Notification(Notify.Service, title, message, Notify.TickerCategory, options.DashboardUrlPath);
            try { await ha.CallServiceAsync(target.Domain, target.Service, data, ct); }
            catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning))
            {
                // As the hooks do: the session is on the dashboard already, and a lost push must not
                // hold anything else up.
                log($"Home Assistant connector notification via {Notify.Service} failed: {Describe(e)}");
            }
        }
    }

    // The push the agent's hook sends for a question, by agent and kind.
    (string Title, string Message)? QuestionPush(Session full, string name, Question question)
    {
        var plan = HaDecision.Plan(question, full.Agent);
        return (full.Agent, question.Kind) switch
        {
            (AgentKind.Copilot, QuestionKind.Approval) => HaNotify.CopilotPermission(question.Message),
            (AgentKind.Copilot, _) => HaNotify.CopilotQuestion(name, options.MachineName, plan.Question, plan.Choices),
            (AgentKind.Claude, QuestionKind.Approval) => HaNotify.ClaudeWaiting(name, options.MachineName, question.Message),
            (AgentKind.Claude, _) => HaNotify.ClaudeQuestion(name, options.MachineName, plan.Question, plan.Choices, plan.TerminalOnly),
            (AgentKind.Codex, QuestionKind.Approval) => HaNotify.CodexApproval(name, question.Message),
            _ => null,
        };
    }
}
