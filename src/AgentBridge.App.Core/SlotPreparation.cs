namespace AgentBridge.App.Core;

public static class SlotPreparation
{
    public sealed record Prepared(string RelativePath, string Version);

    public static Prepared Prepare(string root, string version, string package, PackageDigest digest) =>
        throw new NotImplementedException("Immutable slot preparation is not implemented.");
}
