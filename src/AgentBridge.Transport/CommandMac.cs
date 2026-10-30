using System.Security.Cryptography;
using System.Text;

namespace AgentBridge.Transport;

// The MAC a person's device signs a command with for one machine (design §5.5,
// docs/protocol/v1-transfer.md "The signed command"): device keys are ECDH keys (a TPM's, or
// WebCrypto's non-extractable ones, which cannot sign), so the key is from static-static ECDH
// between the signing device and the machine it is for, and only those two can make it.
//
//   key = HKDF-SHA256(ikm: ECDH(signer, machine), salt: "agent-bridge command v1",
//                     info: "<signer device id>|<machine device id>", 32 bytes)
//   mac = HMAC-SHA256(key, the body's exact UTF-8 bytes)
//
// Here so the host (AgentBridge.Host's SignedCommands, which checks one) and the Windows app
// (which signs one) share a single implementation; pwa/js/transfer.js is the other, held to it by
// a test vector.
public static class CommandMac
{
    static readonly byte[] Salt = "agent-bridge command v1"u8.ToArray();

    public static byte[] Key(P256KeyPair self, byte[] peerPublic, string signerId, string machineId)
    {
        var z = self.Dh(peerPublic);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, z, 32, Salt, Encoding.ASCII.GetBytes($"{signerId}|{machineId}"));
    }

    // The MAC of body by signer for the machine whose public key is machinePublic.
    public static byte[] Of(P256KeyPair signer, byte[] machinePublic, byte[] body) =>
        HMACSHA256.HashData(Key(signer, machinePublic, Wire.DeviceId(signer.Public), Wire.DeviceId(machinePublic)), body);
}
