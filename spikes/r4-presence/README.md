# R4 presence proof (spike, not landed)

The harness was not landed. It is on branch `sg-r4-presence-proof-r5` at commit
f54238ef3ecaa5fa81ca1b695d1aa4346505d097 (danswett/agent-bridge), run with
`pwsh spikes\r4-presence\Run-R4Proof.ps1`.

`RESULTS-DSWETT-HOME.md` is the one owner-attended run on DSWETT-HOME, 2026-11-20, as received from
the runner session. Verdict: Inconclusive. Creating the test passkey returned 0x80090027
(NTE_INVALID_PARAMETER) before any Hello prompt appeared, so no attack ran and nothing was created.
Nothing is claimed: Windows app presence stays `none`.
