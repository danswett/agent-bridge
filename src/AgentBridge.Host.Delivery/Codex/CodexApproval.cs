using System.Text.Json;

namespace AgentBridge.Host.Delivery.Codex;

// The keys for a Codex approval, as agent-ha-bridge types them (Invoke-PendingCodexApprovals):
// Codex's approval prompt takes y to approve and n to deny, sent as a reply is, the letter
// then Enter after 300 ms. agent-ha-bridge saw an approval delivered this way go through
// live; it never confirmed one, and Codex records no answer to an approval that names it,
// so a host cannot say more than that the keys were typed.
public static class CodexApproval
{
    public const int EnterDelayMs = 300;

    public static (KeyPlan? Keys, string? Decision, string? Problem) Plan(IReadOnlyDictionary<string, JsonElement>? fields)
    {
        if (fields is null || !fields.TryGetValue("decision", out var value) || value.ValueKind != JsonValueKind.String)
        {
            return (null, null, "an approval is answered with decision approve or deny");
        }
        return value.GetString() switch
        {
            "approve" => (new KeyPlan([new KeySend("y", true, EnterDelayMs, 0)]), "approve", null),
            "deny" => (new KeyPlan([new KeySend("n", true, EnterDelayMs, 0)]), "deny", null),
            _ => (null, null, "an approval is answered with decision approve or deny"),
        };
    }
}
