namespace AgentBridge.App.Core;

public sealed record UpdateCoordinatorRequest(
    string Root, string Packages, string Package, PackageDigest Digest, string PackageId,
    string TargetVersion, string[] RestartArguments, int OriginalProcessId, long OriginalStartedUtcTicks);
