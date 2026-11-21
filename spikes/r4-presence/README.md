# R4 presence proof harness (spike, not shipped)

Brief 211 Phase 1. It asks whether code running as you, unelevated, can get a Windows Hello
(webauthn.dll) assertion without you. It makes one test passkey for `localhost` (deleted by its own id; other passkeys are untouched),
attacks only its own Hello prompt, and deletes the passkey at the end. It never types, sees or stores
your PIN, never touches the bridge, its profile, tokens or keys, and changes no Hello or TPM setting.

For the owner, on DSWETT-HOME, in a normal (not admin) terminal at the agent-bridge checkout:

1. `git fetch origin sg-r4-presence-proof-r5; git switch sg-r4-presence-proof-r5`
2. `pwsh spikes\r4-presence\Run-R4Proof.ps1`
3. When Windows Hello asks (twice: "make a passkey", then "sign in"), type your PIN.
4. When it says "hands off", do not touch the keyboard or mouse, and do not answer any prompt, until it says done (about 2 minutes).
5. Send `spikes\r4-presence\RESULTS-DSWETT-HOME.md` back to Chief.

Needs the .NET 10 SDK and the packages already in this machine's NuGet cache: it restores offline,
never from nuget.org, and stops with a clear message if one is missing. Build output goes to
`spikes\r4-presence\.out` and is deleted at the end. Ctrl+C is safe: it stops, cancels the open prompt and deletes the test passkey.
If cleanup reports a failure, remove the "R4 presence proof" passkey for localhost in Settings, Accounts, Passkeys.

## What it tries

Every Hello call names a small "R4 presence proof" window of the harness as its parent. An attack acts
only on a new window owned by that exact window, re-checked before each action; if none is, it sends
nothing and records inconclusive. Each attack opens a fresh Hello prompt and then, with nobody at the keyboard: asks for an assertion
with user verification required and with it discouraged (silent); sends Esc and Alt+F4 by SendInput;
clicks Cancel by SendInput; lists and invokes the prompt's controls by UI Automation; writes keys with
`agent-bridge-injector` (src/AgentBridge.Injector) to its own console and to the prompt's process;
minimises and closes the prompt. It also replays your assertion for a new challenge through the roster
verifier. Injected keys are dismissals, not a PIN guess (wrong PINs count toward TPM lockout): if the
prompt ends during an injection, the input reached it and the proof FAILS; any assertion without you
FAILS. Not tried: direct NCrypt access to the Hello key, keyboard hooks, WM_CHAR posting.

If Windows refuses the passkey request as an invalid parameter, it retries once per single change
(resident key off, then attestation none; the parent is always the harness's own window), stops at the first Windows accepts,
records which, deletes anything made and runs no attack: you may be asked for the PIN once more.

Verdict: held (all blocked, exit 0), failed (exit 1), inconclusive (exit 2). Tests:
`dotnet test spikes\r4-presence\R4Proof.Tests -c Release` (no Hello needed).
