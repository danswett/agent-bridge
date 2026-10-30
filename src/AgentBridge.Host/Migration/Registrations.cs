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
public sealed record FilePlan(string Role, string Path, bool Existed, byte[] Original, string? NewText, IReadOnlyList<EntryChange> Changes, IReadOnlyList<string> Notes)
{
    // Why the migration must not go ahead with this file as it is, or null.
    public string? Refusal { get; init; }

    // agent-ha-bridge's Go hook entries left in place rather than re-pointed (Codex, when this
    // product's hook path has a space): they spool to the host, but a session started before the
    // file was last written may hold the PowerShell hooks they replaced, as with re-pointed ones.
    public bool KeptGoHook { get; init; }
}

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
                            return SameCommand(command, $"\"{install.GoHook}\" claude {s.Name} \"{script}\"")
                                || SameCommand(command, $"\"{launcher}\" \"{script}\"");
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
            var pwsh = CodexPowerShell(install);
            foreach (var (eventName, list) in hooks)
            {
                if (list is not JsonArray groups) { continue; }
                foreach (var group in groups.OfType<JsonObject>())
                {
                    if (group["hooks"] is not JsonArray inner) { continue; }
                    for (var i = 0; i < inner.Count; i++)
                    {
                        if (inner[i] is not JsonObject h || Text(h["command"]) is not { } command) { continue; }
                        if (!SameCommand(command, native) && !SameCommand(command, pwsh)) { continue; }
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
            // Left alone, an entry that runs agent-ha-bridge's PowerShell directly would go on
            // writing registrations and approval markers (and to Home Assistant) beside the host.
            // Only the Go hook's entries hand their events to whoever takes the spool.
            if (CodexRunsPowerShell(install))
            {
                return plan with
                {
                    Refusal = $"Codex runs agent-ha-bridge's PowerShell hook directly ({install.CodexHooksFile}), and cannot run this product's hook from a path with a space ({hook}), so it would go on running beside the host. Install the app where its path has no space, or let agent-ha-bridge register Codex through its native hook, and migrate again",
                };
            }
            return plan with { KeptGoHook = true, Notes = [.. plan.Notes, $"Codex's hooks are left on agent-ha-bridge's: Codex cannot run a command whose path has a space ({hook}); they spool to the host while it runs and fall back to PowerShell when it does not"] };
        }
        if (plan.Changes.Count > 0)
        {
            return plan with { Notes = [.. plan.Notes, "Codex asks for its hooks to be trusted again once, since their command changed (and again after a rollback)"] };
        }
        return plan;
    }

    // Whether a replaced entry ran agent-ha-bridge's PowerShell itself rather than its Go hook. A
    // session keeps the hooks it started with, and these write registrations, approval markers and
    // Home Assistant state on their own; the Go hook's hand their events to the spool the host takes.
    public static bool RanPowerShell(AgentHaBridgeInstall install, EntryChange change)
    {
        if (Text(change.Original["exec"]) is { } exec) { return !string.Equals(exec, install.GoHook, PathCase); }
        if (Text(change.Original["command"]) is not { } command) { return true; }
        var words = Words(command);
        return !words.StartsWith(install.GoHook + " ", PathCase) && !words.StartsWith($"\"{install.GoHook}\" ", PathCase);
    }

    // The same command as a shell reads it: Claude and Codex run it through one, which takes
    // "a  b" or a tab for "a b". Each word, quoted paths included, must still be exact; a second
    // space or a tab must not hide an entry of agent-ha-bridge's, which would go on running its
    // PowerShell (and writing to Home Assistant) beside the host.
    static bool SameCommand(string command, string expected) => string.Equals(Words(command), Words(expected), PathCase);

    // Runs of whitespace outside quotes as one space, and none at either end.
    static string Words(string command)
    {
        var words = new StringBuilder(command.Length);
        var quoted = false;
        var gap = false;
        foreach (var c in command.Trim())
        {
            if (c == '"') { quoted = !quoted; }
            if (!quoted && char.IsWhiteSpace(c)) { gap = true; continue; }
            if (gap) { words.Append(' '); gap = false; }
            words.Append(c);
        }
        return words.ToString();
    }

    static string CodexPowerShell(AgentHaBridgeInstall install) => $"pwsh -NoProfile -ExecutionPolicy Bypass -File {install.CodexHookScript}";

    static bool CodexRunsPowerShell(AgentHaBridgeInstall install)
    {
        try
        {
            if (JsonNode.Parse(Decode(File.ReadAllBytes(install.CodexHooksFile)), documentOptions: Lenient) is not JsonObject { } root || root["hooks"] is not JsonObject hooks) { return false; }
            var pwsh = CodexPowerShell(install);
            return hooks.Select(e => e.Value).OfType<JsonArray>().SelectMany(g => g.OfType<JsonObject>())
                .Select(g => g["hooks"]).OfType<JsonArray>().SelectMany(h => h.OfType<JsonObject>())
                .Any(h => Text(h["command"]) is { } command && SameCommand(command, pwsh));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    // A file that is there but cannot be read refuses the migration: a session may have started
    // while it still held agent-ha-bridge's PowerShell hooks, and with no entries to re-point,
    // nothing would check for one still running them beside the host.
    static FilePlan Plan(string role, string path, Func<JsonObject, List<EntryChange>> rewrite)
    {
        if (!File.Exists(path)) { return new FilePlan(role, path, false, [], null, [], []); }
        byte[] original;
        JsonObject root;
        try
        {
            original = File.ReadAllBytes(path);
            root = JsonNode.Parse(Decode(original), documentOptions: Lenient) as JsonObject ?? throw new JsonException("not a JSON object");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new FilePlan(role, path, true, [], null, [], [])
            {
                Refusal = $"{path} could not be read as JSON ({e.Message}), so whether it holds agent-ha-bridge's hooks (and a running session started with them) cannot be told; fix it and migrate again",
            };
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

    // Puts back the command of each entry migration re-pointed, wherever an entry under the same
    // event still runs its replacement, leaving everything else in the file as it is now: for a
    // file someone has edited since the migration (a timeout changed, a hook of their own added).
    // Entries are paired one to one, in order, within their event: two originals can share one
    // replacement (Claude's SessionStart and UserPromptSubmit both become `claude register`), and
    // one must never be restored into the other's place. One left over still runs this host's
    // program, which the caller reports (Names).
    public static (string? Text, int Replaced) Revert(byte[] current, IReadOnlyList<EntryChange> changes)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(Decode(current), documentOptions: Lenient); }
        catch (JsonException) { return (null, 0); }
        if (root is not JsonObject { } top) { return (null, 0); }
        if (top["hooks"] is not JsonObject hooks) { return (Encode(top, current), 0); }
        var replaced = 0;
        var used = new HashSet<int>();
        void Walk(JsonNode? node, string eventName)
        {
            switch (node)
            {
                case JsonArray array:
                    foreach (var item in array) { Walk(item, eventName); }
                    break;
                case JsonObject obj:
                    var command = CommandOf(obj);
                    var index = command is null ? -1 : Enumerable.Range(0, changes.Count).FirstOrDefault(i => !used.Contains(i) && changes[i].Event == eventName && CommandOf(changes[i].Replacement) == command, -1);
                    if (index >= 0 && changes[index].Original is JsonObject original)
                    {
                        used.Add(index);
                        foreach (var field in CommandFields)
                        {
                            if (original[field] is { } value) { obj[field] = value.DeepClone(); }
                            else { obj.Remove(field); }
                        }
                        replaced++;
                        break;
                    }
                    foreach (var (_, child) in obj.ToList()) { Walk(child, eventName); }
                    break;
            }
        }
        foreach (var (eventName, node) in hooks.ToList()) { Walk(node, eventName); }
        return (Encode(top, current), replaced);
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
