namespace AgentBridge.Rehearsal;

internal enum ConformanceVerdict { Pass, Fail, NotSupported, Missing }
internal sealed record ConformanceRun(string Client, string Version, int ProtocolVersion, string ImplementationHead, string RunId);
internal sealed record ConformanceCapability(string Name, bool Live = false);
internal sealed record ConformanceResult(ConformanceRun Run, string Capability, ConformanceVerdict Verdict, string Reason, Uri? Evidence = null);

internal static class ClientConformance
{
    public static IReadOnlyList<ConformanceResult> Build(IReadOnlyList<ConformanceRun> runs,
        IReadOnlyList<ConformanceCapability> capabilities, IEnumerable<ConformanceResult> results)
        => throw new NotImplementedException();

    public static IEnumerable<string> JsonLines(IEnumerable<ConformanceResult> rows)
        => throw new NotImplementedException();
}
