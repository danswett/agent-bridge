namespace AgentBridge.Roster;

// A host's signing key (agent-principals.md §8.10, V1). A v2 host add carries `hostSign` beside
// `dh`, with a proof by `hostSign` that it holds the key and that the key belongs to this `dh`.
public static class HostKey
{
    // What `hostSignProof` signs: F("agent-bridge host key binding v1", account, generation, dh).
    public static byte[] Binding(string account, string generation, ReadOnlySpan<byte> dh) =>
        Framing.F(Framing.Label(Framing.HostKeyBindingLabel), Framing.Utf8(account), Framing.Utf8(generation), dh.ToArray());
}
