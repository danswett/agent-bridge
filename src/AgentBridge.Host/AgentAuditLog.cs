using System.Globalization;
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

// The trail of what agents did, one JSON line per event in <profile>\audit\agents-YYYY-MM-DD.jsonl,
// in a folder only this user can open. It keeps the SHA-256 and length of any text an event
// carried and never the text, so reading the log leaks nothing a prompt held. At most 30 days and
// 64 MiB are kept, the oldest files going first, and never the day's file being written.
//
// An unwritable log refuses the action (the #1401 row in design/agent-launch-scope.md): Append
// returns false and never throws, and the caller must then refuse the action it was auditing
// and release any slot. A failure (read-only folder, disk full) is said once in the host log and
// the next success says it recovered. Standalone: nothing calls it yet (slice 4 and #1170 do).
public sealed class AgentAuditLog
{
    public const string FolderName = "audit";
    public const int KeepDays = 30;
    public const long MaxBytes = 64L * 1024 * 1024;
    public const int MaxReason = 200;
    public const int MaxField = 256;
    public const int MaxChain = 16;

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.Never };

    sealed record Line(DateTimeOffset At, string Direction, string Request, string Principal, string[] Chain, string Action,
        string Target, string Outcome, string Reason, string? DataSha256, int? TextLength);

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
            var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, Json) + "\n");
            lock (gate)
            {
                var folder = PrivateFolder.Ensure(Path.Combine(profile, FolderName), "the agent audit folder", "agent actions are not recorded");
                var file = Path.Combine(folder, FileFor(now));
                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
                if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                using (var stream = new FileStream(file, options))
                {
                    // A crash mid-write can leave a partial line: start on a fresh one so it can't swallow this event.
                    if (stream.Length > 0 && EndsWithoutNewline(file)) { stream.Write("\n"u8); }
                    stream.Write(data);
                }
                if (!OperatingSystem.IsWindows()) { File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite); }
                if (failing) { failing = false; log("agent audit log is writing again"); }
                // The line is already written: a retention fault is its own problem, not a lost event.
                try { Retain(folder, now); retentionFailing = false; }
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

    static string Cap(string? value, int max) => value is null ? "" : value.Length > max ? value[..max] : value;

    static bool EndsWithoutNewline(string file)
    {
        using var peek = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        peek.Seek(-1, SeekOrigin.End);
        return peek.ReadByte() != '\n';
    }

    static string FileFor(DateTimeOffset day) => $"agents-{day.UtcDateTime:yyyy-MM-dd}.jsonl";

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
        var total = files.Sum(f => f.Info.Length);
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
