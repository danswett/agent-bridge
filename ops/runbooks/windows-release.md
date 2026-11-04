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
src/AgentBridge.Host/HostUpdates.cs), so they never see draft releases.

Before they download a release, and again just before they install one, they read the **halt
list** (#580, plan §6.4): `rollout.json` on the pinned `rollout` pre-release,
`https://github.com/danswett/agent-bridge/releases/download/rollout/rollout.json`,
`{"halt": ["0.1.<run>"]}`. A halted version is never installed, even one a machine already
downloaded. If the list can't be read, or isn't that shape, they install nothing and say so (the
app's log and Settings, the host's update entity), rather than taking it as "nothing halted".
Nothing installs at app start either: a downloaded release waits for the idle rules, which read
the list again. Until the Dev and Beta channels (#451) exist, the halt list is the lever. Once
they do, a bad build is also stopped by not promoting it past Dev, and machines on Beta never see
it.

**Nothing is ever downgraded, not even from a halted build** (owner's decision on #580, and
#451's never-downgrade rule). Halting stops a release spreading; a newer release is the only way
off it.

## Before the first release that reads the list (Owner, once)

Builds with the halt list install nothing until `rollout.json` exists, so it's created before
the PR that adds it merges:
```powershell
pwsh ops/tools/release-halt.ps1 -Initialize -DryRun   # what it would do
pwsh ops/tools/release-halt.ps1 -Initialize           # the rollout pre-release, with {"halt":[]}
```
It's never marked latest, and `build/publish-prerelease.ps1` only prunes `v*` releases, so it
stays. Don't delete it, or its asset: every machine would stop updating, safely but silently
until someone looks.

## Mitigate

1. **Halt it (Owner).** Adds the version to the list, then marks the release's notes as
   withdrawn, with the reason. Machines stop at their next check (within 30 minutes), including
   any that had already downloaded it.
   ```powershell
   pwsh ops/tools/release-halt.ps1 -Version 0.1.<run> -Reason "<what's wrong, and the issue>" -DryRun
   pwsh ops/tools/release-halt.ps1 -Version 0.1.<run> -Reason "<what's wrong, and the issue>"
   ```
   It uses `gh`'s own sign-in (`gh auth login`), never prints a token, and refuses a version
   that has no release. The release stays published, so its notes say why; there's no un-halt,
   because the way on is a newer release. (A session can run it with `-DryRun` and prepare the
   reason; the halt itself is the owner's, ops/README.md.)
2. **Stop the next merge shipping the same bug.** Revert the PR on `main` (through a PR, with
   Quality as merge coordinator, AGENTS.md), or land the fix. Every green `main` run publishes
   a new, higher release, which machines take by the ordinary idle rules. A release published
   while the first was being halted is halted the same way: run step 1 again for it, one version
   per run (`-Version 0.1.<a>`, then `-Version 0.1.<b>`, each with its `-Reason`). Don't list
   several in one `-Version`: from a shell, `pwsh <file> -Version 0.1.<a>,0.1.<b>` arrives as
   one string, which is refused and halts nothing.
3. **Machines that installed it.** They stay on it until step 2's release: no downgrade. **Owner,
   at the machine** (guard #67: host management isn't done from agent sessions):
   - if the host still runs and reaches the relay, wait for step 2's release: it's a higher
     version, and installs normally;
   - if it doesn't, wait for step 2's release, then install it by hand from its GitHub release
     page (`Setup.exe` for the machine's architecture). Don't install an older release: that's
     a downgrade.
4. **Machines not yet updated:** the halt covers them. To hold one back from every release,
   turn off **Install updates automatically** in the app's Settings.

## Confirm

- The list names it:
  `gh release download rollout -R danswett/agent-bridge -p rollout.json -O -`.
- A machine that checks says so: Settings → **Check for updates** reads "Version 0.1.<run> has
  been withdrawn…", and the host's HA update entity reports it halted.
- Each affected machine is back online in the PWA, reporting step 2's version.

## Afterwards

- What did main CI not cover? Add the test.
- A review if the release reached more than one machine. The Dev and Beta channels (#451),
  gated by ring 3 on PPE (plan §6.0), are how one bad release stops reaching everyone.
