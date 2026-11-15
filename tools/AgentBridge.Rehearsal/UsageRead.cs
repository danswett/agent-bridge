using AgentBridge.Transport;

namespace AgentBridge.Rehearsal;

internal static class UsageRead
{
    public static Task<int> RunAsync(IReadOnlyList<string> args, IClientChannel channel, TextWriter output, CancellationToken ct) =>
        Task.FromResult(1);
}
