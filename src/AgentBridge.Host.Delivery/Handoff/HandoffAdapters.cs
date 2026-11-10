using AgentBridge.Host.Core;
using AgentBridge.Host.Delivery.Launch;
using AgentBridge.Protocol;

namespace AgentBridge.Host.Delivery.Handoff;

public sealed class FileHandoffTranscript(AgentKind kind, AgentHomes homes) : IHandoffTranscript
{
    public Task<HandoffDigest?> ReadAsync(string sessionId, CancellationToken cancellationToken) =>
        throw new NotImplementedException($"{kind}: {homes.Copilot}");
}

public sealed class ReplyHandoffAgent(ISessionSource source, ISessionReplier replier) : IHandoffAgent
{
    public Task<string?> AskForNoteAsync(string sessionId, string prompt, CancellationToken cancellationToken) =>
        throw new NotImplementedException($"{source.Revision}: {replier.Available}");
}
