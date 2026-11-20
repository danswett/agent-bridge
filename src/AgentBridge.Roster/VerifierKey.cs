namespace AgentBridge.Roster;

public static class VerifierKey
{
    public static byte[] Binding(string account, string generation, ReadOnlySpan<byte> dh) =>
        Framing.F(Framing.Label(Framing.VerifierKeyBindingLabel), Framing.Utf8(account), Framing.Utf8(generation), dh.ToArray());
}
