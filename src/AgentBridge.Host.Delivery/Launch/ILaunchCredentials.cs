namespace AgentBridge.Host.Delivery;

// The credential a launch hands the agent it starts (design/agent-principals.md §3.3). The token
// has to be in the child's environment before the child exists, and the process it is bound to
// is known only once it has started, so issuing and binding are two steps.
public interface ILaunchCredentials
{
    // A token for the session about to start, or null when none is issued. Never logged.
    string? Prepare(string session, string launchId);

    // The launch started: the token now answers only to this process.
    void Bind(string launchId, int processId, DateTimeOffset startedAt);

    // The launch did not start: the token is dead.
    void Abandon(string launchId);
}
