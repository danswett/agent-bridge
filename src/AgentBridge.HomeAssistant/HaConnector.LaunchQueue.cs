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
    readonly List<JsonObject> launchTombstones = [];
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
                    return new JsonObject { ["request"] = id, ["outcome"] = outcome, ["at"] = at };
                }).ToList();
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException)
            {
                File.Move(path, path + ".corrupt");
                Problem("launch-journal", $"Launch journal quarantined: {e.GetType().Name}; recovery is required before launching.");
                throw new IOException("Launch journal quarantined.", e);
            }
            launchTombstones.AddRange(loaded);
        }
        launchJournalLoaded = true;
    }

    void SaveLaunchJournal()
    {
        if (options.StateFile is not { } stateFile) { return; }
        var path = stateFile + ".launch";
        var journal = new JsonObject
        {
            ["tombstones"] = new JsonArray([.. launchTombstones.Select(o => (JsonNode)o.DeepClone())]),
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
        var kept = launchTombstones.Where(o => now - At(o) <= TimeSpan.FromHours(24)).OrderByDescending(At).Take(10000).ToArray();
        launchTombstones.Clear();
        launchTombstones.AddRange(kept);
        launchOutcomes.RemoveAll(o => now - At(o) > HaContract.LaunchOutcomeRetention);
        launchOutcomes.Sort((a, b) => At(b).CompareTo(At(a)));
        if (launchOutcomes.Count > 20) { launchOutcomes.RemoveRange(20, launchOutcomes.Count - 20); }
        launchRequestsHandled.Clear();
        launchRequestsHandled.AddRange(kept.Select(o => o["request"]!.GetValue<string>()));
    }

    static JsonObject LaunchTombstone(JsonObject o) => new()
    {
        ["request"] = o["request"]!.DeepClone(), ["outcome"] = o["outcome"]!.DeepClone(), ["at"] = o["at"]!.DeepClone(),
    };

    JsonObject LaunchOutcomePayload()
    {
        var details = launchOutcomes.ToDictionary(o => o["request"]!.GetValue<string>(), StringComparer.Ordinal);
        var recent = new JsonArray([.. launchTombstones
            .Where(o => time.GetUtcNow() - DateTimeOffset.Parse(o["at"]!.GetValue<string>(), CultureInfo.InvariantCulture) <= HaContract.LaunchOutcomeRetention)
            .Select(o => (JsonNode)(details.GetValueOrDefault(o["request"]!.GetValue<string>()) ?? o).DeepClone())]);
        var payload = new JsonObject { ["recent"] = recent };
        static int Bytes(JsonNode node) => Encoding.UTF8.GetByteCount(HaContract.Serialize(node));
        var bytes = Bytes(payload);
        // Prefer keeping ids/outcomes over older verbose diagnostics.
        for (var i = recent.Count - 1; i >= 1 && bytes > 12 * 1024; i--)
        {
            var o = recent[i]!.AsObject();
            var before = Bytes(o);
            o.Remove("launched");
            o.Remove("note");
            bytes += Bytes(o) - before;
        }
        while (recent.Count > 1 && bytes > 12 * 1024)
        {
            bytes -= Bytes(recent[^1]!) + (recent.Count > 1 ? 1 : 0);
            recent.RemoveAt(recent.Count - 1);
        }
        if (recent.Count == 1 && bytes > 12 * 1024)
        {
            recent[0]!.AsObject().Remove("launched");
            recent[0]!.AsObject().Remove("note");
            if (Bytes(payload) > 12 * 1024) { recent.Clear(); }
        }
        return payload;
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
