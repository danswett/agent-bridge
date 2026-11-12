using AgentBridge.Roster;

namespace AgentBridge.Host;

public sealed partial class HostRosterReader
{
    public string? CheckAcceptHostSign(PersonCommand command, CommandContext context) => "unsupported-verifier";

    internal string? RecordCommand(PersonCommand command, CommandContext context) => "transition-store";

    internal Action? BeforeTransitionCommit { get; set; }
}
