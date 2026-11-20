namespace AgentBridge.Protocol;

public static class NotifyOwnerProtocol
{
    public const string Op = "notify-owner";
    public const int MaxRequestBytes = 4096;
}

public sealed record NotifyOwnerRequest(Guid Request, string Audience, string Target, string Title, string Body);
[UnknownAs(Unknown)]
public enum OwnerClientKind { Unknown, App, Pwa }
[UnknownAs(Refused)]
public enum OwnerNotifyOutcome { Refused, Delivered, Queued }
public sealed record OwnerClientAck(OwnerClientKind Client, OwnerNotifyOutcome Outcome, string Reason);
public sealed record NotifyOwnerResult(Guid Request, IReadOnlyList<OwnerClientAck> Clients);
