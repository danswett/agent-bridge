using System.Text.Json;

namespace AgentBridge.Host.Core.Intake;

// One hook event the host has taken from the spool and acted on, for whatever else in the
// host wants to know of it the moment it happens: the Home Assistant connector, which on a
// migrated machine publishes what agent-ha-bridge's hooks used to (docs/ha-connector.md), and
// later a shadow comparison (#40). The hook path itself never reaches Home Assistant.
//
// What was recorded is in the same files agent-ha-bridge's hooks write, which discovery
// already reads; this is the event as it arrived, and what was made of it.
public sealed record HookNotice(
    string Agent,
    string Hook,
    // hook_event_name, when the agent sent one (Claude and Codex do; Copilot does not).
    string? EventName,
    string? SessionId,
    // The agent process found from the recorded ancestry, or null.
    int? OwnerPid,
    // The status the registration now carries for this event (working, waiting, idle,
    // ended), or null when the event sets none.
    string? Status,
    // For a Codex approval: the generation now in its marker, which a client answers with.
    string? ApprovalId,
    JsonElement Event,
    IReadOnlyList<int> Ancestors,
    DateTimeOffset? ReceivedAt,
    DateTimeOffset HandledAt);
