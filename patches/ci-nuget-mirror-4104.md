Part of #4104. **Supply chain, sensitive: full read.** One file, `.github/workflows/ci.yml`: `product` restores through a NuGet mirror on self-hosted (ab-ci) runners only.

**Why:** run 45277647321 (product windows on ab-ci-vm3-5) failed at `dotnet tool restore` with NU1301 ("Unable to load the service index for source https://api.nuget.org/v3/index.json"). These VMs can't reach nuget.org: the TLS connection fails. The repository's `nuget.config` clears every source but nuget.org, so no runner setting alone can redirect NuGet. (`setup-dotnet`'s fallback to the runner's profile was a warning, not the failure.)

## Quality's checks

**Mirror only on ab-ci legs; hosted unchanged.**
- The mirror step runs only `if: runner.environment == 'self-hosted'`.
- It sets `RestoreConfigFile` and `NUGET_MIRROR_CONFIG` in the job's environment. A hosted runner never has them.
- The hosted tool restore is the existing `- run: dotnet tool restore` step, with the same command and the same shell. Its `if:` only gains `&& env.NUGET_MIRROR_CONFIG == ''`, which is always true on hosted.
- A separate step does the self-hosted tool restore.
- `nuget.config` is not touched, so the NuGet cache key, which hashes `**/nuget.config`, is the same.

**HTTPS: who runs the mirror and what it proxies.**
- The URL comes from the runner's `.env` (`AGENT_BRIDGE_NUGET_SOURCE`, set by the owner's `setup/fix-abci-env.ps1`). The step refuses anything that isn't `https://<host>/...`.
- The mirror is `https://packagefeedproxy.microsoft.io/nuget/v3/index.json`. Its TLS certificate is `CN=packagefeedproxy.microsoft.io, O=Microsoft Corporation`, issued by `Microsoft TLS G2 RSA CA OCSP 10`, valid to 2027-01-20, and validated by Windows.
- Its service index is served by an Azure Artifacts feed (`ms-feed-25.pkgs.visualstudio.com`), including `VulnerabilityInfo`.
- It is a pull-through of nuget.org: every package it served on VM7 verifies with `dotnet nuget verify --all`. Those without an author signature are repository-signed "NuGet.org Repository by Microsoft". Microsoft's author-signed ones passed the check below, which requires nuget.org's repository signature.

**Integrity.**
- The config the job writes lists only the mirror (`<clear />`, then one source). It clears `fallbackPackageFolders`, so no machine-wide Visual Studio offline folder is used, and there is no fallback feed.
- It sets `signatureValidationMode=require`, and `trustedSigners` holds nuget.org's repository certificate (SHA-256 `1F4B311D9ACC115C8DC8018B5A49E00FCE6DA8E2855F9F014CA6F34570BC482D`). So every package must carry nuget.org's repository signature: the mirror can serve nuget.org's own bytes or nothing.
- Checked on VM7:
  - with the right fingerprint, `AgentBridge.App.Core` restores (5 packages) and `dotnet tool restore` passes;
  - with a wrong fingerprint the restore fails with **NU3034**.
- The repository has no `packages.lock.json` today, so `RestoreLockedMode` isn't available. Signatures are the integrity check, and lock files would be a separate change for every leg.
- NuGet's vulnerability audit passes through the mirror with warnings as errors.
- A rotation of nuget.org's certificate (rare, and announced) fails closed with NU3034 until this fingerprint is updated.

**No credentials.** The mirror is anonymous. The job writes no credential to the repository, the config or the log. The runner's `.env` holds a URL, not a secret.

**A developer's local restore is unchanged:** `nuget.config` is the same and nothing outside the job's `RUNNER_TEMP` changes.

**Fails closed.**
- A runner with no https mirror fails the step: "names no https mirror in AGENT_BRIDGE_NUGET_SOURCE".
- A mirror that is down fails the restore with **NU1301**, and there is no other source. Checked with an unreachable mirror: exit 1, nothing downloaded.

**First-run proof:** the `ab-ci smoke` workflow (`patches/ab-ci-smoke-4104.patch` on `sg-patch/ab-ci-env-4104`). It is dispatch-only and runs as the runner: who runs it, where each tool resolves, `dotnet tool restore` and a Release build through this mirror. It needs #3835's run-by-hand alternative to pass the lint, so it lands after #3835.

## Tests
- With this patch on main, the Quality tests pass (894 passed, 0 failed, 1 skipped on main 351a2625) and the catalog is complete.
- The four restore checks above (mirror without the config, with it, wrong fingerprint, mirror down) were run on VM7, on the same network as the ab-ci runners.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
