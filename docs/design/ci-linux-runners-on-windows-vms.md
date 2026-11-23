# Linux runners on the existing Windows VMs: WSL2 or a nested Hyper-V VM (design)

Part of #3965 (and #2454). **Design only**: nothing here is installed, registered or routed.
#3965 proposed dedicated Linux VMs, and the owner said "not now". This note answers the owner's next question: can
the existing Windows VMs host the Linux browser legs instead, with no new VMs? It compares (A) WSL2 with (B) a Linux
VM nested in Hyper-V, and ends with a recommendation. Everything in #3965 not restated here (the routing guard and its
lint, the cache rule, the fork-PR controls, the capacity check) applies unchanged to either option.

## Recommendation

**(B), a nested Hyper-V Linux VM, on VM6 and VM7, one per host**, if SRE's read-only check confirms nested
virtualisation on both (it is **unverified** for them; see the next section):
- each with fixed caps of 12 vCPU and 24 GB, and 3 runners;
- routing only `pwa-ui`'s Chromium legs (after item B, the only browser legs left on main's push runs);
- turned on only once a busy-hour capacity check passes on both the hosts and the new pool.

Why B over A:
- The boundary between a job and the fleet's sessions and credentials on the same host is a hypervisor, not a
  config file.
- Its CPU and memory caps are enforced outside the guest.

(A) is acceptable only if SRE finds Hyper-V unusable on those hosts, and only with every control in its section below.

## Read-only check: is nested virtualisation on?

- **What I could read:** only the builder VM I run on (no changes made). It is a `Standard_D32s_v5`, 32 vCPU and
  128 GB, Intel Xeon Platinum 8573C, standard security type (not Trusted Launch). Inside it:
  - a hypervisor is present;
  - the Windows features Microsoft-Hyper-V, VirtualMachinePlatform and Microsoft-Windows-Subsystem-Linux are enabled;
  - virtualisation-based security is running;
  - `wsl --status` reports default version 2, with no distribution installed.
- **What that means:** a Windows guest can run Hyper-V, and so WSL2, only when Azure exposes nested virtualisation.
  So nested virtualisation is on for this one VM.
- **One reading looks contradictory but isn't.** `Win32_Processor` reports `VirtualizationFirmwareEnabled=False` and
  no SLAT. That is expected once a hypervisor runs: Windows hides those flags from the root partition, so they say
  nothing either way.
- **VM6 and VM7: UNVERIFIED.** I could not read them, so I don't know their Azure size, and nothing here shows nested
  virtualisation is enabled or supported on them. The reading above is the builder VM's only. SRE confirms each,
  read-only, before anything else:
  - `(Get-CimInstance Win32_ComputerSystem).HypervisorPresent`;
  - `Get-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V-All, VirtualMachinePlatform` (elevated);
  - the size, from `Invoke-RestMethod -Headers @{Metadata='true'} 'http://169.254.169.254/metadata/instance/compute?api-version=2021-02-01'`.
  - Then check that size's Azure documentation page for nested-virtualisation support. I did not read it, so no
    support claim for any size is made here.
  - Both options need it: WSL2 is itself a lightweight Hyper-V VM.

## What would move, and the queue win

Today's measured load and waits, from #3965:
- a main run spends **52** Linux job-minutes in `pwa-ui`: Chromium 19.2 over 3 legs, WebKit 32.5 over 3 legs;
- Linux jobs wait **4.3 min median, 12.9 min p90** for a hosted runner;
- WebKit runs only on push (`ci.yml` adds its legs when `github.event_name != 'pull_request'`), and item B, now
  approved, takes it off push too.

So after item B, main's push runs carry only Chromium's 3 legs, about **6.4 min each**. Same-repo PR runs carry the
same 3 legs; fork PRs stay hosted (#3965's guard).

| | Hosted Linux job-min per main run | Browser legs to place, per main run |
|---|---|---|
| today | 80 | 52 (6 legs) |
| after item B | ~47.5 | 19.2 (3 legs) |
| after item B + Chromium on these runners | **~28.3** | 0 |

**Runners needed**, at main's measured peak of 12 runs an hour:
- 36 Chromium legs an hour = 230 job-minutes an hour = **3.8 runners busy on average**.
- **A self-hosted label never overflows to hosted.** A routed job waits for one of these runners, so the pool must
  stay well under full:

| Runners | Average use at main's peak | Verdict |
|---|---|---|
| 4 | 96% | a queue forms |
| 6 | 64% | the proposal: 3 per VM |
| 8 | 48% | if PR load needs it |

- Same-repo PR runs add 19 job-minutes each on top. The capacity check measures the real total before routing.

**Expected queue win** (an estimate, measured after as for Windows): 19 job-minutes a run leave the hosted Linux pool.
That is about 40% of what item B leaves there. The hosted Linux wait should fall from 4.3 / 12.9 min (median / p90)
towards 0 to 1 min. The Chromium legs themselves should also lose about 0.5 min of browser and package download each,
since those are baked in.

## Which VMs have room

- VM6 and VM7 are the 32-core hosts. VM7 already runs 8 Windows `ab-ci` runners and fleet sessions; what VM6 runs
  today I could not read.
- **Room rule (SRE measures; I can't read those hosts):**
  - Over one busy hour, sample `\Processor(_Total)\% Processor Time` and `\Memory\Available MBytes`.
  - A host gets a Linux VM only if its p95 CPU stays at or under 50% (16 or more vCPU idle) and 40 GB or more of RAM
    stays free.
  - Then 12 vCPU and 24 GB go to Linux, which leaves at least 4 vCPU and 16 GB of headroom for host builds and agents.
  - A host that fails the rule gets 8 vCPU, 16 GB and 2 runners, or none.
- **Runners per VM:** 3, each with 4 vCPU and 8 GB. That's the shape of a hosted runner; Playwright runs 2 workers a
  leg, and `ci.yml` notes a third worker made WebKit slower on 4 CPUs.
  - All runners in one Linux VM share its 12 vCPU, so 3 legs at once use what was allotted and no more.

## The two options

| | (A) WSL2 | (B) Nested Hyper-V Linux VM |
|---|---|---|
| Boundary from the host | A WSL2 distro runs in a utility VM, one per Windows user. The isolation that matters is config inside the distro (`/etc/wsl.conf`) and in that user's `.wslconfig`. | A full Hyper-V guest. Nothing is shared with the host unless it is configured. |
| Host files and credentials | Unreachable only while `[automount] enabled=false` (no `/mnt/c`) and `[interop] enabled=false` (no Windows programs) hold, plus `appendWindowsPath=false`. Root in the distro can undo them; the runner accounts have no sudo. | Unreachable: no shared folders, no Guest Services file copy, no enhanced session. Nothing to turn back on from inside. |
| Network to the host's own services | NAT mode: the host is the distro's gateway, so anything on the host listening on all interfaces is reachable unless the Hyper-V firewall for WSL (`firewall=true` in `.wslconfig`) and host rules block it. Mirrored mode would expose the host's localhost: never use it. | An internal NAT switch plus host firewall rules: the guest's subnet may reach the internet and nothing on the host or the fleet's network. |
| Separate account | A dedicated, non-admin Windows local account (`abci-wsl`) owns the distro, so its utility VM is not the fleet user's. Inside, one Linux account per runner, no sudo (as #3965). | One Linux account per runner, no sudo (as #3965). The VM runs under Hyper-V, not under any fleet user. |
| CPU and RAM caps | `.wslconfig` `[wsl2] processors=12`, `memory=24GB`, `swap=0`: a cap on the utility VM, read at its start, in the `abci-wsl` profile. | Fixed 12 vCPU, static 24 GB. Hyper-V processor weight 50 (host processes keep priority); optionally a 75% maximum, so a busy guest never takes all 12. |
| Staying up | WSL stops an idle utility VM, so it needs `systemd=true` and a scheduled task at boot that holds the distro running as `abci-wsl` (no logon). Fragile across WSL updates. | Automatic start action: always start. A guest is a normal service of the host. |
| Per-job wipe | #3965's job hooks, inside the distro. | #3965's job hooks, inside the guest. |
| Baked image | A distro tarball SRE builds once, imported with `wsl --import`. | An Ubuntu 24.04 cloud-image VHDX, configured once by cloud-init. A checkpoint of it is the rollback point. |
| Elevation | Admin: create the account, the firewall rules and the boot task. The WSL feature is already on (it is on the builder). | Admin (or Hyper-V Administrators): create the switch, the NAT, the firewall rules and the VM. Hyper-V is already on (it is on the builder). |
| Overhead | Small; not measured here. | Small for a nested guest; not measured here. Step 6 measures it against hosted runners before anything is routed. |

**On both:** the same labels (`self-hosted, linux, x64, ab-ci-linux`), the same guard variable
(`AB_CI_LINUX_SELF_HOSTED`), the same lint pairing a guard with its pool, and the same fork-PR controls. Nothing a fork
PR can start reaches them, and no secret is routed to them.

## The image: browsers baked in

Built once by SRE, root-owned, read-only to every runner account:
- Ubuntu 24.04, git, curl, ca-certificates, Node 22.
- Playwright **1.56.1**, the version `pwa/package-lock.json` pins today. Its system packages and its Chromium and
  WebKit go in `/opt/ms-playwright`, installed as root with `npx -y playwright@1.56.1 install --with-deps chromium webkit`.
  - WebKit is baked too: it costs nothing to hold, and it keeps item B reversible.
- Jobs set `PLAYWRIGHT_BROWSERS_PATH=/opt/ms-playwright` and skip `--with-deps`, since there is no sudo.
- On a Playwright bump, a job finds no matching browser. It downloads into `RUNNER_TEMP` and warns, until SRE rebuilds
  the image (#3965's rule).
- No Docker engine and no Docker socket.

## Setup (SRE; the owner only where elevation is needed)

For (B), on each host that passed the room rule:

1. **Read-only first:** confirm nested virtualisation and the room rule (both sections above). Stop on any doubt.
2. **Elevated, owner or SRE:**
   - create the internal switch `abci-linux` and a NAT for 172.31.250.0/28;
   - add host firewall rules: block the subnet from every host port, and allow it out to the internet only.
3. **Create the guest:**
   - Generation 2, Secure Boot with the Microsoft UEFI Certificate Authority template;
   - 12 vCPU, processor weight 50, static 24 GB, a 128 GB VHDX on the host's data disk (not the temporary disk);
   - the `abci-linux` switch;
   - Guest Services off, no shared folders, automatic start action: always start.
4. **Image:** boot the cloud image with SRE's cloud-init. It:
   - creates the runner accounts with no sudo;
   - installs the image packages and Playwright;
   - installs #3965's job hooks;
   - registers 3 runners per guest with `--disableupdate`, running SRE's install script with `-WhatIf` first and its
     fork-policy check before each runner.
   Then take a checkpoint (`baseline`).
5. **Not routed yet:** Engineering lands #3965's lint and cache rules and the `runs-on`, with the variable unset.
6. **Two runs by hand** on main with `use_ab_ci`. They must show:
   - Chromium legs on `ab-ci-linux`, and everything else hosted;
   - both runs green, the second on reused runners;
   - the host's own builds and agents unslowed: SRE compares the host counters with the busy-hour baseline.
7. **Capacity:** `quality/ab-ci-capacity.ps1 -Label ab-ci-linux` (#3878 plus the `-Label` change in #3965) must exit
   0 in a busy hour.
8. **The owner** sets `AB_CI_LINUX_SELF_HOSTED=true`. The check runs again in the next busy hour.

**Every step that needs admin, elevation or a reboot, for (B)** (the owner approved (B) on VM6/VM7, provided SRE's
read-only check passes first):

| Step | Who | Elevation / reboot |
|---|---|---|
| Read nested virtualisation, Hyper-V's feature state, the size and the room rule | SRE | read-only; the feature query needs an elevated shell |
| Enable Hyper-V if it is off (`Microsoft-Hyper-V-All`) | OWNER | admin, then a **reboot** of the host |
| Create the `abci-linux` switch, the NAT and the host firewall rules | OWNER | admin |
| Add SRE's account to Hyper-V Administrators | OWNER | admin |
| Create, start and checkpoint the guest | SRE | Hyper-V Administrators; no host admin |
| Root inside the guest (cloud-init: accounts, packages, hooks) | SRE | the guest's root, never the host's |
| A runner registration token | OWNER | repository admin (short-lived; SRE's script uses it once and holds no token) |
| Set `AB_CI_LINUX_SELF_HOSTED=true`, last | OWNER | repository admin |

For (A), the same order, with step 3 instead:
- a non-admin `abci-wsl` account;
- `wsl --import` of the baked tarball as that account;
- `/etc/wsl.conf` with automount, interop and `appendWindowsPath` off and `systemd=true`;
- `.wslconfig` with `processors=12`, `memory=24GB`, `swap=0`, `firewall=true` and NAT networking;
- a boot task that keeps the distro running.

## Rollback

- **At once, no machine touched:** delete `AB_CI_LINUX_SELF_HOSTED`. The next runs take hosted `ubuntu-24.04`.
- **The pool:** SRE's script `-Remove` unregisters the runners.
- **(B):** stop and remove the guest, its VHDX, the switch, the NAT and the firewall rules. The host is as before; no
  Windows feature needs to be turned off. A bad image is reverted to the `baseline` checkpoint.
- **(A):** `wsl --unregister` the distro as `abci-wsl`, then remove the boot task, the firewall rules and the account.

## What neither option protects (for the owner to accept)

- These runners share a physical host with the fleet's sessions.
  - (B) puts a hypervisor between them; (A) puts a config file and a firewall.
  - Either way, a guest kernel or hypervisor escape is the residual risk, and it is out of scope for a design.
  - Dedicated VMs (#3965) remove it, at the cost the owner declined for now.
- Runners persist, so the per-job wipe is hygiene, not isolation (#3965). The controls that matter don't change: the
  fork-PR approval setting, the guard and its lint, no secrets here, and the read-only image.
- A busy Linux pool still costs the host. The caps bound it at 12 of 32 vCPU, and the room rule and the run-by-hand
  check (step 6) must show the host's own work unslowed before the owner turns it on.

## Open items for the owner

- **Approve (B)**, or (A) only if SRE reports Hyper-V unusable on VM6 or VM7.
- **The order:** item B lands first (it decides the 19-minute load above). This goes after it.
- **No new Azure cost**, but 24 of the 64 vCPU across VM6 and VM7 go to Linux at peak. If the hosts later need them
  back, the rollback is the variable.
