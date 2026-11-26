namespace AgentBridge.Host.Delivery;

// The credential a launch hands the agent it starts (design/agent-principals.md §3.3). The token
// has to be in the child's environment before the child exists, and the process it is bound to
// is known only once it has started, so issuing and binding are two steps.
public interface ILaunchCredentials
{
    // A token for the session about to start, or null when none is issued. Never logged. An empty
    // session is an agent that chooses its own (Codex): the token answers to nobody until Resolve.
    string? Prepare(string session, string launchId);
    string? PrepareFleet(string session, string launchId) => null;

    // The launch started: the token now answers only to this process.
    void Bind(string launchId, int processId, DateTimeOffset startedAt);

    // The launch did not start, or (a Codex) never had its session proved: the token is dead.
    void Abandon(string launchId);

    // A Codex launch's session is now proved (never by folder or timing): the token that was issued
    // with no session now answers for it (#3644). False when it did not take it.
    bool Resolve(string launchId, string session) => false;
}
