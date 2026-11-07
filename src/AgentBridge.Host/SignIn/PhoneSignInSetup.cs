using AgentBridge.Host.Core;
using AgentBridge.Protocol;

namespace AgentBridge.Host.SignIn;

// Which credential store and which failing sessions a phone sign-in is for, from the host's own
// structured state only: the Copilot home its sessions are read from, and each session's
// attention. Several accounts needing sign-in with none chosen is ambiguous, and refused.
internal sealed class HostSignInContextResolver(ISessionSource source, string copilotHome) : ISignInContextResolver
{
    const int MaxFailures = 50;

    public SignInTarget? ResolveTarget(AgentKind agent, string? account) => Resolve(agent, account)?.Target;

    public SignInContext? Resolve(AgentKind agent, string? account)
    {
        if (agent != AgentKind.Copilot) { return null; }
        var failing = source.Snapshot()
            .Where(s => s.Agent == AgentKind.Copilot && s.Attention is { Kind: AttentionKind.SignInNeeded, Agent: AgentKind.Copilot, Provider: "github" })
            .ToList();
        var accounts = failing.Select(s => s.Attention!.Account).Distinct(StringComparer.Ordinal).ToList();
        if (account is not null ? !accounts.Contains(account, StringComparer.Ordinal) : accounts.Count != 1) { return null; }
        var chosen = account ?? accounts[0];
        var failures = failing.Where(s => s.Attention!.Account == chosen)
            .OrderByDescending(s => s.Attention!.Since).Take(MaxFailures)
            .Select(s => new SignInFailure(s.Id, s.Attention!.Since)).ToArray();
        return new(new(Path.GetFullPath(copilotHome), AgentKind.Copilot, chosen), failures);
    }
}

// The per-machine switch (#972): sign-in from a person's device runs only where the machine's own
// permissions.phoneSignIn turns it on, and only for an agent with a production adapter here.
internal static class PhoneSignInSetup
{
    // Copilot alone, and only where its adapter can run: on Windows, where its login is contained
    // in a job object, with Copilot found at start as an .exe (copilotAdapter). Offering a Sign in
    // that could never start would leave the person a button that only fails.
    public static IReadOnlyCollection<AgentKind> Agents(HostSettings settings, bool copilotAdapter) =>
        settings.PhoneSignIn && copilotAdapter ? [AgentKind.Copilot] : [];

    public static AgentSignInService? Create(HostSettings settings, bool copilotAdapter, ISessionSource source, ISignInAuthority authority,
        ISignInContextResolver resolver, IPrivateSignInProcessFactory copilot, Func<bool> readOnly, TimeProvider? time = null) =>
        Agents(settings, copilotAdapter).Contains(AgentKind.Copilot)
            ? new AgentSignInService(source, authority, resolver, new UnavailableSignInRecovery(),
                new PrivateSignInRunner(copilot, time ?? TimeProvider.System), readOnly, time) { Enabled = true }
            : null;
}
