using AgentBridge.Protocol;

namespace AgentBridge.Host.SignIn;

// What the machine's verified copy of #55's roster says about person devices: None until there
// is one, Ready, or Unreadable. Only Ready pins anyone (Trust's condition 1 on #972, 6700902097).
public enum RosterState { None, Ready, Unreadable }

// A device the roster pins as a person's (#972 slice 2): its signed add entry, active, of a person
// kind, with a presence-proven key, and past probation and confirmed (device-approval.md §3.4).
// Generation and AddHash change when it is removed or added again, so a grant bound to them ends.
internal sealed record PinnedDevice(string Id, string Kind, string Platform, string Name, string Presence, string Generation, string AddHash);

// The pin for phone sign-in. Trust's #55 slice A implements it, with IRosterGate, on its verifier.
// Find matches the channel's full key and relay account only, and returns the roster's own kind
// and presence: the channel's self-declared kind is never an input. Pinned and Find return only
// devices past probation and confirmed. StillPinned is false on any doubt, a stale roster
// included. Changed fires on every change, a removal heard from another host included.
internal interface IPersonDeviceRoster
{
    RosterState State { get; }
    PinnedDevice? Find(byte[] key, string account);
    IReadOnlyList<PinnedDevice> Pinned(string account);
    bool StillPinned(PinnedDevice device);
    event Action? Changed;
}

// Until #55's roster lands: no roster, so nobody is pinned and no device can sign an agent in.
internal sealed class NoPersonRoster : IPersonDeviceRoster
{
    public RosterState State => RosterState.None;
    public PinnedDevice? Find(byte[] key, string account) => null;
    public IReadOnlyList<PinnedDevice> Pinned(string account) => [];
    public bool StillPinned(PinnedDevice device) => false;
    public event Action? Changed { add { } remove { } }
}

internal static class PersonDevices
{
    // RosterKinds.IsPerson (device-approval.md §3.2), and the presences that can do a person-only
    // action (§3.1). Checked here too, so a roster that returned anything else still pins nobody.
    static readonly HashSet<string> Kinds = new(["app", "pwa", "phone"], StringComparer.Ordinal);
    static readonly HashSet<string> Presences = new(["key", "webauthn"], StringComparer.Ordinal);

    public static bool Qualifies(PinnedDevice? d) => d is not null && Kinds.Contains(d.Kind) && Presences.Contains(d.Presence)
        && TransferProtocol.IsDeviceId(d.Id) && !string.IsNullOrEmpty(d.Generation) && !string.IsNullOrEmpty(d.AddHash);

    // Whether anyone can sign an agent in here now; nobody on any doubt.
    public static bool Any(IPersonDeviceRoster roster, string? account) =>
        View(roster, account, null) is { State: SignInDeviceStates.Ready, Devices.Count: > 0 };

    // The read-only list for the app's Settings and a device's Machines page: name, kind and
    // platform only, and which is the device asking (self: its device id). Never an id or a key. The
    // name and platform are the enrollee's own words, so they are cleaned as a machine's name is.
    // Only a roster read lists anyone; one that can't be read lists nobody and says so.
    public static SignInDevices View(IPersonDeviceRoster roster, string? account, string? self)
    {
        try
        {
            var state = roster.State;
            if (state != RosterState.Ready || string.IsNullOrEmpty(account))
            {
                return new(state == RosterState.Unreadable ? SignInDeviceStates.Unreadable : SignInDeviceStates.None, []);
            }
            return new(SignInDeviceStates.Ready, [.. roster.Pinned(account).Where(d => Qualifies(d) && roster.StillPinned(d))
                .Select(d => new SignInDevice(HostConfigFile.CleanName(d.Name) ?? "A device", d.Kind, HostConfigFile.CleanName(d.Platform) ?? "",
                    d.Id == self ? true : null))]);
        }
        catch (Exception) { return new(SignInDeviceStates.Unreadable, []); }
    }
}