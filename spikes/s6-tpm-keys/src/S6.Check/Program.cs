// Spike S6 checks: a device key kept in the TPM does everything the protocol needs.
//
//   s6-check check [--require-tpm]   run the checks; without a TPM the TPM checks are SKIP
//                                    unless --require-tpm, and the DPAPI checks still run
//   s6-check agree --folder F --peer HEX
//                                    (used by the checks) open F's device key in this
//                                    process and print its public key and the ECDH secret
using System.Diagnostics;
using System.Security.Cryptography;
using S2.Noise;
using S4;
using S6;

var mode = args.Length > 0 ? args[0] : "";
string? Arg(string name) { var i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }

if (mode == "agree")
{
    var dk = DeviceKeys.LoadOrCreate(Arg("--folder")!);
    Console.WriteLine($"{dk.Storage} {Wire.Hex(dk.Key.Public)} {Wire.Hex(dk.Key.Dh(Wire.Unhex(Arg("--peer")!)))}");
    return 0;
}
if (mode != "check") { Console.Error.WriteLine("usage: s6-check check [--require-tpm] | agree --folder F --peer HEX"); return 2; }

var requireTpm = args.Contains("--require-tpm");
int failed = 0, skipped = 0;
var root = Path.Combine(Path.GetTempPath(), $"s6-check-{Guid.NewGuid():N}");
var tpmFolder = Path.Combine(root, "tpm");
var hasTpm = TpmP256.IsAvailable(out var platform);
DeviceKey? tpmKey = null;

void Check(string name, Func<(bool Ok, string Detail)> body, bool needsTpm = false)
{
    if (needsTpm && tpmKey is null)
    {
        if (requireTpm) { failed++; Console.WriteLine($"FAIL  {name}  (no TPM)"); }
        else { skipped++; Console.WriteLine($"SKIP  {name}  (no TPM)"); }
        return;
    }
    try
    {
        var (ok, detail) = body();
        if (!ok) { failed++; }
        Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  ({detail})");
    }
    catch (Exception e)
    {
        failed++;
        Console.WriteLine($"FAIL  {name}  ({e.GetType().Name}: {e.Message})");
    }
}

static bool Throws(Action a) { try { a(); return false; } catch (CryptographicException) { return true; } catch (NotSupportedException) { return true; } }

// One full Noise XX handshake and a message each way; true when both ends authenticated
// the other's static key and the transport works.
static bool Handshake(P256KeyPair initiatorKey, P256KeyPair responderKey)
{
    var prologue = "agent-bridge-s6|test"u8.ToArray();
    var i = new XXHandshake(true, initiatorKey, prologue);
    var r = new XXHandshake(false, responderKey, prologue);
    r.ReadMessage(i.WriteMessage([]));
    i.ReadMessage(r.WriteMessage([]));
    r.ReadMessage(i.WriteMessage([]));
    var there = r.Receive!.DecryptWithAd([], i.Send!.EncryptWithAd([], "to the host"u8.ToArray()));
    var back = i.Receive!.DecryptWithAd([], r.Send!.EncryptWithAd([], "to the phone"u8.ToArray()));
    return i.RemoteStatic!.AsSpan().SequenceEqual(responderKey.Public) && r.RemoteStatic!.AsSpan().SequenceEqual(initiatorKey.Public)
        && i.HandshakeHash.AsSpan().SequenceEqual(r.HandshakeHash)
        && there.AsSpan().SequenceEqual("to the host"u8) && back.AsSpan().SequenceEqual("to the phone"u8);
}

static string Ms(List<double> t) { t.Sort(); return $"median {t[t.Count / 2]:F1} ms, p95 {t[(int)(t.Count * 0.95)]:F1} ms"; }

try
{
    if (hasTpm) { Console.WriteLine($"PASS  the machine's TPM is reachable through the Platform Crypto Provider  ({platform})"); }
    else if (requireTpm) { failed++; Console.WriteLine("FAIL  the machine's TPM is reachable through the Platform Crypto Provider  (no TPM, and --require-tpm was given)"); }
    else { skipped++; Console.WriteLine("SKIP  the machine's TPM is reachable through the Platform Crypto Provider  (no TPM: the TPM checks below are skipped)"); }

    var createMs = 0.0;
    if (hasTpm)
    {
        var sw = Stopwatch.StartNew();
        tpmKey = DeviceKeys.LoadOrCreate(tpmFolder, KeyStorage.Tpm);
        createMs = sw.Elapsed.TotalMilliseconds;
    }

    Check("the TPM is only chosen when it can hold a P-256 key: the probe asks for the algorithm, not just the TPM (a TPM 1.2 has only RSA)", () =>
    {
        var p256 = TpmP256.IsAvailable(out _);
        var unsupported = TpmP256.IsAvailable(out _, "NOT_AN_ALGORITHM");
        return (p256 && !unsupported, $"ECDH_P256 {p256}, an algorithm the TPM lacks {unsupported}");
    }, needsTpm: true);

    Check("a device key made in the TPM is per-user and its private half cannot be exported in any format", () =>
    {
        using var t = TpmP256.Open(tpmKey!.TpmKeyName!);
        var k = t.CngKey;
        using var cng = new ECDiffieHellmanCng(k);
        var refused = new[]
        {
            Throws(() => k.Export(CngKeyBlobFormat.EccPrivateBlob)),
            Throws(() => k.Export(CngKeyBlobFormat.Pkcs8PrivateBlob)),
            Throws(() => cng.ExportParameters(true)),
            Throws(() => cng.ExportEncryptedPkcs8PrivateKey("x"u8, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 1000))),
            Throws(() => tpmKey.Key.Export()),
        };
        var files = Directory.GetFiles(tpmFolder).Select(Path.GetFileName).ToArray();
        return (refused.All(x => x) && k.ExportPolicy == CngExportPolicies.None && !k.IsMachineKey && !tpmKey.Key.IsExportable && files.SequenceEqual(["device-key.json"]),
            $"{refused.Count(x => x)} of {refused.Length} export routes refused; export policy {k.ExportPolicy}; machine key {k.IsMachineKey}; profile holds only {string.Join(", ", files)}; created in {createMs:F0} ms");
    }, needsTpm: true);

    Check("the TPM's ECDH equals software ECDH, for 50 random peers in both directions", () =>
    {
        var times = new List<double>();
        var same = 0;
        for (int n = 0; n < 50; n++)
        {
            using var peer = P256KeyPair.Generate();
            var sw = Stopwatch.StartNew();
            var a = tpmKey!.Key.Dh(peer.Public);
            times.Add(sw.Elapsed.TotalMilliseconds);
            if (a.AsSpan().SequenceEqual(peer.Dh(tpmKey.Key.Public))) { same++; }
        }
        return (same == 50, $"{same} of 50 equal; TPM ECDH {Ms(times)}");
    }, needsTpm: true);

    Check("an off-curve point is refused before it reaches the TPM", () =>
    {
        using var peer = P256KeyPair.Generate();
        var off = (byte[])peer.Public.Clone();
        off[64] ^= 1;
        var guarded = Throws(() => tpmKey!.Key.Dh(off));
        using var t = TpmP256.Open(tpmKey!.TpmKeyName!);
        string tpmItself;
        try { t.Agree(off); tpmItself = "ACCEPTED it (the guard is what protects the key)"; }
        catch (CryptographicException e) { tpmItself = $"refuses it too ({e.Message})"; }
        return (guarded, $"refused by the protocol's check; the TPM, asked directly, {tpmItself}");
    }, needsTpm: true);

    Check("Noise XX completes with the TPM key as host or as client, against a software peer and another TPM key", () =>
    {
        using var soft = P256KeyPair.Generate();
        var otherFolder = Path.Combine(root, "tpm2");
        var other = DeviceKeys.LoadOrCreate(otherFolder, KeyStorage.Tpm);
        try
        {
            var asResponder = Handshake(soft, tpmKey!.Key);
            var asInitiator = Handshake(tpmKey.Key, soft);
            var both = Handshake(tpmKey.Key, other.Key);
            var times = new List<double>();
            var softTimes = new List<double>();
            for (int n = 0; n < 20; n++)
            {
                using var phone = P256KeyPair.Generate();
                var sw = Stopwatch.StartNew(); Handshake(phone, tpmKey.Key); times.Add(sw.Elapsed.TotalMilliseconds);
                using var host = P256KeyPair.Generate();
                sw.Restart(); Handshake(phone, host); softTimes.Add(sw.Elapsed.TotalMilliseconds);
            }
            return (asResponder && asInitiator && both, $"TPM as responder {asResponder}, as initiator {asInitiator}, TPM to TPM {both}; whole handshake with a TPM host {Ms(times)}, all-software {Ms(softTimes)}");
        }
        finally { other.Key.Dispose(); DeviceKeys.Delete(otherFolder); }
    }, needsTpm: true);

    Check("a proof of possession made with the TPM key verifies the way the relay checks it, and another key's does not", () =>
    {
        using var eph = P256KeyPair.Generate();
        using var impostor = P256KeyPair.Generate();
        var nonce = RandomNumberGenerator.GetBytes(32);
        var mac = Wire.ProofMac(tpmKey!.Key.Dh(eph.Public), nonce, "3709749");
        var relaySide = Wire.ProofMac(eph.Dh(tpmKey.Key.Public), nonce, "3709749");
        var forged = Wire.ProofMac(impostor.Dh(eph.Public), nonce, "3709749");
        return (CryptographicOperations.FixedTimeEquals(mac, relaySide) && !CryptographicOperations.FixedTimeEquals(forged, relaySide), $"device {Wire.DeviceId(tpmKey.Key.Public)}");
    }, needsTpm: true);

    Check("the key survives the process: another process opens it by name and agrees the same secret", () =>
    {
        using var peer = P256KeyPair.Generate();
        var self = Environment.ProcessPath!;
        var dll = System.Reflection.Assembly.GetEntryAssembly()!.Location;
        var psi = Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? new ProcessStartInfo(self) { ArgumentList = { dll } }
            : new ProcessStartInfo(self);
        foreach (var a in new[] { "agree", "--folder", tpmFolder, "--peer", Wire.Hex(peer.Public) }) { psi.ArgumentList.Add(a); }
        psi.RedirectStandardOutput = true;
        using var p = Process.Start(psi)!;
        var parts = p.StandardOutput.ReadToEnd().Trim().Split(' ');
        p.WaitForExit();
        var ok = parts.Length == 3 && parts[0] == "Tpm" && parts[1] == Wire.Hex(tpmKey!.Key.Public) && parts[2] == Wire.Hex(peer.Dh(tpmKey.Key.Public));
        return (ok, $"other process (pid {p.Id}) saw a {parts.ElementAtOrDefault(0)} key, device {Wire.DeviceId(Wire.Unhex(parts.ElementAtOrDefault(1) ?? ""))}");
    }, needsTpm: true);

    Check("a profile opened again gets the same key back from the TPM, not a new one", () =>
    {
        var sw = Stopwatch.StartNew();
        var again = DeviceKeys.LoadOrCreate(tpmFolder);
        var openMs = sw.Elapsed.TotalMilliseconds;
        var ok = again.Storage == KeyStorage.Tpm && again.TpmKeyName == tpmKey!.TpmKeyName && again.Key.Public.AsSpan().SequenceEqual(tpmKey.Key.Public);
        again.Key.Dispose();
        return (ok, $"opened in {openMs:F0} ms");
    }, needsTpm: true);

    Check("a key reference the TPM can no longer open (cleared, or the profile copied to another machine) is an error, not a quiet new key", () =>
    {
        var lost = Path.Combine(root, "lost");
        Directory.CreateDirectory(lost);
        var refJson = $"{{\"Storage\":\"tpm\",\"Name\":\"agent-bridge-device-{Guid.NewGuid():N}\"}}";
        File.WriteAllText(Path.Combine(lost, "device-key.json"), refJson);
        var refused = Throws(() => DeviceKeys.LoadOrCreate(lost));
        var unchanged = File.ReadAllText(Path.Combine(lost, "device-key.json")) == refJson && Directory.GetFiles(lost).Length == 1;
        return (refused && unchanged, $"refused {refused}, reference left as it was {unchanged}");
    }, needsTpm: true);

    Check("forgetting the device deletes the key from the TPM itself, not just its file", () =>
    {
        var name = tpmKey!.TpmKeyName!;
        var before = TpmP256.Exists(name);
        tpmKey.Key.Dispose();
        DeviceKeys.Delete(tpmFolder);
        var after = TpmP256.Exists(name);
        var files = Directory.GetFiles(tpmFolder).Length;
        tpmKey = null;
        return (before && !after && files == 0, $"in the TPM before {before}, after {after}; files left {files}");
    }, needsTpm: true);

    // Without a TPM (an older PC, a VM without a virtual TPM): DPAPI, as design §5.7 says.
    Check("without a TPM, the device key is kept with DPAPI: encrypted on disk, the same key on reload, and a tampered file is refused", () =>
    {
        var folder = Path.Combine(root, "dpapi");
        var dk = DeviceKeys.LoadOrCreate(folder, KeyStorage.Dpapi);
        var d = dk.Key.Export().D;
        var blob = File.ReadAllBytes(Path.Combine(folder, "device-key.dpapi"));
        var plainOnDisk = blob.AsSpan().IndexOf(d) >= 0;
        var again = DeviceKeys.LoadOrCreate(folder);
        var same = again.Storage == KeyStorage.Dpapi && again.Key.Public.AsSpan().SequenceEqual(dk.Key.Public);
        var agrees = Handshake(P256KeyPair.Generate(), again.Key);
        blob[^5] ^= 0xFF;
        File.WriteAllBytes(Path.Combine(folder, "device-key.dpapi"), blob);
        var tamperRefused = Throws(() => DeviceKeys.LoadOrCreate(folder));
        DeviceKeys.Delete(folder);
        return (!plainOnDisk && same && agrees && tamperRefused && Directory.GetFiles(folder).Length == 0,
            $"private key on disk in the clear {plainOnDisk}; same key on reload {same}; Noise XX {agrees}; tampered file refused {tamperRefused}");
    });

    Check("a new profile chooses the TPM when there is one, else DPAPI", () =>
    {
        var folder = Path.Combine(root, "default");
        var dk = DeviceKeys.LoadOrCreate(folder);
        var expected = hasTpm ? KeyStorage.Tpm : KeyStorage.Dpapi;
        dk.Key.Dispose();
        DeviceKeys.Delete(folder);
        return (dk.Storage == expected, $"chose {dk.Storage}");
    });
}
finally
{
    if (tpmKey != null) { tpmKey.Key.Dispose(); DeviceKeys.Delete(tpmFolder); }
    try { Directory.Delete(root, true); } catch (IOException) { }
}

Console.WriteLine(failed == 0 ? $"all checks passed{(skipped > 0 ? $" ({skipped} skipped: no TPM)" : "")}" : $"{failed} check(s) failed");
return failed == 0 ? 0 : 1;
