using System.Text.Json;

namespace AgentBridge.Host.Core.Parsing;

// Claude Code 2.1.316 writes each sub-agent's own transcript beside the session's,
// <session id>/subagents/agent-<agentId>.jsonl, with agent-<agentId>.meta.json saying what it
// is: agentType, description, toolUseId, spawnDepth, requestShape ("background") and, for one
// another sub-agent started, parentAgentId. That is the only place a nested agent's start and
// parent are written: the session's own transcript has nothing of it but its end, a queued
// task-notification. So the records of nested agents are read here, each again only when it
// changes, with the first line of the agent's transcript for when it started and the prompt
// it was given (tests/fixtures/claude). Read only, and bounded in files and bytes.
internal sealed class ClaudeSubagents
{
    public const int MaxRecords = 256;
    const int MaxMetaBytes = 16 * 1024;
    const int MaxHeadBytes = 64 * 1024;
    const string Prefix = "agent-";
    const string MetaSuffix = ".meta.json";

    public sealed record Record(string Id, string? Parent, int Depth, string? Description, string? AgentType, string? ToolUseId, bool Background, DateTimeOffset? StartedAt, string? Prompt);

    // By file name: its stamp when read, and what it said (null: not a nested agent, or unreadable).
    readonly Dictionary<string, (DateTime Written, long Length, Record? Record)> _read = new(StringComparer.Ordinal);

    // The nested agents recorded beside a transcript, parents first. Past the most that are
    // read, the earliest written: the same ones on every read, whatever order the folder lists
    // them in, and an agent's parent (written before it) kept before it.
    public IReadOnlyList<Record> Read(string transcript)
    {
        var dir = Path.Combine(Path.GetDirectoryName(transcript) ?? "", Path.GetFileNameWithoutExtension(transcript), "subagents");
        List<string> files;
        try
        {
            if (!Directory.Exists(dir)) { return []; }
            files = [.. new DirectoryInfo(dir).EnumerateFiles(Prefix + "*" + MetaSuffix)
                .OrderBy(f => f.CreationTimeUtc).ThenBy(f => f.Name, StringComparer.Ordinal).Take(MaxRecords).Select(f => f.FullName)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return Known(); }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var name = Path.GetFileName(file);
            names.Add(name);
            var id = name[Prefix.Length..^MetaSuffix.Length];
            if (!IsId(id)) { continue; }
            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists) { continue; }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            // A nested agent whose own transcript had no first line yet is read again.
            if (_read.TryGetValue(name, out var known) && known.Written == info.LastWriteTimeUtc && known.Length == info.Length
                && (known.Record is null || known.Record.StartedAt is not null)) { continue; }
            _read[name] = (info.LastWriteTimeUtc, info.Length, info.Length <= MaxMetaBytes ? Parse(file, dir, id) : null);
        }
        foreach (var gone in _read.Keys.Where(k => !names.Contains(k)).ToList()) { _read.Remove(gone); }
        return Known();
    }

    List<Record> Known() => [.. _read.Values.Select(v => v.Record).OfType<Record>().OrderBy(r => r.Depth).ThenBy(r => r.Id, StringComparer.Ordinal)];

    static Record? Parse(string file, string dir, string id)
    {
        JsonElement meta;
        try
        {
            using var doc = JsonDocument.Parse(ReadShared(file, MaxMetaBytes));
            meta = doc.RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return null; }
        if (meta.ValueKind != JsonValueKind.Object || Json.Str(meta, "parentAgentId") is not { } parent || !IsId(parent)) { return null; }
        var (startedAt, prompt) = Head(Path.Combine(dir, Prefix + id + ".jsonl"), id);
        return new Record(id, parent, Json.Int(meta, "spawnDepth") ?? 2, Json.Str(meta, "description"), Json.Str(meta, "agentType"),
            Json.Str(meta, "toolUseId"), Json.Str(meta, "requestShape") == "background", startedAt, prompt);
    }

    // When the agent started and what it was asked: its transcript's first entry, the prompt,
    // stamped with its agentId. One too long to read whole is dated by the file instead; one
    // with no newline yet is read whole, and again later if it is not complete.
    static (DateTimeOffset? At, string? Prompt) Head(string path, string id)
    {
        byte[] bytes;
        long length;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            length = stream.Length;
            bytes = new byte[(int)Math.Min(length, MaxHeadBytes)];
            stream.ReadExactly(bytes);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, null); }
        var newline = Array.IndexOf(bytes, (byte)'\n');
        if (newline < 0)
        {
            if (length <= MaxHeadBytes) { newline = bytes.Length; }
            else
            {
                try { return (new DateTimeOffset(File.GetCreationTimeUtc(path), TimeSpan.Zero), null); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return (null, null); }
            }
        }
        try
        {
            using var doc = JsonDocument.Parse(bytes.AsMemory(0, newline));
            var e = doc.RootElement;
            if (e.ValueKind != JsonValueKind.Object || Json.Str(e, "agentId") != id) { return (null, null); }
            var message = Json.Obj(e, "message");
            var prompt = message is { } m && Json.Get(m, "content") is { ValueKind: JsonValueKind.String } c ? c.GetString() : null;
            return (Json.Time(e, "timestamp"), prompt);
        }
        catch (JsonException) { return (null, null); }
    }

    static byte[] ReadShared(string path, int max)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var bytes = new byte[(int)Math.Min(stream.Length, max)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    // Claude's agent ids are short hex. Letters, digits, '-' and '_' are allowed; anything else
    // is not a name to build a path from.
    static bool IsId(string id) => id.Length is > 0 and <= 64 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
