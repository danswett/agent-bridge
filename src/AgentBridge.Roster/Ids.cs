namespace AgentBridge.Roster;

// Identities in the roster (§3.2): a device's id from its dh key, an entry's hash from its body
// bytes, head order, and the genesis fingerprint people compare at enrollment (§4.2).
public static class Ids
{
    // hex(SHA-256(dh)[0..15]): the first 16 bytes, lowercase, as the relay and transfers compute it.
    public static string Device(ReadOnlySpan<byte> dh) => Codec.Hex(System.Security.Cryptography.SHA256.HashData(dh).AsSpan(0, 16));

    // SHA-256 of the body's exact bytes: the entry's identity, whatever signatures carry it.
    public static byte[] Entry(byte[] body) => Framing.Sha256(body);

    // Ascending by the raw 32-byte hashes, compared byte by byte.
    public static readonly IComparer<byte[]> HeadOrder = Comparer<byte[]>.Create((a, b) => a.AsSpan().SequenceCompareTo(b));

    // The first 64 bits of the genesis entry hash, as four groups of four lowercase hex digits.
    public static string Fingerprint(ReadOnlySpan<byte> genesisHash)
    {
        var hex = Codec.Hex(genesisHash[..8]);
        return $"{hex[..4]} {hex[4..8]} {hex[8..12]} {hex[12..]}";
    }
}
