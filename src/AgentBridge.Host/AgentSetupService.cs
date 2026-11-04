using AgentBridge.Local;

namespace AgentBridge.Host;

// Shared by Settings and first-run; the local pipe checks the person's app before reaching here.
public sealed class AgentSetupService(StandaloneHooks hooks, Func<string?> readOnly, CancellationToken stopping = default)
{
    readonly SemaphoreSlim mutations = new(1, 1);
    int restartRequired;

    public bool RestartRequired => Volatile.Read(ref restartRequired) != 0;
    public event Action? Changed;

    public async Task<AgentSetupResult> ApplyAsync(AgentSetupRequest request)
    {
        var agent = request.Agent == "agency" ? "copilot" : request.Agent;
        if (agent is not ("copilot" or "claude" or "codex")) { throw new ArgumentException("Choose copilot (also Agency), claude or codex."); }
        if (request.Action is not ("install" or "remove")) { throw new ArgumentException("Choose install (also repair) or remove."); }
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        limit.CancelAfter(TimeSpan.FromMinutes(3));
        await mutations.WaitAsync(limit.Token);
        try
        {
            limit.Token.ThrowIfCancellationRequested();
            if (readOnly() is { } why) { throw new InvalidOperationException($"Agent setup is unavailable while this host is read-only: {why}"); }
            using var output = new StringWriter();
            using var error = new StringWriter();
            // Even a failed operation may have saved ownership or registrations. A host restart
            // is never automatic, and failure is never reported as a successful connection.
            Volatile.Write(ref restartRequired, 1);
            var code = await HookCommands.RunSetupAsync(hooks, [agent], request.Action == "install",
                dryRun: false, output, error, limit.Token);
            return new AgentSetupResult(code == 0, output.ToString() + error.ToString(), RestartRequired);
        }
        finally
        {
            mutations.Release();
            Changed?.Invoke();
        }
    }
}
