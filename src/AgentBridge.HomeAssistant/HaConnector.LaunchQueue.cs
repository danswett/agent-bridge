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

    string LaunchJournalPath => options.StateFile is { } path ? path + ".launch"
        : throw new IOException("Durable Home Assistant launch requests require a connector StateFile.");

    void LoadLaunchJournal()
    {
        if (launchJournalLoaded) { return; }
        if (options.StateFile is null) { return; }
        var path = LaunchJournalPath;
        if (File.Exists(path))
        {
            var journal = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            launchRequestsHandled.AddRange(journal["handled"]!.Deserialize<List<string>>()!);
            launchQueue.AddRange(journal["pending"]!.Deserialize<List<QueuedLaunch>>()!);
            launchOutcomes.AddRange(journal["recent"]!.AsArray().Select(o => o!.DeepClone().AsObject()));
        }
        launchJournalLoaded = true;
    }

    void SaveLaunchJournal()
    {
        var path = LaunchJournalPath;
        var journal = new JsonObject
        {
            ["handled"] = JsonSerializer.SerializeToNode(launchRequestsHandled),
            ["pending"] = JsonSerializer.SerializeToNode(launchQueue),
            ["recent"] = new JsonArray([.. launchOutcomes.Select(o => (JsonNode)o.DeepClone())]),
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

    async Task DispatchQueuedLaunchAsync(CancellationToken ct)
    {
        if (launchBusy || launchQueue.Count == 0) { return; }
        var next = launchQueue[0];
        launchQueue.RemoveAt(0);
        try { await OutcomeAsync(next.Id, "starting", "Starting the queued launch.", pressed: next.Pressed, ct: ct); }
        catch { launchQueue.Insert(0, next); throw; }
        await LaunchNoteAsync(StartingNote(next.Launched), ct);
        Dispatch(next.Request, next.Id, next.Pressed, next.Driver, next.Principal, next.Launched, null);
    }
}
