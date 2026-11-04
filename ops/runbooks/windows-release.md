# A bad Windows release

**Alert:** A-RELEASE (once OBS-2 and the channels of #451 ship). Today, the signs are:
- machines going offline after an update (the PWA's machine list, status.md's table);
- the HA dashboard's update entities;
- a report from a machine.

## How releases reach machines today

Every merge to `main` publishes a GitHub **pre-release** (`v0.1.<run>`), with x64 (`win`) and
Arm64 (`win-arm64`) channels. Installed apps and hosts check every 30 minutes and install when
the host is idle, or after 6 hours regardless (AutoUpdater.cs). They read releases
**anonymously** from the GitHub API (`GithubSource` with no token: src/AgentBridge.App/Platform.cs,
src/AgentBridge.Host/HostUpdates.cs), so they never see draft releases. Until the Dev and Beta
channels (#451) and the halt list (REL-2) exist, that's the only lever. Once they do, a bad
build is stopped by not promoting it past Dev, and machines on Beta never see it.

## Mitigate

1. **Stop more machines taking it (Owner, or a session with the owner's go-ahead).** Turn the
   release back into a draft. Anonymous clients stop seeing it at their next check, and the
   newest published release becomes the latest again.
   ```powershell
   gh release edit v0.1.<run> -R danswett/agent-bridge --draft=true
   ```
   Machines that already **downloaded** it (downloads start as soon as a release is seen;
   only the install waits for idle) may still install it. Treat them as affected.
2. **Stop the next merge shipping the same bug.** Revert the PR on `main` (through a PR, with
   Quality as merge coordinator, AGENTS.md), or land the fix. Every green `main` run publishes
   a new release, which supersedes the draft.
3. **Machines that installed it.** Installs never downgrade (owner's rule, #451), so the fix
   is a newer build. **Owner, at the machine** (guard #67: host management isn't done from
   agent sessions):
   - if the host still runs and reaches the relay, wait for the reverted release (step 2):
     it's a higher version, and installs normally;
   - if it doesn't, install the last good release by hand from its GitHub release page
     (`Setup.exe` for the machine's architecture).
4. **Machines not yet updated:** to hold one back, turn off **Install updates automatically**
   in the app's Settings.

## Confirm

Each affected machine is back online in the PWA, reporting the expected version.

## Afterwards

- What did main CI not cover? Add the test.
- A review if the release reached more than one machine. The Dev and Beta channels (#451),
  gated by ring 3 on PPE (plan §6.0), are how one bad release stops reaching everyone.
