using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentBridge.Protocol;

// "Diagnose this machine" (docs/protocol/v1-diagnostics.md, #802): a person's device approved at
// this machine reads, over the end-to-end channel, the machine's content-free journal (#737,
// Journal.cs) and a closed snapshot of its health. Every field is a number, a time, a validated
// version or a closed code: nothing here carries a message, prompt, title, path, URL, account,
// token or exception text. What a host does not know is said as a DiagnosticsMissing code,
// never filled in with a healthy default.
public static class DiagnosticsProtocol
{
    public const string Capability = "diagnostics";
    public const string Get = "diagnostics.get";
    public const int Schema = 1;

    // Journal entries a request gets when it names none, and at most.
    public const int DefaultEntries = 100;
    public const int MaxEntries = 200;
    // The journal's share of a reply (its canonical entry bytes), and the whole reply: within one
    // channel message (ProtocolJson.MaxMessageBytes) uncompressed, for a peer that does not inflate.
    public const int MaxJournalBytes = 32 * 1024;
    public const int MaxResponseBytes = 40 * 1024;
    public const int MaxAgents = 8;
    // A request has at most this many bytes of JSON; it only ever names maxEntries.
    public const int MaxRequestBytes = 256;
    // One read at a time per host, and one per device this often: a reply is bounded, and asking
    // again sooner gets a refusal, never another read.
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(2);
    // A host gives up a read after this long, or once the asking channel closes, and frees its
    // one slot: well inside a client's own wait (the Windows app's is 15 seconds).
    public static readonly TimeSpan ServerDeadline = TimeSpan.FromSeconds(5);

    // A host's three reasons for not answering, sent as these exact words so that a client can say
    // each in the person's own; a client shows no other reason a host gives.
    public const string Refused = "diagnostics are for a person's device approved at this machine, through the relay";
    public const string TooSoon = "diagnostics were read moments ago; try again shortly";
    public const string Unfinished = "diagnostics could not be read in time; try again shortly";

    // The journal's one rule for a version (numeric, with a closed set of prerelease words): the
    // host's or an agent's version is sent only if it passes it.
    public static bool IsVersion(string? version) => JournalContract.IsVersion(version);

    // The request's one field. Anything else (another property, a principal or origin a device
    // might claim, a wrong type) is refused, never ignored: who asks is the channel's pinned key.
    public static int ReadRequest(JsonElement data)
    {
        if (data.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) { return DefaultEntries; }
        if (data.ValueKind != JsonValueKind.Object) { throw new ArgumentException($"{Get} takes an object with only maxEntries"); }
        if (data.GetRawText().Length > MaxRequestBytes) { throw new ArgumentException($"{Get} takes only maxEntries"); }
        int? asked = null;
        foreach (var p in data.EnumerateObject())
        {
            if (p.Name != "maxEntries" || asked is not null) { throw new ArgumentException($"{Get} takes only maxEntries"); }
            if (p.Value.ValueKind != JsonValueKind.Number || !p.Value.TryGetInt32(out var n) || n < 1)
            {
                throw new ArgumentException($"{Get}'s maxEntries is a whole number from 1");
            }
            asked = Math.Min(n, MaxEntries);
        }
        return asked ?? DefaultEntries;
    }
}

// Why a part of the snapshot is absent. NotRecorded: this host keeps no record of it. NotStarted:
// what knows it has not started (or reported) yet. NotConfigured: this host runs without it.
// Invalid: what it had did not pass validation, so it is withheld. Failed: reading it failed.
// Unknown: a newer host's reason this client does not know.
[UnknownAs(Unknown)]
public enum DiagnosticsMissing { Unknown, NotRecorded, NotStarted, NotConfigured, Invalid, Failed }

// Why the machine acts on nothing from other devices, as a code: the reason text stays local.
[UnknownAs(Other)]
public enum DiagnosticsReadOnly { None, ConfigReadOnly, ConfigProblem, AgentHaBridge, Other }

[UnknownAs(Unknown)]
public enum DiagnosticsRelayState { Unknown, Off, SignedOut, Connecting, Connected, Refused }

[UnknownAs(Unknown)]
public enum DiagnosticsCheck { Unknown, Missing, NeedsAction, Working, Blocked, Failed }

// A launcher id is configurable; one not in this list is Other.
[UnknownAs(Other)]
public enum DiagnosticsAgentId { Other, Copilot, Claude, Codex }

// A field whose parameter has no default is written even when it is null, though ProtocolJson
// leaves nulls out: a client reads a snapshot strictly, every such field required.
public sealed record DiagnosticsHost([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version, bool Dev, DateTimeOffset StartedAt, long UptimeSeconds,
    DiagnosticsMissing? VersionMissing = null);

// The last restart is this process's start; why it restarted is not recorded yet (#802).
public sealed record DiagnosticsRestart([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? At, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DiagnosticsMissing? CauseMissing);

public sealed record DiagnosticsRelay(DiagnosticsRelayState State, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? Since, DiagnosticsMissing? Missing = null);

// Discovery held: the agents whose sessions are kept while discovery cannot read them. ReadOnly is
// null, with ReadOnlyMissing, when whether the machine is read-only is not known now.
public sealed record DiagnosticsHolds([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DiagnosticsReadOnly? ReadOnly,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] IReadOnlyList<AgentKind>? DiscoveryHeld,
    DiagnosticsMissing? DiscoveryMissing = null, DiagnosticsMissing? ReadOnlyMissing = null);

public sealed record DiagnosticsAgent(DiagnosticsAgentId Id, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version, DiagnosticsCheck Installation, DiagnosticsCheck VersionCheck,
    DiagnosticsCheck Hooks, DiagnosticsCheck Authentication, DiagnosticsCheck Activation, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? LastHookHandledAt,
    DiagnosticsMissing? VersionMissing = null);

public sealed record DiagnosticsReadiness([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? CheckedAt, IReadOnlyList<DiagnosticsAgent> Agents, int Omitted,
    DiagnosticsMissing? Missing = null);

// Entries are JournalCodec's canonical form of JournalEntry, newest first, each re-read
// strictly before it is sent; one that does not read is dropped and counted in Invalid.
// OmittedBySize: entries the reply's size limit left out after the journal's own limits. The
// counters are the journal's own (JournalSnapshot); LoadFailed: its file could not be read at
// start, so only entries since then are here.
public sealed record DiagnosticsJournalPart(IReadOnlyList<JsonElement> Entries, int OmittedByLimit, int OmittedBySize, int Invalid,
    long EvictedByCount, long EvictedByAge, long EvictedByBytes, long RejectedOversized, long RejectedMalformed, long DroppedOnLoad,
    bool LoadFailed, long PersistFailures, DiagnosticsMissing? Missing = null);

public sealed record DiagnosticsSnapshot(int Schema, DateTimeOffset At, DiagnosticsHost Host, DiagnosticsRestart LastRestart,
    DiagnosticsRelay Relay, DiagnosticsHolds Holds, DiagnosticsReadiness Readiness, DiagnosticsJournalPart Journal);
