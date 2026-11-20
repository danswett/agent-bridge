using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentBridge.Host.Delivery.Staging;
using AgentBridge.Roster;
using AgentBridge.Transport;

namespace AgentBridge.Host;

// What a person vouched for, from outside the relay: the genesis fingerprint (first 8 bytes)
// they compared out of band, or later a full 32-byte genesis hash from the signed-in app's own
// verified roster or an approved enrollment (#3450, owner 13:24 PT Nov 20). Never the relay.
internal sealed record PersonGenesis
{
    public string Origin { get; }
    public byte[] Expected { get; }

    public PersonGenesis(string origin, byte[] expected)
    {
        if (expected.Length is not (8 or 32) || string.IsNullOrEmpty(origin)) { throw new ArgumentException("a person genesis is 8 or 32 bytes"); }
        (Origin, Expected) = (origin, expected.ToArray());
    }

    // "7d0f 3a2e 1c4b 4d5e" (docs/protocol/devices.md). Single spaces between groups or none;
    // anything else is refused rather than guessed at.
    public static PersonGenesis? FromFingerprint(string? typed)
    {
        var text = typed?.Trim() ?? "";
        var hex = text.Replace(" ", "");
        if (hex.Length != 16 || !hex.All(char.IsAsciiHexDigit) || (text.Length != 16 && text != string.Join(' ', hex.Chunk(4).Select(c => new string(c)))))
        {
            return null;
        }
        return new("operator-fingerprint", Convert.FromHexString(hex));
    }
}

// The only creator of roster-pin.json (#3450, brief 182). It never trusts the relay: the person's
// genesis selects the chain, the verifier checks it, and every ambiguity is a closed refusal code.
// Callers are local person surfaces only; HostRosterPinWriterTests pins the list.
internal static class HostRosterPinWriter
{
    // A folder of its own: the profile folder inherits a non-private ACL, which the reader refuses.
    public static string PinPath(string profileFolder) => Path.Combine(profileFolder, "roster", "roster-pin.json");

    public static string Fingerprint(byte[] genesis) => string.Join(' ', Convert.ToHexStringLower(genesis, 0, 8).Chunk(4).Select(c => new string(c)));

    public static string Write(string pinPath, PersonGenesis? source, RelayRoster? relay, string? account, Func<TimeSpan> clock)
    {
        if (source is null) { return "no-source"; }
        if (string.IsNullOrEmpty(account)) { return "no-account"; }
        var full = Path.GetFullPath(pinPath);
        if (File.Exists(full)) { return "pin-exists"; }
        if (relay is null || relay.Entries.Count == 0) { return "no-roster"; }
        var entries = new List<Entry>();
        try { entries.AddRange(relay.Entries.Select(t => Entry.Parse(Encoding.UTF8.GetBytes(t)))); }
        catch (RosterFormatException) { return "chain-invalid"; }
        var geneses = entries.Where(e => e.Body.Op == EntryOp.Genesis).ToList();
        if (geneses.Any(g => g.Body.Account != account)) { return "other-account"; }
        if (geneses.Count != 1) { return geneses.Count == 0 ? "no-genesis" : "genesis-ambiguous"; }
        var genesis = geneses[0];
        if (relay.Generation != genesis.Body.Generation) { return "generation-mismatch"; }
        if (!CryptographicOperations.FixedTimeEquals(genesis.Hash.AsSpan(0, source.Expected.Length), source.Expected)) { return "fingerprint-mismatch"; }

        var verifier = new RosterVerifier(account, genesis.Body.Generation, clock);
        if (verifier.Offer(entries).Any(o => o.Verdict == Verdict.Refused) || verifier.Graph.Count == 0 ||
            !verifier.Graph[0].Hash.AsSpan().SequenceEqual(genesis.Hash)) { return "chain-invalid"; }
        var heads = verifier.Heads;
        if (heads.Count is < 1 or > RosterVerifier.MaxHeads) { return "chain-invalid"; }

        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            account, generation = genesis.Body.Generation, genesis = Codec.Base64Url(genesis.Hash),
            heads = heads.Select(h => Codec.Base64Url(h)).ToArray(),
        });
        var temporary = full + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            PrivateFolder.Ensure(Path.GetDirectoryName(full)!, "the roster pin folder", "no pin is written");
            using (var stream = PrivateFolder.CreateNew(temporary)) { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            // Never over a pin another writer finished first.
            File.Move(temporary, full, overwrite: false);
        }
        catch (IOException) when (File.Exists(full)) { return "pin-exists"; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) { return "pin-store"; }
        finally { File.Delete(temporary); }

        // Read back as the host will, so a pin it would refuse is never left as if it worked.
        if (new HostRosterReader(full, clock).Update(relay, account).Outcome != "verified")
        {
            File.Delete(full);
            return "pin-readback";
        }
        return "pinned";
    }
}
