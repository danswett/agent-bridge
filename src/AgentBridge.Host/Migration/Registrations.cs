using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.Host.Migration;

// One hook entry migration replaces: what agent-ha-bridge's installer wrote, and what replaces
// it. Kept in the migration record, so a rollback can put the original back even into a file
// someone has edited since.
public sealed record EntryChange(string Agent, string Event, JsonNode Original, JsonNode Replacement);

// What migration does to one agent's hook file: its bytes before, its text after, and each
// entry it re-points. Null text when there is nothing of agent-ha-bridge's in it.
public sealed record FilePlan(string Role, string Path, bool Existed, byte[] Original, string? NewText, IReadOnlyList<EntryChange> Changes, IReadOnlyList<string> Notes);

// Finding agent-ha-bridge's own hook entries in each agent's settings, exactly as its installers
// wrote them, and re-pointing them at this product's hook. Anything else in those files belongs
// to the person or another tool and is left as it is. The matching follows each installer's own
// removal code (Remove-BridgeCopilotHookEntries, install-claude.ps1's Remove-BridgeHooks), so
// an entry another installation owns is never taken.
public static class Registrations
{
    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, IndentSize = 2 };
    static readonly JsonDocumentOptions Lenient = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };
    static readonly StringComparison PathCase = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    // install.ps1's Copilot scripts, by the native hook name each is spooled as.
    static readonly (string Script, string Name)[] CopilotScripts =
        [("notify-agent-response.ps1", "agent_stop"), ("route-ask-user-v3.ps1", "ask_user"), ("notify-home-assistant.ps1", "permission")];

    static readonly (string Script, string Name)[] ClaudeScripts =
        [("route-askuserquestion.ps1", "ask"), ("route-notification.ps1", "notification"), ("notify-claude-stop.ps1", "stop"), ("register-claude-session.ps1", "register")];

    // ~/.copilot/hooks/decision-notifier.json: { "hooks": { "<event>": [ { type, matcher?,
    // timeoutSec, exec + args | powershell } ] }, "version": 1 }. Each becomes exec with no
    // shell, as agent-ha-bridge's own native registrations are; a Copilot too old to run exec
    // drops the hook, which costs nothing here: the host reads Copilot sessions from their
    // transcripts.
    public static FilePlan Copilot(AgentHaBridgeInstall install, string hook) =>
        Plan("copilot", install.CopilotHooksFile, root =>
        {
            var changes = new List<EntryChange>();
            if (root["hooks"] is not JsonObject hooks) { return changes; }
            foreach (var (eventName, list) in hooks)
            {
                if (list is not JsonArray entries) { continue; }
                for (var i = 0; i < entries.Count; i++)
                {
                    if (entries[i] is not JsonObject e) { continue; }
                    var name = CopilotScripts.FirstOrDefault(s =>
                    {
                        var script = Path.Combine(install.HooksDir, s.Script);
                        if (Text(e["powershell"]) is { } ps && string.Equals(ps, $"& '{script}'", PathCase)) { return true; }
                        return Text(e["exec"]) is { } exec && string.Equals(exec, install.GoHook, PathCase)
                            && e["args"] is JsonArray args && args.Any(a => Text(a) is { } t && string.Equals(t, script, PathCase));
                    }).Name;
                    if (name is null) { continue; }
                    var replacement = (JsonObject)e.DeepClone();
                    replacement.Remove("powershell");
                    replacement.Remove("bash");
                    replacement["exec"] = hook;
                    replacement["args"] = new JsonArray("copilot", name);
                    changes.Add(new EntryChange("copilot", eventName, e.DeepClone(), replacement.DeepClone()));
                    entries[i] = replacement;
                }
            }
            return changes;
        });

    // ~/.claude/settings.json: { "hooks": { "<event>": [ { matcher, hooks: [ { type, command,
    // timeout } ] } ] }, ...the person's own settings }. Claude runs the command through its
    // shell, so the program is quoted, as install-claude.ps1 quotes the Go hook.
    public static FilePlan Claude(AgentHaBridgeInstall install, string hook) =>
        Plan("claude", install.ClaudeSettingsFile, root =>
        {
            var changes = new List<EntryChange>();
            if (root["hooks"] is not JsonObject hooks) { return changes; }
            var launcher = Path.Combine(install.ClaudeAdapterDir, "run-hook.cmd");
            foreach (var (eventName, list) in hooks)
            {
                if (list is not JsonArray matchers) { continue; }
                foreach (var matcher in matchers.OfType<JsonObject>())
                {
                    if (matcher["hooks"] is not JsonArray inner) { continue; }
                    for (var i = 0; i < inner.Count; i++)
                    {
                        if (inner[i] is not JsonObject h || Text(h["command"]) is not { } command) { continue; }
                        var name = ClaudeScripts.FirstOrDefault(s =>
                        {
                            var script = Path.Combine(install.ClaudeAdapterDir, s.Script);
                            return string.Equals(command, $"\"{install.GoHook}\" claude {s.Name} \"{script}\"", PathCase)
                                || string.Equals(command, $"\"{launcher}\" \"{script}\"", PathCase);
                        }).Name;
                        if (name is null) { continue; }
                        var replacement = (JsonObject)h.DeepClone();
                        replacement["command"] = $"\"{hook}\" claude {name}";
                        changes.Add(new EntryChange("claude", eventName, h.DeepClone(), replacement.DeepClone()));
                        inner[i] = replacement;
                    }
                }
            }
            return changes;
        });

    // <bridgeHome>\codex-bridge\plugins\agent-ha-bridge\hooks.json, the plugin install-codex.ps1
    // registers: { "description", "hooks": { "<event>": [ { hooks: [ { type, command, timeout }
    // ] } ] } }. Codex runs the command unquoted (a quoted path fails), so a hook whose path has
    // a space cannot be registered at all, and agent-ha-bridge's stays.
    public static FilePlan Codex(AgentHaBridgeInstall install, string hook)
    {
        var plan = Plan("codex", install.CodexHooksFile, root =>
        {
            var changes = new List<EntryChange>();
            if (hook.Any(char.IsWhiteSpace) || root["hooks"] is not JsonObject hooks) { return changes; }
            var native = $"{install.GoHook} codex hook {install.CodexHookScript}";
            var pwsh = $"pwsh -NoProfile -ExecutionPolicy Bypass -File {install.CodexHookScript}";
            foreach (var (eventName, list) in hooks)
            {
                if (list is not JsonArray groups) { continue; }
                foreach (var group in groups.OfType<JsonObject>())
                {
                    if (group["hooks"] is not JsonArray inner) { continue; }
                    for (var i = 0; i < inner.Count; i++)
                    {
                        if (inner[i] is not JsonObject h || Text(h["command"]) is not { } command) { continue; }
                        if (!string.Equals(command, native, PathCase) && !string.Equals(command, pwsh, PathCase)) { continue; }
                        var replacement = (JsonObject)h.DeepClone();
                        replacement["command"] = $"{hook} codex hook";
                        changes.Add(new EntryChange("codex", eventName, h.DeepClone(), replacement.DeepClone()));
                        inner[i] = replacement;
                    }
                }
            }
            return changes;
        });
        if (hook.Any(char.IsWhiteSpace) && plan.Existed)
        {
            return plan with { Notes = [.. plan.Notes, $"Codex's hooks are left on agent-ha-bridge's: Codex cannot run a command whose path has a space ({hook}); they spool to the host while it runs and fall back to PowerShell when it does not"] };
        }
        if (plan.Changes.Count > 0)
        {
            return plan with { Notes = [.. plan.Notes, "Codex asks for its hooks to be trusted again once, since their command changed (and again after a rollback)"] };
        }
        return plan;
    }

    static FilePlan Plan(string role, string path, Func<JsonObject, List<EntryChange>> rewrite)
    {
        if (!File.Exists(path)) { return new FilePlan(role, path, false, [], null, [], []); }
        var original = File.ReadAllBytes(path);
        JsonObject root;
        try
        {
            root = JsonNode.Parse(Decode(original), documentOptions: Lenient) as JsonObject ?? throw new JsonException("not a JSON object");
        }
        catch (JsonException e)
        {
            return new FilePlan(role, path, true, original, null, [], [$"{path} is not valid JSON ({e.Message}); its hooks were left as they are"]);
        }
        var changes = rewrite(root);
        return new FilePlan(role, path, true, original, changes.Count == 0 ? null : Encode(root, original), changes, []);
    }

    // What runs, in a hook entry: the fields migration rewrote. Anything else in an entry (its
    // matcher, timeout, type) is the person's to change and is kept.
    static readonly string[] CommandFields = ["command", "exec", "args", "powershell", "bash"];

    static string? CommandOf(JsonNode? node) =>
        node is JsonObject o && CommandFields.Any(o.ContainsKey)
            ? string.Join("\n", CommandFields.Select(f => o[f]?.ToJsonString() ?? ""))
            : null;

    // Puts back the command of each entry migration re-pointed, wherever an entry that still runs
    // its replacement now is, leaving everything else in the file as it is now: for a file
    // someone has edited since the migration (a timeout changed, a hook of their own added).
    public static (string? Text, int Replaced) Revert(byte[] current, IReadOnlyList<EntryChange> changes)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(Decode(current), documentOptions: Lenient); }
        catch (JsonException) { return (null, 0); }
        if (root is null) { return (null, 0); }
        var replaced = 0;
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonArray array:
                    foreach (var item in array) { Walk(item); }
                    break;
                case JsonObject obj:
                    if (CommandOf(obj) is { } command && changes.FirstOrDefault(c => CommandOf(c.Replacement) == command) is { Original: JsonObject original })
                    {
                        foreach (var field in CommandFields)
                        {
                            if (original[field] is { } value) { obj[field] = value.DeepClone(); }
                            else { obj.Remove(field); }
                        }
                        replaced++;
                        break;
                    }
                    foreach (var (_, child) in obj.ToList()) { Walk(child); }
                    break;
            }
        }
        Walk(root);
        return (Encode(root, current), replaced);
    }

    // Whether any value in the file still names this program: a hook rollback could not restore.
    public static bool Names(byte[] file, string program)
    {
        try
        {
            var found = false;
            void Walk(JsonNode? node)
            {
                switch (node)
                {
                    case JsonArray a: foreach (var i in a) { Walk(i); } break;
                    case JsonObject o: foreach (var (_, c) in o) { Walk(c); } break;
                    case JsonValue v when v.TryGetValue<string>(out var s) && s.Contains(program, PathCase): found = true; break;
                }
            }
            Walk(JsonNode.Parse(Decode(file), documentOptions: Lenient));
            return found;
        }
        catch (JsonException) { return Decode(file).Contains(program, PathCase); }
    }
    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    internal static string Decode(byte[] bytes) =>
        bytes.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3) : Encoding.UTF8.GetString(bytes);

    // Written the way the installers wrote it (ConvertTo-Json's two-space indent, a trailing
    // newline), keeping a byte order mark the file had.
    static string Encode(JsonNode root, byte[] original)
    {
        var text = root.ToJsonString(Pretty) + Environment.NewLine;
        return original.AsSpan().StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]) ? "\uFEFF" + text : text;
    }
}
