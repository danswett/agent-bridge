namespace AgentBridge.Host.Delivery;

// What a Copilot launch may start with (#1518): the token to hand the child, or the plain reason
// it may not start. Exactly one of the two is set.
public sealed record LaunchAccount(string? Token, string? Refusal)
{
    public static LaunchAccount Allowed(string token) => new(token, null);
    public static LaunchAccount Refuse(string reason) => new(null, reason);
}

// Settles, before anything is created for a Copilot launch, which account the child will spend.
// The Copilot CLI picks a stored login when it is given no token, and the stored danswett login is
// out of premium quota, so a launch that inherited whatever the host's process had was refused
// by GitHub or spent the wrong account. Never a fallback: no token, no launch.
public interface ILaunchAccount
{
    // expectedLogin is launch.copilotAccount; null asks only for a valid token with quota.
    Task<LaunchAccount> CheckAsync(string? expectedLogin, CancellationToken cancellationToken);
}
