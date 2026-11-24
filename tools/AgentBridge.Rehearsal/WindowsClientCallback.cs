namespace AgentBridge.Rehearsal;

internal static class WindowsClientCallback
{
    public static WindowsConformanceCallback Create(IReadOnlyList<ConformanceRun> runs,
        IReadOnlyList<ConformanceResult> evidence)
    {
        var windowsRuns = runs.Where(run => run.Client == "Windows").ToArray();
        var recorded = evidence.Where(result => result.Run.Client == "Windows").ToArray();
        // Validate before indexing: foreign runs, unknown capabilities and duplicates are not missing evidence.
        ClientConformance.Build(windowsRuns, ConformanceInventory.ForClient("Windows"), recorded);
        var byRow = recorded.ToDictionary(result => (result.Run, result.Capability));
        return (run, capability) => byRow.GetValueOrDefault((run, capability.Name));
    }
}
