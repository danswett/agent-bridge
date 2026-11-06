# Code signing the Windows app (#734)

*Design note, 2026-11-06 (PT). Design only: no workflow or script changes ship with it.
Today's releases are unsigned ([app-windows.md](app-windows.md), "Releases and updates"). A
tester's first sight is SmartScreen's "Windows protected your PC", and the NativeAOT
injector and hook are the kind of binary Defender flags. Things I could not check from here
are marked **unverified**: confirm them against Microsoft's current pages before the owner
commits money.*

## 1. How releases are built today (why it shapes the design)

* `app` job: `build/pack-app.ps1` publishes the host, launcher, injector, hook and app into one
  folder, then runs `vpk pack` (x64 and Arm64, each its own channel). It runs on every pull
  request too, so anything it needs in the way of secrets must be unavailable there.
* `pack-app.ps1` already accepts `-SignParams` and `-AzureTrustedSignFile` and hands them to
  `vpk pack`, so vpk's own signing hook is the integration point. Nothing passes them yet.
* `publish` job (main only, `contents: write`): downloads both packs, may **rebuild the delta**
  with `vpk delta generate` against the latest published full package
  (`Update-Delta` in `build/publish-prerelease.ps1`), then `vpk upload`. `attest` adds SLSA
  provenance and an SBOM. Provenance says *which build* made a file; a signature says *who
  vouches for it*. They complement each other and neither replaces the other.

## 2. The options

| | Azure Artifact Signing (was Trusted Signing) | OV certificate | EV certificate |
|---|---|---|---|
| Cost | About $10 a month for the basic tier (**unverified**, check the price page) | Roughly $200 to $500 a year, varies by CA | Roughly $300 to $700 a year, plus a token or cloud HSM |
| Key custody | Microsoft holds the key; we only ask it to sign. Certificates are short-lived (days) and renewed by the service | Since 2023 the key must live on an HSM or token, so use the CA's cloud HSM or Azure Key Vault Premium/Managed HSM; a plain `.pfx` is not allowed | Same, and often a physical token, which a CI runner cannot use. Insist on a cloud signing service |
| SmartScreen | Signed identity is recognised; reputation builds with use. Microsoft says its service helps with SmartScreen, but no certificate buys instant trust (**unverified** for current behaviour) | Warning stays until downloads build reputation, per publisher identity | EV no longer skips the warning outright (**unverified**; Microsoft changed this in 2024), so its extra cost buys little now |
| Defender | A valid signature lowers heuristic suspicion and lets us submit a false-positive report against a named publisher. It does not guarantee a NativeAOT injector is left alone | Same | Same |
| Who can get one | Organizations in the US, Canada, EU and UK; individual developers in the US and Canada (**unverified**: eligibility has been widening, and identity validation can take days) | Any validated individual or org; an individual's name then appears as publisher | Org only in practice |
| CI fit | First class: `vpk pack --azureTrustedSignFile`, GitHub OIDC, no stored secret | Only through a cloud HSM with its own API and credentials | Same as OV |

**What the owner must decide:** who the publisher is on the signature (the owner personally
or a legal entity), whether to pay for validation under that name, and the monthly spend.

## 3. Recommendation

**Azure Artifact Signing**, under the owner's validated identity (organization if one exists,
otherwise individual if eligible). Why: the key is never ours to leak; the repo already uses
GitHub OIDC to Azure for `relay.yml`, so the trust pattern exists; vpk supports it directly;
and it is the cheapest route. Fall back to an OV certificate in a cloud HSM only if the owner
is ineligible, since that is the same flow with more cost and a secret to guard.

Be honest with testers: signing removes "Unknown publisher" and gives Defender a name to
trust, but SmartScreen reputation still ramps up with installs. Expect a softer, not absent,
warning for the first weeks. **Unverified** until a real signed prerelease is downloaded on a
clean machine.

## 4. Key custody and gating

* **No key on any disk.** The service signs; the runner only sends file digests. Nothing to
  store, so nothing in `secrets`.
* **GitHub OIDC federation**: an Azure app registration with a federated credential whose subject is
  `repo:danswett/agent-bridge:environment:release-signing`, holding only the
  *Artifact Signing Certificate Profile Signer* role on one certificate profile. `azure/login`
  exchanges the run's token for a short-lived Azure token. Client, tenant and subscription IDs
  are not secrets, but live in environment variables, not the code.
* **Who can sign:** only a job in this repository that declares the protected environment
  `release-signing`. That environment allows deployment from `main` only, and has the owner
  as required reviewer for the first weeks (then it can be relaxed to no reviewer, because
  main's CI is already gated by the merge rule). Pull requests, forks and other branches
  can't get the token because their OIDC subject doesn't match.
* **Keep the signing job small.** Today `app` runs PR code and `publish` has
  `contents: write`. Signing goes in its own job, `app-release`, on main pushes only, that
  builds and packs with `-AzureTrustedSignFile`, uploads the signed packs, and holds no
  `contents: write`. PRs keep the unsigned `app` job. This mirrors why `attest` is separate.
* **Rotation:** the service rotates certificates itself. We rotate nothing on a schedule;
  there is no secret. We review the role assignment and federated credential once a quarter.
* **Revocation and a leak:** there is no key to leak, but the *ability to sign* could be
  abused if the workflow or environment were compromised. Then: remove the role assignment
  (signing stops at once, in seconds), revoke the certificate profile's certificate in the
  portal, rebuild and re-sign a clean release, and tell testers. Past signatures made with a
  revoked certificate stop validating where revocation is checked, so cut a new release
  before revoking when possible. (A leaked signing key once forced a production rotation
  here; this route is chosen so that class of event cannot recur.)

## 5. What gets signed, in what order

`vpk pack` signs inside the pack, so one call does it all, and the order matters:

1. `dotnet publish` produces the folder (unsigned).
2. `vpk pack --azureTrustedSignFile` signs the PE files in the folder **before** it zips them
   into the full package: `AgentBridge.exe`, `agent-bridge-host.exe`,
   `agent-bridge-hostw.exe`, `agent-bridge-injector.exe` and `agent-bridge-host-hook.exe`
   (the two NativeAOT ones), plus our own `.dll`s. It then signs `Update.exe` and
   `Setup.exe`. Whether vpk 1.2.161 also signs every Microsoft runtime `.dll` (already
   signed) or skips them is **unverified**; either is acceptable, as long as the check
   in section 6 passes for all of them.
3. The Portable zip contains the same signed files. The `.nupkg` and `.zip` are archives, not
   PE files, and are not signed themselves; they are covered by attestation.
4. **Deltas stay valid** because a delta is built from the *signed* new full package against
   the *signed* base. `publish` rebuilds with `vpk delta generate` over the downloaded
   packages and never edits a file, so the update reconstructs the signed bytes exactly.
   Rule: **nothing may touch a PE file after `vpk pack`**. The first signed release has no
   signed base, so its first delta is built from an unsigned one and is by definition a
   full replacement of every file; that is fine and happens once (see section 7).
5. **Timestamping is mandatory**, using Microsoft's timestamp service in the signing
   metadata. Without it, a signature dies when the short-lived certificate expires and
   installed copies would suddenly look unsigned.

## 6. Verification and tests

**Publish-time check** (new step in `publish-prerelease.ps1`, before upload, and again on the
downloaded release in `attest`): extract the Full package and the Portable zip of each
channel, and for the Setup.exe, Update.exe and every `.exe` and `.dll` inside, run
`Get-AuthenticodeSignature`. Fail the publish unless `Status` is `Valid`, the signer is the
expected subject, and a timestamp is present. Windows runner only, since `publish` already
is one. Skipped when the kill switch (section 7) says unsigned.

**CI tests without real keys:**

* A Pester or script test for the checker against stand-in files: an unsigned exe, an exe
  signed with a throwaway self-signed certificate created in the test and trusted only in the
  process (its chain is untrusted, so the checker takes the expected subject and a test-only
  "allow untrusted root" switch), a signed-then-modified exe (`HashMismatch`)
  and a file without a timestamp. Each must fail or pass as named.
* The existing `test-publish-prerelease.ps1` stand-ins for gh and vpk gain a case: an
  unsigned file in a "signed" release fails the publish.
* A workflow lint rule (the existing `workflow-lint.yml` route) that fails if any job other
  than `app-release` mentions the signing environment or `azure/login`.
* One manual rehearsal on a disposable VM: install the first signed prerelease, check
  Properties, Digital Signatures and SmartScreen.

## 7. Rollout and risks

1. Owner validates identity and creates the Azure resources (see questions below). Nothing
   in the repo changes yet.
2. Land the checker and its tests in report-only mode.
3. Add `app-release` and turn signing on for the **prerelease ring only**, which is the
   only ring there is today. A repository variable `SIGNING_MODE` (`on` or `off`) is the
   **kill switch**: `off` makes `app-release` pack unsigned and the checker skip. Flipping
   it is one variable change and the next merge publishes unsigned again.
4. **The signed/unsigned boundary:** as far as I know Velopack does not check Authenticode
   when it updates (it checks its own package hash and the feed; **unverified**, test it on
   the rehearsal VM), so an unsigned copy takes a signed update and the reverse. The first
   signed release is a larger download, because every file differs from its unsigned base,
   so expect one full-size update. Switching back to unsigned is the same one-time larger download.
5. Risks: a signing outage blocks publishing (then use the kill switch and say so);
   Azure identity validation delays (start early, it is the long pole); the signed
   NativeAOT hook is still flagged by Defender (submit it as a false positive from the
   named publisher); a signature timestamp service outage (retry; do not publish unsigned
   silently); extra publish time (signing adds minutes per release).

## 8. Open questions

**For the owner:**
1. Publisher name: you personally, or an organization? Is there a legal entity to validate?
2. Are you eligible for Artifact Signing in your country and entity type, and will you start
   identity validation now (it can take days)?
3. Spend: about $10 a month acceptable? If you are not eligible, is an OV certificate in a
   cloud HSM (a few hundred dollars a year) acceptable?
4. Do you want to be required reviewer on `release-signing` at the start?
5. Who may be added to the signer role besides the workflow (answer: nobody, unless you say so)?

**For Trust:**
1. Is a federated credential limited to one environment on `main` an acceptable gate, or do
   you also want a required reviewer permanently?
2. Is the "break glass" (remove the role assignment, revoke, re-release) enough, and who
   holds the Azure subscription owner role?
3. Should the check also pin the signing certificate's thumbprint or chain (stricter, but
   breaks when the service rotates), or only the subject (recommended)?
4. Does the signature identity belong in the attestation policy and the public docs?
