using System.Text.Json;
using AgentBridge.App.Core.Feedback;

namespace AgentBridge.App.Core;

public sealed record HangRecord(DateTimeOffset At, int ThreadId, double SecondsStalled, IReadOnlyList<string> LastLogLines);
public sealed record PreviousRun(bool Unclean, string? Version = null, DateTimeOffset? StartedAt = null,
    string? Crash = null, HangRecord? Hang = null);

// Local evidence only. Callers supply diagnostics, never conversation/transcript text.
public sealed class RunMarker : IDisposable
{
    [ThreadStatic] internal static Action<string, byte[]>? WriteFileOverride;
    readonly object gate = new();
    readonly string path;
    readonly TimeProvider clock;
    readonly FileStream ownership;
    PreviousRun current;
    bool completed, disposed;

    public PreviousRun Previous { get; }

    RunMarker(string dir, string version, TimeProvider clock)
    {
        this.clock = clock;
        path = Path.Combine(dir, $"run-marker.{Guid.NewGuid():N}.json");
        current = new(true, FeedbackRedactor.Redact(version), clock.GetUtcNow());
        Directory.CreateDirectory(dir);
        ownership = UserLock.TryTake(path + ".lock", deleteOnClose: true) ?? throw new IOException("The new run record is already owned.");
        var saved = false;
        List<(string Path, FileStream Owner)> recoveredLeases = [];
        try
        {
            Previous = ReadPrevious(dir, recoveredLeases, out var recovered);
            Save();
            if (recovered is not null) { File.Delete(recovered); }
            saved = true;
        }
        finally
        {
            foreach (var lease in recoveredLeases) { lease.Owner.Dispose(); }
            if (!saved) { ownership.Dispose(); }
        }
    }

    public static RunMarker Start(string dir, string version, TimeProvider? clock = null) =>
        new(dir, version, clock ?? TimeProvider.System);

    public static PreviousRun ReadPrevious(string dir)
    {
        List<(string Path, FileStream Owner)> leases = [];
        try { return ReadPrevious(dir, leases, out _); }
        finally { foreach (var lease in leases) { lease.Owner.Dispose(); } }
    }

    static PreviousRun ReadPrevious(string dir, List<(string Path, FileStream Owner)> leases, out string? record)
    {
        record = null;
        try
        {
            PreviousRun previous = new(false);
            foreach (var file in Directory.EnumerateFiles(dir, "run-marker*.json"))
            {
                // Keep abandoned records owned until the replacement is durable and consumed.
                var owner = UserLock.TryTake(file + ".lock", deleteOnClose: true);
                if (owner is null) { continue; }
                leases.Add((file, owner));
                PreviousRun candidate;
                try { candidate = (JsonSerializer.Deserialize<PreviousRun>(File.ReadAllText(file)) ?? new(true)) with { Unclean = true }; }
                catch (FileNotFoundException) { continue; }
                catch (JsonException) { candidate = new(true); }
                if (!previous.Unclean || candidate.StartedAt.GetValueOrDefault() >= previous.StartedAt.GetValueOrDefault()) { previous = candidate; record = file; }
            }
            return previous;
        }
        catch (DirectoryNotFoundException) { record = null; return new(false); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            record = null;
            return new(true, Crash: "The run marker could not be read.");
        }
    }

    public void RecordHang(int threadId, TimeSpan stalled, IReadOnlyList<string> lastLogLines)
    {
        lock (gate)
        {
            if (completed || disposed) { return; }
            var lines = FeedbackRedactor.RedactLogLines(string.Join('\n', lastLogLines))
                .Split('\n').TakeLast(40).Select(line => Limit(line, 2048)).ToArray();
            current = current with { Hang = new(clock.GetUtcNow(), threadId, stalled.TotalSeconds, lines) };
            Save();
        }
    }

    public void RecordCrash(Exception? error)
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            completed = false;
            // Exception messages can contain transcript text; only type and frames belong here.
            current = current with { Crash = DescribeCrash(error) };
            Save();
        }
    }

    public static string DescribeCrash(Exception? error) => error is null ? "Unknown exception" :
        Limit(FeedbackRedactor.Redact(error.GetType().FullName ?? "Exception") + "\n" +
            FeedbackRedactor.Redact(new System.Diagnostics.StackTrace(error, fNeedFileInfo: false).ToString()), 8192);

    public void Complete()
    {
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (completed || current.Crash is not null) { return; }
            File.Delete(path);
            completed = true;
        }
    }

    // Letting go without Complete represents an abandoned run; its evidence stays.
    public void Dispose()
    {
        lock (gate) { disposed = true; ownership.Dispose(); }
    }

    static string Limit(string text, int length) => text.Length <= length ? text : text[..length];
    void Save()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(current);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            (WriteFileOverride ?? WriteFlushed)(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    static void WriteFlushed(string file, byte[] bytes)
    {
        using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }
}
