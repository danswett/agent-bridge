namespace AgentBridge.Local;

public sealed record LaunchFolderRequest(string? Baseline = null, IReadOnlyList<string>? Folders = null, bool Confirmed = false);
public sealed record LaunchFolderSnapshot(string Baseline, IReadOnlyList<string> Folders);
