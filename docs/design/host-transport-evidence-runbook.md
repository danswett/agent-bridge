# Host transport evidence run: the runbook

Part of #3924. Docs only. This is the run plan for the launch-flip evidence run designed in
[host-transport-evidence.md](host-transport-evidence.md) (what runs, the pass bar, the receipt) and the
cutover rows in [fleet-grants.md](fleet-grants.md). The owner approved the run (owner decision relayed by the lead, about 08:35 PT):
it runs **after the G9 PRs land**, on disposable hosts SRE picks, with the owner issuing the launch grant and approving the induced
restart and reconnect **at run time**. This document grants none of those approvals and changes no host, trust, credential, key or
permission. No machine name appears here or in any record the run keeps.

## Roles: every step names who presses

- **SRE** runs the evidence, presses every control the run needs that is not listed under OWNER, and keeps the raw records.
- **Deploys are SRE-approved** (owner decision, about 08:27 PT): the owner is never asked to approve a deploy. But the host app update installs
  through the app, and **the restart that finishes it is OWNER-only** (guard 67): the owner presses that restart on each disposable host.
- **OWNER** is live for exactly these: **the launch grant (S1)**, **the restart that finishes each deploy (S-1)**, **each induced host restart (S8) and the host restart that ends the established relay connection in every relay-drop event (S5, S8)**, plus
  a restart if a lost-response fault needs one (S5). The owner types nothing and runs no terminal step: they use the host's own person
  surface (the app) when SRE asks.
- **Quality** attests the receipt against the bars; **the lead** approves the flip from the attested receipt.
- A builder who wrote the code never witnesses the run.

## Approvals: the only changes this run may make

No host, trust, credential, key, ACL or permission change is made unless it is in this table with the owner's explicit approval at run
time. Anything else is out of bounds and stops the run.

| Action | Who presses | Approved by |
|---|---|---|
| Issue and confirm the launch grant for the run | OWNER, in the app | The owner, at S1, after seeing scope and caps |
| Restart a disposable host (the restart that finishes each deploy at S-1, S8, the warm-up restarts, and the restart that ends the established relay connection in every relay drop at S5 and S8) | OWNER, from that host's own control | The owner, per event, before it happens |
| Drop the **drop host's** relay connection, then restore it (S5, S8; the one host only) | The **elevated relay-drop session** (see below), at SRE's request, running exactly the command block below per event (arm the dead-man, drop, restore, disarm) and nothing else. Drop (**outbound only**, scoped to the AgentBridge host program **and** the relay addresses on TCP 443): `New-NetFirewallRule -DisplayName 'SG-relay-drop-<event id>' -Group 'SG-relay-drop' -Direction Outbound -Action Block -Program '<AgentBridge host exe path from the frozen plan>' -Protocol TCP -RemotePort 443 -RemoteAddress <relay addresses read for this event>`. Restore, one command: `Remove-NetFirewallRule -Group 'SG-relay-drop'` | The owner, per event, before it happens. A firewall rule is a host change, so it is these commands only: never an inbound rule, never a profile-wide or default-action change, never a process kill or a service stop |
| Cut the response read to a disposable HA (S5) | **There is no disposable HA and the HA comparison is waived by the owner**, so the HA-side lost-response twin is not run and nothing is cut | n/a; the receipt says "no HA comparison" |
| Start and stop the sessions the run itself launches | SRE, by list, through the existing end path | Already covered by the grant and the plan |
| Deploy a build to a disposable host | SRE, approved by SRE | The gates (Quality merge, staging and smoke green, health check); never the owner |

Nothing else: no trust change, no key or credential change, no ACL, no settings change, no new account, no change to the owner's real Home Assistant (there is no disposable HA: the HA-fallback checks are NOT COVERED and the HA comparison is waived by the owner).

### The elevated relay-drop session

The firewall commands need elevation, so the run uses **one elevated session, for this run only**, and it runs **on one host only**.

- **ALL relay-drop events run on ONE target host, the drop host** (SRE ruling). The frozen plan names it (one of the three disposable hosts, never in this
  repository), and the elevated session is started on that host and nowhere else: **no remoting, one approval**. The lost-response events (S5) use pairs
  whose **target is the drop host**, so the reply is the one that is lost; the induced relay reconnect and its restarts (S8) are on it too. The other two hosts
  take part in launches and the owner's plain restarts (the warm-up restarts and the deploy restarts), which need no elevation. The relay-drop coverage is
  therefore **one host, not three**, and the receipt says so. If the run proves to need drops on several hosts, that is a **new owner decision** (remoting, or one
  session per host); it is not done here.
- **Its own approval.** It is launched later as its **own owner approval through the Chief**, not as part of any other approval here. This runbook does not
  launch it and the run does not start until it exists.
- **Exactly what it runs.** Per induced event, after the owner approves that event, it runs the command block below (arm the dead-man, drop, scope check, restore, disarm), in order, with the
  event's id in the rule name, and **nothing else**: no other rule, no other command, no process kill, no service stop, no change to
  any setting, trust, credential, key or ACL.
- **Owner's OK (about 09:20 PT).** The owner presses **Restart host** in the app right after each relay-drop block is in place; that restart is what ends the
  established relay connection. **No relay-link restart command is added**: none is supported, and this run does not create one.
- **RDP safety (owner ask).** The block rule is **outbound only**, scoped to the AgentBridge host program path **and** the relay remote addresses on TCP 443,
  so a remote-desktop connection (inbound) is never touched; it is **never** an inbound rule and **never** a profile-wide or default-action change. Every
  rule is in **one named group, `SG-relay-drop`**, so removal is one command: `Remove-NetFirewallRule -Group 'SG-relay-drop'`.
- **Ended after the run.** SRE ends the session as soon as the last event is restored, and in any case at S11. SRE then confirms by a read, not through
  that session, that the group `SG-relay-drop` has no rule left on any disposable host (`Get-NetFirewallRule -Group 'SG-relay-drop'` returns none), and records the end and that read.
- **Per-event record.** Each drop and restore is recorded with its event id, time, host and the owner's approval; the receipt carries no relay address.

#### The final command block (what the owner sees)

The values come from the frozen plan, except the address list: **the address list** is the set of remote addresses the AgentBridge host process is connected to on TCP 443, read at S0 **and re-read for every event, immediately before step 1**, with that event's own read used for its rule and recorded with the event (the relay's addresses can change between events),
read by SRE (a read, no elevation: `Get-NetTCPConnection -OwningProcess <host process id> -RemotePort 443 | Select-Object -ExpandProperty RemoteAddress -Unique`);
**the dead-man N = 20 minutes**, long enough that it cannot fire before the owner's restart and the proofs are done; **the dead-man task name is `SG-relay-drop-revert`**. Nothing else is in the block.

Pre-check, reads only, recorded before the first event on each host, and the RDP access path (inbound 3389 or Azure Bastion) confirmed up:

```powershell
Get-NetFirewallProfile | Select-Object Name, DefaultInboundAction, DefaultOutboundAction          # recorded; must be unchanged after every event
Get-NetFirewallRule -Direction Inbound -Enabled True | Where-Object { ($_ | Get-NetFirewallPortFilter).LocalPort -eq 3389 }   # RDP inbound rules, recorded
```

Per event, in this order, by the elevated session, after the owner approves that event:

```powershell
# 0. Re-read the relay addresses for THIS event (read, before anything else; use this event's own result in step 2, and record it):Get-NetTCPConnection -OwningProcess <host process id> -RemotePort 443 -State Established | Select-Object -ExpandProperty RemoteAddress -Unique
# 1. Arm the dead-man FIRST: it deletes the whole group after N minutes whatever happens to the session.
$act = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -Command Remove-NetFirewallRule -Group 'SG-relay-drop'"
Register-ScheduledTask -TaskName 'SG-relay-drop-revert' -Action $act -Trigger (New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(20)) -User 'SYSTEM' -RunLevel Highest
# 2. Drop: outbound only, host program AND the relay addresses on TCP 443, in the one group.
New-NetFirewallRule -DisplayName 'SG-relay-drop-<event id>' -Group 'SG-relay-drop' -Direction Outbound -Action Block -Program '<AgentBridge host exe path from the frozen plan>' -Protocol TCP -RemotePort 443 -RemoteAddress <relay addresses read for this event>
# 3. Scope check (read): must show only Outbound, the host program, TCP 443, and no 3389.
Get-NetFirewallRule -Group 'SG-relay-drop' | ForEach-Object { $_.Direction; ($_ | Get-NetFirewallApplicationFilter).Program; ($_ | Get-NetFirewallPortFilter) | Select-Object Protocol, LocalPort, RemotePort }
# 3b. END the existing relay connection (the owner pressing Restart host right after each block was approved by the owner, about 09:20 PT). A block rule is checked when a connection is SET UP, so it does not cut a socket that is already established.
#     No host command restarts only the relay link (the host CLI has: stop, which ends a session, grants, fleet, launches, sessions, audit, approve, devices,
#     host-sign, roster-pin, ha and serve), so the OWNER presses the host restart in the app (person-only, guard 67). The restarted host must set up a NEW
#     connection, and the rule blocks it. Without this press the drop does not happen and the step is not runnable.
# 3c. PROOF the drop happened (read, after the restart, new host process id): no Established connection from the host to a relay address. It must return
#     nothing; if it returns a row, the drop did not happen: restore (step 4) and void the event.
Get-NetTCPConnection -OwningProcess <new host process id> -RemotePort 443 -State Established | Where-Object { $_.RemoteAddress -in '<relay addresses read for this event>' }
# 4. Restore, one command:
Remove-NetFirewallRule -Group 'SG-relay-drop' -ErrorAction SilentlyContinue    # tolerant: the dead-man may already have deleted the group
# 4b. PROOF of the reconnect (read): the host sets up a new connection by itself (it reconnects with backoff); the same read as 3c now returns a relay address.
# 5. Disarm the dead-man:
Unregister-ScheduledTask -TaskName 'SG-relay-drop-revert' -Confirm:$false -ErrorAction SilentlyContinue    # tolerant: the dead-man task may be gone already
# 5b. READ that both are gone (each must return nothing):
Get-ScheduledTask -TaskName 'SG-relay-drop-revert' -ErrorAction SilentlyContinue
Get-NetFirewallRule -Group 'SG-relay-drop' -ErrorAction SilentlyContinue
```

**RDP is unaffected** because the rule is outbound only and scoped to the host program and the relay addresses: nothing inbound and no other program is
blocked, and the profile defaults are untouched. SRE re-reads the profile defaults and the RDP inbound rules after every event and they must match the
pre-check, otherwise the run stops. **The exact rollback**, if anything looks wrong at any point, is the same single command
`Remove-NetFirewallRule -Group 'SG-relay-drop'`, then `Unregister-ScheduledTask -TaskName 'SG-relay-drop-revert' -Confirm:$false`; even if the session is lost,
the dead-man task runs that removal after N minutes.

## Preconditions (SRE checks, records each)

1. The G9 stack is merged on main and every disposable host runs a build that contains it, deployed at S-1 (SRE-approved, never the owner). Record each host's build head.
2. **Hosts (owner answer, about 09:00 PT).** **A disposable host is a fleet VM with no live sessions.** The run uses **three hosts the owner approved
   draining**: two named by the owner and one more, drained **at a quiet hour at run time**. SRE proposes the hour and the owner confirms it through the Chief
   on the day; draining stops live sessions, so nothing is drained before that confirmation. The lead who owns a host's sessions ends them (by list) first. The
   host names are written only in the frozen plan, kept off the repository, never in this runbook or any record the receipt carries. The 3 source-target
   pairs run in both directions. SRE verifies each host shows no live session that is not the run's own; a host with any other live session is not disposable.
3. **No disposable HA (owner answer, about 09:00 PT): the HA-fallback checks are SKIPPED and are NOT COVERED.** Pass bar item 4, the HA fallback counter
   reads in S0 to S11, and the stop condition on an HA fallback cannot be observed without one. The evidence and the receipt say **NOT COVERED** for each,
   in those words, so the lead's approval of the flip reads it. Nothing is improvised and the real Home Assistant is never used. **The HA twin comparison is
   WAIVED by the owner (about 09:10 PT).** With no HA there is nothing to compare against, so the run proves the **Slipgates launch path on its own bars** and
   claims no parity with HA: the receipt says **"no HA comparison"**, makes **no parity claim**, and no compared pairs, no 100-pair count and no mismatch bar are claimed. **Quality R9 ruled this reduced bar** (HA comparison waived by the owner): the run is judged on the Slipgates launch path alone and makes no parity claim.
4. The launch grant is person-issued at S1; no script or builder creates, widens or reuses one.
5. **The run plan is frozen before request 1** (it names the drop host) and attached to the receipt: **50 consecutive Host launches** (the HA comparison is waived: no "compared" launches and no pairs),
   every coverage floor, the live-session cap, the unique launch names (from the fixed `SG Builder <n>` / `SG Worker <n>` list), the batch
   sizes, the window, and the exact controls named per host. A number chosen or changed after a request is sent voids the run.
6. An evidence folder exists **off the repository**, with a sha256 recorded for every raw record, and no name, token, URL, path or
   machine name in anything the receipt carries.
7. Load baseline taken (see "Load rule").

## Load rule

Before **and after** every phase SRE records, per host: run queue per core and CPU. A phase starts only if the run queue is at most 1 per
core and CPU is under 50%. A reading above either limit before a phase means wait and re-read (the wait is recorded and counts against the
time box); a reading above either limit after a phase marks that phase's results INCONCLUSIVE and the phase repeats from its start. A load
reading that cannot be taken is INCONCLUSIVE, never a pass.

The numbers come from **each disposable VM's own counters**: CPU percent and run queue per core, sampled every **15 seconds**, never from the
real Home Assistant or any other fleet host. The samples (not just the summary) are kept in the evidence folder with a sha256.

## Time box

The whole run, S2 to S11, completes within **24 hours** of S2 starting, and no single step takes longer than the limit the frozen plan
sets for it. Running over, on either limit, ends the run INCONCLUSIVE; a repeat is a new run from S0. The window floor (6 hours across at
least 4 distinct hours) sits inside the time box.

**If the owner is unreachable for an approval, the run pauses.** Every owner step (the grant at S1, a deploy restart at S-1, each induced
restart or reconnect) waits for the owner. The wait counts against the time box. The run never proceeds without the approval and never skips the
step; if the wait carries it past the time box, the run ends INCONCLUSIVE.

## Steps

Each step records its raw outputs (command output, receipts, session lists, counter reads, host logs) into the evidence folder with a
sha256, and the load readings before and after.

| Step | Who | What | Evidence recorded |
|---|---|---|---|
| **S-1** | **SRE approves and installs; OWNER presses the restart** | **Deploy** the G9 build to each disposable host. Deploys never need the owner (owner decision, about 08:27 PT): SRE approves each one after the gates pass, namely the Quality merge, staging and smoke green, and a health check, with rollback by the deploy runbook. The update installs through the app; **the OWNER presses the restart** on each host, once, when SRE asks | Each host's build head, the owner's restart press and its time, the gate results, the health check, and the approving SRE |
| **S0** | SRE | Freeze the run plan (Preconditions 5) and take the baseline: build heads, session lists on every host (empty), the receiver counters, load (the HA fallback counter is NOT COVERED: no disposable HA) | The plan, the baseline reads |
| **S1** | **OWNER** (SRE asks) | The owner issues and confirms the launch grant for the run in the app, after seeing its scope and caps | The grant id, scope and caps (no secret) |
| **S2** | SRE | **Smoke:** one Host launch on one pair (no HA twin: the comparison is waived by the owner). Stop if it does not start and register exactly once | The receipt, the three double-launch counts |
| **S3** | SRE | **Main Host launches** (the HA comparison is waived by the owner, so these are Slipgates-path launches on their own bars, not compared pairs; no parity claim), in batches no larger than the cap, each batch ended by list before the next | Per launch: the receipt and exit code, `sessions.list`, `launches list`, the counter rise |
| **S4** | SRE | **Host-only refusals:** 5 of each Host-only case, 3 for the warm-up case (the warm-up needs a restart: S8) | Per case: the receipt, the refusal code, the unchanged counters |
| **S5** | SRE; **OWNER presses the host restart** that ends the established relay connection in each drop, on the drop host | **Lost response x3:** the elevated relay-drop session drops the target's relay connection after the send on the Host side, at SRE's request (no HA-side twin: the comparison is waived); each is resolved with `launches status` | The fault time, the receipt, the resolving read |
| **S6** | SRE | **Concurrent bursts x3** inside the cap | The invariants: sessions equal accepted requests, one session per request, counter rise equal, no double |
| **S7** | SRE | **Long prompts x5**, one near 60 KB | Receipts, and that the prompt text is not kept in any record |
| **S8** | **OWNER** presses each restart, including the one that ends the established relay connection in each drop; the elevated relay-drop session adds and removes the block at SRE's request | **Window:** at least 6 hours across at least 4 distinct hours with one induced host restart and one relay reconnect, and the 3 warm-up restarts. The owner approves each event before it happens | Each event, its time, who pressed, the approval, and the receipts around it |
| **S9** | SRE | **Late second session:** after each launch, wait 5 minutes and confirm no second session for that request | The late reads |
| **S10** | SRE | **End-by-list sweep:** end whatever the plan's names still show live, by the ends rule below (two idle reads, name check, end only, worktrees stay), never a raw kill; confirm none of the plan's names remains on any disposable host | The sweep results and the final session lists |
| **S11** | SRE, then Quality | Assemble the receipt (the frozen plan, actual counts and denominators per floor, every record with its sha256, the evidence path, failed and INCONCLUSIVE runs kept); Quality attests against the bars | The receipt |

## Launching and ending the run's sessions

- **Launches:** SRE launches **only the sessions the frozen plan names**, under the person-issued grant, and only with the **Chief's go** for
  the run. No other launch, name or target is used.
- **Ends use SRE's rule:** a session is ended only after **two idle reads**, with a **name check** that it is one of the plan's names, and the
  **end only**: its worktree stays. Never a raw kill, never a name outside the plan. This applies to the batch ends in S3 and the sweep in S10.
- **No prompt text in the evidence:** nothing from a host log that carries prompt text is kept; records keep ids, outcomes, counts, times and
  sha256 values only.

## Stop conditions

Any of these **stops the run at once**, fails or marks it INCONCLUSIVE as stated in the pass bar, and is never retried with a fresh id or
excused as load:

- a double launch, or any new session on a target in a request's window that the request does not explain;
- any HA fallback press on a Host-path request (NOT COVERED: no disposable HA, so this cannot be observed; the receipt says so);
- any launch whose outcome does not match what the receiver's own records show (there is no HA twin to compare: waived by the owner);
- a launch that lands on the wrong target;
- a person-guard bypass (any action that needed a person and happened without one);
- a counter, session list or load reading that cannot be read;
- a change outside the Approvals table, or any sign of a token, key, credential or prompt text in a kept record;
- the time box exceeded, or the live-session cap reached.

The lead decides which of these also triggers the rollback below; the receipt records which condition fired.

## The flip and the rollback: the exact commands

The flip is **not** part of this run. It is two separate small Fleet PRs (the launch default and the dispatcher switch), each citing the
attested receipt, and it happens only after the lead approves the receipt.

- **Flip, launch:** a launch that gives `-Grant` or `-TargetNode` and no `-Transport` goes to Host. A Host launch looks like
  `launch-remote.ps1 -Grant <grant id> -TargetNode <node id> -Name 'SG Worker <n>' -Workspace <label> -Prompt <text> -Permissions 'Allow all'`.
  A `-Machine` launch with no `-Grant`/`-TargetNode` keeps HA and logs an explicit-HA call. Plain `-End` and the HA-only modes stay on HA.
- **Flip, dispatcher:** set the dispatcher config `launchGrant` to the grant id and `hostTargets` per VM at run time; with no grant it is unchanged.
- **Rollback, per call:** add `-Transport HA` to the call, for example `launch-remote.ps1 -Transport HA -Machine <machine> -Name <name> -Prompt <text>`.
- **Rollback, dispatcher:** set the dispatcher config `launchTransport` to `HA`; the HA calls are still in the code.
- **Rollback, default:** revert the flip PR (a new PR that reverts its merge commit). No HA code is removed before E4, so a revert restores the
  previous behavior exactly.

An `unconfirmed` request is never resent by a rollback: it is resolved with `launches status --request <uuid> --json` first.

## Doc status

New file. `host-transport-evidence.md` and `fleet-grants.md` are unchanged: they own the pass bar, the receipt and the cutover order this
runbook follows.
