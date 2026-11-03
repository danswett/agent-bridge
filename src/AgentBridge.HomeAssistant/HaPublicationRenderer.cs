using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentBridge.HomeAssistant;

internal sealed record HaFleetSession(string Node, string Name, string Machine, string Kind = "");
internal sealed record HaFleetMachine(string Slug, string Machine, bool IncludeProfile = false, bool IncludeResume = false,
    bool IncludeAgent = false, bool IncludeTuning = false, bool IncludePermissions = false, bool IncludeDetailed = false,
    bool IsDev = false, bool Online = true, IReadOnlyList<string>? SessionNodes = null);
internal sealed record HaRenderedDashboard(JsonObject Config, string InputHash, IReadOnlyList<string> RenderedNodes, string CardUrl);

internal static partial class HaPublicationRenderer
{
    const string Prefix = "AgentBridge.HomeAssistant.Publication.";
    static readonly JsonObject Templates = JsonNode.Parse(Read("session-templates.json"))!["sessions"]!.AsObject();
    public static readonly string CardUrl = "data:text/javascript;base64," +
        Convert.ToBase64String(Bytes("agent-bridge-reply-card.js")) + "#agent-bridge-reply-card.js?v=1.42.0";
    public static readonly HaPublicationArtifact Card = HaPublicationArtifact.ReadInlineCard(CardUrl);
    // Include all output-affecting helpers/constants, not only a nominal renderer
    // version. Normalize source line endings so Windows and Unix build the same fence.
    public static readonly HaPublicationArtifact Artifact = HaPublicationArtifact.Read(JsonSerializer.SerializeToElement(new
    {
        version = "1.15.0",
        hash = HaPublicationJson.Hash(string.Join("\n", new[] { "HaPublicationRenderer.cs", "HaPublicationWriter.cs", "HaPublicationSelector.cs", "HaPublicationFleet.cs", "HaConnector.Publication.cs", "HaPublicationPolicy.cs", "HaContract.cs", "session-templates.json" }
            .Select(name => name + "\n" + Read(name).Replace("\r\n", "\n", StringComparison.Ordinal)))),
    }));

    static string Read(string name)
        => Encoding.UTF8.GetString(Bytes(name));

    static byte[] Bytes(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(Prefix + name)
            ?? throw new InvalidDataException("Missing native publication asset: " + name);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }

    public static bool TrustCard(string url)
    {
        if (!url.StartsWith("data:", StringComparison.Ordinal)) { return true; }
        try
        {
            var served = HaPublicationArtifact.ReadInlineCard(url);
            return served.VersionText != Card.VersionText || served.Hash == Card.Hash;
        }
        catch (InvalidDataException) { return false; }
    }

    [GeneratedRegex(@"[?&]v=([0-9]+(?:\.[0-9]+){1,3})", RegexOptions.CultureInvariant)]
    private static partial Regex CardVersion();
    [GeneratedRegex("agent_bridge_templatenode|__BRIDGE_SESSION_NAME__|__BRIDGE_MACHINE_NAME__", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateVariable();

    static bool SafeId(string value) => value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
    static JsonArray Array(IEnumerable<string> values) => new([.. values.Select(s => JsonValue.Create(s))]);
    static JsonArray Array(IEnumerable<JsonObject> values) => new([.. values.Cast<JsonNode?>()]);
    static JsonObject Markdown(string content) => new() { ["type"] = "markdown", ["content"] = content };
    static JsonObject Stack(IEnumerable<JsonObject> cards) => new() { ["type"] = "vertical-stack", ["cards"] = Array(cards) };
    static JsonObject Row(string entity, string? name = null)
    {
        var row = new JsonObject { ["entity"] = entity };
        if (name is not null) { row["name"] = name; }
        return row;
    }
    static JsonObject Entities(IEnumerable<JsonObject> rows, string? title = null, bool? toggle = null)
    {
        var card = new JsonObject { ["type"] = "entities", ["entities"] = Array(rows) };
        if (title is not null) { card["title"] = title; }
        if (toggle is not null) { card["show_header_toggle"] = toggle; }
        return card;
    }
    static JsonObject Conditional(JsonArray conditions, JsonObject card) => new() { ["type"] = "conditional", ["conditions"] = conditions, ["card"] = card };

    public static HaRenderedDashboard Render(IReadOnlyCollection<HaFleetSession> sessions, IReadOnlyCollection<HaFleetMachine> machines,
        string selector, string cardUrl, bool? cardTrusted = null)
    {
        if (machines.Count == 0 || machines.Any(m => !SafeId(m.Slug)) || sessions.Any(s => !SafeId(s.Node)) ||
            machines.Select(m => m.Slug).Distinct(StringComparer.OrdinalIgnoreCase).Count() != machines.Count ||
            sessions.Select(s => s.Node).Distinct(StringComparer.OrdinalIgnoreCase).Count() != sessions.Count ||
            machines.Any(m => m.SessionNodes?.Any(n => !SafeId(n)) == true) ||
            (selector.Length != 0 && (!selector.StartsWith("input_select.", StringComparison.Ordinal) || !SafeId(selector["input_select.".Length..]))))
        {
            throw new InvalidDataException("Ambiguous or unsafe retained fleet publication input.");
        }
        var all = machines.OrderBy(m => m.Slug, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var orderedSessions = sessions.OrderBy(s => s.Node, StringComparer.CurrentCultureIgnoreCase).ToArray();
        var online = all.Where(m => m.Online).ToArray();
        if (online.Length == 0) { online = all; }
        var trusted = string.IsNullOrWhiteSpace(cardUrl) || (cardTrusted ?? TrustCard(cardUrl));
        var match = CardVersion().Match(cardUrl);
        var served = trusted && match.Success && Version.TryParse(match.Groups[1].Value, out var version) ? version : new Version(0, 0, 0);
        bool Gate(string minimum) => !string.IsNullOrWhiteSpace(cardUrl) && served >= Version.Parse(minimum);
        var decisions = orderedSessions.Select(s => $"select.{s.Node}_decision").ToArray();
        var cards = new List<JsonObject>();
        var usage = all.SelectMany(m => new[] { "copilot", "claude", "codex" }.Select(c => HaContract.MachineEntityId("sensor", "usage_" + c, m.Slug))).ToArray();
        if (Gate("1.25.0"))
        {
            cards.Add(new JsonObject { ["type"] = "custom:agent-bridge-usage-card", ["title"] = "Agent usage",
                ["entities"] = Array(all.SelectMany(m => HaContract.UsageSensorKeys.Select(key => HaContract.MachineEntityId("sensor", "usage_" + key, m.Slug)))) });
        }
        if (Gate("1.19.0"))
        {
            cards.Add(new JsonObject
            {
                ["type"] = "custom:agent-bridge-status-card", ["title"] = "Agent sessions", ["decisions"] = Array(decisions),
                ["machines"] = Array(all.Select(m =>
                {
                    string Id(string domain, string key) => HaContract.MachineEntityId(domain, key, m.Slug);
                    var entry = new JsonObject { ["machine"] = m.Machine, ["online"] = Id("binary_sensor", "online"),
                        ["sessions"] = Id("sensor", "sessions"), ["version"] = Id("update", "update") };
                    if (m.IncludeDetailed) { entry["detailed"] = Id("input_boolean", "detailed_activity"); }
                    if (Gate("1.31.0")) { entry["install"] = Id("button", "install_update"); }
                    if (Gate("1.40.0")) { entry["check"] = Id("button", "check_updates"); }
                    if (m.IsDev) { entry["dev"] = true; }
                    if (Gate("1.20.0")) { entry["forget"] = Array(HaContract.ForgetTopics(m.Slug, m.SessionNodes ?? [])); }
                    return entry;
                })),
            });
        }
        else
        {
            var live = "{{ " + string.Join(" + ", online.Select(m => $"states('{HaContract.MachineEntityId("sensor", "sessions", m.Slug)}')|int(0)")) + " }}";
            var pending = "{% set dc = [" + string.Join(",", decisions.Select(s => "'" + s + "'")) + "] %}{{ dc | map('states') | reject('in',['Idle','unavailable','unknown','']) | list | count }}";
            var summary = $"**Live sessions:** {live} &bull; **Pending decisions:** {pending}";
            if (all.Length == 1) { summary += $" &bull; **Bridge** {{{{ state_attr('{HaContract.MachineEntityId("update", "update", online[0].Slug)}', 'installed_version') or '?' }}}}{(online[0].IsDev ? " (dev)" : "")}"; }
            cards.Add(Stack([Markdown("## Agent sessions\n\n" + summary)]));
            cards.Add(Stack(new[] { Markdown("### Machines") }.Concat(all.Select(m =>
            {
                string Id(string domain, string key) => HaContract.MachineEntityId(domain, key, m.Slug);
                var line = $"{{% if is_state('{Id("binary_sensor", "online")}','on') %}}🟢 **{m.Machine}** &bull; " +
                    $"{{{{ states('{Id("sensor", "sessions")}')|int(0) }}}} session(s) &bull; " +
                    $"{{{{ state_attr('{Id("update", "update")}','installed_version') or '?' }}}}{(m.IsDev ? " (dev)" : "")}" +
                    $"{{% else %}}⚪ **{m.Machine}** &bull; offline{{% endif %}}";
                return m.IncludeDetailed ? new JsonObject { ["type"] = "horizontal-stack",
                    ["cards"] = new JsonArray(Markdown(line), Entities([Row(Id("input_boolean", "detailed_activity"), "Detail")])) } : Markdown(line);
            }))));
        }
        if (!Gate("1.31.0"))
        {
            foreach (var m in online)
            {
                var id = HaContract.MachineEntityId("update", "update", m.Slug);
                cards.Add(Conditional([new JsonObject { ["entity"] = id, ["state"] = "on" }],
                    Entities([Row(id, "Version"), Row(HaContract.MachineEntityId("button", "install_update", m.Slug), "Install now")],
                        all.Length > 1 ? $"Bridge update available on {m.Machine}" : "Bridge update available")));
            }
        }
        if (Gate("1.12.0"))
        {
            var launch = new JsonObject { ["type"] = "custom:agent-bridge-launch-card", ["title"] = "Start a new session", ["machines"] = Array(online.Select(m =>
            {
                string Id(string domain, string key) => HaContract.MachineEntityId(domain, key, m.Slug);
                var entry = new JsonObject { ["machine"] = m.Machine, ["workspace"] = Id("select", "new_workspace"), ["prompt"] = Id("text", "new_prompt"),
                    ["launch"] = Id("button", "new_session"), ["result"] = Id("sensor", "new_session_result") };
                if (m.IncludeResume) { entry["resume"] = Id("select", "new_resume"); }
                if (m.IncludeAgent) { entry["agent"] = Id("select", "new_agent"); }
                if (m.IncludeProfile) { entry["profile"] = Id("select", "new_profile"); }
                if (m.IncludeTuning && Gate("1.16.0")) { foreach (var axis in new[] { "model", "effort", "context" }) { entry[axis] = Id("select", "new_" + axis); } }
                if (m.IncludePermissions && Gate("1.17.0")) { entry["permissions"] = Id("select", "new_permissions"); }
                if (Gate("1.18.0")) { entry["promptTopic"] = HaContract.MachineTopicRoot(m.Slug) + "/newsession/promptpayload"; }
                return entry;
            })) };
            if (selector.Length != 0) { launch["selector"] = selector; }
            cards.Add(launch);
        }
        else
        {
            var launches = new List<JsonObject>();
            if (selector.Length != 0) { launches.Add(Entities([Row(selector, "Machine")], "Start a new session", false)); }
            foreach (var m in online)
            {
                string Id(string domain, string key) => HaContract.MachineEntityId(domain, key, m.Slug);
                var rows = new List<JsonObject>();
                if (m.IncludeResume) { rows.Add(Row(Id("select", "new_resume"), "Resume")); }
                rows.Add(Row(Id("select", "new_workspace"), "Workspace"));
                if (m.IncludeAgent) { rows.Add(Row(Id("select", "new_agent"), "Agent")); }
                if (m.IncludeProfile) { rows.Add(Row(Id("select", "new_profile"), "Profile")); }
                if (m.IncludeTuning) { foreach (var (axis, label) in new[] { ("model", "Model"), ("effort", "Effort"), ("context", "Context") }) { rows.Add(Row(Id("select", "new_" + axis), label)); } }
                if (m.IncludePermissions) { rows.Add(Row(Id("select", "new_permissions"), "Permissions")); }
                rows.Add(Row(Id("text", "new_prompt"), "First message"));
                rows.Add(Row(Id("button", "new_session"), "Launch"));
                var launch = Entities(rows, selector.Length == 0 ? "Start a new session" : null, false);
                var result = Id("sensor", "new_session_result");
                var conditions = new JsonArray();
                if (selector.Length != 0) { conditions.Add(new JsonObject { ["entity"] = selector, ["state"] = m.Machine }); }
                foreach (var excluded in new[] { "", "unknown", "unavailable" }) { conditions.Add(new JsonObject { ["entity"] = result, ["state_not"] = excluded }); }
                var note = Conditional(conditions, Markdown($"{{{{ states('{result}') }}}}"));
                if (selector.Length == 0) { launches.Add(Stack([launch, note])); }
                else
                {
                    launches.Add(Conditional([new JsonObject { ["entity"] = selector, ["state"] = m.Machine }], launch));
                    launches.Add(note);
                }
            }
            cards.AddRange(selector.Length == 0 ? launches : [Stack(launches)]);
        }
        if (!Gate("1.25.0"))
        {
            cards.Add(Conditional([new JsonObject { ["entity"] = usage[0], ["state_not"] = "unavailable" }], Entities(usage.Select(e => Row(e)), "Agent usage")));
        }
        foreach (var session in orderedSessions)
        {
            var key = session.Kind.Equals("mcp", StringComparison.OrdinalIgnoreCase) ? "mcp" :
                Templates.Select(p => p.Key).Where(k => k != "mcp" && Version.Parse(k) <= served)
                    .OrderByDescending(Version.Parse).First();
            if (key == "0.0.0" && !string.IsNullOrWhiteSpace(cardUrl)) { key = "1.0.0"; }
            cards.Add(Expand(Templates[key]!, session).AsObject());
        }
        if (orderedSessions.Length == 0) { cards.Add(Markdown("No live agent sessions right now.")); }
        var config = new JsonObject { ["title"] = "Agent Sessions", ["views"] = new JsonArray(new JsonObject
        {
            ["title"] = "Sessions", ["path"] = "decision", ["type"] = "masonry", ["cards"] = Array(cards),
        }) };
        return new(config, HaPublicationJson.Hash(InputJson(orderedSessions, all, selector, cardUrl, trusted)), orderedSessions.Select(s => s.Node).ToArray(), cardUrl);
    }

    static JsonNode Expand(JsonNode template, HaFleetSession session) => template switch
    {
        JsonObject obj => new JsonObject(obj.Select(p => KeyValuePair.Create(p.Key, p.Value is null ? null : Expand(p.Value, session)))),
        JsonArray array => new JsonArray([.. array.Select(p => p is null ? null : Expand(p, session))]),
        JsonValue value when value.TryGetValue<string>(out var text) => JsonValue.Create(TemplateVariable().Replace(text, match => match.Value switch
        {
            "agent_bridge_templatenode" => session.Node, "__BRIDGE_SESSION_NAME__" => session.Name, _ => session.Machine,
        }))!,
        _ => template.DeepClone(),
    };

    internal static string InputJson(IEnumerable<HaFleetSession> sessions, IEnumerable<HaFleetMachine> machines, string selector, string url, bool trusted)
    {
        var input = new JsonObject
        {
            // Legacy Sort-Object uses linguistic, case-insensitive ordering, not
            // ordinal JSON-key ordering. Preserve its ambient-culture semantics,
            // including ASCII contractions (for example Czech "ch").
            ["sessions"] = Array(sessions.OrderBy(s => s.Node, StringComparer.CurrentCultureIgnoreCase).Select(s => new JsonObject { ["Node"] = s.Node, ["Name"] = s.Name, ["Machine"] = s.Machine, ["Kind"] = s.Kind })),
            ["machines"] = Array(machines.OrderBy(m => m.Slug, StringComparer.CurrentCultureIgnoreCase).Select(m => new JsonObject
            {
                ["Slug"] = m.Slug, ["Machine"] = m.Machine, ["IncludeProfile"] = m.IncludeProfile, ["IncludeResume"] = m.IncludeResume,
                ["IncludeAgent"] = m.IncludeAgent, ["IncludeTuning"] = m.IncludeTuning, ["IncludePermissions"] = m.IncludePermissions,
                ["IncludeDetailed"] = m.IncludeDetailed, ["IsDev"] = m.IsDev, ["Online"] = m.Online,
                // Preserve protocol 1's PowerShell pipeline unrolling: zero nodes
                // becomes null, one becomes a string, and two or more an array.
                ["SessionNodes"] = m.SessionNodes?.Count switch
                {
                    null or 0 => null,
                    1 => JsonValue.Create(m.SessionNodes[0]),
                    _ => Array(m.SessionNodes.Order(StringComparer.CurrentCultureIgnoreCase)),
                },
            })),
            ["selector"] = selector, ["cardUrl"] = url, ["cardTrusted"] = string.IsNullOrWhiteSpace(url) || trusted,
        };
        return HaPublicationJson.Canonical(input);
    }
}
