namespace AgentBridge.Protocol;

public static class NotifyOwnerProtocol
{
    public const string Op = "notify-owner";
    public const int MaxRequestBytes = 4096;
}

public sealed record NotifyOwnerRequest(Guid Request, string Audience, string Target, string Title, string Body);
public enum OwnerClientKind { App, Pwa }
public enum OwnerNotifyOutcome { Delivered, Queued, Refused }
public sealed record OwnerClientAck(OwnerClientKind Client, OwnerNotifyOutcome Outcome, string Reason);
public sealed record NotifyOwnerResult(Guid Request, IReadOnlyList<OwnerClientAck> Clients);
