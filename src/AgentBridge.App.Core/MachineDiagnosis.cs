using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using AgentBridge.Host.Core.Diagnostics;
using AgentBridge.Local;
using AgentBridge.Protocol;

namespace AgentBridge.App.Core;

// "Diagnose this machine" (#802, docs/protocol/v1-diagnostics.md) on another machine's row: a
// read-only snapshot of its health, asked for over its end-to-end channel, where its host serves
// only a device pinned there as a person's (this app is one). Shown as plain text for the person
// to read, and copied only after they say they have reviewed it; nothing here sends it anywhere.
// This machine's row has none: its host is reached over the local pipe, which never serves it.
//
// The host sends closed codes, validated versions, times and counts. This reads the snapshot
// strictly (a missing field refuses it, never a default), shows a version only if it is one, and
// reads each journal entry with the journal's one reader, JournalCodec: one that does not read is
// left out and counted. The host and this app list the same product names (Host.Core and the hook
// contract, by assembly); a frame or exception type only the host lists, such as one from its
// own executable or another version, reads as <external> or <unrecognized>, marked, never by its
// name. So no free text reaches the person or their clipboard.
public sealed class MachineDiagnosisViewModel : Observable
{
    static MachineDiagnosisViewModel() => ExceptionShape.TrustProduct();

    static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);
    static readonly JsonSerializerOptions Strict = new(ProtocolJson.Options)
    {
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
    };

    readonly TimeProvider time;
    bool asking;
    string? text;
    bool reviewed;
    bool copied;
    string? note;
    DateTimeOffset? askedAt;
    CancellationTokenSource? ask;

    public MachineDiagnosisViewModel(Machine machine, TimeProvider time)
    {
        Machine = machine;
        this.time = time;
    }

    public Machine Machine { get; }
    public string Name => Machine.Name;

    // The snapshot as text, once one has come; null before and after Close.
    public string? Text => text;
    public bool HasText => text is not null;
    // The person's word that they have read it: Copy waits for it.
    public bool Reviewed
    {
        get => reviewed;
        set
        {
            if (text is null || !Set(ref reviewed, value)) { return; }
            copied = false;
            Changed();
        }
    }
    public bool CanDiagnose => !asking;
    // Diagnose is offered only when this machine's host lists it: an older host, or one that does
    // not take this device as a person's, is never asked, and its row says to update it instead.
    public bool Offered => Machine.Host.Can(DiagnosticsProtocol.Capability);
    public bool NotOffered => !Offered;
    public string UpdateHint => $"Can't be diagnosed from this device · update {Product.Name} on {Name}";
    public bool CanCopy => text is not null && reviewed;
    public string CopyText => copied ? "Copied" : "Copy";
    public string? Note => note;
    public bool HasNote => note is not null;
    public string DiagnoseName => $"Diagnose {Name}";
    public string CopyName => $"Copy {Name}'s diagnostics";

    public void Diagnose() => _ = DiagnoseAsync();

    // The machine's name or its host's capabilities may have changed.
    public void Refresh()
    {
        Raise(nameof(Name));
        Raise(nameof(Offered));
        Raise(nameof(NotOffered));
        Raise(nameof(UpdateHint));
        Raise(nameof(DiagnoseName));
        Raise(nameof(CopyName));
    }

    public async Task DiagnoseAsync()
    {
        if (asking || !Offered) { return; }
        var now = time.GetUtcNow();
        if (askedAt is { } last && now - last < DiagnosticsProtocol.MinInterval) { return; }
        askedAt = now;
        asking = true;
        var mine = ask = new CancellationTokenSource();
        Show(null, null);
        string? got = null;
        // Said unless a snapshot is read, so that whatever goes wrong, the row is never left asking.
        string? why = Refusal(null, Name);
        try
        {
            var result = await Machine.Host.CallAsync(DiagnosticsProtocol.Get, new { maxEntries = DiagnosticsProtocol.DefaultEntries }, Patience, mine.Token);
            got = Read(result) is { } snapshot ? Format(snapshot, Name) : null;
            why = got is null ? $"{Name} sent diagnostics this app can't read" : null;
        }
        // Only the host's own three reasons are worded; its text is never shown.
        catch (HostErrorException e) { why = Refusal(e.Message, Name); }
        catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException or InvalidOperationException
            or JsonException or ObjectDisposedException or NotSupportedException)
        {
            // Not reached, refused, or gone: said as couldn't be reached.
        }
        finally
        {
            if (ReferenceEquals(ask, mine))
            {
                ask = null;
                asking = false;
                Show(got, why);
            }
            mine.Dispose();
        }
    }

    // DiagnosticsProtocol's three reasons in the person's words, as the PWA says them; any other
    // reason a host gives is not shown.
    internal static string Refusal(string? reason, string name) => reason switch
    {
        DiagnosticsProtocol.Refused => $"Approve this device at {name} to diagnose it",
        DiagnosticsProtocol.TooSoon => "Diagnosed moments ago · try again shortly",
        DiagnosticsProtocol.Unfinished => $"{name} didn't answer in time · try again",
        _ => $"{name} couldn't be reached · try again",
    };

    // Copied only once the person has said they reviewed it. put: the clipboard, false when it
    // could not take the text.
    public bool Copy(Func<string, bool> put)
    {
        if (text is null || !reviewed) { return false; }
        var ok = put(text);
        copied = ok;
        note = ok ? null : "It could not be copied here. Select the text to copy it.";
        Changed();
        return ok;
    }

    public void Close()
    {
        ask?.Cancel();
        ask = null;
        asking = false;
        Show(null, null);
    }

    void Show(string? text, string? note)
    {
        this.text = text;
        this.note = note;
        reviewed = false;
        copied = false;
        Changed();
    }

    internal static DiagnosticsSnapshot? Read(JsonElement result)
    {
        try
        {
            return result.ValueKind == JsonValueKind.Object && result.Deserialize<DiagnosticsSnapshot>(Strict) is { Schema: DiagnosticsProtocol.Schema } s
                && Whole(s) ? s : null;
        }
        // A time ProtocolJson cannot parse throws FormatException, not JsonException.
        catch (Exception e) when (e is JsonException or FormatException or OverflowException or ArgumentException or NotSupportedException or InvalidOperationException)
        {
            return null;
        }
    }

    // System.Text.Json reads a null element of a list whatever its annotation says, so an agent
    // sent as null is refused here rather than read as a snapshot that throws when shown.
    static bool Whole(DiagnosticsSnapshot s) => s.Readiness.Agents.All(a => a is not null);

    // The plain text shown for review and copied after it, as the PWA's (pwa/js/diagnostics.js):
    // times in Pacific, as everything this project shows; the wire keeps them in UTC.
    internal static string Format(DiagnosticsSnapshot d, string name)
    {
        var lines = new List<string>
        {
            $"{Product.Name} diagnostics · {name} · {At(d.At)}",
            $"host: {Version(d.Host.Version, d.Host.VersionMissing)}{(d.Host.Dev ? " (dev)" : "")}, started {At(d.Host.StartedAt)}, up {Math.Max(0, d.Host.UptimeSeconds)}s",
            $"last restart: {(d.LastRestart.At is { } r ? At(r) : Unavailable(DiagnosticsMissing.NotRecorded))}, cause {(d.LastRestart.CauseMissing is { } c ? Unavailable(c) : "unknown")}",
            $"relay: {(d.Relay.Missing is { } rm ? Unavailable(rm) : Code(d.Relay.State))}{(d.Relay.Since is { } since ? $" since {At(since)}" : "")}",
            $"read-only: {(d.Holds.ReadOnlyMissing is { } om ? Unavailable(om) : d.Holds.ReadOnly is { } ro ? Code(ro) : Unavailable(null))}",
            $"discovery held: {(d.Holds.DiscoveryMissing is { } dm ? Unavailable(dm) : Held(d.Holds.DiscoveryHeld))}",
        };
        var agents = d.Readiness.Agents.Take(DiagnosticsProtocol.MaxAgents).ToList();
        var more = Math.Max(0, d.Readiness.Omitted) + d.Readiness.Agents.Count - agents.Count;
        lines.Add($"readiness: {(d.Readiness.Missing is { } am ? Unavailable(am) : $"checked {(d.Readiness.CheckedAt is { } ca ? At(ca) : Unavailable(null))}")}{(more > 0 ? $", {more} more not shown" : "")}");
        foreach (var a in agents)
        {
            lines.Add($"  {Code(a.Id)} {Version(a.Version, a.VersionMissing)}: installation {Code(a.Installation)}, versionCheck {Code(a.VersionCheck)}, hooks {Code(a.Hooks)}, "
                + $"authentication {Code(a.Authentication)}, activation {Code(a.Activation)}{(a.LastHookHandledAt is { } h ? $", last hook {At(h)}" : "")}");
        }
        var j = d.Journal;
        if (j.Missing is { } jm)
        {
            lines.Add($"journal: {Unavailable(jm)}");
            return string.Join('\n', lines);
        }
        var raws = j.Entries.Take(DiagnosticsProtocol.MaxEntries).ToList();
        var entries = new List<JournalEntry>();
        foreach (var raw in raws)
        {
            if (raw.ValueKind == JsonValueKind.Object && JournalCodec.TryReadFromAnotherProcess(Encoding.UTF8.GetBytes(raw.GetRawText()), out var entry)) { entries.Add(entry); }
        }
        var unreadable = (long)Math.Max(0, j.Invalid) + j.Entries.Count - entries.Count;
        var counts = new (string What, long N)[]
        {
            ("not asked for", j.OmittedByLimit), ("left out for size", j.OmittedBySize), ("unreadable", unreadable),
            ("evicted by count", j.EvictedByCount), ("evicted by age", j.EvictedByAge), ("evicted by size", j.EvictedByBytes),
            ("rejected oversized", j.RejectedOversized), ("rejected malformed", j.RejectedMalformed), ("dropped on load", j.DroppedOnLoad),
            ("persist failures", j.PersistFailures),
        };
        lines.Add($"journal: {entries.Count} entries, newest first{string.Concat(counts.Where(c => c.N > 0).Select(c => $"; {c.N} {c.What}"))}{(j.LoadFailed ? "; load failed" : "")}");
        foreach (var e in entries) { lines.Add($"  {Entry(e)}"); }
        return string.Join('\n', lines);
    }

    // As JournalCodec writes them: enum names as declared.
    static string Entry(JournalEntry e)
    {
        var parts = new List<string> { $"#{e.Seq}", At(e.AtUtc), $"{e.Family}.{e.Kind}", e.Outcome.ToString() };
        if (e.Code is { } code) { parts.Add($"code {code.ToString(CultureInfo.InvariantCulture)}"); }
        if (e.Version is { } v) { parts.Add(v); }
        if (e.DurationMs is { } ms) { parts.Add($"{ms.ToString(CultureInfo.InvariantCulture)}ms"); }
        if (e.Stages.Count > 0) { parts.Add($"stages {string.Join(" > ", e.Stages.Select(s => $"{s.Name} {s.Ms.ToString(CultureInfo.InvariantCulture)}ms"))}"); }
        if (e.Exception is { } x)
        {
            parts.Add($"exception {x.Type}{(x.Frames.Count > 0 ? $" at {string.Join(" < ", x.Frames.Select(f => $"{f.Type}.{f.Method}"))}" : "")}");
        }
        var omitted = Enum.GetValues<JournalOmissions>().Where(o => o != JournalOmissions.None && e.Omitted.HasFlag(o)).Select(o => o.ToString()).ToList();
        if (omitted.Count > 0) { parts.Add($"omitted {string.Join(',', omitted)}"); }
        return string.Join(' ', parts);
    }

    static string At(DateTimeOffset t)
    {
        var local = TimeZoneInfo.ConvertTime(t, Pacific);
        var zone = ReferenceEquals(Pacific, TimeZoneInfo.Utc) ? "UTC" : Pacific.IsDaylightSavingTime(local) ? "PDT" : "PST";
        return $"{local.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)} {zone}";
    }

    static readonly TimeZoneInfo Pacific = FindPacific();

    // As HaAuthGuard finds it: by IANA id, else Windows', else UTC, said as UTC.
    static TimeZoneInfo FindPacific()
    {
        foreach (var id in new[] { "America/Los_Angeles", "Pacific Standard Time" })
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        }
        return TimeZoneInfo.Utc;
    }
    // A snapshot's codes are camelCase on the wire; shown the same.
    static string Code<T>(T value) where T : struct, Enum => JsonNamingPolicy.CamelCase.ConvertName(value.ToString());
    static string Unavailable(DiagnosticsMissing? why) => $"unavailable ({(why is { } w ? Code(w) : "unknown")})";
    static string Version(string? version, DiagnosticsMissing? missing) =>
        DiagnosticsProtocol.IsVersion(version) ? version! : Unavailable(version is null ? missing : DiagnosticsMissing.Invalid);
    // null is not known, never none: only an empty list says nothing is held. A kind this app
    // doesn't know (a newer host's, read as Unknown) is held too, so it is said, never dropped.
    static string Held(IReadOnlyList<AgentKind>? held)
    {
        if (held is null) { return Unavailable(null); }
        var kinds = held.Where(k => k != AgentKind.Unknown).Distinct().Select(k => Code(k)).ToList();
        var unknown = held.Contains(AgentKind.Unknown);
        if (kinds.Count == 0) { return unknown ? Unavailable(null) : "none"; }
        if (unknown) { kinds.Add("unknown"); }
        return string.Join(", ", kinds);
    }

    void Changed()
    {
        Raise(nameof(Name));
        Raise(nameof(Text));
        Raise(nameof(HasText));
        Raise(nameof(Reviewed));
        Raise(nameof(CanDiagnose));
        Raise(nameof(Offered));
        Raise(nameof(NotOffered));
        Raise(nameof(UpdateHint));
        Raise(nameof(CanCopy));
        Raise(nameof(CopyText));
        Raise(nameof(Note));
        Raise(nameof(HasNote));
        Raise(nameof(DiagnoseName));
        Raise(nameof(CopyName));
    }
}
