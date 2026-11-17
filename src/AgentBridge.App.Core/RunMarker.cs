using System.Text.Json;
using AgentBridge.App.Core.Feedback;

namespace AgentBridge.App.Core;

public sealed record HangRecord(DateTimeOffset At, int ThreadId, double SecondsStalled, IReadOnlyList<string> LastLogLines);
public sealed record PreviousRun(bool Unclean, string? Version = null, DateTimeOffset? StartedAt = null,
    string? Crash = null, HangRecord? Hang = null);

// Local evidence only. Callers supply diagnostics, never conversation/transcript text.
public sealed class RunMarker
{
    const string FileName = "run-marker.json";
    readonly object gate = new();
    readonly string path;
    readonly TimeProvider clock;
    PreviousRun current;
    bool completed;

    public PreviousRun Previous { get; }

    RunMarker(string dir, string version, TimeProvider clock)
    {
        this.clock = clock;
        path = Path.Combine(dir, FileName);
        Previous = ReadPrevious(dir);
        current = new(true, FeedbackRedactor.Redact(version), clock.GetUtcNow());
        Directory.CreateDirectory(dir);
        Save();
    }

    public static RunMarker Start(string dir, string version, TimeProvider? clock = null) =>
        new(dir, version, clock ?? TimeProvider.System);

    public static PreviousRun ReadPrevious(string dir)
    {
        try
        {
            return (JsonSerializer.Deserialize<PreviousRun>(File.ReadAllText(Path.Combine(dir, FileName)))
                ?? new(true)) with { Unclean = true };
        }
        catch (FileNotFoundException) { return new(false); }
        catch (DirectoryNotFoundException) { return new(false); }
        catch (JsonException) { return new(true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(true, Crash: "The run marker could not be read.");
        }
    }

    public void RecordHang(int threadId, TimeSpan stalled, IReadOnlyList<string> lastLogLines)
    {
        lock (gate)
        {
            if (completed) { return; }
            var lines = lastLogLines.TakeLast(40).Select(line => Limit(FeedbackRedactor.Redact(line), 2048)).ToArray();
            current = current with { Hang = new(clock.GetUtcNow(), threadId, stalled.TotalSeconds, lines) };
            Save();
        }
    }

    public void RecordCrash(Exception? error)
    {
        lock (gate)
        {
            if (completed) { return; }
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
            if (completed || current.Crash is not null) { return; }
            File.Delete(path);
            completed = true;
        }
    }

    static string Limit(string text, int length) => text.Length <= length ? text : text[..length];
    void Save() => File.WriteAllText(path, JsonSerializer.Serialize(current));
}
