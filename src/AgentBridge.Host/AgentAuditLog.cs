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
// The log is evidence, not a gate: a write that fails (read-only folder, disk full) is said once
// in the host log and never thrown to the operation being audited, and the next success says it
// recovered. Standalone: nothing calls it yet (slice 4 and #1170 do).
public sealed class AgentAuditLog
{
    public const string FolderName = "audit";
    public const int KeepDays = 30;
    public const long MaxBytes = 64L * 1024 * 1024;
    public const int MaxReason = 200;

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
            var reason = entry.Reason ?? "";
            if (reason.Length > MaxReason) { reason = reason[..MaxReason]; }
            var line = new Line(now, entry.Direction, entry.Request, entry.Principal, entry.Chain, entry.Action, entry.Target, entry.Outcome, reason,
                bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), bytes?.Length);
            var data = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(line, Json) + "\n");
            lock (gate)
            {
                var folder = PrivateFolder.Ensure(Path.Combine(profile, FolderName), "the agent audit folder", "agent actions are not recorded");
                var file = Path.Combine(folder, FileFor(now));
                var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read };
                if (!OperatingSystem.IsWindows()) { options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite; }
                using (var stream = new FileStream(file, options)) { stream.Write(data); }
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
        // Evidence is not a gate: whatever goes wrong here must not fail the action being audited.
        catch (Exception e)
        {
            lock (gate)
            {
                if (!failing) { failing = true; log($"agent audit log could not write, so agent actions are not recorded until it can: {e.GetType().Name}"); }
            }
            return false;
        }
    }

    static string FileFor(DateTimeOffset day) => $"agents-{day.UtcDateTime:yyyy-MM-dd}.jsonl";

    static bool TryDay(string name, out DateTime day)
    {
        day = default;
        return name.StartsWith("agents-", StringComparison.Ordinal) && name.EndsWith(".jsonl", StringComparison.Ordinal)
            && DateTime.TryParseExact(name[7..^6], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out day);
    }

    // Files older than KeepDays go, then the oldest until the rest fit. A file that can't be
    // deleted is skipped, and the one being written is never a candidate.
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
        foreach (var (day, info) in files)
        {
            if (day >= today) { break; }
            if (day >= today.AddDays(-KeepDays) && total <= maxBytes) { break; }
            var size = info.Length;
            try { info.Delete(); total -= size; }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
