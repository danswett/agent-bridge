namespace AgentBridge.Host.Delivery.Launch;

public sealed class LaunchDispatchSnapshot
{
    public IReadOnlyList<string> ExtraArguments { get; }
    public string? AuthenticatedPrincipal { get; }

    internal LaunchDispatchSnapshot(IReadOnlyList<string> extraArguments, string? authenticatedPrincipal)
    {
        ExtraArguments = Array.AsReadOnly(extraArguments.ToArray());
        AuthenticatedPrincipal = string.IsNullOrWhiteSpace(authenticatedPrincipal) ? null : authenticatedPrincipal;
    }
}

public sealed partial class LaunchService
{
    // The trusted dispatcher supplies the resolved launcher and authenticated identity, never
    // request fields or a peer label. This only captures facts; it grants no launch authority.
    public LaunchDispatchSnapshot CaptureDispatch(string launcherId, string? authenticatedPrincipal)
    {
        if (Launchers.Find(launcherId) is not { } launcher)
        {
            throw new ArgumentException("Dispatch requires a resolved launcher.", nameof(launcherId));
        }
        return new LaunchDispatchSnapshot(Config.Agent(launcher.Id).ExtraArgs, authenticatedPrincipal);
    }
}
