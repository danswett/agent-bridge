using System.Buffers;
using System.Collections.Frozen;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace AgentBridge.Protocol;

// The diagnostics journal's record (#737): what happened, when, in which version, how it ended
// and how long it took, and for a failure the exception's type and the frames it passed through.
// Never content. There is no free text: every field is a closed enum, a bounded number, a
// numeric version, or a name from a closed set (JournalIdentifiers): an exception type the
// journal lists, or a type and method of the product's own trusted assemblies, taken from their
// metadata. Anything else is dropped and the omission marked, before the record is stored or written, and
// a stored record that does not meet the same rules is not read back. One journal: the feedback
// report (#738) and remote diagnostics (#802) read these records, and nothing sends them anywhere
// by itself.
public enum JournalFamily { RelayConnect, RelayDrop, Delivery, HookIntake, Launch, Update, HostLifecycle, Crash }

public enum JournalOutcome { Ok, Refused, Failed, Cancelled, TimedOut, Unavailable }

// Each kind belongs to exactly one family (JournalContract.FamilyOf).
public enum JournalKind
{
    RelayConnected,
    RelayConnectFailed,
    RelayClosed,
    DeliveryStage,
    DeliveryRefused,
    HookPassFailed,
    HookSpoolRetry,
    HookSpoolDropped,
    HookIntakePaused,
    HookIntakeResumed,
    LaunchRequested,
    UpdateStep,
    HostStarted,
    HostStopped,
    UnhandledException,
}

// What a record had to leave out, so a missing field never reads as a field that was empty.
[Flags]
public enum JournalOmissions
{
    None = 0,
    VersionInvalid = 1,
    ExceptionTypeUnrecognized = 2,
    FramesTruncated = 4,
    // Past MaxStages, or by a name that is not the journal's.
    StagesTruncated = 8,
    CodeOutOfRange = 16,
    DurationClamped = 32,
    FrameUnrecognized = 64,
}

// #611's stage timings, by closed name.
public enum JournalStageName { Received, Queued, Typed, Submitted, Confirmed, Spooled, Handled, Connect, Handshake, Other }

public sealed record JournalStage(JournalStageName Name, long Ms);

// A frame is its declaring type and method only: never a file, a line, an offset or a message.
public sealed record JournalFrame(string Type, string Method);

public sealed record JournalException(string Type, IReadOnlyList<JournalFrame> Frames);

public sealed record JournalEntry(
    int Schema,
    long Seq,
    DateTimeOffset AtUtc,
    JournalFamily Family,
    JournalKind Kind,
    JournalOutcome Outcome,
    int? Code,
    string? Version,
    long? DurationMs,
    IReadOnlyList<JournalStage> Stages,
    JournalException? Exception,
    JournalOmissions Omitted);

public static partial class JournalContract
{
    public const int Schema = 1;
    // Bounds, all explicit: the last 500 events or 24 hours, whichever is less, in at most
    // 256 KiB. A typical record is 150-300 bytes and a crash with all its frames under 2 KiB, so
    // the count is the usual limit and the bytes the limit under a run of crashes. Small enough
    // to read whole into a feedback report or a remote snapshot; long enough to hold a day of
    // failures, which are what it is for.
    public const int MaxEntries = 500;
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);
    public const int MaxTotalBytes = 256 * 1024;
    public const int MaxEntryBytes = 2048;
    public const int MaxStages = 8;
    public const int MaxFrames = 16;
    public const int MaxIdentifier = 128;
    public const int MaxVersion = 64;
    // A code is a number: a WebSocket close code, an HTTP status, an exit code's low bits.
    public const int MaxCode = 65535;
    public const long MaxDurationMs = 24L * 60 * 60 * 1000;

    // A frame or exception outside the product and the runtime: named as such, not by its own
    // name, which could be anything a library or an injected module calls itself.
    public const string External = "<external>";
    public const string Unrecognized = "<unrecognized>";

    public static JournalFamily FamilyOf(JournalKind kind) => kind switch
    {
        JournalKind.RelayConnected or JournalKind.RelayConnectFailed => JournalFamily.RelayConnect,
        JournalKind.RelayClosed => JournalFamily.RelayDrop,
        JournalKind.DeliveryStage or JournalKind.DeliveryRefused => JournalFamily.Delivery,
        JournalKind.HookPassFailed or JournalKind.HookSpoolRetry or JournalKind.HookSpoolDropped
            or JournalKind.HookIntakePaused or JournalKind.HookIntakeResumed => JournalFamily.HookIntake,
        JournalKind.LaunchRequested => JournalFamily.Launch,
        JournalKind.UpdateStep => JournalFamily.Update,
        JournalKind.HostStarted or JournalKind.HostStopped => JournalFamily.HostLifecycle,
        JournalKind.UnhandledException => JournalFamily.Crash,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    // A record for a producer to hand to the journal; the journal assigns its sequence and time.
    public static JournalEntry Draft(JournalKind kind, JournalOutcome outcome, int? code = null, string? version = null,
        long? durationMs = null, IReadOnlyList<JournalStage>? stages = null, JournalException? exception = null) =>
        new(Schema, 0, default, FamilyOf(kind), kind, outcome, code, version, durationMs, stages ?? [], exception, JournalOmissions.None);

    // A release by its numbers only: major.minor[.patch[.revision]], optionally one of a closed
    // set of prerelease labels with numbers. No letters a person or a build could choose.
    [GeneratedRegex(@"^\d{1,9}(\.\d{1,9}){1,3}(-(alpha|beta|rc|preview|dev|ci)(\.\d{1,9}){0,3})?\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();

    public static bool IsVersion(string? s) => s is { Length: > 0 and <= MaxVersion } && VersionPattern().IsMatch(s);

    // Kept only from the closed sets (JournalIdentifiers): a name that merely looks like the
    // product's or the runtime's is not one.
    static bool IsFrame(JournalFrame f) =>
        f.Type == External ? f.Method == External : JournalIdentifiers.IsFrame(f.Type, f.Method);

    static bool IsExceptionType(string type) => type == Unrecognized || JournalIdentifiers.IsExceptionType(type);

    // The record as it may be kept: every field inside its bounds and grammar, the rest dropped
    // and marked. The sequence and time are the journal's to set. Null for a kind or outcome
    // that is not one of the journal's: there is no closest one to keep it as.
    public static JournalEntry? Fit(JournalEntry e)
    {
        if (!Enum.IsDefined(e.Kind) || !Enum.IsDefined(e.Outcome)) { return null; }
        var omitted = e.Omitted & AllOmissions;
        var code = e.Code;
        if (code is < 0 or > MaxCode) { code = null; omitted |= JournalOmissions.CodeOutOfRange; }
        var version = e.Version;
        if (version is not null && !IsVersion(version)) { version = null; omitted |= JournalOmissions.VersionInvalid; }
        var duration = e.DurationMs;
        if (duration is { } d && (d < 0 || d > MaxDurationMs)) { duration = Math.Clamp(d, 0, MaxDurationMs); omitted |= JournalOmissions.DurationClamped; }
        var stages = new List<JournalStage>();
        foreach (var s in e.Stages ?? [])
        {
            if (s is null || !Enum.IsDefined(s.Name)) { omitted |= JournalOmissions.StagesTruncated; continue; }
            if (stages.Count == MaxStages) { omitted |= JournalOmissions.StagesTruncated; break; }
            var ms = s.Ms;
            if (ms < 0 || ms > MaxDurationMs) { ms = Math.Clamp(ms, 0, MaxDurationMs); omitted |= JournalOmissions.DurationClamped; }
            stages.Add(new JournalStage(s.Name, ms));
        }
        JournalException? exception = null;
        if (e.Exception is { } x)
        {
            var type = x.Type;
            // The sentinel a producer put in place of a name is kept, and marked as replaced too.
            if (type is null || !IsExceptionType(type) || type == Unrecognized) { type = Unrecognized; omitted |= JournalOmissions.ExceptionTypeUnrecognized; }
            var frames = new List<JournalFrame>();
            foreach (var f in x.Frames ?? [])
            {
                if (frames.Count == MaxFrames) { omitted |= JournalOmissions.FramesTruncated; break; }
                var kept = f is not null && IsFrame(f) ? new JournalFrame(f.Type, f.Method) : null;
                if (kept is null) { omitted |= JournalOmissions.FrameUnrecognized; kept = new JournalFrame(External, External); }
                // A run of frames outside the product reads as one.
                if (kept.Type == External && frames.Count > 0 && frames[^1].Type == External) { continue; }
                frames.Add(kept);
            }
            exception = new JournalException(type, frames);
        }
        return new JournalEntry(Schema, e.Seq, e.AtUtc, FamilyOf(e.Kind), e.Kind, e.Outcome, code, version, duration, stages, exception, omitted);
    }

    // Whether a record is exactly as Fit would keep it: what a stored record must be to be read.
    public static bool IsFit(JournalEntry e)
    {
        if (e.Schema != Schema || e.Seq < 1 || e.AtUtc.Offset != TimeSpan.Zero) { return false; }
        if (!Enum.IsDefined(e.Kind) || !Enum.IsDefined(e.Outcome) || FamilyOf(e.Kind) != e.Family) { return false; }
        if ((e.Omitted & ~AllOmissions) != 0) { return false; }
        if (e.Code is < 0 or > MaxCode) { return false; }
        if (e.Version is not null && !IsVersion(e.Version)) { return false; }
        if (e.DurationMs is < 0 or > MaxDurationMs) { return false; }
        if (e.Stages is null || e.Stages.Count > MaxStages) { return false; }
        foreach (var s in e.Stages) { if (s is null || !Enum.IsDefined(s.Name) || s.Ms is < 0 or > MaxDurationMs) { return false; } }
        if (e.Exception is { } x)
        {
            if (x.Type is null || !IsExceptionType(x.Type)) { return false; }
            if (x.Frames is null || x.Frames.Count > MaxFrames) { return false; }
            foreach (var f in x.Frames) { if (f is null || !IsFrame(f)) { return false; } }
        }
        return true;
    }

    // The record with every exception type and frame this process does not list replaced, as
    // Fit would at capture, and marked; nothing else changed. For a record another process wrote
    // (another machine's host, another version of the product), whose trusted assemblies differ:
    // a frame of the host's own executable, or a method renamed since, is <external> here rather
    // than the whole record being lost, and never shown by its own name. Every other field is
    // left as it is, for IsFit to judge.
    internal static JournalEntry WithKnownNames(JournalEntry e)
    {
        if (e.Exception is not { Type: not null, Frames: not null } x) { return e; }
        var omitted = e.Omitted;
        var type = x.Type;
        if (!IsExceptionType(type) || type == Unrecognized) { type = Unrecognized; omitted |= JournalOmissions.ExceptionTypeUnrecognized; }
        var frames = new List<JournalFrame>(x.Frames.Count);
        foreach (var f in x.Frames)
        {
            if (f is null) { return e; }
            var kept = f;
            if (!IsFrame(f)) { omitted |= JournalOmissions.FrameUnrecognized; kept = new JournalFrame(External, External); }
            if (kept.Type == External && frames.Count > 0 && frames[^1].Type == External) { continue; }
            frames.Add(kept);
        }
        return e with { Exception = new JournalException(type, frames), Omitted = omitted };
    }

    const JournalOmissions AllOmissions = JournalOmissions.VersionInvalid | JournalOmissions.ExceptionTypeUnrecognized
        | JournalOmissions.FramesTruncated | JournalOmissions.StagesTruncated | JournalOmissions.CodeOutOfRange
        | JournalOmissions.DurationClamped | JournalOmissions.FrameUnrecognized;
}

// The closed sets of names a record may hold. An exception type is one of the runtime's listed
// here by type, or an exception type declared in a trusted assembly; a frame is a type and method
// declared in a trusted assembly. Trust is by the Assembly itself, handed over by code that
// holds it (JournalIdentifiers.Trust), never by a name or a namespace: a type anyone calls
// System.Something, in an assembly anyone calls AgentBridge.Something, is not trusted. The
// runtime's own frames are not named at all (they read as <external>): its method names are
// not listed, and the frames that matter are the product's. Captured by identity (TypeName,
// FrameOf), checked as a set of strings (IsExceptionType, IsFrame), so a record built or read
// directly with any other name is refused.
public static partial class JournalIdentifiers
{
    static readonly Type[] RuntimeExceptions =
    [
        typeof(Exception), typeof(SystemException), typeof(ApplicationException), typeof(InvalidOperationException),
        typeof(ArgumentException), typeof(ArgumentNullException), typeof(ArgumentOutOfRangeException),
        typeof(NotSupportedException), typeof(NotImplementedException), typeof(PlatformNotSupportedException),
        typeof(NullReferenceException), typeof(IndexOutOfRangeException), typeof(KeyNotFoundException),
        typeof(FormatException), typeof(OverflowException), typeof(ArithmeticException), typeof(DivideByZeroException),
        typeof(InvalidCastException), typeof(ObjectDisposedException), typeof(OperationCanceledException),
        typeof(TaskCanceledException), typeof(TimeoutException), typeof(OutOfMemoryException),
        typeof(InsufficientExecutionStackException), typeof(TypeInitializationException), typeof(TargetInvocationException),
        typeof(AggregateException), typeof(IOException), typeof(FileNotFoundException), typeof(DirectoryNotFoundException),
        typeof(PathTooLongException), typeof(EndOfStreamException), typeof(InvalidDataException),
        typeof(UnauthorizedAccessException), typeof(JsonException), typeof(DecoderFallbackException),
        typeof(EncoderFallbackException), typeof(RegexMatchTimeoutException), typeof(UriFormatException),
        typeof(HttpRequestException), typeof(WebSocketException), typeof(SocketException), typeof(Win32Exception),
        typeof(ExternalException), typeof(COMException), typeof(SEHException), typeof(CryptographicException),
        typeof(AuthenticationException), typeof(ThreadInterruptedException), typeof(SemaphoreFullException),
        typeof(SynchronizationLockException), typeof(LockRecursionException), typeof(AbandonedMutexException),
        typeof(DllNotFoundException), typeof(EntryPointNotFoundException), typeof(BadImageFormatException),
        typeof(MissingMethodException), typeof(ChannelClosedException),
    ];

    const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

    static readonly Lock Gate = new();
    static readonly HashSet<Assembly> Trusted = [];
    static volatile FrozenSet<string> _exceptionTypes = FrozenSet<string>.Empty;
    static volatile FrozenSet<(string Type, string Method)> _frames = FrozenSet<(string, string)>.Empty;

    static JournalIdentifiers()
    {
        _exceptionTypes = RuntimeExceptions.Select(t => t.FullName!).ToFrozenSet(StringComparer.Ordinal);
        Trust(typeof(JournalIdentifiers).Assembly);
    }

    // Lists the exception types, types and methods the assembly declares, by its metadata. For the
    // product's own assemblies only, from code that references them.
    public static void Trust(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        lock (Gate)
        {
            if (!Trusted.Add(assembly)) { return; }
            var exceptionTypes = new HashSet<string>(_exceptionTypes, StringComparer.Ordinal);
            var frames = new HashSet<(string, string)>(_frames);
            foreach (var t in Loadable(assembly))
            {
                if (Canonical(t) is not { } name) { continue; }
                if (typeof(Exception).IsAssignableFrom(t)) { exceptionTypes.Add(name); }
                foreach (var m in t.GetMethods(Declared).Cast<MethodBase>().Concat(t.GetConstructors(Declared)))
                {
                    if (IsMethodName(m.Name)) { frames.Add((name, m.Name)); }
                }
            }
            _exceptionTypes = exceptionTypes.ToFrozenSet(StringComparer.Ordinal);
            _frames = frames.ToFrozenSet();
        }
    }

    public static bool IsExceptionType(string type) => _exceptionTypes.Contains(type);

    public static bool IsFrame(string type, string method) => _frames.Contains((type, method));

    // The exception type's name if it is one the journal may keep: listed above, or an exception
    // type of a trusted assembly. By the type itself, not by its name.
    public static string? TypeName(Type t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (Array.IndexOf(RuntimeExceptions, t) >= 0) { return t.FullName; }
        bool trusted;
        lock (Gate) { trusted = Trusted.Contains(t.Assembly); }
        return trusted && typeof(Exception).IsAssignableFrom(t) && Canonical(t) is { } name && IsExceptionType(name) ? name : null;
    }

    // A frame's method as its declaring type and name, if it was declared in a trusted assembly;
    // null for anything else, the runtime's own frames included.
    public static JournalFrame? FrameOf(MethodBase? method)
    {
        if (method?.DeclaringType is not { } t) { return null; }
        bool trusted;
        lock (Gate) { trusted = Trusted.Contains(t.Assembly); }
        return trusted && Canonical(t) is { } name && IsFrame(name, method.Name) ? new JournalFrame(name, method.Name) : null;
    }

    // The type's full name without generic arguments or arity, nested types dotted; null for a
    // name too long or outside the identifier grammar.
    static string? Canonical(Type t)
    {
        if (t.IsGenericType && !t.IsGenericTypeDefinition) { t = t.GetGenericTypeDefinition(); }
        if (t.FullName is not { } full || full.Contains('[')) { return null; }
        var name = Arity().Replace(full, "").Replace('+', '.');
        return name.Length <= JournalContract.MaxIdentifier && TypePattern().IsMatch(name) ? name : null;
    }

    static bool IsMethodName(string name) => name.Length is > 0 and <= JournalContract.MaxIdentifier && MethodPattern().IsMatch(name);

    static IEnumerable<Type> Loadable(Assembly assembly)
    {
        try { return assembly.GetTypes(); }
        // The types that did load; the rest are not listed, so their frames read as external.
        catch (ReflectionTypeLoadException e) { return e.Types.OfType<Type>(); }
    }

    // Segments of code identifiers, with the compiler's <>, |, $ and _ (closures, state machines,
    // local functions): no spaces, slashes, colons, quotes or backslashes.
    [GeneratedRegex(@"^[A-Za-z0-9_<>|$]+(\.[A-Za-z0-9_<>|$]+)*\z", RegexOptions.CultureInvariant)]
    private static partial Regex TypePattern();

    [GeneratedRegex(@"^[A-Za-z0-9_<>|$.]+\z", RegexOptions.CultureInvariant)]
    private static partial Regex MethodPattern();

    [GeneratedRegex(@"`\d+", RegexOptions.CultureInvariant)]
    private static partial Regex Arity();
}

// The journal's one serialized form, a JSON object per record (a line of the journal's file).
// Written field by field, never by reflection over the record, and read strictly: an unknown or
// repeated property, an enum by number or by an unknown name, a value of the wrong kind or out
// of bounds, and the whole record is refused. There is no fallback record.
public static class JournalCodec
{
    static readonly JsonWriterOptions WriterOptions = new() { Indented = false, SkipValidation = false };

    public static byte[] Write(JournalEntry e)
    {
        if (!JournalContract.IsFit(e)) { throw new ArgumentException("the record is not fit to keep (JournalContract.Fit)", nameof(e)); }
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var w = new Utf8JsonWriter(buffer, WriterOptions))
        {
            w.WriteStartObject();
            w.WriteNumber("schema", e.Schema);
            w.WriteNumber("seq", e.Seq);
            w.WriteString("at", e.AtUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            w.WriteString("family", e.Family.ToString());
            w.WriteString("kind", e.Kind.ToString());
            w.WriteString("outcome", e.Outcome.ToString());
            if (e.Code is { } code) { w.WriteNumber("code", code); }
            if (e.Version is { } version) { w.WriteString("version", version); }
            if (e.DurationMs is { } ms) { w.WriteNumber("ms", ms); }
            if (e.Stages.Count > 0)
            {
                w.WriteStartArray("stages");
                foreach (var s in e.Stages)
                {
                    w.WriteStartObject();
                    w.WriteString("name", s.Name.ToString());
                    w.WriteNumber("ms", s.Ms);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            if (e.Exception is { } x)
            {
                w.WriteStartObject("exception");
                w.WriteString("type", x.Type);
                w.WriteStartArray("frames");
                foreach (var f in x.Frames)
                {
                    w.WriteStartObject();
                    w.WriteString("type", f.Type);
                    w.WriteString("method", f.Method);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                w.WriteEndObject();
            }
            if (e.Omitted != JournalOmissions.None) { w.WriteNumber("omitted", (int)e.Omitted); }
            w.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    public static bool TryRead(ReadOnlySpan<byte> utf8, [NotNullWhen(true)] out JournalEntry? entry) => TryRead(utf8, false, out entry);

    // A record written by another process (diagnostics.get's reply, read by the Windows app): as
    // strict as TryRead, except that an exception type or frame this process does not list reads
    // as <unrecognized> or <external>, marked, instead of refusing the record
    // (JournalContract.WithKnownNames). An invalid version, code, stage, omission or anything
    // else still refuses it.
    public static bool TryReadFromAnotherProcess(ReadOnlySpan<byte> utf8, [NotNullWhen(true)] out JournalEntry? entry) => TryRead(utf8, true, out entry);

    static bool TryRead(ReadOnlySpan<byte> utf8, bool foreignNames, [NotNullWhen(true)] out JournalEntry? entry)
    {
        entry = null;
        if (utf8.Length == 0 || utf8.Length > JournalContract.MaxEntryBytes) { return false; }
        try
        {
            using var doc = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { MaxDepth = 8, AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            var read = Entry(doc.RootElement);
            if (read is not null && foreignNames) { read = JournalContract.WithKnownNames(read); }
            if (read is null || !JournalContract.IsFit(read)) { return false; }
            entry = read;
            return true;
        }
        catch (JsonException) { return false; }
        // A string escape that is no UTF-16 text (an unpaired surrogate, "\uD800") parses as a
        // document but cannot be decoded: unreadable like any other malformed record, never thrown
        // past a reader, where it would stop a host loading its journal at start.
        catch (InvalidOperationException) { return false; }
    }

    static readonly string[] EntryFields = ["schema", "seq", "at", "family", "kind", "outcome", "code", "version", "ms", "stages", "exception", "omitted"];
    static readonly string[] Required = ["schema", "seq", "at", "family", "kind", "outcome"];

    static JournalEntry? Entry(JsonElement o)
    {
        if (Properties(o, EntryFields) is not { } p || Required.Any(r => !p.ContainsKey(r))) { return null; }
        if (Int(p["schema"]) is not { } schema || Long(p["seq"]) is not { } seq || Time(p["at"]) is not { } at) { return null; }
        if (Name<JournalFamily>(p["family"]) is not { } family || Name<JournalKind>(p["kind"]) is not { } kind || Name<JournalOutcome>(p["outcome"]) is not { } outcome) { return null; }
        int? code = null;
        if (p.TryGetValue("code", out var c)) { if (Int(c) is not { } v) { return null; } code = v; }
        string? version = null;
        if (p.TryGetValue("version", out var ver)) { if (ver.ValueKind != JsonValueKind.String) { return null; } version = ver.GetString(); }
        long? ms = null;
        if (p.TryGetValue("ms", out var m)) { if (Long(m) is not { } v) { return null; } ms = v; }
        var stages = new List<JournalStage>();
        if (p.TryGetValue("stages", out var st))
        {
            if (st.ValueKind != JsonValueKind.Array || st.GetArrayLength() is 0 or > JournalContract.MaxStages) { return null; }
            foreach (var s in st.EnumerateArray())
            {
                if (Properties(s, ["name", "ms"]) is not { Count: 2 } sp || Name<JournalStageName>(sp["name"]) is not { } name || Long(sp["ms"]) is not { } sms) { return null; }
                stages.Add(new JournalStage(name, sms));
            }
        }
        JournalException? exception = null;
        if (p.TryGetValue("exception", out var ex))
        {
            if (Properties(ex, ["type", "frames"]) is not { Count: 2 } xp || xp["type"].ValueKind != JsonValueKind.String || xp["frames"].ValueKind != JsonValueKind.Array) { return null; }
            if (xp["frames"].GetArrayLength() > JournalContract.MaxFrames) { return null; }
            var frames = new List<JournalFrame>();
            foreach (var f in xp["frames"].EnumerateArray())
            {
                if (Properties(f, ["type", "method"]) is not { Count: 2 } fp || fp["type"].ValueKind != JsonValueKind.String || fp["method"].ValueKind != JsonValueKind.String) { return null; }
                frames.Add(new JournalFrame(fp["type"].GetString()!, fp["method"].GetString()!));
            }
            exception = new JournalException(xp["type"].GetString()!, frames);
        }
        var omitted = JournalOmissions.None;
        if (p.TryGetValue("omitted", out var om))
        {
            // Written only when something was omitted.
            if (Int(om) is not { } bits || bits == 0) { return null; }
            omitted = (JournalOmissions)bits;
        }
        return new JournalEntry(schema, seq, at, family, kind, outcome, code, version, ms, stages, exception, omitted);
    }

    // The object's properties, if it has only known ones, each once.
    static Dictionary<string, JsonElement>? Properties(JsonElement o, string[] known)
    {
        if (o.ValueKind != JsonValueKind.Object) { return null; }
        var found = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var p in o.EnumerateObject())
        {
            if (Array.IndexOf(known, p.Name) < 0 || !found.TryAdd(p.Name, p.Value)) { return null; }
        }
        return found;
    }

    static int? Int(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v) ? v : null;

    static long? Long(JsonElement e) => e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v) ? v : null;

    static DateTimeOffset? Time(JsonElement e) =>
        e.ValueKind == JsonValueKind.String
        && DateTimeOffset.TryParseExact(e.GetString(), "yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at)
            ? at : null;

    // By exact name only: never a number, nor a name in another case, nor a combination.
    static T? Name<T>(JsonElement e) where T : struct, Enum
    {
        if (e.ValueKind != JsonValueKind.String || e.GetString() is not { } s) { return null; }
        foreach (var value in Enum.GetValues<T>())
        {
            if (string.Equals(value.ToString(), s, StringComparison.Ordinal)) { return value; }
        }
        return null;
    }
}
