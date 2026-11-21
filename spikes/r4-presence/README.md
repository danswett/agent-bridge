# R4 presence proof (spike, not landed)

The harness was not landed. It is on branch `sg-r4-presence-proof-r5` at commit
f54238ef3ecaa5fa81ca1b695d1aa4346505d097 (danswett/agent-bridge), run with
`pwsh spikes\r4-presence\Run-R4Proof.ps1`.

`RESULTS-DSWETT-HOME.md` is the one owner-attended run on DSWETT-HOME, 2026-11-20, as received from
the runner session. Verdict: Inconclusive. Creating the test passkey returned 0x80090027
(NTE_INVALID_PARAMETER) before any Hello prompt appeared, so no attack ran and nothing was created.

## Later runs (as relayed by Chief S6; the runner's result files were not received here)

All three owner-attended runs on DSWETT-HOME on 2026-11-20 were Inconclusive (exit 2). In every one,
Create returned 0x80090027 (NTE_INVALID_PARAMETER, WebAuthNGetErrorName "NotSupportedError")
before any Hello prompt appeared. No attack ran, no passkey was made, and there was nothing to clean up.

* 17:11, harness f54238ef: the default request (attachment platform, resident key, UV required,
  attestation direct, rpId `.invalid`). The full results file is `RESULTS-DSWETT-HOME.md`.
* about 17:37, harness 928aa063: rpId `localhost`, the calls made on the harness window's own
  thread. Refused: default, resident key off, attestation none.
* 17:44, harness 97179469: the harness's own window was in the foreground for every attempt.
  Refused: default, owner window activated, UV preferred, options version 1, ES256+RS256,
  rpId r4-proof.example.com.

Every variant tried used attachment platform. On DSWETT-HOME, Edge creates and uses passkeys
(webauthn.io), but they are synced passkeys from Microsoft Password Manager: transports hybrid and
internal, AAGUID d3452668-01fd-4c12-926c-83a4204853aa. They are not device-bound Windows Hello/TPM
credentials.

Builder observation on DSWETT-DEV-VM6 (build 26200, webauthn.dll API 9, no Hello): the same structs
reproduce 0x80090027 for attachment platform. With attachment any, the same structs pass validation
and Windows shows its UI. The x64 struct sizes match webauthn.h (RP 32, user 40, COSE param 24,
client data 24, make options v3 80 bytes).

Owner decision, 17:49 PT: the same-PC Hello harness is stopped. No further runs, and no further
harness commits. Attachment any or cross-platform was never run on DSWETT-HOME.

What a synced passkey would lose, if one were used: it is not device-bound (backup eligible, a copy
in the Microsoft account and on other devices), so it fails §3.1/§4.6, which require BE clear. There
is no TPM attestation either. Its UV prompt could still be tested for injection, but a pass would not
support declaring presence `webauthn`.

Nothing is claimed: Windows app presence stays `none`.