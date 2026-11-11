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

// One thing an agent did, or was refused (design/agent-principals.md §5.6). Direction is "out"
// (an agent here acted on another machine), "in" (an agent elsewhere acted here) or "local".
// Principal is "agent:<machine>/<session>", never the owner. Reason says why in words and
// holds no prompt text, path or token: callers pass a short reason, and the log cuts it at
// MaxReason as well.
public sealed record AgentAuditEntry(string Direction, string Request, string Principal, string[] Chain, string Action,
    string Target, string Outcome, string Reason = "");

// The trail of what agents did, one JSON line per event in <profile>\audit\agents-YYYY-MM-DD.jsonl
// or launches-YYYY-MM-DD.jsonl for session.launch, with a separate 16 MiB launch-history quota,
// in a folder only this user can open. It keeps the SHA-256 and length of any text an event
// carried and never the text, so reading the log leaks nothing a prompt held. Agent files keep at
// most 30 days and 64 MiB, oldest files going first, and never the day's file being written.
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
        string Target, string Outcome, string Reason, string? DataSha256, int? TextLength, int V = 1, string PreviousSha256 = "");

    static readonly string Genesis = new('0', 64);
    readonly object gate = new();
    readonly string profile;
    readonly TimeProvider time;
    readonly Action<string> log;
    readonly long maxBytes;
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
            var bytes = text is null ? null : Encoding.UTF8.GetBytes(text);
            var chain = (entry.Chain ?? []).Take(MaxChain).Select(c => Cap(c, MaxField)).ToArray();
            var line = new Line(now, Cap(entry.Direction, MaxField), Cap(entry.Request, MaxField), Cap(entry.Principal, MaxField), chain,
                Cap(entry.Action, MaxField), Cap(entry.Target, MaxField), Cap(entry.Outcome, MaxField), Cap(entry.Reason, MaxReason),
                bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes?.Length);
            lock (gate)
            {
                var folder = PrivateFolder.Ensure(Path.Combine(profile, FolderName), "the agent audit folder", "agent actions are not recorded");
                using var folderLock = Lock(folder);
                var launch = entry.Action == "session.launch";
                var file = Path.Combine(folder, FileFor(now, launch));
                if (Files(folder, launch).Any(f => StringComparer.Ordinal.Compare(f, file) > 0)) { throw new IOException("the agent audit clock moved backwards"); }
                var length = File.Exists(file) ? new FileInfo(file).Length : 0;
                var previous = TailHash(length > 0 ? file : Files(folder, launch).LastOrDefault(f => StringComparer.Ordinal.Compare(f, file) < 0));
                var header = length == 0 ? JsonSerializer.Serialize(new { v = 1, previousSha256 = previous }, Json) + "\n" : "";
                line = line with { PreviousSha256 = header.Length == 0 ? previous : Hash(Encoding.UTF8.GetBytes(header[..^1])) };
                var data = Encoding.UTF8.GetBytes(header + JsonSerializer.Serialize(line, Json) + "\n");
                // Include a separator after a torn line, and refuse before creating or changing any file.
                var torn = length > 0 && EndsWithoutNewline(file);
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
                }
                if (OperatingSystem.IsWindows()) { NarrowFileWindows(file); }
                else { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
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

    // The oldest retained header is the pruning anchor. A6 adds a durable head to detect terminal truncation.
    public (bool Intact, bool LegacyPrefix) Verify(bool launch = false)
    {
        var legacy = false;
        try
        {
            lock (gate)
            {
                var folder = PrivateFolder.Ensure(Path.Combine(profile, FolderName), "the agent audit folder", "agent actions are not recorded");
                using var folderLock = Lock(folder);
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
                        if (doc is null) { if (fragment) { return (false, legacy); } fragment = true; if (index == 0) { previous = Genesis; } continue; }
                        if (reader.Read() || reader.BytesConsumed != Encoding.UTF8.GetByteCount(raw)) { return (false, legacy); }
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("v", out var version))
                        {
                            if (chained || fragment) { return (false, legacy); }
                            legacy = true;
                        }
                        else
                        {
                            if (!version.TryGetInt32(out var v) || v != 1 || root.TryGetProperty("action", out _) == (index == 0 && !fragment)
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
                return (true, legacy);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { log($"agent audit chain could not be verified: {e.GetType().Name}"); return (false, legacy); }
    }

    static FileStream Lock(string folder)
    {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
        return new FileStream(folder + ".lock", options);
    }

    static IEnumerable<string> Files(string folder, bool launch) =>
        Directory.EnumerateFiles(folder, $"{(launch ? "launches" : "agents")}-*.jsonl")
            .Where(f => TryDay(Path.GetFileName(f).Replace("launches-", "agents-", StringComparison.Ordinal), out _)).Order(StringComparer.Ordinal);

    static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    static string TailHash(string? file)
    {
        if (file is null) { return Genesis; }
        using var stream = File.OpenRead(file);
        var end = PreviousNewline(stream, stream.Length);
        if (end < 0) { return Genesis; }
        var start = PreviousNewline(stream, end) + 1;
        var bytes = new byte[checked((int)(end - start))];
        stream.Position = start;
        stream.ReadExactly(bytes);
        return Hash(bytes);
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

    static string FileFor(DateTimeOffset day, bool launch) => $"{(launch ? "launches" : "agents")}-{day.UtcDateTime:yyyy-MM-dd}.jsonl";

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
        var today = now.UtcDateTime.Date;
        var files = new List<(DateTime Day, FileInfo Info)>();
        foreach (var f in new DirectoryInfo(folder).EnumerateFiles("agents-*.jsonl"))
        {
            if (TryDay(f.Name, out var day)) { files.Add((day, f)); }
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
            info.Delete();
            total -= size;
        }
    }
}
