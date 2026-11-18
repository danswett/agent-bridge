using System.Text.Json;

namespace AgentBridge.Host.Reaping;

public sealed record SessionReaperReceipt(int Schema, string Id, string Machine, DateTimeOffset At, string Mode,
    IReadOnlyList<ReaperDecision> Ended, IReadOnlyList<ReaperDecision> Kept, string? Problem = null);

public sealed partial class SessionReaperService : IAsyncDisposable
{
    readonly string folder, machine;
    readonly Func<CancellationToken, Task<ReaperSnapshot>> read;
    readonly Func<SessionReaperSettings> settings;
    readonly Action<string> log;
    readonly TimeProvider clock;
    readonly CancellationTokenSource stop;
    readonly SemaphoreSlim gate = new(1, 1);
    Task<ReaperSnapshot>? pendingRead;
    Task? loop;
    int started, disposed;
    public event Action<SessionReaperReceipt>? Completed;

    public SessionReaperService(string folder, string machine, Func<CancellationToken, Task<ReaperSnapshot>> read,
        Func<SessionReaperSettings> settings, Action<string> log, TimeProvider? time = null, CancellationToken stopping = default)
    {
        this.folder = folder; this.machine = machine; this.read = read; this.settings = settings; this.log = log;
        clock = time ?? TimeProvider.System;
        stop = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref started, 1) == 0) { loop = Task.Run(LoopAsync); }
    }

    public async Task<SessionReaperReceipt?> CycleAsync(CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct)) { return null; }
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2), clock);
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct, stop.Token, timeout.Token);
            var receipt = new SessionReaperReceipt(1, Guid.NewGuid().ToString("N"), machine, clock.GetUtcNow(), "dry-run", [], []);
            ReaperSnapshot? snapshot = null; SessionReaperSettings? config = null;
            try
            {
                config = settings();
                if (pendingRead is { IsCompleted: false }) { throw new InvalidOperationException("previous read still pending"); }
                ResetEvidence?.Invoke();
                snapshot = Bind(await ReadEvidenceAsync(limit.Token), config);
                receipt = receipt with
                {
                    Kept = SessionReaperPolicy.Plan(snapshot, clock.GetUtcNow(), config.ProtectedIds, config.ProtectedNames)
                        .Select(row => row.Eligible ? row with { Eligible = false, Reason = "dry-run: eligible" } : row).ToArray(),
                };
            }
            catch (Exception e)
            {
                receipt = receipt with { Problem = $"evidence unavailable ({e.GetType().Name}); all sessions kept" };
                log($"session reaper: {receipt.Problem}");
            }
            try
            {
                WriteReceipt(receipt);
                if (receipt.Problem is null && snapshot is not null && config is not null)
                    receipt = await EndApprovedAsync(receipt, snapshot, config, limit.Token);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { log($"session reaper receipt unavailable ({e.GetType().Name}); no ending permitted"); throw; }
            log($"session reaper {receipt.Mode}: ended={receipt.Ended.Count} kept={receipt.Kept.Count}; receipt={receipt.Id}.json");
            Completed?.Invoke(receipt);
            return receipt;
        }

        finally { gate.Release(); }
    }

    ReaperSnapshot Bind(ReaperSnapshot snapshot, SessionReaperSettings config) => snapshot.Machine != machine
        ? throw new InvalidDataException("wrong machine")
        : snapshot with { IsVm = config.MachineId == machine ? config.IsVm : null, OwnerLaptop = config.MachineId == machine ? config.OwnerLaptop : null };
    async Task<ReaperSnapshot> ReadEvidenceAsync(CancellationToken ct)
    {
        if (pendingRead is { IsCompleted: false }) { throw new InvalidOperationException("previous read still pending"); }
        pendingRead = Task.Run(() => read(ct), CancellationToken.None);
        return await pendingRead.WaitAsync(ct);
    }

    async Task LoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(15), clock);
        try
        {
            do
            {
                try { await CycleAsync(stop.Token); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                { log($"session reaper cycle unavailable ({e.GetType().Name}); next attempt in fifteen minutes"); }
            } while (await timer.WaitForNextTickAsync(stop.Token));
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    void WriteReceipt(SessionReaperReceipt receipt)
    {
        var directory = Path.Combine(folder, "reaper", "receipts");
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, receipt.Id + ".json");
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(file, receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web));
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) { File.Delete(temporary); } }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) { return; }
        await stop.CancelAsync();
        if (loop is not null) { await loop; }
        stop.Dispose();
    }
}
