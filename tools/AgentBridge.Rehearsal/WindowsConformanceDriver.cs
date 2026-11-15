namespace AgentBridge.Rehearsal;

internal delegate ConformanceResult? WindowsConformanceCallback(ConformanceRun run, ConformanceCapability capability);

internal static class WindowsConformanceDriver
{
    public static IReadOnlyList<ConformanceResult> Run(ConformanceRun run,
        IReadOnlyList<ConformanceCapability> capabilities, WindowsConformanceCallback? callback)
    {
        if (run.Client != "Windows")
            throw new ArgumentException("The Windows driver requires a Windows run.", nameof(run));
        ArgumentNullException.ThrowIfNull(callback);
        var results = capabilities.Select(capability => callback(run, capability)).OfType<ConformanceResult>();
        return ClientConformance.Build([run], capabilities, results);
    }
}
