using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentBridge.HomeAssistant;

// What the connector notifies phones with through Home Assistant: agent-ha-bridge's notifications
// and answerButtons settings, the same names and defaults (decision-bridge-common.ps1), kept in
// the homeAssistant section here.
//
//   notifications   one notify-style service (notify.notify, notify.mobile_app_<phone>,
//                   ticker.notify) the hooks pushed a question, a permission prompt and a
//                   finished turn to. Off by default.
//   answerButtons   the mobile_app notify services a pending question goes to with its options as
//                   buttons, answered by tapping one (daemon-decision-notify.ps1). On, but with no
//                   services, so nothing is sent until a phone is named.
public sealed record HaNotifySettings
{
    public bool Enabled { get; init; }
    public string Service { get; init; } = "notify.notify";
    public string TickerCategory { get; init; } = "";
    public bool AnswerButtons { get; init; } = true;
    public IReadOnlyList<string> AnswerButtonServices { get; init; } = [];
    public int MaxButtons { get; init; } = 6;
    public string Icon { get; init; } = "mdi:chat-question";
    public string IconColor { get; init; } = "#FFFFFF";
    public string Color { get; init; } = "#FF9F0A";
    public string InterruptionLevel { get; init; } = "time-sensitive";

    public static readonly HaNotifySettings Default = new();

    // Answer buttons go out only to phones that are named.
    public bool ButtonsOn => AnswerButtons && AnswerButtonServices.Count > 0;

    // The two subsections of homeAssistant; every value that is wrong is a problem, as the rest of
    // the section's are.
    public static HaNotifySettings Read(JsonElement section, List<string> problems)
    {
        var settings = Default;
        if (Sub(section, "notifications", problems) is { } n)
        {
            settings = settings with
            {
                Enabled = Bool(n, "notifications", "enabled", settings.Enabled, problems),
                Service = ServiceName(n, "notifications", "service", problems) ?? settings.Service,
                TickerCategory = Text(n, "notifications", "tickerCategory", problems, allowEmpty: true) ?? settings.TickerCategory,
            };
        }
        if (Sub(section, "answerButtons", problems) is { } b)
        {
            settings = settings with
            {
                AnswerButtons = Bool(b, "answerButtons", "enabled", settings.AnswerButtons, problems),
                MaxButtons = Int(b, "answerButtons", "maxButtons", settings.MaxButtons, problems),
                Icon = Text(b, "answerButtons", "icon", problems, allowEmpty: true) ?? settings.Icon,
                IconColor = Text(b, "answerButtons", "iconColor", problems, allowEmpty: true) ?? settings.IconColor,
                Color = Text(b, "answerButtons", "color", problems, allowEmpty: true) ?? settings.Color,
                InterruptionLevel = Text(b, "answerButtons", "interruptionLevel", problems, allowEmpty: true) ?? settings.InterruptionLevel,
            };
            if (b.TryGetProperty("services", out var s) && s.ValueKind != JsonValueKind.Null)
            {
                if (s.ValueKind == JsonValueKind.Array && s.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String && HaNotify.Split(x.GetString()!) is not null))
                {
                    settings = settings with { AnswerButtonServices = [.. s.EnumerateArray().Select(x => x.GetString()!.Trim()).Distinct(StringComparer.Ordinal)] };
                }
                else { problems.Add("homeAssistant.answerButtons.services is not a list of domain.service names"); }
            }
        }
        return settings;
    }

    static JsonElement? Sub(JsonElement section, string name, List<string> problems)
    {
        if (!section.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) { return null; }
        if (v.ValueKind == JsonValueKind.Object) { return v; }
        problems.Add($"homeAssistant.{name} is not an object");
        return null;
    }

    static bool Bool(JsonElement s, string section, string name, bool fallback, List<string> problems)
    {
        if (!s.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) { return fallback; }
        if (v.ValueKind is JsonValueKind.True or JsonValueKind.False) { return v.GetBoolean(); }
        problems.Add($"homeAssistant.{section}.{name} is not true or false");
        return fallback;
    }

    static int Int(JsonElement s, string section, string name, int fallback, List<string> problems)
    {
        if (!s.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) { return fallback; }
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) && i >= 0) { return i; }
        problems.Add($"homeAssistant.{section}.{name} is not a whole number of zero or more");
        return fallback;
    }

    static string? Text(JsonElement s, string section, string name, List<string> problems, bool allowEmpty)
    {
        if (!s.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) { return null; }
        if (v.ValueKind == JsonValueKind.String && (allowEmpty || !string.IsNullOrWhiteSpace(v.GetString()))) { return v.GetString()!.Trim(); }
        problems.Add($"homeAssistant.{section}.{name} is not text");
        return null;
    }

    static string? ServiceName(JsonElement s, string section, string name, List<string> problems)
    {
        var text = Text(s, section, name, problems, allowEmpty: false);
        if (text is null || HaNotify.Split(text) is not null) { return text; }
        problems.Add($"homeAssistant.{section}.{name} is not a domain.service name");
        return null;
    }
}

// A pending question as the daemon's decision marker carries it, which is what its notification
// is built from (Write-CopilotDecisionMarker).
public sealed record NotifyField(string Label, IReadOnlyList<string> Options, bool IsText, bool MultiSelect);

public sealed record NotifyMarker(string DecisionId, string Question, string Mode, IReadOnlyList<string> Choices, IReadOnlyList<NotifyField> Fields, bool TerminalOnly);

// A tapped notification button, as Get-BridgeAnswerButtonChoice reads the event.
public sealed record TapChoice(string SessionId, string DecisionId, int Index, string ReplyText);

// agent-ha-bridge's notification payloads, ported from hooks/daemon-decision-notify.ps1,
// hooks/decision-bridge-common.ps1 (Send-BridgeNotification), hooks/bridge-adapter.ps1 and the
// Copilot and Claude hooks, and checked against what those functions send
// (Fixtures/ha-notify.json).
public static partial class HaNotify
{
    public const string ClearMessage = "clear_notification";
    public const string ActionEvent = "mobile_app_notification_action";

    // "domain.service", or null when it is not one (the daemon skips such an entry).
    public static (string Domain, string Service)? Split(string service)
    {
        var parts = service.Trim().Split('.');
        return parts.Length == 2 && parts[0].Length > 0 && parts[1].Length > 0 ? (parts[0], parts[1]) : null;
    }

    // The marker the daemon's hook would have written for the question the card is armed with.
    public static NotifyMarker Marker(DecisionPlan plan) => new(
        plan.QuestionId, plan.Question,
        plan.Choices.Count > 0 || plan.Fields.Count > 0 ? "multiple_choice" : "freeform",
        plan.Choices,
        [.. plan.Fields.Select(f => new NotifyField(f.Label, f.Options, f.IsText, f.MultiSelect))],
        plan.TerminalOnly);

    // Get-BridgeAnswerButtonTag.
    public static string Tag(string sessionId) => "bridge_decision_" + HaContract.NodeId(sessionId);

    // Get-BridgeAnswerButtonOptions: the labels a question can be answered with by tapping, or none.
    public static IReadOnlyList<string> Options(NotifyMarker marker)
    {
        var (from, indexes) = OptionSources(marker);
        return [.. indexes.Select(i => from[i])];
    }

    // Which entries the buttons are, in order: positions in the question's choices, or in its lone
    // field's options when it has no choices. A tap names a button by position, and labels need
    // not be unique, so the answer is found by this, never by looking its label up.
    public static (IReadOnlyList<string> From, IReadOnlyList<int> Indexes) OptionSources(NotifyMarker marker)
    {
        if (marker.TerminalOnly || marker.Mode != "multiple_choice" || marker.Fields.Count > 1) { return ([], []); }
        IReadOnlyList<string> from = marker.Choices;
        if (!from.Any(c => !string.IsNullOrWhiteSpace(c)))
        {
            from = marker.Fields.Count == 1 && !marker.Fields[0].IsText ? marker.Fields[0].Options : [];
        }
        return (from, [.. from.Select((c, i) => (c, i)).Where(x => !string.IsNullOrWhiteSpace(x.c) && !OtherEntry().IsMatch(x.c)).Select(x => x.i)]);
    }

    [GeneratedRegex(@"^\s*other\s*\(type your answer\)\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex OtherEntry();

    // Get-BridgeAnswerButtonActions. textAction overrides whether the text action is offered; by
    // default it is, as the daemon offers it, for anything but a form or a terminal-only question.
    public static JsonArray Actions(NotifyMarker marker, string decisionId, IReadOnlyList<string> options, int maxButtons, bool? textAction = null)
    {
        var key = Regex.Replace(decisionId, "[^a-zA-Z0-9]", "");
        if (key.Length > 12) { key = key[^12..]; }
        var actions = new JsonArray();
        var limit = Math.Max(0, Math.Min(maxButtons, options.Count));
        for (var i = 0; i < limit; i++)
        {
            actions.Add(new JsonObject { ["action"] = $"BRIDGE_{key}_{i}", ["title"] = Title(options[i]) });
        }
        if (textAction ?? (!marker.TerminalOnly && marker.Fields.Count <= 1))
        {
            actions.Add(new JsonObject
            {
                ["action"] = $"BRIDGE_{key}_text",
                ["title"] = "Type an answer",
                ["behavior"] = "textInput",
                ["textInputButtonTitle"] = "Send",
                ["textInputPlaceholder"] = "Your answer",
            });
        }
        return actions;
    }

    // Get-BridgeAnswerButtonTitle: short enough for a Lock Screen, its leading marker kept.
    public static string Title(string? text, int maxLength = 30)
    {
        var flat = Whitespace().Replace(text ?? "", " ").Trim();
        if (flat.Length <= maxLength) { return flat; }
        var cut = flat[..maxLength];
        var space = cut.LastIndexOf(' ');
        if (space >= (int)(maxLength * 0.5)) { cut = cut[..space]; }
        return cut.TrimEnd(' ', ',', '.', ';', ':') + "...";
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();

    // Get-BridgeAnswerButtonPayload: an ordinary notification, its buttons, and the identity a tap
    // echoes back.
    public static JsonObject Payload(string sessionId, string sessionName, NotifyMarker marker, JsonArray actions, string dashboardUrlPath, string machineName,
        string icon = "mdi:chat-question", string iconColor = "#FFFFFF", string color = "#FF9F0A", string interruptionLevel = "time-sensitive")
    {
        var question = Whitespace().Replace(marker.Question, " ").Trim();
        if (string.IsNullOrWhiteSpace(question)) { question = "This session is waiting for an answer."; }
        var data = new JsonObject
        {
            ["tag"] = Tag(sessionId),
            ["url"] = $"/{dashboardUrlPath}/decision",
            ["actions"] = actions.DeepClone(),
            ["group"] = "agent-bridge",
            ["action_data"] = new JsonObject { ["bridge"] = "decision", ["session"] = sessionId, ["decision"] = marker.DecisionId },
        };
        if (!string.IsNullOrWhiteSpace(icon))
        {
            data["notification_icon"] = icon;
            data["notification_icon_color"] = iconColor;
            data["color"] = color;
        }
        if (!string.IsNullOrWhiteSpace(interruptionLevel)) { data["interruption-level"] = interruptionLevel; }
        if (!string.IsNullOrWhiteSpace(machineName)) { data["subtitle"] = $"{machineName} - waiting on you"; }
        return new JsonObject { ["title"] = sessionName, ["message"] = question, ["data"] = data };
    }

    // Clear-DaemonAnswerButtons' payload: the question's notification withdrawn by its tag.
    public static JsonObject Clear(string sessionId) =>
        new() { ["message"] = ClearMessage, ["data"] = new JsonObject { ["tag"] = Tag(sessionId) } };

    // Get-BridgeAnswerButtonChoice: what a tap means, or null when the event is not one of ours.
    public static TapChoice? Choice(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty("action_data", out var ad) || ad.ValueKind != JsonValueKind.Object) { return null; }
        if (Str(ad, "bridge") != "decision") { return null; }
        var session = Str(ad, "session");
        var decision = Str(ad, "decision");
        if (string.IsNullOrWhiteSpace(session) || string.IsNullOrWhiteSpace(decision)) { return null; }
        var index = -1;
        if (Str(data, "action") is { } action && ActionIndex().Match(action) is { Success: true } m && int.TryParse(m.Groups[1].Value, out var i)) { index = i; }
        return new TapChoice(session, decision, index, Str(data, "reply_text") ?? "");
    }

    [GeneratedRegex(@"_(\d+)$")]
    private static partial Regex ActionIndex();

    static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind != JsonValueKind.Null
        ? (v.ValueKind == JsonValueKind.String ? v.GetString() : v.ToString()) : null;

    // Format-BridgeNotificationTitle.
    public static string NotificationTitle(string title) => title.Length > 190 ? title[..187] + "..." : title;

    // Send-BridgeNotification's body: a ticker.* service also gets its own fields.
    public static JsonObject Notification(string service, string title, string message, string tickerCategory, string dashboardUrlPath)
    {
        var data = new JsonObject { ["title"] = title, ["message"] = message };
        if (Split(service) is ("ticker", _))
        {
            data["category"] = tickerCategory;
            data["actions"] = "none";
            data["navigate_to"] = $"/{dashboardUrlPath}/decision";
            data["expiration"] = 8;
        }
        return data;
    }

    // Send-BridgeResponseNotification: the title and capped preview of a finished turn, or null for
    // an empty one. Copilot's hook words it its own way.
    public static (string Title, string Message)? Response(string sessionName, string? response, bool copilot)
    {
        if (string.IsNullOrWhiteSpace(response)) { return null; }
        var (prefix, label) = copilot ? ("Copilot response", "the Agent Sessions dashboard") : ("Response", "the dashboard");
        var preview = response;
        if (preview.Length > 880) { preview = preview[..880].TrimEnd() + $"...\n\nFull response is on {label}."; }
        return (NotificationTitle($"{prefix}: {sessionName}"), preview);
    }

    // The Copilot hook's question push (Invoke-CopilotAskUserHook).
    public static (string Title, string Message) CopilotQuestion(string sessionName, string machine, string question, IReadOnlyList<string> choices)
    {
        IEnumerable<string> lines = choices.Count > 0
            ? [$"Session: {sessionName}", $"Machine: {machine}", "", question, "", string.Join("\n", choices.Select((c, i) => $"{i + 1}. {c}")), "", "Answer in the terminal or on the Agent Sessions dashboard."]
            : [$"Session: {sessionName}", $"Machine: {machine}", "", question, "", "Answer in the terminal or in the Reply box on the Agent Sessions dashboard."];
        return (NotificationTitle($"Copilot: {sessionName}"), Cap(string.Join("\n", lines)));
    }

    // The Claude hook's question push (Invoke-ClaudeAskUserQuestionHook).
    public static (string Title, string Message) ClaudeQuestion(string sessionName, string machine, string question, IReadOnlyList<string> choices, bool terminalOnly)
    {
        var numbered = choices.Count > 0 ? string.Join("\n", choices.Select((c, i) => $"{i + 1}. {c}")) + "\n" : "";
        var body = string.Join("\n", $"Session: {sessionName}", $"Machine: {machine}", "", question, "", numbered,
            terminalOnly ? "Answer this one in the terminal." : "Answer in the terminal or on the dashboard.");
        return (NotificationTitle(sessionName), Cap(body));
    }

    // The Claude hook's push when Claude is blocked on the person (Invoke-ClaudeNotificationHook).
    public static (string Title, string Message) ClaudeWaiting(string sessionName, string machine, string? message)
    {
        var said = string.IsNullOrWhiteSpace(message) ? "Claude is waiting for you." : message;
        if (said.Length > 600) { said = said[..597] + "..."; }
        var body = string.Join("\n", $"Session: {sessionName}", $"Machine: {machine}", "", said, "", "Answer in the terminal, or try the Reply box on the dashboard.");
        return (NotificationTitle($"Waiting: {sessionName}"), body);
    }

    // The Copilot hook's permission push (Invoke-CopilotPermissionHook), with the prompt's own words
    // where the host has them.
    public static (string Title, string Message) CopilotPermission(string? message) =>
        ("Copilot permission needed", string.IsNullOrWhiteSpace(message) ? "Copilot CLI is waiting for your input." : message);

    // The Codex hook's approval push (Invoke-CodexHook).
    public static (string Title, string Message) CodexApproval(string sessionName, string question) =>
        (NotificationTitle($"Approval needed: {sessionName}"), question);

    static string Cap(string body) => body.Length > 950 ? body[..947] + "..." : body;
}
