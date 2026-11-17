using System.Globalization;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentBridge.Host.Delivery.Staging;

namespace AgentBridge.Host;

// One agent action or person host-CLI management action (design/agent-principals.md §5.6). Direction is "out"
// (an agent here acted on another machine), "in" (an agent elsewhere acted here) or "local".
// Principal is "agent:<machine>/<session>" for agent rows, never the owner; person ingress uses
// "person:local" or "person:<device id>", and the private connector uses "ha-component:local".
// Reason says why in words and
// holds no prompt text, path or token: callers pass a short reason, and the log cuts it at
// MaxReason as well.
public sealed record AgentAuditEntry(string Direction, string Request, string Principal, string[] Chain, string Action,
    string Target, string Outcome, string Reason = "", GateAuditMetadata? Gate = null)
{
    public string? Grant { get; init; }
}

internal sealed record AuditLockTiming(Func<long> TickCount, Action Wait);

// Agent actions and person host-CLI management, one JSON line per event in <profile>\audit\agents-YYYY-MM-DD.jsonl
// or launches-YYYY-MM-DD.jsonl for session.launch, with a separate 16 MiB launch-history quota,
// in a folder only this user can open. It keeps the SHA-256 and length of any text an event
// carried and never the text, so reading the log leaks nothing a prompt held. Agent files target
// 30 days and 64 MiB, oldest files first, never deleting the day's file being written.
// Retention skips coverage-break or unverified roots/chains with a host-log warning,
// but not audit-incomplete alone; blocked pruning can exceed the target.
//
// An unwritable log refuses the action (the #1401 row in design/agent-launch-scope.md): Append
// returns false and never throws, and the caller must then refuse the action it was auditing
// and release any slot. A failure (read-only folder, disk full) is said once in the host log and
// the next success says it recovered. agent.send appends before dispatch (#1985).
public sealed class AgentAuditLog
{
    public const string FolderName = "audit";
    public const int KeepDays = 30;
    public const long MaxBytes = 64L * 1024 * 1024;
    public const long MaxLaunchBytes = 16L * 1024 * 1024;
    public const int MaxReason = 200;
    public const int MaxField = 256;
    public const int MaxChain = 16;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    sealed record Line(DateTimeOffset At, string Direction, string Request, string Principal, string[] Chain, string Action,
        string Target, string Outcome, string Reason, string? DataSha256, int? TextLength, int V = 1, string PreviousSha256 = "",
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] GateAuditMetadata? Gate = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool PreviousTailIncomplete = false,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Grant = null);

    static readonly string Genesis = new('0', 64);
    readonly object gate = new();
    readonly string profile;
    internal string Profile => profile;
    GateRunContext? controlWriterRun;
    internal GateRunContext? ControlWriterRun
    {
        get => Volatile.Read(ref controlWriterRun);
        set => Volatile.Write(ref controlWriterRun, value);
    }
    readonly TimeProvider time;
    readonly Action<string> log;
    readonly long maxBytes;
    internal AuditLockTiming? LockTiming { get; init; }
    bool failing;
    bool retentionFailing;

    public AgentAuditLog(string profileFolder, TimeProvider? time = null, Action<string>? log = null, long maxBytes = MaxBytes)
    {
        profile = profileFolder;
        this.time = time ?? TimeProvider.System;
        this.log = log ?? (_ => { });
        this.maxBytes = maxBytes;
    }

    // text is only measured and hashed, never kept. Returns whether the line was written.
    public bool Append(AgentAuditEntry entry, string? text = null)
    {
        var now = time.GetUtcNow();
        try
        {
            var bytes = text is null || entry.Gate is not null ? null : Encoding.UTF8.GetBytes(text);
            var chain = (entry.Chain ?? []).Take(MaxChain).Select(c => Cap(c, MaxField)).ToArray();
            var line = new Line(now, Cap(entry.Direction, MaxField), Cap(entry.Request, MaxField), Cap(entry.Principal, MaxField), chain,
                Cap(entry.Action, MaxField), Cap(entry.Target, MaxField), Cap(entry.Outcome, MaxField), Cap(entry.Reason, MaxReason),
                bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes?.Length, Grant: entry.Grant);
            lock (gate)
            {
                var folder = PrivateFolder.Ensure(Path.Combine(profile, FolderName), "the agent audit folder", "agent actions are not recorded");
                using var folderLock = Lock(folder, timing: LockTiming);
                // A first notice is evidence too, not a legacy prefix for the first answer to adopt.
                var evidence = entry.Gate is not null || entry.Action == "push";
                if (evidence) { Bootstrap(folder); }
                if (entry.Gate is { } metadata)
                {
                    metadata = metadata with { Sequence = checked(new[] { LastGateSequence(folder, false), LastGateSequence(folder, true),
                        ReadHead(folder, false)?.Sequence ?? 0, ReadHead(folder, true)?.Sequence ?? 0 }.Max() + 1) };
                    if (!metadata.Valid(entry.Action, entry.Target, entry.Outcome)) { throw new IOException("invalid gate audit metadata"); }
                    if (entry.Action == "session.launch" && metadata is { Phase: "intent", LaunchBinding: not null })
                    {
                        var prior = GateAuditProjection.Load(profile, alreadyLocked: true);
                        if (!prior.Complete || prior.Rows.Any(r => r.Action == entry.Action && r.Request == entry.Request))
                        { throw new IOException("the launch request is already audited or its evidence is incomplete"); }
                    }
                    line = line with { Gate = metadata, Reason = metadata.ReasonCode, DataSha256 = null, TextLength = null };
                }
                var launch = entry.Action == "session.launch";
                var anchored = evidence || HasHeads(folder);
                var expected = ReadHead(folder, launch);
                string[] faults = anchored ? new[] { Continuity(profile, false, log), Continuity(profile, true, log) } : [];
                var fault = faults.Contains("coverage-break") ? "coverage-break" : faults.FirstOrDefault(f => f != "none") ?? "none";
                var file = Path.Combine(folder, FileFor(now, launch));
                if (!File.Exists(file) && Path.GetFileName(file) == expected?.Tail) { throw new IOException("missing audit evidence must not be recreated"); }
                if (Files(folder, launch).Any(f => StringComparer.Ordinal.Compare(f, file) > 0)) { throw new IOException("the agent audit clock moved backwards"); }
                var length = File.Exists(file) ? new FileInfo(file).Length : 0;
                // Only this file receives a separator; predecessor tails still require LF.
                var tail = TailHash(length > 0 ? file : null, promoteTail: true);
                var previousFile = tail.HasLine ? null : Files(folder, launch).LastOrDefault(f => StringComparer.Ordinal.Compare(f, file) < 0);
                var predecessor = TailHash(previousFile);
                if (previousFile is not null && !predecessor.HasLine) { throw new IOException("the previous agent audit day has no complete line"); }
                var previous = tail.HasLine ? tail.Hash : predecessor.Hash;
                var torn = length > 0 && EndsWithoutNewline(file);
                line = line with { PreviousTailIncomplete = torn };
                var header = tail.HasLine ? "" : JsonSerializer.Serialize(new { v = 1, previousSha256 = previous }, Json) + "\n";
                line = line with { PreviousSha256 = header.Length == 0 ? previous : Hash(Encoding.UTF8.GetBytes(header[..^1])) };
                var data = Encoding.UTF8.GetBytes(header + JsonSerializer.Serialize(line, Json) + "\n");
                // Include a separator after a torn line, and refuse before creating or changing any file.
                var used = launch ? new DirectoryInfo(folder).EnumerateFiles("launches-*.jsonl").Sum(f => f.Length) : length;
                var limit = launch ? MaxLaunchBytes : maxBytes;
                if (used > limit - data.Length - (torn ? 1 : 0)) { throw new IOException("the agent audit quota is full"); }
                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
                if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                using (var stream = new FileStream(file, options))
                {
                    // A crash mid-write can leave a partial line: start on a fresh one so it can't swallow this event.
                    if (torn) { stream.Write("\n"u8); }
                    stream.Write(data);
                    stream.Flush(flushToDisk: true);
                }
                if (OperatingSystem.IsWindows()) { NarrowFileWindows(file); }
                else { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                if (anchored) { WriteHead(folder, launch, expected, line.Gate?.Sequence ?? expected?.Sequence ?? 0, fault); }
                if (failing) { failing = false; log("agent audit log is writing again"); }
                // The line is already written: a retention fault is its own problem, not a lost event.
                try { if (!launch) { Retain(folder, now); retentionFailing = false; } }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    if (!retentionFailing) { retentionFailing = true; log($"agent audit log could not trim old files: {e.GetType().Name}"); }
                }
            }
            return true;
        }
        // Nothing is thrown to the caller, who must refuse the action when this returns false (design/agent-launch-scope.md, #1401).
        catch (Exception e)
        {
            lock (gate)
            {
                if (!failing) { failing = true; log($"agent audit log could not write, so agent actions are not recorded until it can: {e.GetType().Name}"); }
            }
            return false;
        }
    }

    public (bool Intact, bool LegacyPrefix) Verify(bool launch = false)
    {
        lock (gate) { return VerifyProfile(profile, launch, log, synchronize: true); }
    }

    internal bool Bootstrap()
    {
        try
        {
            lock (gate)
            {
                var folder = PrivateFolder.Ensure(Path.Combine(profile, FolderName), "the agent audit folder", "agent actions are not recorded");
                using var held = Lock(folder);
                Bootstrap(folder);
            }
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { log($"agent audit bootstrap refused: {e.GetType().Name}"); return false; }
    }

    void Bootstrap(string folder)
    {
        if (HasHeads(folder)) { return; }
        var streams = new[] { false, true }.Where(launch => Files(folder, launch).Any()).ToArray();
        var candidates = new List<(bool Launch, string Fault)>();
        foreach (var launch in streams)
        {
            var verified = VerifyProfile(profile, launch, log, chainOnly: true);
            if (!verified.Intact) { throw new IOException("unanchored audit chain is broken"); }
            var fault = Files(folder, launch).Any(EndsWithoutNewline) ? "audit-incomplete" : "none";
            foreach (var raw in Files(folder, launch).SelectMany(File.ReadLines))
            {
                try
                {
                    using var doc = JsonDocument.Parse(raw);
                    if (doc.RootElement.TryGetProperty("previousTailIncomplete", out var incomplete) && incomplete.GetBoolean())
                    { fault = "audit-incomplete"; }
                }
                catch (JsonException) { fault = "audit-incomplete"; }
            }
            candidates.Add((launch, fault));
        }
        foreach (var (launch, fault) in candidates) { WriteHead(folder, launch, null, LastGateSequence(folder, launch), fault, adopted: true); }
    }

    static (bool Intact, bool LegacyPrefix) VerifyProfile(string profile, bool launch, Action<string> log,
        bool synchronize = false, bool chainOnly = false)
    {
        var legacy = false;
        try
        {
            var folder = synchronize ? PrivateFolder.Ensure(Path.Combine(profile, FolderName), "the agent audit folder", "agent actions are not recorded")
                : Path.Combine(profile, FolderName);
            using (var folderLock = synchronize ? Lock(folder) : null)
            {
                var gated = false;
                string? previous = null;
                var chained = false;
                foreach (var file in Files(folder, launch))
                {
                    var index = 0;
                    var fragment = false;
                    foreach (var raw in File.ReadLines(file).SkipLast(new FileInfo(file).Length > 0 && EndsWithoutNewline(file) ? 1 : 0))
                    {
                        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(raw), isFinalBlock: false, state: default);
                        using var doc = JsonDocument.TryParseValue(ref reader, out var parsed) ? parsed : null;
                        // Only the next valid link can prove this fragment was bypassed during crash recovery.
                        if (doc is null) { if (fragment) { return (false, legacy); } fragment = true; continue; }
                        if (reader.Read() || reader.BytesConsumed != Encoding.UTF8.GetByteCount(raw)) { return (false, legacy); }
                        var root = doc.RootElement;
                        if (root.TryGetProperty("gate", out var metadata) && metadata.ValueKind != JsonValueKind.Null) { gated = true; }
                        if (!root.TryGetProperty("v", out var version))
                        {
                            if (chained || fragment) { return (false, legacy); }
                            legacy = true;
                        }
                        else
                        {
                            if (!version.TryGetInt32(out var v) || v != 1 || root.TryGetProperty("action", out _) == (index == 0)
                                || !root.TryGetProperty("previousSha256", out var link) || link.ValueKind != JsonValueKind.String
                                || link.GetString() is not { Length: 64 } hash || hash.Any(c => !char.IsAsciiHexDigitLower(c))
                                || previous is not null && previous != hash) { return (false, legacy); }
                            chained = true;
                        }
                        fragment = false;
                        previous = Hash(Encoding.UTF8.GetBytes(raw));
                        index++;
                    }
                    if (index == 0 || fragment) { return (false, legacy); }
                }
                return ((chainOnly || !gated || ReadHead(folder, launch) is { Sequence: > 0 }) && (chainOnly || HeadFault(folder, launch) == "none"), legacy);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { log($"agent audit chain could not be verified: {e.GetType().Name}"); return (false, legacy); }
    }

    sealed record Head([property: JsonRequired] int V,
        [property: JsonRequired] string Root, [property: JsonRequired] string RootSha256,
        [property: JsonRequired] string Tail, [property: JsonRequired] string TailSha256,
        [property: JsonRequired] long Length, [property: JsonRequired] long Sequence = 0,
        [property: JsonRequired] string Fault = "none", long RetainedThrough = 0, string? RetiringRoot = null, bool Adopted = false);
    static string HeadFile(string folder, bool launch) => Path.Combine(folder, launch ? "launches.head" : "agents.head");
    internal static bool HasHeads(string folder) => File.Exists(HeadFile(folder, false)) || File.Exists(HeadFile(folder, true));
    static Head? ReadHead(string folder, bool launch)
    {
        try { return JsonSerializer.Deserialize<Head>(File.ReadAllText(HeadFile(folder, launch)), Json) is
                { V: 1, Root: not null, RootSha256: not null, Tail: not null, TailSha256: not null,
                    Length: > 0, Sequence: >= 0, RetainedThrough: >= 0, Fault: "none" or "audit-incomplete" or "coverage-break" } head
                && head.RetainedThrough <= head.Sequence && (head.RetiringRoot is null
                    || !launch && TryDay(head.RetiringRoot, out _) && StringComparer.Ordinal.Compare(head.RetiringRoot, head.Root) < 0)
                ? head : throw new IOException("invalid audit head"); }
        catch (FileNotFoundException) { return null; }
    }
    internal static long RetainedThrough(string folder) => ReadHead(folder, false)?.RetainedThrough ?? 0;
    internal static bool Adopted(string folder) => ReadHead(folder, false)?.Adopted == true || ReadHead(folder, true)?.Adopted == true;
    static Head CaptureHead(string folder, bool launch, string? retiringRoot = null)
    {
        var files = Files(folder, launch).Where(f => Path.GetFileName(f) != retiringRoot).ToArray();
        return new(1, Path.GetFileName(files.First()), Hash(Encoding.UTF8.GetBytes(File.ReadLines(files.First()).First())),
            Path.GetFileName(files.Last()), TailHash(files.Last(), promoteTail: true).Hash, new FileInfo(files.Last()).Length);
    }
    static string HeadFault(string folder, bool launch)
    {
        var expected = ReadHead(folder, launch);
        if (expected is null) { return "none"; }
        if (expected.Fault == "coverage-break" || !File.Exists(Path.Combine(folder, expected.Root))
            || !File.Exists(Path.Combine(folder, expected.Tail)) || !Files(folder, launch).Any()
            || Files(folder, launch).Any(f => new FileInfo(f).Length == 0)) { return "coverage-break"; }
        var actual = CaptureHead(folder, launch, expected.RetiringRoot);
        if (actual.Root != expected.Root || actual.RootSha256 != expected.RootSha256) { return "coverage-break"; }
        var oldTail = Path.Combine(folder, expected.Tail);
        if ((actual.Tail != expected.Tail || actual.Length > expected.Length)
            && File.Exists(oldTail) && PrefixIntact(oldTail, expected)) { return "audit-incomplete"; }
        if (actual.Tail != expected.Tail) { return "coverage-break"; }
        if (actual.TailSha256 != expected.TailSha256) { return "coverage-break"; }
        if (actual.Length == expected.Length) { return expected.Fault; }
        // Losing only LF does not prove that the complete JSON record's contents vanished.
        return actual.Length > expected.Length || actual.Length == expected.Length - 1
            && EndsWithoutNewline(Path.Combine(folder, actual.Tail)) ? "audit-incomplete" : "coverage-break";
    }
    static bool PrefixIntact(string file, Head expected)
    {
        using var stream = File.OpenRead(file);
        if (stream.Length < expected.Length) { return false; }
        var end = expected.Length - 1;
        stream.Position = end;
        if (stream.ReadByte() != '\n') { end++; }
        var start = PreviousNewline(stream, end) + 1;
        var bytes = new byte[checked((int)(end - start))];
        stream.Position = start;
        stream.ReadExactly(bytes);
        return Hash(bytes) == expected.TailSha256;
    }
    internal static string Continuity(string profile, bool launch, Action<string> log)
    {
        try
        {
            var folder = Path.Combine(profile, FolderName);
            var fault = HeadFault(folder, launch);
            if (ReadHead(folder, launch) is null && Files(folder, launch).SelectMany(File.ReadLines).Any(raw =>
                JsonSerializer.Deserialize<JsonElement>(raw, Json).TryGetProperty("gate", out var gate) && gate.ValueKind != JsonValueKind.Null))
            { return "audit-incomplete"; }
            return VerifyProfile(profile, launch, log, chainOnly: true).Intact ? fault
                : ReadHead(folder, launch) is null ? "audit-incomplete" : "coverage-break";
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { log($"agent audit continuity unavailable: {e.GetType().Name}"); return "audit-incomplete"; }
    }
    static void WriteHead(string folder, bool launch, Head? previous, long sequence, string fault, bool adopted = false)
    {
        var head = CaptureHead(folder, launch);
        // Advancing the head must not bless a suffix after its original root was lost.
        head = head with { Root = previous?.Root ?? head.Root, RootSha256 = previous?.RootSha256 ?? head.RootSha256,
            Sequence = sequence, Fault = fault, RetainedThrough = previous?.RetainedThrough ?? 0,
            RetiringRoot = previous?.RetiringRoot, Adopted = previous?.Adopted ?? adopted };
        SaveHead(folder, launch, head);
    }
    static void SaveHead(string folder, bool launch, Head head)
    {
        var file = HeadFile(folder, launch);
        File.Delete(file + ".new");
        using (var stream = PrivateFolder.CreateNew(file + ".new"))
        { stream.Write(JsonSerializer.SerializeToUtf8Bytes(head, Json)); stream.Flush(flushToDisk: true); }
        File.Move(file + ".new", file, overwrite: true);
    }

    internal static FileStream Lock(string folder, bool read = false, AuditLockTiming? timing = null)
    {
        var options = new FileStreamOptions { Mode = read ? FileMode.Open : FileMode.OpenOrCreate,
            Access = read ? FileAccess.Read : FileAccess.ReadWrite, Share = read ? FileShare.Read : FileShare.None };
        if (!read && !OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        var deadline = (timing?.TickCount() ?? Environment.TickCount64) + 2000;
        while (true)
        {
            try { return new FileStream(folder + ".lock", options); }
            catch (IOException e) when ((OperatingSystem.IsWindows() ? (e.HResult & 0xffff) is 32 or 33 : e.HResult is 11 or 35)
                && (timing?.TickCount() ?? Environment.TickCount64) < deadline)
            {
                if (timing is null) { Thread.Sleep(20); }
                else { timing.Wait(); }
            }
        }
    }

    static IEnumerable<string> Files(string folder, bool launch) =>
        Directory.EnumerateFiles(folder, $"{(launch ? "launches" : "agents")}-*.jsonl")
            .Select(f => TryDay(Path.GetFileName(f).Replace("launches-", "agents-", StringComparison.Ordinal), out _)
                ? f : throw new IOException("the agent audit folder contains an invalid day name")).Order(StringComparer.Ordinal);

    long LastGateSequence(string folder, bool launch, string? onlyFile = null)
    {
        var warned = false;
        IEnumerable<string> selected = onlyFile is { } path ? new[] { path } : Files(folder, launch).Reverse();
        foreach (var file in selected)
        {
            using var stream = File.OpenRead(file);
            var end = stream.Length;
            while (end > 0)
            {
                var start = PreviousNewline(stream, end - 1) + 1;
                var bytes = new byte[checked((int)(end - start))];
                stream.Position = start;
                stream.ReadExactly(bytes);
                end = start;
                try
                {
                    using var doc = JsonDocument.Parse(bytes);
                    if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("gate", out var gate)
                        && gate.ValueKind == JsonValueKind.Object && gate.TryGetProperty("sequence", out var sequence)
                        && sequence.ValueKind == JsonValueKind.Number && sequence.TryGetInt64(out var value) && value > 0) { return value; }
                }
                catch (JsonException)
                {
                    if (!warned) { warned = true; log("gate audit sequence scan skipped a malformed record; evidence remains incomplete"); }
                }
            }
        }
        return 0;
    }
    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static bool CompleteObject(byte[] bytes)
    {
        var reader = new Utf8JsonReader(bytes, isFinalBlock: false, state: default);
        using var doc = JsonDocument.TryParseValue(ref reader, out var parsed) ? parsed : null;
        return doc is not null && doc.RootElement.ValueKind == JsonValueKind.Object
            && !reader.Read() && reader.BytesConsumed == bytes.Length;
    }

    static (string Hash, bool HasLine) TailHash(string? file, bool promoteTail = false)
    {
        if (file is null) { return (Genesis, false); }
        using var stream = File.OpenRead(file);
        var end = PreviousNewline(stream, stream.Length);
        stream.Position = end + 1;
        if (promoteTail && (stream.ReadByte() is '{' or ' ' or '\t' or '\r'))
        {
            var tail = new byte[checked((int)(stream.Length - end - 1))];
            stream.Position = end + 1;
            stream.ReadExactly(tail);
            if (CompleteObject(tail)) { return (Hash(tail), true); }
        }
        if (end < 0) { return (Genesis, false); }
        var start = PreviousNewline(stream, end) + 1;
        var bytes = new byte[checked((int)(end - start))];
        stream.Position = start;
        stream.ReadExactly(bytes);
        if (!CompleteObject(bytes)) { throw new IOException("the agent audit day ends with an incomplete terminated line"); }
        return (Hash(bytes), true);
    }

    static long PreviousNewline(FileStream stream, long position)
    {
        Span<byte> buffer = stackalloc byte[4096];
        while (position > 0)
        {
            var count = (int)Math.Min(buffer.Length, position);
            stream.Position = position -= count;
            stream.ReadExactly(buffer[..count]);
            var index = buffer[..count].LastIndexOf((byte)'\n');
            if (index >= 0) { return position + index; }
        }
        return -1;
    }

    // The folder's DACL is inherited by new files, but a file already there can carry a looser one.
    [SupportedOSPlatform("windows")]
    static void NarrowFileWindows(string file)
    {
        var me = WindowsIdentity.GetCurrent().User!;
        var info = new FileInfo(file);
        var current = info.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var rules = current.GetAccessRules(true, true, typeof(SecurityIdentifier));
        if (current.AreAccessRulesProtected && me.Equals(current.GetOwner(typeof(SecurityIdentifier))) && rules.Count == 1
            && rules.Cast<FileSystemAccessRule>().Single().IdentityReference.Equals(me)) { return; }
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(me, FileSystemRights.FullControl, AccessControlType.Allow));
        security.SetOwner(me);
        info.SetAccessControl(security);
    }

    static string Cap(string? value, int max) => value is null ? "" : value.Length > max ? value[..max] : value;

    static bool EndsWithoutNewline(string file)
    {
        using var peek = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        peek.Seek(-1, SeekOrigin.End);
        return peek.ReadByte() != '\n';
    }

    static string FileFor(DateTimeOffset day, bool launch) =>
        (launch ? "launches-" : "agents-") + day.UtcDateTime.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".jsonl";

    static bool TryDay(string name, out DateTime day)
    {
        day = default;
        return name.StartsWith("agents-", StringComparison.Ordinal) && name.EndsWith(".jsonl", StringComparison.Ordinal)
            && DateTime.TryParseExact(name[7..^6], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    // Files older than KeepDays go, then the oldest until the rest fit, and the one being written
    // is never a candidate. The age cutoff never runs ahead of the newest earlier file by more
    // than a day, so a clock that jumped forward can't age out the whole history. Deleting stops
    // at the first file that can't be deleted, so newer days are never lost while an older one stays.
    void Retain(string folder, DateTimeOffset now)
    {
        var expected = ReadHead(folder, false);
        if (expected is not null && (HeadFault(folder, false) == "coverage-break"
            || !VerifyProfile(profile, false, log, chainOnly: true).Intact))
        { log("agent audit retention skipped: coverage-break or unverifiable chain"); return; }
        var today = now.UtcDateTime.Date;
        var files = new List<(DateTime Day, FileInfo Info)>();
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("agents-*.jsonl"))
        {
            if (TryDay(f.Name, out var day)) { files.Add((day, f)); }
            else { throw new IOException("the agent audit folder contains an invalid day name"); }
        }
        files.Sort((a, b) => a.Day.CompareTo(b.Day));
        // A future-dated file (clock stepped back) is never deleted, so it must not count or it would wipe every real day.
        var total = files.Where(f => f.Day <= today).Sum(f => f.Info.Length);
        var earlier = files.Where(f => f.Day < today).Select(f => f.Day).DefaultIfEmpty(today).Max();
        var anchor = earlier.AddDays(1) < today ? earlier.AddDays(1) : today;
        foreach (var (day, info) in files)
        {
            if (day >= today) { break; }
            if (day > anchor.AddDays(-KeepDays) && total <= maxBytes) { break; }
            var size = info.Length;
            if (expected is not null)
            {
                var next = files.First(f => f.Day > day).Info;
                var removed = LastGateSequence(folder, false, info.FullName);
                expected = expected with { Root = next.Name, RootSha256 = Hash(Encoding.UTF8.GetBytes(File.ReadLines(next.FullName).First())),
                    RetiringRoot = info.Name, RetainedThrough = Math.Max(expected.RetainedThrough, removed), Sequence = Math.Max(expected.Sequence, removed) };
                // Commit the exact authorized deletion before removing evidence; a crash may leave this one file present.
                SaveHead(folder, false, expected);
            }
            info.Delete();
            if (expected is not null) { expected = expected with { RetiringRoot = null }; SaveHead(folder, false, expected); }
            total -= size;
        }
    }
}
