using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentBridge.Host.Shadow;

// Where agent-ha-bridge (1.33.x, an installation with metadata) keeps what its daemon shows. Read
// only, and only these exact paths:
//   runtime/agent-bridge-daemon-state.json   every live session the daemon publishes, written
//                                            whole and renamed into place (Write-DaemonState)
//   runtime/agent-bridge-daemon.heartbeat    touched on every daemon pass (Set-BridgeDaemonAlive)
//   installer/VERSION                        the installed release
//   <copilot>/session-state/<id>/agent-pending-decision.json, or
//   runtime/copilot-bridge-markers/<id>/agent-pending-decision.json
//                                            a question its hook armed (Get-CopilotDecisionMarkerPath)
// Never config.json (it holds the Home Assistant token), never the spool, and no directory is
// ever listed.
public sealed partial record AgentHaBridgePaths(string Home, string CopilotHome)
{
    public static AgentHaBridgePaths ForCurrentUser(string copilotHome) =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".agent-ha-bridge"), copilotHome);

    public string Runtime => Path.Combine(Home, "runtime");
    public string StateFile => Path.Combine(Runtime, "agent-bridge-daemon-state.json");
    public string Heartbeat => Path.Combine(Runtime, "agent-bridge-daemon.heartbeat");
    public string VersionFile => Path.Combine(Home, "installer", "VERSION");

    // As Get-CopilotDecisionMarkerPath resolves it: beside a Copilot session's own state when that
    // folder exists, else in the bridge's fallback folder (Claude, Codex).
    public string Marker(string sessionId)
    {
        var key = SafeKey(sessionId);
        var beside = Path.Combine(CopilotHome, "session-state", key);
        return Directory.Exists(beside)
            ? Path.Combine(beside, "agent-pending-decision.json")
            : Path.Combine(Runtime, "copilot-bridge-markers", key, "agent-pending-decision.json");
    }

    // Get-CopilotSafeSessionKey: ids arrive from hook payloads, so they are cleaned before they
    // become part of a path.
    public static string SafeKey(string id)
    {
        var clean = Unsafe().Replace(id, "").TrimStart('.');
        if (string.IsNullOrWhiteSpace(clean)) { return "unknown"; }
        return clean.Length > 96 ? clean[..96] : clean;
    }

    [GeneratedRegex("[^a-zA-Z0-9._-]")]
    private static partial Regex Unsafe();
}

public sealed partial class DaemonReader(AgentHaBridgePaths paths, TimeProvider time)
{
    // The daemon's own test of whether it is running (Test-BridgeDaemonAlive): a pass in the last
    // minute. Past that its state file is a record of the past, not of what it shows.
    public static readonly TimeSpan HeartbeatFresh = TimeSpan.FromSeconds(60);

    const long MaxStateBytes = 64L * 1024 * 1024;
    const long MaxMarkerBytes = 4L * 1024 * 1024;

    public ShadowView Read()
    {
        if (!Directory.Exists(paths.Home)) { return new() { Problem = "agent-ha-bridge is not installed for this user" }; }
        var version = ReadShared(paths.VersionFile, 256) is { } v ? ShadowHash.Token(v.Trim()) : null;
        if (!File.Exists(paths.Heartbeat)) { return new() { Problem = "the daemon has never written its heartbeat", Version = version }; }
        if (time.GetUtcNow().UtcDateTime - File.GetLastWriteTimeUtc(paths.Heartbeat) >= HeartbeatFresh)
        {
            return new() { Problem = "the daemon's heartbeat is over a minute old", Version = version };
        }
        Dictionary<string, ShadowSession> sessions = [];
        if (File.Exists(paths.StateFile))
        {
            var text = ReadShared(paths.StateFile, MaxStateBytes);
            if (text is null) { return new() { Problem = "the daemon's state file could not be read", Version = version }; }
            try
            {
                using var doc = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) { return new() { Problem = "the daemon's state file is not an object", Version = version }; }
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.Object) { sessions[entry.Name] = Session(entry.Name, entry.Value); }
                }
            }
            catch (JsonException) { return new() { Problem = "the daemon's state file is not valid JSON", Version = version }; }
        }
        return new() { Sessions = sessions, Version = version };
    }

    ShadowSession Session(string id, JsonElement e)
    {
        var (question, unknown) = Question(id);
        return new()
        {
            Id = id,
            // Get-DaemonEntryKind: an entry recorded before kinds existed is Copilot's.
            Agent = Str(e, "Kind") is { Length: > 0 } kind ? kind : "copilot",
            Status = Str(e, "Status") ?? "",
            Agents = Count(e, "BackgroundAgents"),
            Shells = Count(e, "BackgroundShells"),
            Model = Str(e, "Model") is { Length: > 0 } m ? m : null,
            Compactions = e.TryGetProperty("Compactions", out var c) && c.TryGetInt32(out var n) ? n : 0,
            ResponseTail = ShadowHash.Tail(Str(e, "LastResponse")),
            Question = question,
            QuestionUnknown = unknown,
        };
    }

    (ShadowQuestion? Question, bool Unknown) Question(string id)
    {
        var path = paths.Marker(id);
        if (!File.Exists(path)) { return (null, false); }
        var text = ReadShared(path, MaxMarkerBytes);
        // The hook writes the marker with Set-Content, not by renaming, so it can be caught half
        // written. That is "do not know yet", which the next sample settles.
        if (text is null) { return (null, true); }
        try
        {
            using var doc = JsonDocument.Parse(text);
            var m = doc.RootElement;
            if (m.ValueKind != JsonValueKind.Object) { return (null, true); }
            List<ShadowField> fields = [];
            if (m.TryGetProperty("fields", out var fs) && fs.ValueKind == JsonValueKind.Array && fs.GetArrayLength() > 0)
            {
                foreach (var f in fs.EnumerateArray()) { fields.Add(Field(f)); }
            }
            else if (Strings(m, "choices") is { Count: > 0 } choices)
            {
                fields.Add(new("choice", choices.Count, ShadowHash.Options(choices)));
            }
            return (new ShadowQuestion
            {
                CallId = Str(m, "toolCallId") is { Length: > 0 } call ? call : null,
                AskedAt = Asked(m),
                MessageHead = ShadowHash.Head(Str(m, "question")),
                AtMachine = m.TryGetProperty("terminalOnly", out var t) && t.ValueKind == JsonValueKind.True,
                Fields = ShadowCompare.Canonical(fields),
            }, false);
        }
        catch (JsonException) { return (null, true); }
    }

    // Get-DecisionSchemaFields' record: IsText, MultiSelect, the option labels and the values
    // the CLI records. Values are what an answer is checked against, so they are what is compared.
    static ShadowField Field(JsonElement f)
    {
        var values = Strings(f, "Values") is { Count: > 0 } v ? v : Strings(f, "Options") ?? [];
        var isText = f.TryGetProperty("IsText", out var t) ? t.ValueKind == JsonValueKind.True : values.Count == 0;
        if (isText) { return new("text", 0, null); }
        var multi = f.TryGetProperty("MultiSelect", out var ms) && ms.ValueKind == JsonValueKind.True;
        return new(multi ? "multi" : "choice", values.Count, ShadowHash.Options(values));
    }

    // When the hook armed it; failing that, the hook's clock in the decision id's tail.
    static DateTimeOffset? Asked(JsonElement m)
    {
        if (Str(m, "armedAt") is { } a && DateTimeOffset.TryParse(a, CultureInfo.InvariantCulture, DateTimeStyles.None, out var at)) { return at; }
        if (Str(m, "decisionId") is { } id && Millis().Match(id) is { Success: true } ms && long.TryParse(ms.Groups[1].Value, out var n))
        {
            try { return DateTimeOffset.FromUnixTimeMilliseconds(n); }
            catch (ArgumentOutOfRangeException) { }
        }
        return null;
    }

    [GeneratedRegex(@"-(\d{12,14})$")]
    private static partial Regex Millis();

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // PowerShell's ConvertTo-Json writes a one-item list as the item itself in older versions.
    static int Count(JsonElement e, string name) => !e.TryGetProperty(name, out var v) ? 0 : v.ValueKind switch
    {
        JsonValueKind.Array => v.EnumerateArray().Count(x => x.ValueKind != JsonValueKind.String || !string.IsNullOrEmpty(x.GetString())),
        JsonValueKind.String => string.IsNullOrEmpty(v.GetString()) ? 0 : 1,
        _ => 0,
    };

    // Strings as the host's options carry them: a string as itself, any other scalar as its JSON.
    static List<string>? Strings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) { return null; }
        if (v.ValueKind == JsonValueKind.String) { return [v.GetString()!]; }
        if (v.ValueKind != JsonValueKind.Array) { return null; }
        return [.. v.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()! : x.GetRawText())];
    }

    // Opened the way the daemon can live with: sharing read, write and delete, so its write and
    // rename of the state file, and a hook rewriting a marker, never meet a file held by this.
    // Closed at once. Null when it could not be read now.
    public static string? ReadShared(string path, long maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, new FileStreamOptions
            {
                Mode = FileMode.Open,
                Access = FileAccess.Read,
                Share = FileShare.ReadWrite | FileShare.Delete,
            });
            if (stream.Length > maxBytes) { return null; }
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
