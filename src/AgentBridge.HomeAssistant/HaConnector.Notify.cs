using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
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

    // Questions whose answer buttons reached a phone, by session and question, so one is never
    // sent twice however often the session changes (DaemonAnswerButtonSent). Forgotten once the
    // question is finished.
    readonly HashSet<string> buttonsSent = new(StringComparer.Ordinal);
    // Which phones hold a session's notification, by session: what a clear goes to, and what is
    // still to be withdrawn when a clear failed or the host stopped first. Kept in the ledger.
    readonly Dictionary<string, HashSet<string>> onPhones = new(StringComparer.Ordinal);
    // Taps the listener has taken, waiting for a pass.
    readonly ConcurrentQueue<HaEvent> taps = new();
    Task tapping = Task.CompletedTask;
    // The first pass after a start: what sessions already show then was the hooks' news when it
    // happened, not now, so nothing in it is pushed (answer buttons are, as the daemon re-sends them).
    bool baselinePass;
    // When a withdrawal that failed is next tried.
    DateTimeOffset staleRetryAt;

    static string SentKey(string sessionId, string questionId) => sessionId + "\n" + questionId;

    sealed class NotifyState
    {
        // The question whose answer buttons are on the phones now, and when sending them is next tried.
        public string? Shown;
        public DateTimeOffset RetryAt;
        // What the hooks' pushes have said already, and what the session was doing when last seen.
        public bool Seen;
        public string? Question;
        public bool Waiting;
        public string? FinishedKey;
        public SessionStatus? LastStatus;
        public int? LastTurn;
        // A finished turn waiting to be sure it stays finished: when, and which.
        public (DateTimeOffset At, string Key)? FinishedDue;
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

    // Whether a session's question belongs on the phones: armed, still waiting, and not an approval
    // (the daemon has no marker for one, so never sends it, however the card shows it).
    static bool Wanted(Published p) => p.Armed is { Settled: false } a && a.Question.Kind != QuestionKind.Approval;

    // The buttons' notification for the question a session's card is armed with.
    (JsonArray Actions, JsonObject Payload) Buttons(string sessionId, Published p, Armed armed)
    {
        var marker = HaNotify.Marker(armed.Plan);
        var actions = HaNotify.Actions(marker, armed.Question.Id, HaNotify.Options(marker), Notify.MaxButtons, textAction: TakesWords(armed.Plan));
        return (actions, HaNotify.Payload(sessionId, p.Name, marker, actions, options.DashboardUrlPath, options.MachineName,
            Notify.Icon, Notify.IconColor, Notify.Color, Notify.InterruptionLevel));
    }

    // Sends the answer buttons for every armed question that has none on the phones yet, and
    // withdraws what is left on a phone with no question to show.
    async Task ButtonsAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (now >= staleRetryAt)
        {
            List<string> stale;
            lock (gate) { stale = [.. onPhones.Keys.Where(id => !published.TryGetValue(id, out var p) || !Wanted(p))]; }
            foreach (var id in stale) { await ClearButtonsAsync(id, null, forget: false, ct); }
            bool left;
            lock (gate) { left = stale.Any(onPhones.ContainsKey); }
            if (left) { staleRetryAt = now + options.ButtonsRetry; }
        }
        if (!ServesButtons || !options.ConsumeAnswers) { return; }
        List<(string Id, Published P)> due;
        lock (gate)
        {
            due = [.. published.Where(x => Wanted(x.Value) && !buttonsSent.Contains(SentKey(x.Key, x.Value.Armed!.Question.Id)) && x.Value.Notify.RetryAt <= now)
                .Select(x => (x.Key, x.Value))];
        }
        foreach (var (id, p) in due)
        {
            var armed = p.Armed!;
            var (actions, payload) = Buttons(id, p, armed);
            var sent = await SendAsync(id, Notify.AnswerButtonServices, payload, "answer buttons", ct);
            if (sent.Count > 0)
            {
                lock (gate)
                {
                    buttonsSent.Add(SentKey(id, armed.Question.Id));
                    if (!onPhones.TryGetValue(id, out var phones)) { onPhones[id] = phones = new(StringComparer.Ordinal); }
                    phones.UnionWith(sent);
                }
                p.Notify.Shown = armed.Question.Id;
                SaveLedger();
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

    // Withdraws a session's notification from every phone that has it (Clear-DaemonAnswerButtons).
    // A phone that could not be reached keeps it on the list and is tried again on a later pass.
    // Forget once the question is finished, so a question asked again is sent again.
    async Task ClearButtonsAsync(string sessionId, NotifyState? state, bool forget, CancellationToken ct)
    {
        if (forget && state?.Shown is { } shown)
        {
            lock (gate) { buttonsSent.Remove(SentKey(sessionId, shown)); }
            state.Shown = null;
        }
        List<string> phones;
        lock (gate) { phones = onPhones.TryGetValue(sessionId, out var p) ? [.. p] : []; }
        if (phones.Count == 0) { return; }
        var cleared = await SendAsync(sessionId, phones, HaNotify.Clear(sessionId), "answer button clear", ct);
        lock (gate)
        {
            if (onPhones.TryGetValue(sessionId, out var left))
            {
                left.ExceptWith(cleared);
                if (left.Count == 0) { onPhones.Remove(sessionId); }
            }
        }
        if (cleared.Count > 0) { SaveLedger(); }
    }

    // Send-BridgeAnswerButtonNotification: one payload to each service, a failure at one logged and
    // the rest still sent. The services that took it.
    async Task<List<string>> SendAsync(string sessionId, IEnumerable<string> services, JsonObject payload, string what, CancellationToken ct)
    {
        var sent = new List<string>();
        foreach (var service in services)
        {
            if (HaNotify.Split(service) is not { } target) { continue; }
            try
            {
                await ha.CallServiceAsync(target.Domain, target.Service, payload.DeepClone().AsObject(), ct);
                sent.Add(service);
                Resolved($"{what}:{service}");
            }
            catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning))
            {
                // Said once until it works again: a phone that is gone would otherwise be a line
                // on every retry.
                Problem($"{what}:{service}", $"Home Assistant connector {what} via {service} failed for {sessionId[..Math.Min(8, sessionId.Length)]}: {Describe(e)}");
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
        if (!Wanted(p) || armed.Plan.TerminalOnly || armed.Plan.AnswerOnTap) { return; }
        // Only an action this question's notification offers: another app's action that happens to
        // end in a number, or one past maxButtons, is not a tap on one of these buttons.
        var action = tap.Data.TryGetProperty("action", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        if (action is null || !Buttons(choice.SessionId, p, armed).Actions.Any(x => x?["action"]?.GetValue<string>() == action))
        {
            log($"Home Assistant connector: an answer button for {who} is not one this question offers; ignored");
            return;
        }
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
        // One tap, however often it arrives: the same button, or the same words, for this attempt.
        // A delivery the host refused counts an attempt, so the same button tapped again after one
        // is a new answer, not the refused one read twice.
        var press = $"phone:{armed.Attempts}:{choice.Index}:{choice.ReplyText}";
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
        if (lone.IsText || index < 0 || (source.Options is null && source.Type != FieldType.Boolean)) { return null; }
        JsonElement value = source.Type switch
        {
            FieldType.Boolean => JsonSerializer.SerializeToElement(index == 0),
            // One tap is one choice of a "choose any" question, as the daemon's Selections @(answer).
            _ when lone.MultiSelect => JsonSerializer.SerializeToElement(new[] { source.Options![index].Value }),
            _ => JsonSerializer.SerializeToElement(source.Options![index].Value),
        };
        return Answer(sessionId, armed, new Dictionary<string, JsonElement> { [source.Name] = value }, null, label, null, press, driver);
    }

    // A finished turn, by turn and reply: the same words ending a later turn are that turn's own.
    static string? FinishedKey(Session s) =>
        string.IsNullOrWhiteSpace(s.Activity.Response) ? null
            : $"{s.Activity.TurnCount}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Activity.Response)))[..16]}";

    // The hooks' pushes for one session, from what the host now shows of it.
    async Task PushesAsync(Session full, Published p, string name, DateTimeOffset now, CancellationToken ct)
    {
        var state = p.Notify;
        var status = full.Status;
        var question = full.Question;
        var turn = full.Activity.TurnCount;
        var key = FinishedKey(full);
        // Claude blocked on the person with nothing to answer on the card (Invoke-ClaudeNotificationHook).
        var blocked = full.Agent == AgentKind.Claude && status == SessionStatus.Waiting && question is null;
        if (!state.Seen)
        {
            // A reply already there when the session first appears is an earlier turn's (a resumed
            // session's, or one from before this start): the hooks pushed it when it was new, if ever.
            state.Seen = true;
            state.FinishedKey = key;
            state.LastStatus = status;
            state.LastTurn = turn;
            if (baselinePass)
            {
                state.Question = question?.Id;
                state.Waiting = blocked;
                return;
            }
        }
        var pushes = new List<(string Title, string Message)>();
        if (question is not null && question.Id != state.Question)
        {
            state.Question = question.Id;
            if (QuestionPush(full, name, question) is { } push) { pushes.Add(push); }
        }
        if (question is null) { state.Question = null; }
        if (blocked && !state.Waiting) { pushes.Add(HaNotify.ClaudeWaiting(name, options.MachineName, null)); }
        state.Waiting = blocked;
        // A finished turn (the agentStop and Stop hooks): a session that was working or waiting, or
        // has a new turn, now at rest with a reply that turn has not been pushed for. Sent once it
        // has stayed at rest a moment, as the host's own notifier waits: a question answered at the
        // machine passes through idle on its way back to work. A background agent's message while
        // the session rests is not a finished turn.
        if (status == SessionStatus.Idle && question is null && key is not null && key != state.FinishedKey
            && (state.LastStatus is SessionStatus.Working or SessionStatus.Waiting || state.LastTurn != turn))
        {
            state.FinishedDue = (now + options.FinishedDelay, key);
        }
        else if (status != SessionStatus.Idle || question is not null) { state.FinishedDue = null; }
        state.LastStatus = status;
        state.LastTurn = turn;
        if (!Notify.Enabled) { return; }
        foreach (var (title, message) in pushes) { await PushAsync(title, message, ct); }
    }

    // Finished turns that have stayed finished, pushed.
    async Task FinishedAsync(DateTimeOffset now, CancellationToken ct)
    {
        List<(string Id, Published P, string Key)> due;
        lock (gate)
        {
            due = [.. published.Where(x => x.Value.Notify.FinishedDue is { } d && d.At <= now).Select(x => (x.Key, x.Value, x.Value.Notify.FinishedDue!.Value.Key))];
        }
        foreach (var (id, p, key) in due)
        {
            p.Notify.FinishedDue = null;
            if (source.Get(id) is not { Status: SessionStatus.Idle, Question: null } s || FinishedKey(s) != key) { continue; }
            p.Notify.FinishedKey = key;
            if (Notify.Enabled && HaNotify.Response(p.Name, s.Activity.Response, s.Agent == AgentKind.Copilot) is { } finished)
            {
                await PushAsync(finished.Title, finished.Message, ct);
            }
        }
    }

    async Task PushAsync(string title, string message, CancellationToken ct)
    {
        if (HaNotify.Split(Notify.Service) is not { } target) { return; }
        var data = HaNotify.Notification(Notify.Service, title, message, Notify.TickerCategory, options.DashboardUrlPath);
        try { await ha.CallServiceAsync(target.Domain, target.Service, data, ct); }
        catch (Exception e) when (e is not (OperationCanceledException or AgentHaBridgeRunning))
        {
            // As the hooks do: the session is on the dashboard already, and a lost push must not
            // hold anything else up.
            log($"Home Assistant connector notification via {Notify.Service} failed: {Describe(e)}");
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

    // The ledger's record of which phones hold which session's notification.
    JsonObject OnPhonesLedger()
    {
        var o = new JsonObject();
        foreach (var (id, phones) in onPhones.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            o[id] = new JsonArray([.. phones.Order(StringComparer.Ordinal).Select(s => (JsonNode)s)]);
        }
        return o;
    }

    void LoadOnPhones(JsonElement root)
    {
        if (!root.TryGetProperty("onPhones", out var o) || o.ValueKind != JsonValueKind.Object) { return; }
        foreach (var session in o.EnumerateObject())
        {
            if (session.Value.ValueKind != JsonValueKind.Array) { continue; }
            var phones = session.Value.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String && HaNotify.Split(x.GetString()!) is not null).Select(x => x.GetString()!).ToList();
            if (phones.Count == 0) { continue; }
            if (!onPhones.TryGetValue(session.Name, out var set)) { onPhones[session.Name] = set = new(StringComparer.Ordinal); }
            set.UnionWith(phones);
        }
    }
}