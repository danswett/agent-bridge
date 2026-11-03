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

    // By file name: its stamp when read, its agent's transcript's then, and what it said (null:
    // not a nested agent, or unreadable).
    readonly Dictionary<string, (DateTime Written, long Length, (DateTime, long) Head, Record? Record)> _read = new(StringComparer.Ordinal);

    // The nested agents recorded beside a transcript, parents first. Past the most that are
    // read, the first by name: the same ones on every read and every platform, whatever order the
    // folder lists them in. Not by time: Claude rewrites a record when its agent finishes, and a
    // creation time is not kept, or kept coarsely, on every file system.
    public IReadOnlyList<Record> Read(string transcript)
    {
        var dir = Path.Combine(Path.GetDirectoryName(transcript) ?? "", Path.GetFileNameWithoutExtension(transcript), "subagents");
        List<string> files;
        try
        {
            if (!Directory.Exists(dir)) { return []; }
            files = [.. Directory.EnumerateFiles(dir, Prefix + "*" + MetaSuffix).Order(StringComparer.Ordinal).Take(MaxRecords)];
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
            // A nested agent whose own transcript had no first line yet is read again, once that
            // transcript has changed.
            var head = Path.Combine(dir, Prefix + id + ".jsonl");
            if (_read.TryGetValue(name, out var known) && known.Written == info.LastWriteTimeUtc && known.Length == info.Length
                && (known.Record is null || known.Record.StartedAt is not null || known.Head == Stamp(head))) { continue; }
            var stamp = Stamp(head);
            if (info.Length > MaxMetaBytes) { _read[name] = (info.LastWriteTimeUtc, info.Length, stamp, null); continue; }
            // One that could not be read is not remembered, so it is tried again on the next read.
            if (Parse(file, dir, id, out var record)) { _read[name] = (info.LastWriteTimeUtc, info.Length, stamp, record); }
            else { _read.Remove(name); }
        }
        foreach (var gone in _read.Keys.Where(k => !names.Contains(k)).ToList()) { _read.Remove(gone); }
        return Known();
    }

    static (DateTime, long) Stamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (info.LastWriteTimeUtc, info.Length) : default;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return default; }
    }

    List<Record> Known() => [.. _read.Values.Select(v => v.Record).OfType<Record>().OrderBy(r => r.Depth).ThenBy(r => r.Id, StringComparer.Ordinal)];

    // Whether the record could be read (it may still be no nested agent's: record null), so one
    // read mid-write or while locked is tried again.
    static bool Parse(string file, string dir, string id, out Record? record)
    {
        record = null;
        JsonElement meta;
        try
        {
            using var doc = JsonDocument.Parse(ReadShared(file, MaxMetaBytes));
            meta = doc.RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return false; }
        if (meta.ValueKind != JsonValueKind.Object || Json.Str(meta, "parentAgentId") is not { } parent || !IsId(parent)) { return true; }
        var (startedAt, prompt) = Head(Path.Combine(dir, Prefix + id + ".jsonl"), id);
        record = new Record(id, parent, Json.Int(meta, "spawnDepth") ?? 2, Json.Str(meta, "description"), Json.Str(meta, "agentType"),
            Json.Str(meta, "toolUseId"), Json.Str(meta, "requestShape") == "background", startedAt, prompt);
        return true;
    }

    // When the agent started and what it was asked: its transcript's first entry, the prompt,
    // stamped with its agentId. One too long to read whole is dated by its own timestamp, read as
    // far as it goes, with no prompt; one with no newline yet is read whole, and again later if it
    // is not complete.
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
            else { return (Started(bytes, id), null); }
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

    // A first entry too long to read whole: its own timestamp, read as far as it goes. Claude
    // writes timestamp and agentId before the message (tests/fixtures/claude), so what is read
    // has them; none is taken unless the entry says it is this agent's.
    static DateTimeOffset? Started(byte[] bytes, string id)
    {
        var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
        (DateTimeOffset? at, bool mine) = (null, false);
        try
        {
            while (reader.Read())
            {
                if (reader.TokenType != JsonTokenType.PropertyName || reader.CurrentDepth != 1) { continue; }
                var name = reader.GetString();
                if (name is not ("timestamp" or "agentId")) { continue; }
                if (!reader.Read()) { break; }
                if (reader.TokenType != JsonTokenType.String) { continue; }
                if (name == "timestamp") { at = Json.ParseTime(reader.GetString()); }
                else { mine = reader.GetString() == id; }
                if (at is not null && mine) { return at; }
            }
        }
        catch (JsonException) { }
        return null;
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
