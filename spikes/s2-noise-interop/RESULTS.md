# S2: end-to-end encryption interop between .NET and browser WebCrypto

**Verdict: GO** in Node, Chromium and WebKit (Safari's engine). The real-iPhone
Safari run uses the published page (see the last section).

Run on 2026-10-27 on Windows 11 x64 with .NET SDK 10.0.403, Node 24.22.1, Chromium
141.0.7390.37 and WebKit 26.0 (Playwright 1.56.1). CI repeats it on Linux with fresh
keys on every run.

## Question

The design (§5.3) puts every connection (app↔host, phone↔host, host↔host) inside one
end-to-end channel, so the relay, proxies and TLS-terminating middleboxes see only
ciphertext. The phone client starts as a PWA, so the channel must be implementable on
**browser WebCrypto alone** (no WASM, no third-party crypto) and interoperate exactly
with **.NET's built-in crypto** (no native dependencies). Can it?

## The protocol

`Noise_XX_P256_AESGCM_SHA256`: the Noise Protocol Framework (revision 34) XX pattern,
with:

* **DH = NIST P-256.** The 32-byte X coordinate is the shared secret, and public keys
  use the 65-byte uncompressed SEC1 encoding. P-256 is not one of Noise's named
  curves, so `dotnet/Noise.cs` defines it explicitly. It was chosen over X25519
  because it is the one curve that WebCrypto (Safari included), .NET's BCL, Windows'
  TPM provider and Apple's Secure Enclave all support. .NET 10 has no X25519, and
  neither TPMs nor the Secure Enclave do X25519.
* **AES-256-GCM, SHA-256, Noise's HKDF**, unchanged from the spec.

These are the primitives the shipped agent-ha-bridge pairing protocol already uses
(ECDH P-256, HKDF-SHA256, AES-GCM).

## What was run

| File | Role |
|---|---|
| `dotnet/Noise.cs` | The protocol on `System.Security.Cryptography` only |
| `web/noise-p256.js` | A line-by-line mirror on `crypto.subtle` only |
| `dotnet/Program.cs vectors` | Fixed-key transcript: three handshake messages, five transport messages (Unicode, 3 B, 60,000 B, empty, two in a row from one side) |
| `dotnet/Program.cs serve` | Kestrel: the page plus a WebSocket responder (the relay's stack) |
| `web/selftest.js` | Checks shared by Node and the page |
| `js/test-node.mjs`, `js/test-browsers.mjs` | Runners: Node, then Chromium and WebKit through Playwright, including live checks |

## Results

| Check | Node 24 | Chromium 141 | WebKit 26 |
|---|---|---|---|
| All three handshake messages **byte-identical** to .NET (89/196/97 B), and .NET's decrypt | ✅ | ✅ | ✅ |
| Handshake hash identical (channel binding for pairing codes) | ✅ | ✅ | ✅ |
| Five transport messages identical both ways, up to 60,000 B | ✅ | ✅ | ✅ |
| Tampered handshake, different prologue, tampered or replayed transport, off-curve key: all refused | ✅ | ✅ | ✅ |
| Private key generated non-exportable | ✅ | ✅ | ✅ |
| Device key in IndexedDB still works after reload, still non-exportable | n/a | ✅ | ✅ |
| **Live** WebSocket handshake with the .NET server, its key pinned | n/a | ✅ 17 ms | ✅ 412 ms |
| Live echo: Unicode, 60,000 random bytes, empty | n/a | ✅ | ✅ |
| Server closes on a tampered frame (4001) | n/a | ✅ | ✅ |
| A server whose key does not match the pin is refused, before the client reveals its own static key | n/a | ✅ | ✅ |
| **Total** | **20/20** | **27/27** | **27/27** |

Random-key handshake time, including key generation: Node 6.5 ms, Chromium 1.6 ms,
WebKit 823 ms. Playwright's Windows WebKit uses a slow software crypto backend.
Safari on Apple hardware uses CommonCrypto, and the iPhone run below gives the real
figure. Even at the WebKit-on-Windows figure, a once-per-connection cost is
acceptable.

## Consequences for the design

1. **The secure channel can be one protocol everywhere**, including the PWA, with no
   WASM or third-party crypto. The relay only ever forwards Noise frames.
2. **Device keys can be hardware-backed on desktops and non-extractable in browsers**,
   because the static key is P-256 (S6 checks the TPM side).
3. **The handshake hash is shared and authenticated**, so pairing codes (the SAS) can
   be derived from it directly, as the existing pairing protocol derives its code from
   the transcript.
4. Responder identity is checked **before** the initiator sends its static key. That
   is XX's message order, and it means a wrong or malicious relay endpoint learns
   nothing about the device.
5. Noise caps a message at 65,535 bytes, so the protocol layer above (§4.6 blobs)
   chunks anything larger. 60,000-byte messages were exercised.

## Real-iPhone check

`.github/workflows/pages.yml` publishes `web/` as a static page with no server and no
network traffic beyond loading itself:
**https://danswett.github.io/agent-bridge/s2/**. Opening it in iPhone Safari runs the
21 offline checks (vectors, negatives, non-exportable keys, IndexedDB) on the device's
real WebCrypto. The live checks need the spike server and only run with `?live`.

`vectors.json` holds **test-only** keys, generated for this transcript and used for
nothing else.

## Reproduce

```powershell
cd spikes/s2-noise-interop
dotnet build dotnet/S2.Noise.csproj -c Release
dotnet dotnet/bin/Release/net10.0/s2-noise.dll vectors web/vectors.json   # optional: fresh keys
node js/test-node.mjs
npm ci; npx playwright install chromium webkit
node js/test-browsers.mjs
```
