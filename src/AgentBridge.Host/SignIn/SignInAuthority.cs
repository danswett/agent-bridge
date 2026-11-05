using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host.SignIn;

internal sealed record SignInGrant(string Device, string Binding, TrustedPeerLease? Lease = null, string? Account = null);
internal interface ISignInAuthority
{
    SignInGrant? Current(IProtocolChannel channel);
    bool StillCurrent(SignInGrant grant);
}

internal sealed class PersonSignInAuthority(TrustedPeerStore store, Func<bool> readOnly,
    Func<SecureChannel, bool> canRead, Func<SecureChannel, bool> canWrite) : ISignInAuthority
{
    public SignInGrant? Current(IProtocolChannel channel)
    {
        if (channel is not SecureChannel secure || !secure.IsOpen || secure.PeerKind is not ("app" or "pwa")
            || readOnly() || !canRead(secure) || !canWrite(secure) || secure.PeerStatic is not { } key
            || store.Find(key, secure.Relay.AccountId) is not { Role: TrustedPeerStore.Person } lease
            || !store.Current(lease, secure.Relay.AccountId)) { return null; }
        return new(lease.PublicKey, $"{secure.Relay.AccountId}:{lease.Epoch}:{lease.Generation}", lease, secure.Relay.AccountId);
    }

    public bool StillCurrent(SignInGrant grant) => !readOnly() && grant.Lease is { } lease && store.Current(lease, grant.Account);
}

internal sealed record SignInFailure(string Id, DateTimeOffset Since);
internal sealed record SignInContext(SignInTarget Target, IReadOnlyList<SignInFailure> Failures);

internal interface ISignInContextResolver
{
    // Must resolve the effective credential store and account from host/agent-owned state.
    // No production implementation is registered until the entrypoint decision.
    SignInContext? Resolve(AgentKind agent, string? account);
}

internal sealed record SignInResume(IReadOnlyList<string> Resumed, IReadOnlyList<string> Continue);
internal interface ISignInRecovery
{
    Task<bool> HasCandidateAsync(SignInTarget target, CancellationToken ct);
    // Host B must recheck authorize at the effect, including after any queue.
    Task<SignInResume> ResumeAsync(SignInContext context, Func<bool> authorize, CancellationToken ct);
}

// #780 Host B owns restoring/reloading sessions. Host A alone must start nothing.
internal sealed class UnavailableSignInRecovery : ISignInRecovery
{
    public Task<bool> HasCandidateAsync(SignInTarget target, CancellationToken ct) => Task.FromResult(false);
    public Task<SignInResume> ResumeAsync(SignInContext context, Func<bool> authorize, CancellationToken ct) =>
        Task.FromResult(new SignInResume([], []));
}
