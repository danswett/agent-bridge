using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace AgentBridge.Roster;

// Every hash, MAC and signature input of the roster protocol (§3.2, "Framing"):
// F(x₁, …, xₙ) = L(x₁) ‖ … ‖ L(xₙ), where L(x) is x's length in bytes as a 4-byte big-endian
// unsigned integer followed by x. Labels are their ASCII bytes. A signature is never made over raw
// content, so no roster signature can be taken for any other signature the same key makes.
public static class Framing
{
    public const string EntryLabel = "agent-bridge roster entry v1";
    public const string CosignLabel = "agent-bridge roster cosign v1";
    public const string PresenceLabel = "agent-bridge presence v1";
    public const string EnrollCommitLabel = "agent-bridge enroll commit v1";
    public const string EnrollLabel = "agent-bridge enroll v1";
    public const string EnrollCode1Label = "agent-bridge enroll code1 v1";
    public const string EnrollCode2Label = "agent-bridge enroll code2 v1";
    public const string EnrollKeyLabel = "agent-bridge enroll key v1";
    public const string InviteHintLabel = "agent-bridge invite hint v1";
    public const string QrJoinerLabel = "agent-bridge enroll qr joiner v1";
    public const string QrApproverLabel = "agent-bridge enroll qr approver v1";
    public const string HeadLabel = "agent-bridge roster head v1";
    public const string PersonCommandLabel = "agent-bridge person command v1";
    public const string VetoLabel = "agent-bridge reset veto v1";
    public const string HostKeyBindingLabel = "agent-bridge host key binding v1";

    public static byte[] F(params byte[][] parts)
    {
        var length = 0L;
        foreach (var p in parts) { length += 4 + p.Length; }
        if (length > int.MaxValue) { throw new ArgumentException("framed input too long", nameof(parts)); }
        var framed = new byte[length];
        var at = 0;
        foreach (var p in parts)
        {
            BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(at), (uint)p.Length);
            p.CopyTo(framed, at + 4);
            at += 4 + p.Length;
        }
        return framed;
    }

    public static byte[] Label(string label) => Encoding.ASCII.GetBytes(label);

    public static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text);

    public static byte[] Sha256(byte[] data) => SHA256.HashData(data);
}
