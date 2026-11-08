using System.Text.Json;
using AgentBridge.Host.Core.Diagnostics;
using AgentBridge.Host.Delivery;
using AgentBridge.Local;
using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// diagnostics.get (docs/protocol/v1-diagnostics.md, #802): "Diagnose this machine" from a person's
// phone, browser or Windows app. Only for a device pinned at this machine with the person role
// (RemoteAuthorization.IsPinnedPerson), through the relay's end-to-end channel: never this
// machine's pipe (agents, the MCP server), the Home Assistant connector, a service or another
// host, and never while trusted-peer pins are off, when the relay's word for a device's kind is
// all there would be. Read-only: it reads in-memory state and the journal's bounded snapshot,
// runs no command, and reads no setting, credential or log file.
//
// Everything sent is projected onto closed codes here: readiness keeps its states and a
// validated version and drops labels, folders, executables and details; the relay keeps its
// state and drops its address and detail; read-only and held discovery keep a code and the agent
// kinds and drop their reason text. Journal entries are already content-free (#737) and are
// re-read through JournalCodec, so nothing outside that closed form is sent.
public sealed class RemoteDiagnostics
{
    sealed record Sources(Func<RelayStatus> Relay, Func<AgentReadinessReport?> Readiness, DateTimeOffset StartedAt);

    public const string Refusal = DiagnosticsProtocol.Refused;
    public const string TooSoon = DiagnosticsProtocol.TooSoon;
    public const string Unfinished = DiagnosticsProtocol.Unfinished;
    const int MaxAskers = 64;

    readonly Func<SecureChannel, bool> person;
    readonly IDiagnosticsJournalReader? journal;
    readonly string version;
    readonly bool dev;
    readonly Func<DiscoveryHeld?> held;
    readonly Func<(DiagnosticsReadOnly? Code, DiagnosticsMissing? Missing)> readOnly;
    readonly TimeProvider time;
    readonly Action<string> log;
    readonly SemaphoreSlim one = new(1, 1);
    readonly Dictionary<string, DateTimeOffset> asked = new(StringComparer.Ordinal);
    long givenUp;
    Sources? sources;

    public RemoteDiagnostics(Func<SecureChannel, bool> person, IDiagnosticsJournalReader? journal, string version, bool dev,
        Func<DiscoveryHeld?> held, Func<(DiagnosticsReadOnly? Code, DiagnosticsMissing? Missing)> readOnly, TimeProvider? time = null, Action<string>? log = null)
    {
        this.person = person;
        this.journal = journal;
        this.version = version;
        this.dev = dev;
        this.held = held;
        this.readOnly = readOnly;
        this.time = time ?? TimeProvider.System;
        this.log = log ?? (_ => { });
    }

    // The host's status sources come up after the sessions service that serves this.
    public void Bind(Func<RelayStatus> relay, Func<AgentReadinessReport?> readiness, DateTimeOffset startedAt) =>
        Volatile.Write(ref sources, new Sources(relay, readiness, startedAt));

    public bool Offered(IProtocolChannel channel) => channel is SecureChannel secure && person(secure);

    // G5 as a read reports it: the last look something else made (a hello, the hello watch, an
    // action), never a look of its own, so a read never waits on Windows' named objects or Task
    // Scheduler. Unknown, never none: no look within Reported (notStarted), or a look that could
    // not tell (failed).
    public static (DiagnosticsReadOnly? Code, DiagnosticsMissing? Missing) AgentHaBridge(SharedAgentHaBridgeProbe g5) =>
        !g5.TryLast(SharedAgentHaBridgeProbe.Reported, out var reason) ? (null, DiagnosticsMissing.NotStarted)
        : reason == IAgentHaBridgeProbe.Unchecked ? (null, DiagnosticsMissing.Failed)
        : (reason is not null ? DiagnosticsReadOnly.AgentHaBridge : DiagnosticsReadOnly.None, null);

    public async Task<object> HandleAsync(IProtocolChannel channel, JsonElement data)
    {
        if (channel is not SecureChannel secure || !person(secure)) { throw new InvalidOperationException(Refusal); }
        var served = await ReadAsync(secure.PeerId, DiagnosticsProtocol.ReadRequest(data), secure.Closed).ConfigureAwait(false);
        // The host's log line goes after the reply is on the wire (ChannelReply.Then): writing it
        // takes the console and the log file's lock, which no deadline here could bound.
        return new ChannelReply(served.Snapshot, _ =>
        {
            log(served.Note);
            return Task.CompletedTask;
        });
    }

    // A read served, with the host's log line for it, written only once the reply is sent.
    internal sealed record Served(DiagnosticsSnapshot Snapshot, string Note);

    // Reads given up (Unfinished) since the host started. Nothing is written while a read is
    // bounded: the asker hears Unfinished, and the next read served logs the count.
    internal long GivenUp => Interlocked.Read(ref givenUp);

    // The read itself, for a device already found to be a pinned person's: bounded in entries and
    // bytes, and in time. A read already running or one too soon is refused at once; one that
    // cannot finish within ServerDeadline, or whose channel has closed, or whose caller cancels,
    // is given up with Unfinished (never the cause's own text) and frees the slot. Nothing is
    // left running behind it: the only wait is the journal's, and that is cancelled. Nothing in
    // it writes the host's log, which waits on the console and a file.
    internal async Task<Served> ReadAsync(string peer, int entries, Task? closed = null, CancellationToken cancellationToken = default)
    {
        var now = time.GetUtcNow();
        if (!one.Wait(0)) { throw new InvalidOperationException(TooSoon); }
        try
        {
            lock (asked)
            {
                if (asked.TryGetValue(peer, out var last) && now - last < DiagnosticsProtocol.MinInterval) { throw new InvalidOperationException(TooSoon); }
                if (asked.Count >= MaxAskers) { asked.Clear(); }
                asked[peer] = now;
            }
            using var deadline = new CancellationTokenSource(DiagnosticsProtocol.ServerDeadline, time);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, cancellationToken);
            try
            {
                Gone(closed, linked.Token);
                var (snapshot, journalFailure) = await ReadSnapshotAsync(entries, now, linked.Token).ConfigureAwait(false);
                Gone(closed, linked.Token);
                var given = GivenUp;
                return new Served(snapshot, $"diagnostics read by {peer[..Math.Min(8, peer.Length)]}: {snapshot.Journal.Entries.Count} journal entries"
                    + (journalFailure is null ? "" : $"; the journal could not be read ({journalFailure})")
                    + (given == 0 ? "" : $"; {given} given up since the host started"));
            }
            catch (OperationCanceledException)
            {
                Interlocked.Increment(ref givenUp);
                throw new InvalidOperationException(Unfinished);
            }
        }
        finally { one.Release(); }
    }

    static void Gone(Task? closed, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (closed?.IsCompleted == true) { throw new OperationCanceledException(); }
    }

    public async Task<DiagnosticsSnapshot> SnapshotAsync(int maxEntries, DateTimeOffset now, CancellationToken cancellationToken = default) =>
        (await ReadSnapshotAsync(maxEntries, now, cancellationToken).ConfigureAwait(false)).Snapshot;

    // The snapshot, and the type of what kept the journal from being read (sent as failed), for
    // the log line written after the reply.
    async Task<(DiagnosticsSnapshot Snapshot, string? JournalFailure)> ReadSnapshotAsync(int maxEntries, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var bound = Volatile.Read(ref sources);
        var (part, failure) = await JournalAsync(Math.Clamp(maxEntries, 1, DiagnosticsProtocol.MaxEntries), cancellationToken).ConfigureAwait(false);
        var snapshot = new DiagnosticsSnapshot(DiagnosticsProtocol.Schema, now, Host(bound, now),
            new DiagnosticsRestart(bound?.StartedAt, DiagnosticsMissing.NotRecorded),
            Relay(bound), Holds(), Readiness(bound), part);
        return (Fit(snapshot), failure);
    }

    DiagnosticsHost Host(Sources? bound, DateTimeOffset now)
    {
        var valid = DiagnosticsProtocol.IsVersion(version);
        var started = bound?.StartedAt ?? now;
        return new DiagnosticsHost(valid ? version : null, dev, started, Math.Max(0, (long)(now - started).TotalSeconds),
            valid ? null : DiagnosticsMissing.Invalid);
    }

    static DiagnosticsRelay Relay(Sources? bound)
    {
        if (bound is null) { return new DiagnosticsRelay(DiagnosticsRelayState.Unknown, null, DiagnosticsMissing.NotStarted); }
        var relay = bound.Relay();
        var state = relay.State switch
        {
            RelayState.Off => DiagnosticsRelayState.Off,
            RelayState.SignedOut => DiagnosticsRelayState.SignedOut,
            RelayState.Connecting => DiagnosticsRelayState.Connecting,
            RelayState.Connected => DiagnosticsRelayState.Connected,
            RelayState.Refused => DiagnosticsRelayState.Refused,
            _ => DiagnosticsRelayState.Unknown,
        };
        return new DiagnosticsRelay(state, relay.Since, state == DiagnosticsRelayState.Unknown ? DiagnosticsMissing.Invalid : null);
    }

    // A code, or null with why it is not known now: never none in its place.
    DiagnosticsHolds Holds()
    {
        var (code, missing) = readOnly();
        var discovery = held();
        return new DiagnosticsHolds(code is not { } c ? null : Enum.IsDefined(c) ? c : DiagnosticsReadOnly.Other,
            discovery is null ? [] : [.. discovery.Agents.Where(a => a != AgentKind.Unknown).Distinct().Order().Take(DiagnosticsProtocol.MaxAgents)],
            ReadOnlyMissing: code is null ? missing ?? DiagnosticsMissing.Unknown : null);
    }

    static DiagnosticsReadiness Readiness(Sources? bound)
    {
        if (bound?.Readiness() is not { } report)
        {
            return new DiagnosticsReadiness(null, [], 0, DiagnosticsMissing.NotStarted);
        }
        var agents = report.Agents.Take(DiagnosticsProtocol.MaxAgents).Select(Agent).ToList();
        return new DiagnosticsReadiness(report.CheckedAt, agents, Math.Max(0, report.Agents.Count - agents.Count));
    }

    static DiagnosticsAgent Agent(AgentReadiness a)
    {
        var valid = DiagnosticsProtocol.IsVersion(a.Version);
        return new DiagnosticsAgent(AgentId(a.Id), valid ? a.Version : null, Check(a.Installation), Check(a.VersionCheck),
            Check(a.Hooks), Check(a.Authentication), Check(a.Activation), a.LastHookHandledAt,
            valid ? null : a.Version is null ? DiagnosticsMissing.NotRecorded : DiagnosticsMissing.Invalid);
    }

    static DiagnosticsAgentId AgentId(string? id) => id switch
    {
        "copilot" => DiagnosticsAgentId.Copilot,
        "claude" => DiagnosticsAgentId.Claude,
        "codex" => DiagnosticsAgentId.Codex,
        _ => DiagnosticsAgentId.Other,
    };

    static DiagnosticsCheck Check(ReadinessCheck? check) => check?.State switch
    {
        ReadinessState.Missing => DiagnosticsCheck.Missing,
        ReadinessState.NeedsAction => DiagnosticsCheck.NeedsAction,
        ReadinessState.Working => DiagnosticsCheck.Working,
        ReadinessState.Blocked => DiagnosticsCheck.Blocked,
        ReadinessState.Failed => DiagnosticsCheck.Failed,
        _ => DiagnosticsCheck.Unknown,
    };

    async Task<(DiagnosticsJournalPart Part, string? Failure)> JournalAsync(int maxEntries, CancellationToken cancellationToken)
    {
        if (journal is null) { return (Empty(DiagnosticsMissing.NotConfigured), null); }
        JournalSnapshot read;
        try { read = await journal.ReadAsync(maxEntries, DiagnosticsProtocol.MaxJournalBytes, cancellationToken).ConfigureAwait(false); }
        catch (Exception e) when (e is not (OutOfMemoryException or OperationCanceledException))
        {
            return (Empty(DiagnosticsMissing.Failed), e.GetType().Name);
        }
        List<JsonElement> entries = [];
        int invalid = 0, bytes = 0;
        foreach (var entry in read.Entries.Take(maxEntries))
        {
            cancellationToken.ThrowIfCancellationRequested();
            byte[] raw;
            try { raw = JournalCodec.Write(entry); }
            catch (Exception e) when (e is ArgumentException or JsonException or NotSupportedException or InvalidOperationException) { invalid++; continue; }
            if (!JournalCodec.TryRead(raw, out var reread) || reread is null) { invalid++; continue; }
            if (bytes + raw.Length > DiagnosticsProtocol.MaxJournalBytes) { break; }
            bytes += raw.Length;
            using var doc = JsonDocument.Parse(raw);
            entries.Add(doc.RootElement.Clone());
        }
        var dropped = Math.Max(0, Math.Min(read.Entries.Count, maxEntries) - entries.Count - invalid);
        return (new DiagnosticsJournalPart(entries, read.OmittedByLimit + dropped, 0, invalid, read.EvictedByCount, read.EvictedByAge,
            read.EvictedByBytes, read.RejectedOversized, read.RejectedMalformed, read.DroppedOnLoad, read.LoadFailed, read.PersistFailures), null);
    }

    static DiagnosticsJournalPart Empty(DiagnosticsMissing why) => new([], 0, 0, 0, 0, 0, 0, 0, 0, 0, false, 0, why);

    // The whole reply stays within MaxResponseBytes: the oldest journal entries go first.
    static DiagnosticsSnapshot Fit(DiagnosticsSnapshot snapshot)
    {
        while (ProtocolJson.ByteCount(snapshot) > DiagnosticsProtocol.MaxResponseBytes && snapshot.Journal.Entries.Count > 0)
        {
            var part = snapshot.Journal;
            var keep = part.Entries.Count / 2;
            snapshot = snapshot with
            {
                Journal = part with { Entries = [.. part.Entries.Take(keep)], OmittedBySize = part.OmittedBySize + part.Entries.Count - keep },
            };
        }
        return snapshot;
    }
}
