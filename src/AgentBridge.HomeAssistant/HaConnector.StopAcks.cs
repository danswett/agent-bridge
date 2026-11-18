using System.Globalization;
using System.Text.Json.Nodes;
using AgentBridge.Protocol;

namespace AgentBridge.HomeAssistant;

public sealed partial class HaConnector
{
    async Task EndAckAsync(Published p, StopDone done, CancellationToken ct)
    {
        var result = done.Result;
        var outcome = done.TimedOut ? "timeout" : result.Outcome switch
        {
            StopOutcome.Ended => "ended",
            StopOutcome.Stale => done.Session is null ? "not_found" : "already_ended",
            StopOutcome.Busy => "ending",
            _ => "refused",
        };
        p.Stop.LastRid ??= done.Rid;
        RecordStopAck(p, done.Rid, done.Status, outcome, result.Forced == true,
            result.Reason ?? (result.Outcome == StopOutcome.Confirm ? "a second press is required" : ""));
        await PublishStopDetailsAsync(p, ct);
    }

    void RecordStopAck(Published p, string rid, string status, string outcome, bool killed, string reason)
    {
        var history = p.Stop.Acks;
        for (var i = history.Count - 1; i >= 0; i--)
        {
            if (history[i]?["rid"]?.GetValue<string>() == rid) { history.RemoveAt(i); }
        }
        history.Add(new JsonObject
        {
            ["rid"] = rid, ["node"] = p.Node, ["outcome"] = outcome,
            ["state_before"] = status, ["killed"] = killed,
            ["at"] = time.GetUtcNow().ToString("O", CultureInfo.InvariantCulture), ["reason"] = reason,
        });
        if (history.Count > 8) { history.RemoveAt(0); }
    }

    static void AddStopAcks(Published p, JsonObject detail)
    {
        if (p.Stop.LastRid is { } rid) { detail["last_rid"] = rid; }
        if (p.Stop.Acks.Count == 0) { return; }
        detail["end_ack"] = p.Stop.Acks[^1]!.DeepClone();
        detail["end_ack_history"] = p.Stop.Acks.DeepClone();
    }

    Task PublishStopDetailsAsync(Published p, CancellationToken ct)
    {
        var detail = p.Detail?.DeepClone().AsObject() ?? [];
        if (p.Reply.Ack is { } ack) { detail["reply_ack"] = ack.DeepClone(); }
        else { detail.Remove("reply_ack"); }
        AddStopAcks(p, detail);
        return PublishAsync(HaContract.Topics(p.Node).ActivityAttributes, detail, true, ct);
    }
}
