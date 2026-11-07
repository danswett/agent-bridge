using AgentBridge.Protocol;
using AgentBridge.Transport;

namespace AgentBridge.Host.SignIn;

internal sealed record SignInGrant(string Device, string Binding, PinnedDevice? Pinned = null, string? Account = null);
internal interface ISignInAuthority
{
    SignInGrant? Current(IProtocolChannel channel);
    bool StillCurrent(SignInGrant grant);
    // Whatever may have ended a grant: the service looks again at once.
    event Action? Changed;
}

// The person pin (#972 slice 2): a device #55's roster pins as a person's, found by the channel's
// full key and relay account, never by the kind the channel says it is. A #402 trusted-peer pin
// no longer opens sign-in: where that enforcement is on, its person role is still needed for the
// channel to act at all (personRole), but it is never enough. The grant is bound to the roster's
// generation and add, so a removal or a re-add ends a flow at the service's next look.
internal sealed class RosterSignInAuthority(IPersonDeviceRoster roster, Func<bool> readOnly, Func<SecureChannel, bool> personRole) : ISignInAuthority
{
    public SignInGrant? Current(IProtocolChannel channel)
    {
        if (channel is not SecureChannel { IsOpen: true } secure || readOnly() || !personRole(secure)
            || secure.PeerStatic is not { } key || secure.Relay.AccountId is not { Length: > 0 } account) { return null; }
        try
        {
            if (roster.State != RosterState.Ready || roster.Find(key, account) is not { } device
                || device.Id != Wire.DeviceId(key) || !PersonDevices.Qualifies(device) || !roster.StillPinned(device)) { return null; }
            return new(device.Id, $"{account}:{device.Generation}:{device.AddHash}", device, account);
        }
        catch (Exception) { return null; }
    }

    public bool StillCurrent(SignInGrant grant)
    {
        try { return !readOnly() && grant.Pinned is { } device && roster.State == RosterState.Ready && roster.StillPinned(device); }
        catch (Exception) { return false; }
    }

    public event Action? Changed { add => roster.Changed += value; remove => roster.Changed -= value; }
}

internal sealed record SignInFailure(string Id, DateTimeOffset Since);
internal sealed record SignInContext(SignInTarget Target, IReadOnlyList<SignInFailure> Failures);

internal interface ISignInContextResolver
{
    // Synchronous host-owned structured metadata only, never agent prose or CLI output.
    // An account without a structured identity stays null; request text only selects an
    // already-known identity. Production: HostSignInContextResolver.
    SignInContext? Resolve(AgentKind agent, string? account);
    SignInTarget? ResolveTarget(AgentKind agent, string? account);
}

internal sealed record SignInResume(IReadOnlyList<string> Resumed, IReadOnlyList<string> Continue);
internal interface ISignInRecovery
{
    // Positive main-model evidence for the same host-resolved credential scope only.
    // No status command, credential-file inspection or auth probe is permitted.
    Task<bool> HasModelEvidenceAsync(SignInTarget target, CancellationToken ct);
    // Host B must recheck authorize at the effect, including after any queue.
    Task<SignInResume> ResumeAsync(SignInContext context, Func<bool> authorize, CancellationToken ct);
}

// #780 Host B owns restoring/reloading sessions. Host A alone must start nothing.
internal sealed class UnavailableSignInRecovery : ISignInRecovery
{
    public Task<bool> HasModelEvidenceAsync(SignInTarget target, CancellationToken ct) => Task.FromResult(false);
    public Task<SignInResume> ResumeAsync(SignInContext context, Func<bool> authorize, CancellationToken ct) =>
        Task.FromResult(new SignInResume([], []));
}
