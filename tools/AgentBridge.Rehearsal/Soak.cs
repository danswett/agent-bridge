using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.HomeAssistant;

namespace AgentBridge.Rehearsal;

// The scripted load of the shadow soak (docs/host-shadow.md, "A scripted soak"): sessions
// launched, prompted, answered, compacted, ended and resumed through a disposable Home
// Assistant, the way a person drives agent-ha-bridge from its dashboard, so the shadow
// comparison sees every kind of event the gate counts without waiting for organic use.
// This file is the script and the card logic; SoakDriver.cs runs it.

// How a question is answered: from the card's controls and Send (Card), from the reply box and
// Send (Box), by the reply card's publish (Payload), from the card after a few minutes (Late), or
// not at all, the session being ended with the question open (Abandon).
internal enum AnswerStyle { Card, Box, Payload, Late, Abandon }

// One prompt sent to a session and what to do while it runs. Question: the prompt makes the agent
// ask one, answered in Style. Background: the turn starts background work, so it is over only
// once the session is idle with none left.
internal sealed record SoakTurn(string Prompt, AnswerStyle? Question = null, bool Background = false)
{
    public bool Compact => Prompt == "/compact";
}

internal sealed record SoakScenario(string Name, string Agent, IReadOnlyList<SoakTurn> Turns, bool AskPermission = false, bool Resume = false, string? Transcript = null);

internal sealed record SoakEpisode(int Index, SoakScenario Scenario)
{
    public string Tag => $"soak{Index:000}";
}

internal static class SoakScript
{
    public const string Copilot = "Copilot", Claude = "Claude", Codex = "Codex";

    static SoakTurn Q(string prompt, AnswerStyle style) => new(prompt, style);
    static SoakTurn T(string prompt) => new(prompt);
    static SoakTurn Bg(string prompt) => new(prompt, Background: true);
    static readonly SoakTurn Compact = new("/compact");

    // Copilot's ask_user, in the shapes agent-ha-bridge draws differently: a lone choice, free
    // text, a form with a choice, a boolean and text, a choice allowing words, a multi-select.
    static string CopilotChoice(string t) => $"{t}: call ask_user once with the question \"Pick one\" and the choices alpha, beta, gamma, with no free-text answer, then reply with only the choice.";
    static string CopilotText(string t) => $"{t}: call ask_user once with the question \"Soak word?\" and no choices, then reply with only the word you were given.";
    static string CopilotForm(string t) => $"{t}: call ask_user once as a form with fields colour (one of red, green, blue), confirm (boolean) and name (text), then reply with one short line.";
    static string CopilotOther(string t) => $"{t}: call ask_user once with the question \"Pick or write\" and the choices one, two, allowing a free-text answer, then reply with only the answer.";
    static string CopilotMulti(string t) => $"{t}: call ask_user once as a form with one field extras (any of x, y, z), then reply with only the answer.";
    static string Ok(string t) => $"{t}: say ok and nothing else.";

    // The comparison samples every 15 s and counts background work only as agent-ha-bridge
    // lists it, which it does for Copilot alone; an agent that finished in 6 s was never seen.
    // So Copilot's background work sleeps long enough to be sampled several times.
    static string CopilotShell(string t) => $"{t}: start the shell command \"Start-Sleep 60\" in the background without waiting for it, say that you started it, and end your turn.";
    static string CopilotShells(string t) => $"{t}: start two shell commands in the background without waiting for them: \"Start-Sleep 60\" and \"Start-Sleep 75\". Say that you started them, and end your turn.";

    static string ClaudeChoice(string t) => $"{t}: use AskUserQuestion once with the question \"Pick one\" and the options alpha, beta, gamma, then reply with only the choice.";
    static string ClaudeTwo(string t) => $"{t}: use AskUserQuestion once with two questions: \"Colour?\" with options red, green, blue, and \"Size?\" with options small, large. Then reply with one short line.";
    static string ClaudeMulti(string t) => $"{t}: use AskUserQuestion once with the question \"Which extras?\" allowing multiple selections from the options x, y, z, then reply with only the answer.";

    // The scenarios S2 (#174) records transcripts of: nested and background sub-agents.
    public static SoakScenario CopilotNested => new("copilot-nested", Copilot,
    [
        Bg("{t}: use a sub-agent (the task tool) whose own job is to use a further sub-agent to count the files in this folder and report the count. Also start a second agent in the background (do not wait for it) that runs \"git log --oneline -3\" and reports it. When both have reported, summarise in one sentence."),
        Q(CopilotChoice("{t}"), AnswerStyle.Card),
    ], Transcript: "c");

    public static SoakScenario ClaudeNested => new("claude-nested", Claude,
    [
        Bg("{t}: use the Task tool to start two sub-agents in parallel. The first lists the files in this folder. The second must itself use the Task tool to start a further sub-agent that runs \"git log --oneline -3\". Also run the Bash command \"sleep 30\" with run_in_background true. When the sub-agents have finished, summarise in two lines."),
        Q(ClaudeChoice("{t}"), AnswerStyle.Card),
    ], Transcript: "a");

    public static SoakScenario CodexSubagent => new("codex-subagent", Codex,
    [
        Bg("{t}: spawn one sub-agent with spawn_agent to run \"git log --oneline -3\" and report back, wait for it, then reply with its answer in one line."),
        T(Ok("{t}")),
    ], Transcript: "b");

    // A block of the rotation. Resumes take up the session of the same agent ended most recently.
    public static IReadOnlyList<SoakScenario> Block =>
    [
        new("copilot-questions", Copilot,
        [
            Q(CopilotChoice("{t}"), AnswerStyle.Card), Q(CopilotText("{t}"), AnswerStyle.Box), Q(CopilotForm("{t}"), AnswerStyle.Card),
            Q(CopilotOther("{t}"), AnswerStyle.Payload), Q(CopilotMulti("{t}"), AnswerStyle.Card), Q(CopilotText("{t}"), AnswerStyle.Payload),
            Bg(CopilotShell("{t}")), Q(CopilotChoice("{t}"), AnswerStyle.Late),
        ]),
        new("claude-questions", Claude,
        [
            Q(ClaudeChoice("{t}"), AnswerStyle.Card), Q(ClaudeTwo("{t}"), AnswerStyle.Card), Q(ClaudeMulti("{t}"), AnswerStyle.Card),
            Q(ClaudeChoice("{t}"), AnswerStyle.Payload), Q(ClaudeTwo("{t}"), AnswerStyle.Late), Q(ClaudeChoice("{t}"), AnswerStyle.Card),
        ]),
        new("codex-basic", Codex, [T("{t}: run the shell command \"git status --short\" once and reply with one short line."), T(Ok("{t}")), Compact, T(Ok("{t}"))]),
        new("copilot-background", Copilot,
        [
            Q(CopilotChoice("{t}"), AnswerStyle.Card),
            Bg(CopilotShells("{t}")),
            Bg("{t}: start two background agents (do not wait for them). Each runs the shell command \"Start-Sleep 45; git log --oneline -3\" and reports the output. Say that you started them, and end your turn."),
            Q(CopilotText("{t}"), AnswerStyle.Box),
        ]),
        new("claude-background", Claude,
        [
            Q(ClaudeChoice("{t}"), AnswerStyle.Card),
            Bg("{t}: run the Bash command \"sleep 40\" with run_in_background true, say that you started it, and end your turn."),
            Bg("{t}: use the Task tool to start one sub-agent that lists the files in this folder, then summarise in one line."),
            Q(ClaudeChoice("{t}"), AnswerStyle.Late),
        ]),
        new("codex-approvals", Codex, [T("{t}: run the shell command \"New-Item -ItemType File -Force {t}.txt\" once and reply with one short line."), T(Ok("{t}"))], AskPermission: true),
        new("copilot-compact", Copilot,
        [
            Q(CopilotText("{t}"), AnswerStyle.Box), T(Ok("{t}")), Compact, Q(CopilotChoice("{t}"), AnswerStyle.Card),
            Q(CopilotForm("{t}"), AnswerStyle.Card), Q(CopilotChoice("{t}"), AnswerStyle.Abandon),
        ]),
        new("claude-compact", Claude,
        [
            Q(ClaudeTwo("{t}"), AnswerStyle.Card), T(Ok("{t}")), Compact, Q(ClaudeChoice("{t}"), AnswerStyle.Card), Q(ClaudeMulti("{t}"), AnswerStyle.Abandon),
        ]),
        new("codex-compact", Codex, [T(Ok("{t}")), T("{t}: run the shell command \"git log --oneline -2\" once and reply with one short line."), Compact, T(Ok("{t}"))]),
        new("copilot-resume", Copilot, [Q(CopilotChoice("{t}"), AnswerStyle.Card), Bg(CopilotShell("{t}")), Q(CopilotText("{t}"), AnswerStyle.Payload)], Resume: true),
        new("claude-resume", Claude, [Q(ClaudeChoice("{t}"), AnswerStyle.Card), T(Ok("{t}"))], Resume: true),
        new("codex-resume", Codex, [T(Ok("{t}")), T(Ok("{t}"))], Resume: true),
    ];

    // The episodes in order: the three transcript scenarios first (S2 is waiting on them), then
    // the block repeated until there are as many as asked for.
    public static IReadOnlyList<SoakEpisode> Plan(int count, bool transcriptsFirst = true)
    {
        var scenarios = new List<SoakScenario>();
        if (transcriptsFirst) { scenarios.AddRange([CopilotNested, ClaudeNested, CodexSubagent]); }
        var block = Block;
        for (var i = 0; scenarios.Count < count; i++) { scenarios.Add(block[i % block.Count]); }
        return [.. scenarios.Take(count).Select((s, i) => new SoakEpisode(i, s))];
    }

    public static string Prompt(SoakTurn turn, SoakEpisode episode) => turn.Prompt.Replace("{t}", episode.Tag, StringComparison.Ordinal);

    public static IReadOnlyList<SoakScenario> All => [CopilotNested, ClaudeNested, CodexSubagent, .. Block];
}

// A question as agent-ha-bridge's card shows it: the decision selector's attributes
// (Set-CopilotMqttDecision) and the field slots' options.
internal sealed record CardQuestion(string DecisionId, string Mode, IReadOnlyList<string> Choices, int FieldCount, bool AnswerOnTap, bool TakesTypedAnswer, bool MultiField)
{
    // What the selector and slots hold when nothing is chosen: never an answer.
    public static readonly IReadOnlySet<string> Placeholders = new HashSet<string>(StringComparer.Ordinal)
        { "Idle", "Awaiting answer...", "Cancel request", "Choose...", "unknown", "unavailable", "" };

    public static CardQuestion? Read(HaState? decision)
    {
        if (decision is null || decision.Attributes.ValueKind != JsonValueKind.Object) { return null; }
        var a = decision.Attributes;
        if (Str(a, "decision_id") is not { Length: > 0 } id) { return null; }
        // An answered question's attributes linger until the daemon clears them; the selector
        // is back at Idle by then, or still Awaiting when it is being asked.
        if (decision.State is "Idle" or "unknown" or "unavailable") { return null; }
        var choices = a.TryGetProperty("choices", out var c) && c.ValueKind == JsonValueKind.Array
            ? [.. c.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.ToString())]
            : new List<string>();
        var fields = a.TryGetProperty("field_count", out var f) && f.ValueKind == JsonValueKind.Number ? f.GetInt32() : 0;
        return new(id, Str(a, "mode") ?? "", choices, fields, Bool(a, "answer_on_tap"), Bool(a, "takes_typed_answer"), Bool(a, "multi_field"));
    }

    static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static bool Bool(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    // Words are a usable answer: a free-text question, or a choice that allows another answer.
    public bool TakesWords => (FieldCount == 0 && Choices.Count == 0) || TakesTypedAnswer;
}

// One call to make on Home Assistant to answer.
internal sealed record CardAction(string Domain, string Service, JsonObject? Data = null, string? Topic = null, string? Payload = null)
{
    public static CardAction Select(string entity, string option) => new("select", "select_option", new() { ["entity_id"] = entity, ["option"] = option });
    public static CardAction Text(string entity, string value) => new("text", "set_value", new() { ["entity_id"] = entity, ["value"] = value });
    public static CardAction Press(string entity) => new("button", "press", new() { ["entity_id"] = entity });
    public static CardAction Publish(string topic, string payload) => new("mqtt", "publish", Topic: topic, Payload: payload);
}

internal static class CardAnswer
{
    public const string Words = "soakword";

    // The calls that answer a question as the dashboard does (agent-ha-bridge's
    // Read-DaemonDecisionAnswer and Read-DaemonFormAnswer): each slot a choice, words in the reply
    // box for a text field, then Send; a lone choice on the selector, then Send; an approval by
    // its tap alone. Box and Payload give words where words are an answer, and fall back to the
    // card's controls where they are not. Pick chooses among a slot's options, so a run varies
    // its answers and a replay of it does not.
    public static IReadOnlyList<CardAction> Plan(string node, CardQuestion q, AnswerStyle style, Func<int, IReadOnlyList<string>> slotOptions, int pick, DateTimeOffset now)
    {
        var decision = $"select.{node}_decision";
        var submit = $"button.{node}_submit";
        var reply = $"text.{node}_reply";
        if (q.AnswerOnTap && q.Choices.Count > 0)
        {
            // An approval: Approve, else the first choice; the tap is the answer.
            var approve = q.Choices.FirstOrDefault(c => c.StartsWith("Approve", StringComparison.OrdinalIgnoreCase) || c.StartsWith("Allow", StringComparison.OrdinalIgnoreCase) || c.StartsWith("Yes", StringComparison.OrdinalIgnoreCase)) ?? q.Choices[0];
            return [CardAction.Select(decision, approve)];
        }
        // A lone schema field with nothing to choose is free text: its slot offers only Idle.
        var words = q.TakesWords || (q.FieldCount == 1 && q.Choices.Count == 0 && slotOptions(1).All(CardQuestion.Placeholders.Contains));
        if (style == AnswerStyle.Payload && words)
        {
            var payload = new JsonObject { ["at"] = now.ToString("o"), ["text"] = Words, ["decision_id"] = q.DecisionId };
            return [CardAction.Publish($"copilot/cli/{node}/replypayload/set", payload.ToJsonString())];
        }
        if (style == AnswerStyle.Box && words && q.FieldCount <= 1)
        {
            return [CardAction.Text(reply, Words), CardAction.Press(submit)];
        }
        var actions = new List<CardAction>();
        if (q.FieldCount > 0)
        {
            var typed = false;
            for (var i = 1; i <= q.FieldCount; i++)
            {
                var options = slotOptions(i).Where(o => !CardQuestion.Placeholders.Contains(o)).ToList();
                if (options.Count == 0)
                {
                    // A text field has no dropdown: it takes the reply box's words.
                    if (!typed) { actions.Insert(0, CardAction.Text(reply, Words)); typed = true; }
                    continue;
                }
                actions.Add(CardAction.Select($"select.{node}_f{i}", options[(pick + i) % options.Count]));
            }
        }
        else if (q.Choices.Count > 0)
        {
            actions.Add(CardAction.Select(decision, q.Choices[pick % q.Choices.Count]));
        }
        else
        {
            actions.Add(CardAction.Text(reply, Words));
        }
        actions.Add(CardAction.Press(submit));
        return actions;
    }
}
