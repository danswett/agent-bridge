# S6: device keys in the TPM

**Verdict: GO.** A host's long-term P-256 key can live in the TPM, non-exportable, and do
everything the protocol needs:
- the Noise XX static DH, as host or as client
- the relay's proof of possession
- the device id, which is the hash of its public key

The S4 host ran with a TPM key against the live relay. A software device and the user's
iPhone (WebCrypto) both opened end-to-end channels to it. Measured on a Hyper-V virtual
TPM; physical-TPM timings are still to be measured (see Limitations).

## Question

Design §5.7 keeps each device's private key in the TPM (Windows' Platform Crypto
Provider), with DPAPI where there is no TPM. Noise and the relay proof both need **raw**
ECDH: the shared X coordinate, which WebCrypto's `deriveBits` gives the PWA. Will the
TPM provider give that, rather than only a KDF of it? Can the key be made truly
non-exportable, and is it fast enough?

## Findings

1. **The TPM gives raw ECDH, but not through .NET's wrappers.** `ECDiffieHellmanCng`
   hands the TPM provider a peer key held by the *software* provider, and fails with
   `NTE_INVALID_HANDLE`. `CngKey.Import` into the TPM provider passes flags it refuses
   (`NTE_BAD_FLAGS`). Going straight to NCrypt works:
   `NCryptImportKey(ECCPUBLICBLOB)` into the TPM provider, `NCryptSecretAgreement`,
   then `NCryptDeriveKey("TRUNCATE")` (`BCRYPT_KDF_RAW_SECRET`). That's about 40 lines
   of P/Invoke (`src/DeviceKeys.cs`).
2. **CNG returns the raw secret little-endian.** It must be reversed to equal WebCrypto's
   and .NET's software ECDH. The checks compare against software ECDH for 50 random
   peers.
3. **Nothing above the key changes.** S2's `P256KeyPair` gained a mode with a public key
   and a delegated DH (`FromAgreement`). The handshake, relay client, device id and
   channel code are untouched, and S2's and S4's checks pass unchanged.
4. **The TPM refuses off-curve points itself** (`0x80290407`) on top of the protocol's
   own check, which stays because the DPAPI fallback has no such second line.
5. **A TPM key that can't be opened is an error, never a quiet new key.** That happens
   when the TPM was cleared, or the profile was copied or roamed to another machine (the
   wrapped key blob lives under the roaming AppData). The device must be paired again.
   Silently minting a new key would look like a new device to every peer, and a weaker
   one if it fell back to DPAPI.
6. **"There is a TPM" isn't enough to choose it** (found in review). The provider also
   answers for a TPM 1.2, which supports only RSA, so a new profile there would have
   failed instead of falling back to DPAPI. The probe now asks the provider for
   `ECDH_P256` itself. In the same vein, a protection asked for by name (`--key tpm` or
   `dpapi`) on a platform without it is refused. Before, it was silently replaced by a
   plaintext key file. This is checked on Linux in the S4 job.

## Results

`s6-check check` runs in CI on `windows-latest`. GitHub's runners have no TPM, so there
the TPM checks report SKIP and only the DPAPI path and the fallback choice are checked;
the TPM checks below ran on a machine with one. On DSWETT-DEV-VM1 (Hyper-V virtual TPM
2.0, `VendorID:'MSFT'`, revision 1.38):

| Check | Result |
|---|---|
| The machine's TPM is reachable through the Platform Crypto Provider | Pass |
| The TPM is only chosen when it can hold a P-256 key: the probe asks for the algorithm, not just the TPM (a TPM 1.2 has only RSA) | Pass |
| A device key made in the TPM is per-user and its private half cannot be exported in any format (EccPrivateBlob, PKCS#8, `ExportParameters(true)`, encrypted PKCS#8, `P256KeyPair.Export`) | Pass, 5 of 5 refused |
| The TPM's ECDH equals software ECDH, for 50 random peers in both directions | Pass |
| An off-curve point is refused before it reaches the TPM (and the TPM refuses it too) | Pass |
| Noise XX completes with the TPM key as host or as client, against a software peer and against another TPM key | Pass |
| A proof of possession made with the TPM key verifies the way the relay checks it, and another key's does not | Pass |
| The key survives the process: another process opens it by name and agrees the same secret | Pass |
| A profile opened again gets the same key back from the TPM, not a new one | Pass |
| A key reference the TPM can no longer open is an error, not a quiet new key | Pass |
| Forgetting the device deletes the key from the TPM itself, not just its file | Pass |
| Without a TPM, the key is kept with DPAPI: encrypted on disk, the same key on reload, and a tampered file is refused | Pass |
| A new profile chooses the TPM when there is one, else DPAPI | Pass |

Timings (virtual TPM):

| Operation | Time |
|---|---|
| Create a persisted TPM key | 89 ms |
| Open it again | 6 ms |
| One ECDH in the TPM (import peer, agree, derive) | median 2.1 ms, p95 2.6 ms |
| Whole Noise XX handshake, TPM host vs software phone, in memory | median 10.9 ms (all-software: 9.9 ms) |

Live, against the relay on Azure (S4 host `--profile tpm --key tpm`, device
`b063de44…`):

| Peer | Result |
|---|---|
| Software device on the same machine, through the relay | Proved its key at sign-in; channel's key matched the id; handshake 335 ms, 20 pings median 280 ms (network-bound, as S4's software host) |
| The user's iPhone PWA (WebCrypto) | Connected, key matched, echo intact, pings worked |

## What the product does

* `DeviceKeys.LoadOrCreate` decides storage once, when a device is first set up: the TPM
  when `PCP_PLATFORM_TYPE` answers, else DPAPI (CurrentUser, with entropy). The choice
  is recorded in `device-key.json`, which holds no key material.
* The key is per user, not a machine key: another Windows user on the same PC can't use
  it.
* Sign-out and uninstall delete the TPM key (`DeviceKeys.Delete`) and revoke the device
  in the roster.
* `S4.Host --key tpm|dpapi|file` shows the choice. An existing profile keeps its plain
  `key.json`.

## Limitations

* **Only a virtual TPM was measured.** Hyper-V's vTPM runs in the host and is fast.
  Discrete and firmware TPMs are typically tens of milliseconds per ECDH, and slower to
  create a key. That is still well within budget: the static key is used once per
  channel handshake and once per relay connection, not per message. Physical-TPM numbers
  should be added from a real PC (for example DSWETT-HOME or the ARM laptop's Pluton).
* **Any process running as the user can use the key; none can take it.** Malware
  running as the user could act as the device while it runs, but can't copy the key off
  the machine. That is the boundary design §5.1 assumes. A per-use PIN or UI policy
  would stop a headless daemon working, so there is none.
* **macOS isn't covered.** The Secure Enclave or Keychain path is part of P5.
