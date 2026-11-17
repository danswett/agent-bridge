using System.Text.Json;

namespace AgentBridge.App.Core;

public sealed record HangRecord(DateTimeOffset At, int ThreadId, double SecondsStalled, IReadOnlyList<string> LastLogLines);

// Hang-only seam for slice B; startup/clean-exit ownership belongs to slice A.
public sealed class RunMarker(string directory)
{
    public void RecordHang(HangRecord hang)
    {
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "run-marker.json");
        var pending = file + ".tmp";
        File.WriteAllText(pending, JsonSerializer.Serialize(new { Hang = hang }));
        File.Move(pending, file, overwrite: true);
    }
}
