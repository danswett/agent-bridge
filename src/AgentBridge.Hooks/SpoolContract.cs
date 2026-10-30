using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AgentBridge.Hooks;

// The hook spool, exactly as agent-ha-bridge's native hook writes it (hook/main.go) and its
// daemon reads it (hooks/daemon-hookspool.ps1). Both bridges speak it, in both directions:
// agent-ha-bridge's Go hook spools to this host while the host keeps the heartbeat, and this
// product's hook spools to agent-ha-bridge's daemon after a rollback. A session started under
// either set of registrations is therefore served by whichever bridge is running.
//
//   <runtime>/agent-bridge-daemon.heartbeat   touched by the consumer; a hook spools only
//                                             while it is younger than 60 s
//   <runtime>/agent-bridge-spool/<name>.json  one event: { v, agent, hook, ancestors,
//                                             receivedAt, event }, written as <name>.json.tmp
//                                             and renamed, so a reader never sees half of one
//   <runtime>/agent-bridge-hook.log           one JSON line per hook run, kept to 1 MB and
//                                             one rotated .1 (Get-BridgeHookStats reads it)
//
// <name> is the time in Unix nanoseconds, 20 digits, then the hook's pid and 8 random hex
// digits, so names sort in the order the events happened, which is the order they are handled.
public static class SpoolContract
{
    public const string SpoolFolder = "agent-bridge-spool";
    public const string HeartbeatFile = "agent-bridge-daemon.heartbeat";
    public const string LogFile = "agent-bridge-hook.log";
    public const string ClaudeFolder = "agent-bridge-claude";
    public const string CodexFolder = "agent-bridge-codex";
    public const long LogMaxBytes = 1 << 20;
    public const int MaxAncestors = 12;
    public const int MaxEventBytes = 16 << 20;
    public const int MaxEventDepth = 512;
    public const int Version = 1;

    // As Test-BridgeDaemonAlive: a consumer that has not touched the heartbeat for a minute is
    // treated as stopped.
    public static readonly TimeSpan HeartbeatMaxAge = TimeSpan.FromSeconds(60);

    // What each hook prints, which the agent reads. Claude and Codex hooks print nothing:
    // their output would reach the model or change a decision. Copilot's ask_user reply lets
    // the tool run, so the native prompt is shown and stays answerable (invariant 3).
    static readonly Dictionary<string, string> Replies = new(StringComparer.Ordinal)
    {
        ["claude/register"] = "",
        ["claude/stop"] = "",
        ["claude/ask"] = "",
        ["claude/notification"] = "",
        ["codex/hook"] = "",
        ["copilot/ask_user"] = "{\"permissionDecision\":\"allow\"}\n",
        ["copilot/agent_stop"] = "{}\n",
        ["copilot/permission"] = "{}\n",
    };

    public static IEnumerable<string> Keys => Replies.Keys;

    public static string? Reply(string agent, string hook) => Replies.TryGetValue(agent + "/" + hook, out var reply) ? reply : null;

    public static string SpoolDirectory(string runtimeRoot) => Path.Combine(runtimeRoot, SpoolFolder);

    public static string HeartbeatPath(string runtimeRoot) => Path.Combine(runtimeRoot, HeartbeatFile);

    public static string LogPath(string runtimeRoot) => Path.Combine(runtimeRoot, LogFile);

    // Whether a consumer is taking events: the heartbeat is younger than a minute. The reason
    // when it is not uses the Go hook's words, so the two hooks' log lines group together.
    public static (bool Alive, string? Why) ConsumerState(string runtimeRoot, DateTimeOffset now)
    {
        DateTime written;
        try
        {
            var info = new FileInfo(HeartbeatPath(runtimeRoot));
            if (!info.Exists) { return (false, "no daemon heartbeat"); }
            written = info.LastWriteTimeUtc;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return (false, "no daemon heartbeat"); }
        var age = now.UtcDateTime - written;
        return age >= HeartbeatMaxAge ? (false, $"daemon heartbeat {(int)age.TotalSeconds}s old") : (true, null);
    }

    public static string FileName(DateTimeOffset now, int pid, ReadOnlySpan<byte> suffix) =>
        $"{UnixNanoseconds(now):D20}-{pid}-{Convert.ToHexStringLower(suffix)}.json";

    public static long UnixNanoseconds(DateTimeOffset at) => (at.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) * 100;

    // RFC 3339 with the offset, as Go's time.RFC3339Nano writes a local time.
    public static string Stamp(DateTimeOffset at) => at.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", CultureInfo.InvariantCulture);

    // A session id as a file name, as agent-ha-bridge's Get-CopilotSafeSessionKey makes it (and discovery reads it).
    public static string SafeKey(string sessionId)
    {
        var clean = new string(sessionId.Where(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-').ToArray()).TrimStart('.');
        if (string.IsNullOrWhiteSpace(clean)) { return "unknown"; }
        return clean.Length > 96 ? clean[..96] : clean;
    }

    // <runtime>\agent-bridge-codex\<session>.approval.json, the marker an answer is typed through.
    public static string ApprovalMarkerPath(string runtimeRoot, string sessionId) =>
        Path.Combine(runtimeRoot, CodexFolder, SafeKey(sessionId) + ".approval.json");

    // The session_id a Codex event names, or null.
    public static string? CodexSessionId(ReadOnlySpan<byte> utf8) => CodexEvent(utf8).Session;

    // A Codex event's session and hook_event_name, either null where it has none.
    public static (string? Session, string? Event) CodexEvent(ReadOnlySpan<byte> utf8)
    {
        try
        {
            // As deep as any event the hook accepts (IsJson), or a deep one would withdraw nothing.
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = MaxEventDepth });
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return (null, null); }
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
            return (Text("session_id"), Text("hook_event_name"));
        }
        catch (JsonException) { return (null, null); }
    }

    // The Codex events after which a session's approval is no longer being asked, whichever input
    // answered it. SessionStart too: a Codex that died waiting for an approval never sent
    // SessionEnd, and its resumed session must not offer the dead run's approval.
    public static bool RetiresCodexApproval(string? hookEventName) =>
        hookEventName is "SessionStart" or "PreToolUse" or "UserPromptSubmit" or "Stop" or "SessionEnd";

    // Removes the session's approval marker, as agent-ha-bridge's PowerShell hook did whenever the
    // event did not reach its daemon. Best effort; a delete needs no free space.
    public static void WithdrawCodexApproval(string runtimeRoot, string sessionId)
    {
        try { File.Delete(ApprovalMarkerPath(runtimeRoot, sessionId)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    // Whether the bytes are one JSON value, as Go's json.Valid: an event the hook could not
    // parse would be ignored by the PowerShell hook too, so it is not handed on.
    public static bool IsJson(ReadOnlySpan<byte> utf8)
    {
        if (utf8.IsEmpty) { return false; }
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Disallow, MaxDepth = MaxEventDepth });
            if (!reader.Read()) { return false; }
            reader.Skip();
            return !reader.Read();
        }
        catch (JsonException) { return false; }
    }

    // Writes one event to the spool: under a temporary name, then renamed into place.
    public static string Write(string runtimeRoot, string agent, string hook, ReadOnlySpan<byte> utf8Event, IReadOnlyList<int> ancestors, DateTimeOffset now, int pid)
    {
        var directory = SpoolDirectory(runtimeRoot);
        if (OperatingSystem.IsWindows()) { Directory.CreateDirectory(directory); }
        else { Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }

        var buffer = new ArrayBufferWriter<byte>(utf8Event.Length + 256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("v", Version);
            writer.WriteString("agent", agent);
            writer.WriteString("hook", hook);
            writer.WriteStartArray("ancestors");
            foreach (var a in ancestors) { writer.WriteNumberValue(a); }
            writer.WriteEndArray();
            writer.WriteString("receivedAt", Stamp(now));
            writer.WritePropertyName("event");
            writer.WriteRawValue(utf8Event, skipInputValidation: true);
            writer.WriteEndObject();
        }

        Span<byte> suffix = stackalloc byte[4];
        RandomNumberGenerator.Fill(suffix);
        var final = Path.Combine(directory, FileName(now, pid, suffix));
        var partial = final + ".tmp";
        WritePrivate(partial, buffer.WrittenSpan);
        try { File.Move(partial, final); }
        catch
        {
            try { File.Delete(partial); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
        return final;
    }

    static void WritePrivate(string path, ReadOnlySpan<byte> bytes)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        var stream = new FileStream(path, options);
        try
        {
            stream.Write(bytes);
            stream.Dispose();
        }
        catch
        {
            // Created here, so removed here: the intake never reads a .tmp, and a hook failing
            // on a full disk again and again must not keep every event it could not finish.
            try { stream.Dispose(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            try { File.Delete(path); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    // Appends one run's outcome to the hook log, keeping one previous log once it reaches a
    // megabyte. Best effort: a hook never fails for want of a log line.
    public static void Record(string runtimeRoot, string agent, string hook, string path, string? reason, DateTimeOffset now, long ms)
    {
        try
        {
            var log = LogPath(runtimeRoot);
            var buffer = new ArrayBufferWriter<byte>(256);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("at", Stamp(now));
                writer.WriteString("agent", agent);
                writer.WriteString("hook", hook);
                writer.WriteString("path", path);
                if (!string.IsNullOrEmpty(reason)) { writer.WriteString("reason", reason); }
                writer.WriteNumber("ms", Math.Max(0, ms));
                writer.WriteEndObject();
            }
            try
            {
                if (new FileInfo(log) is { Exists: true, Length: >= LogMaxBytes }) { Rotate(log); }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            buffer.Write("\n"u8);
            AtomicAppend.Write(log, buffer.WrittenSpan);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { }
    }

    // Two hooks that both saw a full log would each move "the" log to .1, the second moving the
    // first's fresh log over the megabyte just rotated, and with it the record of unspooled runs.
    // So one rotates at a time, and only a log still full once it holds the lock; one that finds
    // another rotating appends to whichever log is there. (agent-ha-bridge's Go hook rotates the
    // same log unlocked, as it always has; this coordinates this product's own hooks.)
    static void Rotate(string log)
    {
        using var rotating = new FileStream(log + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        BeforeRotate?.Invoke(log);
        if (new FileInfo(log) is { Exists: true, Length: >= LogMaxBytes }) { File.Move(log, log + ".1", overwrite: true); }
    }

    // A test's way into the moment between taking the lock and looking again.
    internal static Action<string>? BeforeRotate;

    // One spooled event, read back. Null when the file is not one: the reader drops it.
    public static SpoolEvent? Parse(ReadOnlySpan<byte> utf8, string name)
    {
        try
        {
            // Whatever IsJson let a hook spool, plus the envelope around it.
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = MaxEventDepth + 1 });
            using var doc = JsonDocument.ParseValue(ref reader);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) { return null; }
            if (!root.TryGetProperty("agent", out var agent) || agent.ValueKind != JsonValueKind.String) { return null; }
            if (!root.TryGetProperty("hook", out var hook) || hook.ValueKind != JsonValueKind.String) { return null; }
            var version = root.TryGetProperty("v", out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;
            var ancestors = new List<int>();
            if (root.TryGetProperty("ancestors", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in list.EnumerateArray())
                {
                    if (a.ValueKind == JsonValueKind.Number && a.TryGetInt32(out var id) && id > 0) { ancestors.Add(id); }
                }
            }
            DateTimeOffset? received = root.TryGetProperty("receivedAt", out var r) && r.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) ? at : null;
            var ev = root.TryGetProperty("event", out var e) ? e.Clone() : default;
            return new SpoolEvent(name, version, agent.GetString()!, hook.GetString()!, ancestors, received, ev);
        }
        catch (JsonException) { return null; }
    }

    public static string Utf8(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}

// One event from the spool, as the hook recorded it.
public sealed record SpoolEvent(string Name, int Version, string Agent, string Hook, IReadOnlyList<int> Ancestors, DateTimeOffset? ReceivedAt, JsonElement Event)
{
    public string Key => Agent + "/" + Hook;
}
