using System.Globalization;
using System.Text;
using System.Text.Json;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Launch;

// Recent sessions that could be resumed, from each agent's own store, as agent-ha-bridge reads
// them (Get-BridgeResumableSessions and the per-agent readers in hooks/session-launch.ps1):
//   * Copilot: ~/.copilot/session-state/<id>/, workspace.yaml's cwd, summary and updated_at,
//     else the first prompt in events.jsonl;
//   * Claude: ~/.claude/projects/<folder>/<id>.jsonl, its custom title, else its AI title, else
//     the first prompt, and the cwd its prompts record;
//   * Codex: ~/.codex/sessions/**/rollout-*.jsonl, its session_meta's id and cwd, and the thread
//     name from session_index.jsonl, else the first prompt.
// A session with nothing to call it by was opened and closed without a prompt, and is left
// out. Only the newest ScanLimit of each are read: a Claude or Codex session records its folder
// inside its transcript, so judging whether it is in an approved folder means opening it.
public sealed class ResumableStore(string copilotHome, string claudeHome, string codexHome, TimeProvider? time = null)
{
    public const int ScanLimit = 200;
    const int Ends = 64 * 1024;

    readonly TimeProvider time = time ?? TimeProvider.System;
    readonly Lock gate = new();
    (DateTimeOffset At, string Key, IReadOnlyList<ResumeEntry> Entries)? cache;

    // Every agent's candidates, each list newest first. Kept for a minute: reading them opens
    // hundreds of files, and a phone opening the launch form twice should not do it twice.
    public IReadOnlyList<ResumeEntry> Read(IReadOnlyCollection<string> agents)
    {
        var key = string.Join(",", agents.Order(StringComparer.Ordinal));
        lock (gate)
        {
            if (cache is { } c && c.Key == key && time.GetUtcNow() - c.At < TimeSpan.FromMinutes(1)) { return c.Entries; }
        }
        var entries = new List<ResumeEntry>();
        if (agents.Contains("claude")) { entries.AddRange(Claude()); }
        if (agents.Contains("codex")) { entries.AddRange(Codex()); }
        if (agents.Contains("copilot")) { entries.AddRange(Copilot()); }
        lock (gate) { cache = (time.GetUtcNow(), key, entries); }
        return entries;
    }

    public void Forget()
    {
        lock (gate) { cache = null; }
    }

    IEnumerable<ResumeEntry> Copilot()
    {
        var root = Path.Combine(copilotHome, "session-state");
        foreach (var dir in Newest(SafeDirectories(root), CopilotActivity))
        {
            var id = Path.GetFileName(dir);
            if (!Guid.TryParseExact(id, "D", out _)) { continue; }
            string? summary = null, folder = null;
            DateTimeOffset updated = Directory.GetLastWriteTimeUtc(dir);
            var yaml = ReadYaml(Path.Combine(dir, "workspace.yaml"));
            if (yaml.TryGetValue("cwd", out var cwd)) { folder = cwd; }
            if (yaml.TryGetValue("summary", out var s)) { summary = s; }
            else if (yaml.TryGetValue("name", out var n)) { summary = n; }
            if (yaml.TryGetValue("updated_at", out var u) && DateTimeOffset.TryParse(u, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)) { updated = t; }
            var events = Path.Combine(dir, "events.jsonl");
            if (File.Exists(events))
            {
                updated = File.GetLastWriteTimeUtc(events);
                if (string.IsNullOrWhiteSpace(summary))
                {
                    foreach (var line in FileEnds(events))
                    {
                        if (!line.Contains("\"type\":\"user.message\"", StringComparison.Ordinal)) { continue; }
                        if (Parse(line) is { } r && r.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("content", out var content))
                        {
                            summary = PromptSummary(content);
                            if (summary.Length > 0) { break; }
                        }
                    }
                }
            }
            if (string.IsNullOrWhiteSpace(summary)) { continue; }
            yield return new ResumeEntry(id.ToLowerInvariant(), AgentKind.Copilot, "copilot", summary, folder, updated);
        }
    }

    // When a Copilot session was last used: its transcript is appended to in place, which does
    // not touch the folder's own time, so a session reopened yesterday after a year would rank
    // by the year and fall outside the scan.
    static DateTime CopilotActivity(string dir)
    {
        var events = File.GetLastWriteTimeUtc(Path.Combine(dir, "events.jsonl"));
        var folder = Directory.GetLastWriteTimeUtc(dir);
        return events > folder ? events : folder;
    }

    IEnumerable<ResumeEntry> Claude()
    {
        var projects = Path.Combine(claudeHome, "projects");
        // Only each project folder's own files: subagent transcripts live in folders below.
        var files = SafeDirectories(projects).SelectMany(d => SafeFiles(d, "*.jsonl", recurse: false));
        foreach (var file in Newest(files, File.GetLastWriteTimeUtc))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!Guid.TryParseExact(id, "D", out _)) { continue; }
            string custom = "", ai = "", prompt = "", folder = "";
            foreach (var line in FileEnds(file))
            {
                if (!line.Contains("\"type\":\"custom-title\"", StringComparison.Ordinal) && !line.Contains("\"type\":\"ai-title\"", StringComparison.Ordinal) && !line.Contains("\"type\":\"user\"", StringComparison.Ordinal)) { continue; }
                if (Parse(line) is not { } r || Str(r, "type") is not { } type) { continue; }
                switch (type)
                {
                    case "custom-title": custom = Str(r, "customTitle") ?? custom; break;
                    case "ai-title": ai = Str(r, "aiTitle") ?? ai; break;
                    case "user":
                        if (folder.Length == 0) { folder = Str(r, "cwd") ?? ""; }
                        var meta = r.TryGetProperty("isMeta", out var m) && m.ValueKind == JsonValueKind.True;
                        if (prompt.Length == 0 && !meta && r.TryGetProperty("message", out var msg) && msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var content))
                        {
                            prompt = PromptSummary(content);
                        }
                        break;
                }
            }
            var summary = custom.Length > 0 ? custom : ai.Length > 0 ? ai : prompt;
            if (summary.Length == 0) { continue; }
            yield return new ResumeEntry(id.ToLowerInvariant(), AgentKind.Claude, "claude", summary, folder.Length > 0 ? folder : null, File.GetLastWriteTimeUtc(file));
        }
    }

    IEnumerable<ResumeEntry> Codex()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var index = Path.Combine(codexHome, "session_index.jsonl");
        if (File.Exists(index))
        {
            foreach (var line in FileEnds(index))
            {
                if (Parse(line) is { } r && Str(r, "id") is { } id && Str(r, "thread_name") is { Length: > 0 } name) { names[id] = name; }
            }
        }
        var files = SafeFiles(Path.Combine(codexHome, "sessions"), "rollout-*.jsonl", recurse: true);
        foreach (var file in Newest(files, File.GetLastWriteTimeUtc))
        {
            string id = "", folder = "", prompt = "";
            foreach (var line in FileEnds(file))
            {
                // The prompt is in the older user_message event, or the newer item_completed
                // one carrying a UserMessage (as CodexParser reads both).
                if (!line.Contains("\"type\":\"session_meta\"", StringComparison.Ordinal) && !line.Contains("\"type\":\"user_message\"", StringComparison.Ordinal)
                    && !line.Contains("\"type\":\"UserMessage\"", StringComparison.Ordinal)) { continue; }
                if (Parse(line) is not { } r || !r.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object) { continue; }
                if (Str(r, "type") == "session_meta")
                {
                    id = Str(payload, "id") ?? id;
                    folder = Str(payload, "cwd") ?? folder;
                }
                else if (prompt.Length == 0 && Str(payload, "type") == "user_message" && payload.TryGetProperty("message", out var message))
                {
                    prompt = PromptSummary(message);
                }
                else if (prompt.Length == 0 && Str(payload, "type") == "item_completed" && payload.TryGetProperty("item", out var item) && item.ValueKind == JsonValueKind.Object
                    && Str(item, "type") == "UserMessage" && item.TryGetProperty("content", out var content))
                {
                    prompt = PromptSummary(content);
                }
            }
            if (!Guid.TryParseExact(id, "D", out _)) { continue; }
            var summary = names.TryGetValue(id, out var n) ? n : prompt;
            if (summary.Length == 0) { continue; }
            yield return new ResumeEntry(id.ToLowerInvariant(), AgentKind.Codex, "codex", summary, folder.Length > 0 ? folder : null, File.GetLastWriteTimeUtc(file));
        }
    }

    // A prompt as a title, or "" for text that is not one: the wrappers the CLIs record around
    // slash commands, environment context and caveats all start with a tag.
    internal static string PromptSummary(JsonElement content)
    {
        var text = "";
        if (content.ValueKind == JsonValueKind.String) { text = content.GetString() ?? ""; }
        else if (content.ValueKind == JsonValueKind.Array)
        {
            foreach (var part in content.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.Object || Str(part, "text") is not { } t) { continue; }
                if (Str(part, "type") is { } type && type is not ("text" or "input_text")) { continue; }
                text = t;
                break;
            }
        }
        text = Collapse(text);
        return text.StartsWith('<') ? "" : text;
    }

    internal static string Collapse(string text)
    {
        var b = new StringBuilder(text.Length);
        var space = false;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c)) { space = b.Length > 0; continue; }
            if (space) { b.Append(' '); space = false; }
            b.Append(c);
        }
        return b.ToString();
    }

    static IEnumerable<string> Newest(IEnumerable<string> paths, Func<string, DateTime> written)
    {
        var list = new List<(string Path, DateTime At)>();
        foreach (var p in paths)
        {
            try { list.Add((p, written(p))); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return list.OrderByDescending(x => x.At).Take(ScanLimit).Select(x => x.Path);
    }

    static IEnumerable<string> SafeDirectories(string root)
    {
        try { return Directory.Exists(root) ? Directory.EnumerateDirectories(root).ToList() : []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    static IEnumerable<string> SafeFiles(string root, string pattern, bool recurse)
    {
        try
        {
            return Directory.Exists(root)
                ? Directory.EnumerateFiles(root, pattern, new EnumerationOptions { RecurseSubdirectories = recurse, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }).ToList()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
    }

    // The complete lines in the first and last 64 KiB of a file, without reading the middle:
    // transcripts run to tens of megabytes, and what a resume list needs sits at either end.
    internal static IEnumerable<string> FileEnds(string path)
    {
        byte[] head, tail;
        long length;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            length = stream.Length;
            head = new byte[(int)Math.Min(length, Ends)];
            stream.ReadExactly(head);
            if (length <= Ends * 2)
            {
                tail = new byte[length - head.Length];
                stream.ReadExactly(tail);
                return Lines(Encoding.UTF8.GetString([.. head, .. tail]), dropFirst: false, dropLast: false);
            }
            tail = new byte[Ends];
            stream.Seek(-Ends, SeekOrigin.End);
            stream.ReadExactly(tail);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return []; }
        return Lines(Encoding.UTF8.GetString(head), dropFirst: false, dropLast: true).Concat(Lines(Encoding.UTF8.GetString(tail), dropFirst: true, dropLast: false));
    }

    static List<string> Lines(string text, bool dropFirst, bool dropLast)
    {
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        if (dropFirst && lines.Count > 0) { lines.RemoveAt(0); }
        if (dropLast && lines.Count > 0) { lines.RemoveAt(lines.Count - 1); }
        return lines.Where(l => l.Length > 0).ToList();
    }

    static JsonElement? Parse(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? doc.RootElement.Clone() : null;
        }
        catch (JsonException) { return null; }
    }

    static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // workspace.yaml's flat top-level `key: value` lines, as Host.Core reads them.
    static Dictionary<string, string> ReadYaml(string path)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        string text;
        try
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 256 * 1024) { return values; }
            text = File.ReadAllText(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return values; }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] is '#' or '-') { continue; }
            var colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0) { continue; }
            var value = line[(colon + 2)..].Trim();
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') { value = value[1..^1].Replace("''", "'"); }
            else if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                try { value = JsonSerializer.Deserialize<string>(value) ?? ""; }
                catch (JsonException) { value = value[1..^1]; }
            }
            if (value is "~" or "null") { value = ""; }
            if (value.Length > 0) { values[line[..colon]] = value; }
        }
        return values;
    }
}
