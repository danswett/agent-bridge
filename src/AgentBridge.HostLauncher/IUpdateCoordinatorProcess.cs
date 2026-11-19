namespace AgentBridge.HostLauncher;

public interface IUpdateCoordinatorProcess : IDisposable
{
    int Id { get; }
    long StartedUtcTicks { get; }
    int ExitCode { get; }
    Task WaitForExitAsync(CancellationToken cancel);
}
