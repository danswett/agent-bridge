namespace AgentBridge.Roster;

// One approved person's device as a verifier's copy serves it.
public sealed record PinnedDevice(
    string Id, string Name, string Kind, string Platform, string Presence, bool Confirmed, bool Quarantined);

// The read-only "pinned person devices" query (#55 slice A, item 4): which person devices an
// account's roster approves, and what presence each can prove, so a caller such as phone sign-in
// can offer only a device that reports presence (§3.1). It reads a verifier and changes nothing.
// It lists devices a host serves: active and not held (§3.5), in id order.
public static class PinnedDevices
{
    // `presence` is the device's declared level: `key`, `webauthn` or `none`. Only `key` and
    // `webauthn` are proof a person verified (§4.6); `none` is listed so a client can say why a
    // device cannot be offered.
    public static IReadOnlyList<PinnedDevice> Query(RosterVerifier verifier)
    {
        var state = verifier.State;
        var graph = verifier.Graph;
        var list = new List<PinnedDevice>();
        foreach (var (id, add) in verifier.Serving)
        {
            var d = graph[add].Body.Device!;
            if (!d.IsPerson) { continue; }
            list.Add(new PinnedDevice(
                id, verifier.Name(id) ?? d.Name, d.Kind, d.Platform, d.Presence ?? Presence.None,
                state.IsConfirmed(add), verifier.IsQuarantined(add)));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return list;
    }

    public static bool CanProvePresence(PinnedDevice d) => d.Presence is Presence.Key or Presence.WebAuthn;
}
