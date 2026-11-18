using AgentBridge.Host.Core;

namespace AgentBridge.Host;

public enum WorkState { Unknown, Cleared, Refused }
public sealed record WorkClearance(WorkState State, string Reason);
public sealed record WorkStateClearance(string Session, SessionProcess Process, DateTimeOffset CheckedAt,
    WorkClearance OpenOwnedPr, WorkClearance UnpushedWork, WorkClearance Held, WorkClearance Private);
public interface IWorkStateClearance
{
    WorkStateClearance Read(string session, SessionProcess process);
}
