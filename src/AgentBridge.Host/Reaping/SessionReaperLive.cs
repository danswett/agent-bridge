using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Reaping;

public sealed partial class SessionReaperService
{
    public Func<bool> CanEnd { get; init; } = () => false;
    public Func<ReaperDecision, StopRequest, CancellationToken, Task<StopResult>>? End { get; init; }
    public Action? ResetEvidence { get; init; }
    Task<StopResult>? pendingEnd;
    static string Hash(string id) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id))).ToLowerInvariant();

    bool Approved(SessionReaperSettings config)
    {
        if (!config.Live || config.ApprovedReceipt is not { } name || !Regex.IsMatch(name, @"\A[a-f0-9]{32}\.json\z")
            || config.ApprovedSha256 is not { } hash || !Regex.IsMatch(hash, @"\A[a-fA-F0-9]{64}\z")) { return false; }
        try
        {
            var path = Path.Combine(folder, "reaper", "receipts", name);
            if (new FileInfo(path).Length > 4 * 1024 * 1024) { return false; }
            var bytes = File.ReadAllBytes(path);
            var approved = JsonSerializer.Deserialize<SessionReaperReceipt>(bytes, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return Convert.ToHexString(SHA256.HashData(bytes)).Equals(hash, StringComparison.OrdinalIgnoreCase)
                && approved is { Schema: 1, Mode: "dry-run", Problem: null, Ended.Count: 0 } && approved.Machine == machine
                && approved.Id + ".json" == name && approved.At.Offset == TimeSpan.Zero && approved.At <= clock.GetUtcNow();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { log($"session reaper approval unavailable ({e.GetType().Name}); staying dry-run"); return false; }
    }

    async Task<SessionReaperReceipt> EndApprovedAsync(SessionReaperReceipt receipt, ReaperSnapshot snapshot,
        SessionReaperSettings config, CancellationToken ct)
    {
        if (End is null || !Approved(config) || !CanEnd() || pendingEnd is { IsCompleted: false }) { return receipt; }
        var candidates = SessionReaperPolicy.Plan(snapshot, clock.GetUtcNow(), config.ProtectedIds, config.ProtectedNames).Where(row => row.Eligible).ToArray();
        receipt = receipt with { Mode = "live" };
        WriteReceipt(receipt);
        foreach (var candidate in candidates)
        {
            var ended = false; var reason = "live eligibility changed";
            try
            {
                var directory = Path.Combine(folder, "reaper", "intents");
                Directory.CreateDirectory(directory);
                async Task<ReaperDecision?> Fresh(string? own = null)
                {
                    ct.ThrowIfCancellationRequested();
                    var current = settings();
                    if (!Approved(current) || !CanEnd()) { return null; }
                    var fresh = Bind(await ReadEvidenceAsync(ct), current);
                    var files = Directory.GetFiles(directory).Select(Path.GetFileName).ToArray();
                    if (files.Any(file => file is null || !Regex.IsMatch(file, @"\A[a-f0-9]{64}\z"))) { throw new InvalidDataException("intent journal"); }
                    var reserved = fresh.Sessions.Where(row => row.Id != own && files.Contains(Hash(row.Id))).Select(row => row.Id).ToArray();
                    return SessionReaperPolicy.Plan(fresh, clock.GetUtcNow(), current.ProtectedIds, current.ProtectedNames, reserved)
                        .SingleOrDefault(row => row.Id == candidate.Id && row.Name == candidate.Name && row.Role == candidate.Role);
                }
                var before = await Fresh();
                if (before is not { Eligible: true }) { reason = before?.Reason ?? reason; }
                else
                {
                    var request = new StopRequest { Id = candidate.Id, Request = Guid.NewGuid().ToString("D") };
                    using (var file = new FileStream(Path.Combine(directory, Hash(candidate.Id)), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    { file.Write(Encoding.UTF8.GetBytes(request.Request)); file.Flush(true); }
                    var after = await Fresh(candidate.Id);
                    reason = after?.Reason ?? reason;
                    if (after is { Eligible: true })
                    {
                        receipt = receipt with { Kept = receipt.Kept.Select(row => row.Id == candidate.Id ? row with { Reason = "stop intent persisted; outcome unproved" } : row).ToArray() };
                        WriteReceipt(receipt);
                        pendingEnd = End(candidate, request, ct);
                        ended = (await pendingEnd.WaitAsync(ct)).Outcome == StopOutcome.Ended;
                        reason = ended ? "confirmed ended by existing session.stop" : "stuck or unproved; intent retained, never second press";
                    }
                }
            }
            catch (Exception e) { reason = $"unavailable or uncertain ({e.GetType().Name}); intent retained if written"; log($"session reaper: {reason}"); }
            var entry = candidate with { Eligible = false, Reason = reason };
            receipt = receipt with { Ended = ended ? [.. receipt.Ended, entry] : receipt.Ended,
                Kept = receipt.Kept.Where(row => row.Id != candidate.Id).Concat(ended ? [] : new[] { entry }).ToArray() };
            WriteReceipt(receipt);
        }
        return receipt;
    }
}
