using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;

namespace AgentBridge.HomeAssistant;

public sealed partial class HaConnector
{
    sealed record CapturedLaunchPress(HaState Press, HaState? Payload, string? Id = null);
    sealed record QueuedLaunch(LaunchRequest Request, string Id, string Pressed, DateTimeOffset At,
        string Driver, string? Principal, JsonObject Launched);

    readonly ConcurrentQueue<HaTrigger> launchIngress = new();
    readonly Queue<(HaState State, string? Id)> launchPayloads = new();
    readonly HashSet<string> launchCapturedIds = new(StringComparer.Ordinal);
    readonly Queue<CapturedLaunchPress> launchPresses = new();
    readonly Queue<QueuedLaunch> launchQueue = new();
    HaState? launchPayloadSnapshot;

    // Keep the watch's values, not the shared slot's later contents. A press consumes the
    // oldest preceding payload, so overlapping writers cannot overwrite an accepted snapshot.
    void CaptureLaunchIngress()
    {
        while (launchIngress.TryDequeue(out var hit))
        {
            if (hit.EntityId == LaunchEntity("sensor", "new_prompt_payload"))
            {
                launchPayloadSnapshot = hit.State;
                var read = ReadLaunchRequest(hit.State, hit.State.LastUpdated ?? time.GetUtcNow());
                if (read.Kind == "fresh" && launchCapturedIds.Add(read.Id!)
                    || read.Kind == "malformed") { launchPayloads.Enqueue((hit.State, read.Id)); }
            }
            else
            {
                launchPresses.Enqueue(CaptureLaunchPress(hit.State));
            }
        }
    }

    CapturedLaunchPress CaptureLaunchPress(HaState press)
    {
        if (DateTimeOffset.TryParse(press.State, CultureInfo.InvariantCulture, DateTimeStyles.None, out var pressedAt)
            && launchPayloads.TryPeek(out var waiting) && waiting.State.LastUpdated <= pressedAt)
        {
            launchPayloads.Dequeue();
            return new(press, waiting.State, waiting.Id);
        }
        return new(press, launchPayloadSnapshot);
    }

    async Task LaunchPressAsync(DateTimeOffset now, CancellationToken ct)
    {
        CaptureLaunchIngress();
        while (launchPresses.TryPeek(out var captured))
        {
            await ReadLaunchPressAsync(now, ct, captured);
            if (launchPressHandled != captured.Press.State) { return; }
            launchPresses.Dequeue();
            CaptureLaunchIngress();
        }
        if (launchPayloads.Count == 0) { launchPayloadSnapshot = null; }
        await ReadLaunchPressAsync(now, ct);
    }

    async Task DispatchQueuedLaunchAsync(CancellationToken ct)
    {
        while (!launchBusy && launchQueue.TryPeek(out var queued))
        {
            if (time.GetUtcNow() - queued.At > TimeSpan.FromMinutes(5))
            {
                const string late = "Launch refused: the request waited more than 5 minutes for this machine, so it was not started late.";
                await LaunchNoteAsync(late, ct);
                await OutcomeAsync(queued.Id, "refused", late, ct: ct);
            }
            else
            {
                await LaunchNoteAsync(StartingNote(queued.Launched), ct);
                Dispatch(queued.Request, queued.Id, queued.Pressed, queued.Driver, queued.Principal, queued.Launched, null);
            }
            launchQueue.Dequeue();
        }
    }
}
