using System.Collections.Concurrent;

namespace AgentBridge.HomeAssistant;

public sealed partial class HaConnector
{
    sealed record CapturedPress(HaState Press, HaState? Payload);
    readonly ConcurrentQueue<HaTrigger> launchIngress = new();
    readonly List<HaState> launchPayloadSnapshots = [];
    readonly List<CapturedPress> launchPressSnapshots = [];

    static string PayloadId(HaState payload) => payload.Attributes.GetProperty("request").GetString()!;
    bool HasLaunchPayload(string id) => launchPayloadSnapshots.Any(p => PayloadId(p) == id)
        || launchPressSnapshots.Any(p => p.Payload is { } payload && PayloadId(payload) == id);

    async Task CaptureLaunchIngressAsync(CancellationToken ct)
    {
        var count = launchIngress.Count;
        for (var i = 0; i < count && launchIngress.TryDequeue(out var hit); i++)
        {
            if (hit.EntityId == LaunchEntity("sensor", "new_prompt_payload"))
            {
                var read = ReadLaunchRequest(hit.State, hit.State.LastUpdated ?? time.GetUtcNow());
                if (read.Kind != "fresh") { continue; }
                if (launchQueue.Count + launchPayloadSnapshots.Count + launchPressSnapshots.Count(p => p.Payload is not null) >= 128)
                {
                    await OutcomeAsync(read.Id!, "refused", "Launch refused: the pending queue is full; retry with a new request id.", ct: ct);
                    continue;
                }
                launchPayloadSnapshots.Add(hit.State);
                await OutcomeAsync(read.Id!, "waiting-press", "Received the payload; waiting for its launch press.", ct: ct);
            }
            else
            {
                if (!DateTimeOffset.TryParse(hit.State.State, out var pressedAt)) { continue; }
                var payload = launchPayloadSnapshots.FirstOrDefault(p => p.LastUpdated <= pressedAt);
                if (payload is not null) { launchPayloadSnapshots.Remove(payload); }
                launchPressSnapshots.Add(new(hit.State, payload));
            }
        }
    }
}
