namespace AgentBridge.App;

sealed record HistoryPinnedSample(double Milliseconds, string Event, double Offset, double Extent,
    int? Drawn, int RealisedCount, int? FirstVisibleIndex, double? Target, bool? Intermediate)
{
    public bool EndCorrection => Target is { } target && Math.Abs(target - Extent) <= 1.5;
}

sealed class HistoryPinnedTrace
{
    internal const int Capacity = 2000;
    readonly Queue<HistoryPinnedSample> entries = new();
    public int EntryLimit => Capacity;
    public IReadOnlyCollection<HistoryPinnedSample> Entries => entries;
    public int DroppedEntries { get; private set; }
    public bool Truncated => DroppedEntries > 0;
    public int EndCorrectionCount { get; private set; }
    public bool EndCorrectionObserved => EndCorrectionCount > 0;
    public double? FirstEndCorrectionMs { get; private set; }
    public double? LastEndCorrectionMs { get; private set; }

    public void Record(HistoryPinnedSample sample)
    {
        if (sample.EndCorrection)
        {
            EndCorrectionCount++;
            FirstEndCorrectionMs ??= sample.Milliseconds;
            LastEndCorrectionMs = sample.Milliseconds;
        }
        if (entries.Count == Capacity) { entries.Dequeue(); DroppedEntries++; }
        entries.Enqueue(sample);
    }
}
