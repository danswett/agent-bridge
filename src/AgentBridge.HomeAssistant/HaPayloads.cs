using System.Globalization;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;

namespace AgentBridge.HomeAssistant;

// What goes into the state and attribute topics for a host's sessions, usage and machine, in
// the shapes agent-ha-bridge's daemon publishes them. Where the daemon builds a value from
// state the host keeps differently (it reads transcripts itself; the host has Session), the
// mapping is here and says which daemon function it stands in for.
public static class HaPayloads
{
    // ResponseMaxChars and ReasoningMaxChars (hooks/agent-bridge-daemon.ps1), and the activity
    // trail's depth, which doubles with Detailed activity because thoughts join it.
    public const int ResponseMaxChars = 6000;
    public const int ReasoningMaxChars = 4000;
    public const int HistoryDepth = 12;
    public const int HistoryDepthDetailed = 24;
    public const string TruncatedNote = "\n\n_(truncated - see terminal)_";

    // Remove-CopilotTemplateMarkup. A session's name comes from its task or its folder, which
    // anyone can choose, and Home Assistant renders templates in the text a dashboard shows:
    // the delimiters are broken with a zero-width space so the text reads the same but is inert.
    public static string NeutraliseTemplates(string text) =>
        string.IsNullOrEmpty(text) ? text : text.Replace("{{", "{\u200B{").Replace("{%", "{\u200B%").Replace("{#", "{\u200B#");

    // Get-CopilotSessionDisplay, Get-ClaudeSessionDisplay and Get-CodexSessionDisplay: the
    // device name, which is also the card's title. Copilot's is its workspace name (the host's
    // title for it); Claude's and Codex's is their folder. Capped at 120 before neutralising.
    public static string SessionName(Session s)
    {
        var shortId = s.Id[..Math.Min(8, s.Id.Length)];
        var name = s.Agent switch
        {
            AgentKind.Claude => "Claude: " + (Leaf(s.Cwd) ?? shortId),
            AgentKind.Codex => "Codex: " + (Leaf(s.Cwd) ?? shortId),
            _ => "Copilot: " + (string.IsNullOrWhiteSpace(s.Title) ? shortId : s.Title.Trim().Trim('"', '\'')),
        };
        if (name.Length > 120) { name = name[..117] + "..."; }
        return NeutraliseTemplates(name);
    }

    static string? Leaf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) { return null; }
        var trimmed = path.TrimEnd('/', '\\');
        var leaf = trimmed[(trimmed.LastIndexOfAny(['/', '\\']) + 1)..];
        return string.IsNullOrWhiteSpace(leaf) ? null : leaf;
    }

    // Get-DaemonEntryKind as published in a session descriptor.
    public static string Kind(AgentKind agent) => agent switch
    {
        AgentKind.Claude => "claude",
        AgentKind.Codex => "codex",
        _ => "copilot",
    };

    // The status sensor's state, from Set-CopilotMqttStatus's vocabulary. A session idle on its
    // own turn but still waiting for background agents or commands it started reads "agents"
    // or "shell", which the card shows in words and not as idle.
    public static string Status(Session s) => s.Status switch
    {
        SessionStatus.Working => "working",
        SessionStatus.Waiting => "waiting",
        SessionStatus.Ending => "ending",
        _ when s.Activity.Background is { Agents: > 0 } => "agents",
        _ when s.Activity.Background is { Shells: > 0 } => "shell",
        _ => "idle",
    };

    // The status sensor's attributes: Add-DaemonTuningAttributes over session, machine,
    // process_id and updated. Settings and counts that are empty or zero are left out, so the
    // card can tell "not known" from a value and never says "0 compactions".
    public static JsonObject StatusAttributes(Session s, string sessionName, string machineName, DateTimeOffset now)
    {
        var a = new JsonObject
        {
            ["session"] = sessionName,
            ["machine"] = machineName,
        };
        if (s.ProcessId is { } pid) { a["process_id"] = pid; }
        a["updated"] = Stamp(now);
        if (!string.IsNullOrWhiteSpace(s.Model)) { a["model"] = s.Model; }
        if (s.Activity.Background is { Agents: > 0 } b) { a["background_agents"] = b.Agents; }
        if (s.Activity.Background is { Shells: > 0 } sh) { a["background_shells"] = sh.Shells; }
        if (s.Activity.Compactions is > 0 and var c) { a["compactions"] = c; }
        return a;
    }

    // The activity sensor's state: what the session is doing in a line, cut to Home Assistant's
    // 255 characters (Set-CopilotMqttActivity). "Running: <tool>" and a reply's first line are
    // the daemon's own wording (Get-ActivityFromEvents); "Thinking" is its word for a working
    // session with nothing newer to say.
    public static string ActivitySummary(Session s)
    {
        string summary;
        if (s.Activity.CurrentTool is { Status: ToolStatus.Running } tool) { summary = "Running: " + tool.Name; }
        else if (FirstLine(s.Activity.Response) is { } line) { summary = line; }
        else if (s.Status == SessionStatus.Working) { summary = "Thinking"; }
        else { summary = "Idle"; }
        return CapState(summary);
    }

    public static string CapState(string text) =>
        text.Length > HaContract.StateMaxChars ? text[..(HaContract.StateMaxChars - 3)] + "..." : text;

    // Inline agents show their thinking in the reply stream, in order; anything else keeps a
    // separate reasoning expander (InlineReasoning in hooks/daemon-agents.ps1).
    public static bool InlineReasoning(AgentKind agent) => agent is AgentKind.Copilot or AgentKind.Claude or AgentKind.Codex;

    // The activity sensor's attributes for a session: the daemon's activity detail (session,
    // machine, verbose, updated, driver, history) with the card's text added as
    // Add-DaemonCardText does. driver is "human" until replies and launches arrive through
    // this host (docs/ha-connector.md, later PRs). thinking says the newest line is the
    // reasoning rather than the reply; the connector knows which changed last, and without it
    // the reasoning is newest only while there is no reply at all.
    public static JsonObject ActivityAttributes(Session full, string sessionName, string machineName, bool verbose, DateTimeOffset now, string driver = "human", bool? thinking = null, string? answerConsumedAt = null)
    {
        var detail = new JsonObject
        {
            ["session"] = sessionName,
            ["machine"] = machineName,
            ["verbose"] = verbose,
            ["updated"] = Stamp(now),
            ["driver"] = driver,
            ["history"] = History(full, verbose),
        };
        var response = full.Activity.Response;
        var reasoning = full.Activity.Reasoning;
        var isThinking = !string.IsNullOrWhiteSpace(reasoning) && (thinking ?? string.IsNullOrWhiteSpace(response));
        AddCardText(detail, InlineReasoning(full.Agent), verbose, response, isThinking ? reasoning : response, isThinking, reasoning);
        // Add-DaemonAnswerStamp: the reply-card publish last used, on every update, so a card
        // that sent it can tell it was.
        if (!string.IsNullOrWhiteSpace(answerConsumedAt)) { detail["answer_consumed_at"] = answerConsumedAt; }
        return detail;
    }

    // Add-DaemonCardText, from the remembered text: the response (or, for an inline agent with
    // Detailed activity on, the newest line of either kind, with response_kind saying which),
    // and for an agent that does not interleave, the reasoning expander when Detailed is on.
    internal static void AddCardText(JsonObject detail, bool inline, bool verbose, string? lastResponse, string? lastMessage, bool lastIsThinking, string? lastReasoning)
    {
        var shown = lastResponse ?? "";
        var kind = "text";
        if (inline && verbose && !string.IsNullOrWhiteSpace(lastMessage))
        {
            shown = lastMessage;
            if (lastIsThinking) { kind = "reasoning"; }
        }
        if (!string.IsNullOrWhiteSpace(shown))
        {
            if (shown.Length > ResponseMaxChars) { shown = shown[..ResponseMaxChars].TrimEnd() + TruncatedNote; }
            detail["response"] = shown;
            detail["response_kind"] = kind;
        }
        if (verbose && !inline && !string.IsNullOrWhiteSpace(lastReasoning))
        {
            var reasoning = lastReasoning;
            if (reasoning.Length > ReasoningMaxChars) { reasoning = reasoning[..ReasoningMaxChars].TrimEnd() + "\u2026"; }
            detail["reasoning"] = reasoning;
        }
    }

    // The activity trail, in the daemon's words: "Reading your message" when the turn began
    // with one, "Running: <tool>" per tool call, a thought line with Detailed activity on, and
    // the reply's first line. Newest last, as deep as the daemon keeps it.
    static JsonArray History(Session full, bool verbose)
    {
        var items = new List<string>();
        if (!string.IsNullOrWhiteSpace(full.Activity.UserMessage)) { items.Add("Reading your message"); }
        foreach (var tool in full.Activity.History ?? []) { items.Add("Running: " + tool.Name); }
        if (verbose && ThoughtLine(full.Activity.Reasoning) is { Length: > 0 } thought) { items.Add(thought); }
        if (FirstLine(full.Activity.Response) is { } line) { items.Add(line); }
        var depth = verbose ? HistoryDepthDetailed : HistoryDepth;
        return [.. items.Skip(Math.Max(0, items.Count - depth)).Select(i => (JsonNode)i)];
    }

    // Get-BridgeThoughtLine: a thought as one line of the trail, its heading markup off.
    public static string ThoughtLine(string? text, int maxChars = 120)
    {
        if (string.IsNullOrWhiteSpace(text)) { return ""; }
        var first = text.Replace("\r", "").Split('\n').FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
        if (first is null) { return ""; }
        var line = first.Trim();
        line = System.Text.RegularExpressions.Regex.Replace(line, @"^\s*#{1,6}\s+", "");
        line = System.Text.RegularExpressions.Regex.Replace(line, @"^\*\*(.+?)\*\*\s*$", "$1");
        line = line.Trim();
        if (line.Length == 0) { return ""; }
        if (line.Length > maxChars) { line = line[..Math.Max(1, maxChars - 1)].TrimEnd() + "\u2026"; }
        return "Thinking: " + line;
    }

    static string? FirstLine(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0);

    // What the launch card serves, for its capability flags: whether there is a card, and its
    // profile (Agency with profiles), resume (any agent) and agent (more than one) rows.
    public sealed record LaunchRows(bool Profile, bool Resume, bool Agent);

    // The capability flags a machine's session list carries (Get-DaemonLaunchCapabilities). A
    // peer rendering the shared dashboard draws only the rows a machine says it serves: the launch
    // card's when the host launches here, and the Detailed activity switch only when the helper is
    // there to draw. launchRequest says an agent's launch request (#227) is read: a number, so the
    // contract can change shape without an older reader taking it for the same thing.
    public static JsonObject Capabilities(bool detailed, bool dev, LaunchRows? launch = null) => new()
    {
        ["newSession"] = launch is not null,
        ["profile"] = launch?.Profile == true,
        ["resume"] = launch?.Resume == true,
        ["agent"] = launch?.Agent == true,
        ["tuning"] = launch is not null,
        ["permissions"] = launch is not null,
        ["launchRequest"] = launch is not null ? 1 : 0,
        ["detailed"] = detailed,
        ["resumeShare"] = false,
        ["dev"] = dev,
        ["fleet"] = "",
    };

    public sealed record Descriptor(string Name, string Machine, string Node, string Kind);

    // Publish-CopilotMqttGlobalStatus's attributes. discoveryHeld is what holds the host's
    // discovery, as Get-DaemonDiscoveryHoldSummary words it for the status line, or "".
    public static JsonObject GlobalAttributes(IEnumerable<Descriptor> sessions, string machineName, string slug, JsonObject capabilities, DateTimeOffset now, string discoveryHeld = "") => new()
    {
        ["sessions"] = new JsonArray([.. sessions.Select(d => (JsonNode)new JsonObject
        {
            ["name"] = d.Name,
            ["machine"] = d.Machine,
            ["node"] = d.Node,
            ["kind"] = d.Kind,
        })]),
        ["machine"] = machineName,
        ["machine_slug"] = slug,
        ["capabilities"] = capabilities,
        ["resumable"] = new JsonArray(),
        ["discovery_held"] = discoveryHeld,
        ["updated"] = Stamp(now),
    };

    // New-BridgeUsageRecord and Get-BridgeUsageWindow, from the host's reading. Percent is how
    // much is gone; the state is the window closest to running out, or "unknown" when there is
    // no reading (an empty bar would read as nothing used).
    public static (string Client, string Label, string State, JsonObject Attributes)? Usage(AgentUsage u, string machineName, string slug, DateTimeOffset now)
    {
        var client = u.Agent switch { AgentKind.Copilot => "copilot", AgentKind.Claude => "claude", AgentKind.Codex => "codex", _ => null };
        if (client is null) { return null; }
        var windows = new JsonArray();
        double? headline = null;
        foreach (var w in u.Windows)
        {
            var window = new JsonObject { ["key"] = w.Key, ["label"] = w.Label, ["percent"] = w.Percent };
            if (w.Used is { } used) { window["used"] = used; }
            if (w.Limit is { } limit) { window["limit"] = limit; }
            if (!string.IsNullOrEmpty(w.Unit)) { window["unit"] = w.Unit; }
            if (w.ResetsAt is { } resets) { window["resets_at"] = resets.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture); }
            if (w.ResetsAt is { } r && w.StartsAt is { } s && r > s)
            {
                var span = (r - s).TotalSeconds;
                var gone = (now.ToUniversalTime() - s).TotalSeconds;
                window["elapsed"] = Math.Round(100.0 * Math.Min(1.0, Math.Max(0.0, gone / span)), 1);
            }
            windows.Add(window);
            if (headline is null || w.Percent > headline) { headline = w.Percent; }
        }
        var attributes = new JsonObject
        {
            ["client"] = client,
            ["label"] = u.Label,
            ["account"] = u.Account ?? "",
            ["account_id"] = u.AccountKey ?? "",
            ["plan"] = u.Plan ?? "",
            ["source"] = u.Source.ToString().ToLowerInvariant(),
            ["windows"] = windows,
            ["error"] = u.Error ?? "",
            ["measured_at"] = u.MeasuredAt.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture),
            ["machine"] = machineName,
            ["machine_slug"] = slug,
            ["updated"] = Stamp(now),
        };
        var state = headline is { } h ? h.ToString(CultureInfo.InvariantCulture) : "unknown";
        return (client, u.Label, state, attributes);
    }

    // Publish-CopilotMqttUpdate's state and attributes, for a host that offers no update through
    // Home Assistant yet: the version it runs, reported as current, so the dashboard shows the
    // version and draws no install button (there is nothing here to press it).
    public static JsonObject UpdateState(string installedVersion) => new()
    {
        ["installed_version"] = installedVersion,
        ["title"] = HaContract.UpdateTitle,
        ["release_summary"] = "",
        ["in_progress"] = false,
        ["update_percentage"] = null,
        ["latest_version"] = installedVersion,
    };

    public static JsonObject UpdateAttributes() => new() { ["stage"] = "", ["stage_detail"] = "" };

    // [DateTimeOffset]::Now.ToString('o'), as every daemon timestamp is written.
    public static string Stamp(DateTimeOffset now) => now.ToLocalTime().ToString("o", CultureInfo.InvariantCulture);
}
