Part of #4104. **Supply chain, sensitive: full read.** `product` restores through a NuGet mirror on self-hosted (ab-ci) runners only. Hosted legs are unchanged.

**Why:** a main run's `product (windows-latest)` leg on an ab-ci runner (run 45277647321) failed at `dotnet tool restore` with NU1301 ("Unable to load the service index for source https://api.nuget.org/v3/index.json"). The ab-ci machines can't reach nuget.org, and the repository's `nuget.config` clears every source but nuget.org, so no runner setting alone can redirect NuGet.

## Quality's checks

**Mirror only on ab-ci legs; hosted unchanged.**
- The mirror step runs only `if: runner.environment == 'self-hosted'`. It is the only step in `ci.yml` that reads `AGENT_BRIDGE_NUGET_SOURCE` (`CiNuGetMirrorTests`).
- The hosted `dotnet tool restore` keeps its command and shell; its `if:` only gains `&& env.NUGET_MIRROR_CONFIG == ''`, which is always true on hosted. `nuget.config` is untouched.
- The NuGet cache key gains a runner-kind part (`ab-ci-` on self-hosted, empty on hosted), so a hosted leg's key value is unchanged and an ab-ci leg never shares packages with the hosted Windows leg.

**The mirror host is pinned.** `New-AbCiNuGetConfig` (`quality/nuget-mirror.ps1`, reusing the file `run.ps1` already uses) parses the URL and accepts only:
- `https://packagefeedproxy.microsoft.io/...`;
- with no user info, no port (an explicit `:443` too), no query and no fragment.

Anything else fails the leg without echoing the value. `NuGetMirrorTests` refuses all of these:
- `evil.example`;
- `packagefeedproxy.microsoft.io.evil.example`;
- the host only in the path;
- `user:pw@` and `host@evil.example`;
- `:8443` and `:443`;
- `http`, a query, a fragment, and an empty value.

**Who runs the mirror and what it proxies.**
- Its TLS certificate is `CN=packagefeedproxy.microsoft.io, O=Microsoft Corporation`, issued by Microsoft's TLS CA and validated by Windows.
- Its service index is served by an Azure Artifacts feed (`ms-feed-25.pkgs.visualstudio.com`), including `VulnerabilityInfo`.
- It is a pull-through of nuget.org: packages it served verify with nuget.org's repository signature.
- **Who can publish to that feed is the owner's known fact to state here; it isn't checkable from CI.** The signature rule below makes the answer non-critical: the mirror can serve nuget.org-signed bytes or nothing.

**Integrity.**
- The config lists only the mirror as package and audit source (`<clear />`, then one source) and clears `fallbackPackageFolders`.
- It sets `signatureValidationMode=require`, and `trustedSigners` holds nuget.org's three repository certificates: the current one and the two earlier ones that still-served packages carry. These are `1F4B311D…`, `5A2901D6…` (valid to 2024-05-15) and `0E5F38F5…` (valid to 2021-04-14).
- **Proven on the builder's machine, which is on the ab-ci runners' network**, with fresh package and HTTP caches:
  - a restore of the **whole solution** (`AgentBridge.slnx`) through the mirror with this config succeeds (73 packages, warnings as errors, audit on), and `dotnet tool restore` succeeds;
  - with a wrong fingerprint, restore fails with NU3034;
  - with an unreachable mirror, restore fails with NU1301 and downloads nothing.
- There is no `packages.lock.json` in the repository, so signatures are the integrity check here; lock files would be a separate change for every leg.
- A future nuget.org certificate rotation fails closed with NU3034 until its fingerprint is added.

**No credentials.** The mirror is anonymous. No credential is in the repository, the config or the log, and the URL is never echoed.

**A developer's local restore is unchanged** (`nuget.config` is the same; `run.ps1`'s own mirror path, `New-NuGetMirrorConfig`, is untouched).

**Fails closed.** A missing, malformed or other mirror fails the step; a mirror that is down fails the restore (NU1301); a package nuget.org didn't sign fails it (NU3034). Nothing falls back to another source.

**First-run proof:** the `ab-ci smoke` workflow (`patches/ab-ci-smoke-4104.patch` on `sg-patch/ab-ci-env-4104`). It is dispatch-only and runs as the runner: who runs it, where each tool resolves, `dotnet tool restore` and a Release build through this mirror. It needs #3835's run-by-hand alternative, so it lands after #3835.

**Known gaps (not in this PR):**
- The ab-ci runners' `.env` must first get the mirror and PATH from the owner's `setup/fix-abci-env.ps1` (`sg-patch/ab-ci-env-4104`). Until then a self-hosted leg fails at the mirror step, closed.
- Only `product` routes to ab-ci. `app` (`dotnet tool install -g vpk`, its publish and build) has no mirror config and would fail at restore if routed there.
- Cache-restored packages are not re-verified, as before; the cache is now per runner kind.

## Tests
Quality tests pass on this head, including `NuGetMirrorTests` (the pinned host and the config) and `CiNuGetMirrorTests` (the workflow shape); the catalog is complete. `quality/README.md`'s ab-ci section says all of the above.

🤖 Generated with [Claude Code](https://claude.com/claude-code)
