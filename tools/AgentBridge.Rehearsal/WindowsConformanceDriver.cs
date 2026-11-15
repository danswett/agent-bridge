namespace AgentBridge.Rehearsal;

internal delegate ConformanceResult? WindowsConformanceCallback(ConformanceRun run, ConformanceCapability capability);

internal static class WindowsConformanceDriver
{
    public static IReadOnlyList<ConformanceResult> Run(ConformanceRun run,
        IReadOnlyList<ConformanceCapability> capabilities, WindowsConformanceCallback? callback)
        => Array.Empty<ConformanceResult>();
}
