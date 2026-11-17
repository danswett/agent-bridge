using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;

namespace AgentBridge.HomeAssistant;

public sealed partial class HaConnector
{
    sealed record QueuedLaunch(LaunchRequest Request, string Id, string Pressed, string Driver, string? Principal, JsonObject Launched);
    readonly List<QueuedLaunch> launchQueue = [];
    bool launchJournalLoaded;

    void LoadLaunchJournal()
    {
        if (launchJournalLoaded) { return; }
        if (options.StateFile is not { } stateFile) { return; }
        var path = stateFile + ".launch";
        if (File.Exists(path + ".corrupt")) { throw new IOException("Launch journal is quarantined; recovery is required before launching."); }
        if (File.Exists(path))
        {
            List<JsonObject> loaded;
            try
            {
                static string Text(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0
                    ? s : throw new JsonException("Missing launch tombstone field.");
                var journal = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new JsonException("Invalid launch journal.");
                var rows = (journal["tombstones"] ?? journal["recent"]) as JsonArray ?? throw new JsonException("Missing launch tombstones.");
                loaded = rows.Select(item =>
                {
                    var id = Text(item?["request"]);
                    var outcome = Text(item?["outcome"]);
                    var at = Text(item?["at"]);
                    if (!RequestIdPattern().IsMatch(id) || !DateTimeOffset.TryParse(at, out _)) { throw new JsonException("Invalid launch tombstone."); }
                    return new JsonObject { ["request"] = id, ["outcome"] = outcome, ["at"] = at,
                        ["pressed"] = "", ["session"] = "", ["note"] = "", ["launched"] = null };
                }).ToList();
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
                File.Move(path, path + ".corrupt");
                Problem("launch-journal", $"Launch journal quarantined: {e.GetType().Name}; recovery is required before launching.");
                throw new IOException("Launch journal quarantined.", e);
            }
            launchOutcomes.AddRange(loaded);
        }
        launchJournalLoaded = true;
    }

    void SaveLaunchJournal()
    {
        if (options.StateFile is not { } stateFile) { return; }
        var path = stateFile + ".launch";
        var journal = new JsonObject
        {
            ["tombstones"] = new JsonArray([.. launchOutcomes.Select(o => (JsonNode)new JsonObject
            {
                ["request"] = o["request"]!.DeepClone(), ["outcome"] = o["outcome"]!.DeepClone(), ["at"] = o["at"]!.DeepClone(),
            })]),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        // The claim must reach disk before dispatch. A crash after it is unconfirmed, never retried.
        using (var stream = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        {
            stream.Write(Encoding.UTF8.GetBytes(HaContract.Serialize(journal)));
            stream.Flush(flushToDisk: true);
        }
        File.Move(path + ".tmp", path, overwrite: true);
    }

    void PruneLaunchRecords()
    {
        var now = time.GetUtcNow();
        static DateTimeOffset At(JsonObject o) => DateTimeOffset.Parse(o["at"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var kept = launchOutcomes.Where(o => now - At(o) <= TimeSpan.FromHours(24)).OrderByDescending(At).Take(10000).ToArray();
        launchOutcomes.Clear();
        launchOutcomes.AddRange(kept);
        launchRequestsHandled.Clear();
        launchRequestsHandled.AddRange(launchOutcomes.Select(o => o["request"]!.GetValue<string>()));
    }

    async Task DispatchQueuedLaunchAsync(CancellationToken ct)
    {
        if (launchBusy || launchQueue.Count == 0) { return; }
        var next = launchQueue[0];
        await OutcomeAsync(next.Id, "starting", "Starting the queued launch.", pressed: next.Pressed, ct: ct);
        await LaunchNoteAsync(StartingNote(next.Launched), ct);
        Dispatch(next.Request, next.Id, next.Pressed, next.Driver, next.Principal, next.Launched, null);
        launchQueue.RemoveAt(0);
    }
}
