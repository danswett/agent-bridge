using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AgentBridge.HomeAssistant;

public sealed partial class HaConnector
{
    JsonObject launchLedger = new() { ["requests"] = new JsonArray(), ["recent"] = new JsonArray() };
    bool launchLedgerUnreadable;

    JsonObject LaunchLedger() => new()
    {
        ["requests"] = new JsonArray([.. launchRequestsHandled.Order(StringComparer.Ordinal).Select(id => (JsonNode)id)]),
        ["recent"] = new JsonArray([.. launchOutcomes.Select(o => o.DeepClone())]),
    };

    // Other ledger writers see immutable snapshots; a failed claim must not leak into their saves.
    bool SaveLaunchLedger(string? claim = null)
    {
        lock (ledgerSave)
        {
            var before = launchLedger;
            var added = claim is not null && launchRequestsHandled.Add(claim);
            lock (gate) { launchLedger = LaunchLedger(); }
            if (options.StateFile is not null && !launchLedgerUnreadable && SaveLedger()) { return true; }
            if (added) { launchRequestsHandled.Remove(claim!); }
            lock (gate) { launchLedger = before; }
            return false;
        }
    }

    void LoadLaunchLedger(JsonElement root)
    {
        if (!root.TryGetProperty("launch", out var state)) { return; }
        if (state.ValueKind != JsonValueKind.Object
            || !state.TryGetProperty("requests", out var ids) || ids.ValueKind != JsonValueKind.Array
            || !state.TryGetProperty("recent", out var recent) || recent.ValueKind != JsonValueKind.Array)
        {
            throw new JsonException("The durable launch ledger is not a request-id list and outcome history.");
        }
        foreach (var item in ids.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } id || !RequestIdPattern().IsMatch(id))
            {
                throw new JsonException("The durable launch ledger contains an invalid request id.");
            }
            launchRequestsHandled.Add(id);
        }
        MergeLaunchOutcomes(recent);
        launchLedger = LaunchLedger();
    }

    static DateTimeOffset LaunchOutcomeAt(JsonObject outcome) =>
        DateTimeOffset.Parse(outcome["at"]!.GetValue<string>(), CultureInfo.InvariantCulture);

    void MergeLaunchOutcomes(JsonElement recent)
    {
        foreach (var item in recent.EnumerateArray())
        {
            if (JsonNode.Parse(item.GetRawText()) is not JsonObject o || o["request"] is not JsonValue r
                || !r.TryGetValue<string>(out var id) || !RequestIdPattern().IsMatch(id))
            {
                Problem("launch-history", "Home Assistant connector ignored a launch outcome with an invalid request id.");
                continue;
            }
            launchRequestsHandled.Add(id);
            if (o["outcome"]?.ToString() is not ("starting" or "started" or "refused" or "busy" or "failed" or "unconfirmed")
                || o["launched"] is not (null or JsonObject)
                || o["pressed"] is { } press && (press is not JsonValue p || !p.TryGetValue<string>(out _)))
            {
                Problem("launch-history", "Home Assistant connector ignored a malformed launch outcome; its request id remains served.");
                continue;
            }
            if (o["at"] is not JsonValue stamp || !stamp.TryGetValue<string>(out var written)
                || !DateTimeOffset.TryParse(written, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            {
                o["at"] = Instant(time.GetUtcNow());
            }
            var previous = launchOutcomes.FirstOrDefault(p => p["request"]!.GetValue<string>() == id);
            if (previous is not null && previous["outcome"]!.GetValue<string>() != "starting"
                && o["outcome"]!.GetValue<string>() == "starting") { continue; }
            var order = previous is null ? -1 : LaunchOutcomeAt(previous).CompareTo(LaunchOutcomeAt(o));
            if (order > 0 || order == 0 && previous?["outcome"]?.ToString() != "starting") { continue; }
            if (previous is not null) { launchOutcomes.Remove(previous); }
            launchOutcomes.Add(o);
        }
        launchOutcomes.Sort((a, b) => LaunchOutcomeAt(b).CompareTo(LaunchOutcomeAt(a)));
        TrimLaunchOutcomes();
    }

    void TrimLaunchOutcomes()
    {
        var cutoff = time.GetUtcNow() - HaContract.LaunchOutcomeRetention;
        for (var i = launchOutcomes.Count - 1; i >= HaContract.LaunchOutcomesKept; i--)
        {
            if (LaunchOutcomeAt(launchOutcomes[i]) < cutoff) { launchOutcomes.RemoveAt(i); }
        }
    }
}
