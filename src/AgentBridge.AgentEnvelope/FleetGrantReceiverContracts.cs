using System.Text.Json;

namespace AgentBridge.AgentEnvelope;

public sealed record FleetGrantBindingRequest(string Receiver, string Profile);
public sealed record FleetGrantBinding(string Account, string Profile, string Receiver,
    string RosterGeneration, string HostSign);
public sealed record FleetGrantCall(FleetGrantProofContext Context, JsonElement Args,
    JsonElement? Command = null);
public sealed record FleetGrantReadRequest(int Offset = 0);
public sealed record FleetGrantReadPage(long Generation, DateTimeOffset ObservedAt,
    JsonElement Grants, int? Next = null);
public sealed record FleetGrantChallenge(Guid Request, Guid Id, long Generation, string Account,
    string Profile, string Receiver, string Action, string Digest, DateTimeOffset ExpiresAt);
public sealed record FleetGrantPrepared(FleetGrantChallenge Challenge, JsonElement Preview);
public sealed record FleetGrantConfirmation(Guid Challenge, Guid Id, long Generation, string Digest);
