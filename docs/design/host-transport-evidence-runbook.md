# Host transport evidence run: the runbook

Part of #3924. Docs only. This is the run plan for the launch-flip evidence run designed in
[host-transport-evidence.md](host-transport-evidence.md) (what runs, the pass bar, the receipt) and the
cutover rows in [fleet-grants.md](fleet-grants.md). The owner approved the run (owner decision relayed by the lead, about 08:35 PT):
it runs **after the G9 PRs land**, on disposable hosts SRE picks, with the owner issuing the launch grant and approving the induced
restart and reconnect **at run time**. This document grants none of those approvals and changes no host, trust, credential, key or
permission. No machine name appears here or in any record the run keeps.

## Roles: every step names who presses

- **SRE** runs the evidence, presses every control the run needs that is not listed under OWNER, and keeps the raw records.
- **Deploys are SRE-approved** (owner decision, about 08:27 PT): the owner is never asked to approve a deploy.
- **OWNER** is live for exactly two things: **the launch grant (S1)** and **each induced host restart or relay reconnect (S8)**, plus
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
| Restart a disposable host (S8, S5 if needed, the warm-up restarts) | OWNER, from that host's own control | The owner, per event, before it happens |
| Drop and restore a disposable host's relay connection (S5, S8) | SRE, with the owner's approval of the event | The owner, per event, before it happens |
| Start and stop the sessions the run itself launches | SRE, by list, through the existing end path | Already covered by the grant and the plan |
| Deploy a build to a disposable host | SRE, approved by SRE | The gates (Quality merge, staging and smoke green, health check); never the owner |

Nothing else: no trust change, no key or credential change, no ACL, no settings change, no new account, no change to the owner's real
Home Assistant (the comparison runs on a disposable HA only).

## Preconditions (SRE checks, records each)

1. The G9 stack is merged on main and every disposable host runs a build that contains it. Record each host's build head.
2. **A disposable host is a fleet VM with no live sessions.** SRE picks at least 3, and 3 source-target pairs, each run in both directions.
   A host with any live session that is not the run's own is not disposable.
3. A **disposable HA instance** exists for the comparison; the real one is never used.
4. The launch grant is person-issued at S1; no script or builder creates, widens or reuses one.
5. **The run plan is frozen before request 1** and attached to the receipt: N = 50 consecutive compared launches inside at least 100 pairs,
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

## Time box

The whole run, S2 to S11, completes within **24 hours** of S2 starting, and no single step takes longer than the limit the frozen plan
sets for it. Running over, on either limit, ends the run INCONCLUSIVE; a repeat is a new run from S0. The window floor (6 hours across at
least 4 distinct hours) sits inside the time box.

## Steps

Each step records its raw outputs (command output, receipts, session lists, counter reads, host logs) into the evidence folder with a
sha256, and the load readings before and after.

| Step | Who | What | Evidence recorded |
|---|---|---|---|
| **S-1** | **SRE approves and deploys** | **Deploy** the G9 build to each disposable host. Deploys never need the owner (owner decision, about 08:27 PT): SRE approves each one after the gates pass, namely the Quality merge, staging and smoke green, and a health check, with rollback by the deploy runbook | Each host's build head, the gate results, the health check, and the approving SRE |
| **S0** | SRE | Freeze the run plan (Preconditions 5) and take the baseline: build heads, session lists on every host (empty), the receiver counters, the HA fallback counter, load | The plan, the baseline reads |
| **S1** | **OWNER** (SRE asks) | The owner issues and confirms the launch grant for the run in the app, after seeing its scope and caps | The grant id, scope and caps (no secret) |
| **S2** | SRE | **Smoke:** one Host launch and its HA twin on one pair. Stop if it does not start and register exactly once | Both receipts, the three double-launch counts |
| **S3** | SRE | **Main pairs:** at least 100 compared pairs in batches no larger than the cap, each batch ended by list before the next; the 50 consecutive count is inside this | Per pair: both receipts and exit codes, `sessions.list`, `launches list`, the counter rise |
| **S4** | SRE | **Host-only refusals:** 5 of each Host-only case, 3 for the warm-up case (the warm-up needs a restart: S8) | Per case: the receipt, the refusal code, the unchanged counters |
| **S5** | SRE (the owner only if a fault needs a restart) | **Lost response x3:** SRE drops the target's relay connection after the send on the Host side, and cuts the response read after the press on the HA side; each is resolved with `launches status` | The fault time, the receipt, the resolving read |
| **S6** | SRE | **Concurrent bursts x3** inside the cap | The invariants: sessions equal accepted requests, one session per request, counter rise equal, no double |
| **S7** | SRE | **Long prompts x5**, one near 60 KB | Receipts, and that the prompt text is not kept in any record |
| **S8** | **OWNER** presses each restart; SRE drops and restores the relay link | **Window:** at least 6 hours across at least 4 distinct hours with one induced host restart and one relay reconnect, and the 3 warm-up restarts. The owner approves each event before it happens | Each event, its time, who pressed, the approval, and the receipts around it |
| **S9** | SRE | **Late second session:** after each launch, wait 5 minutes and confirm no second session for that request | The late reads |
| **S10** | SRE | **End-by-list sweep:** end whatever the plan's names still show live, through the existing end path, never a raw kill; confirm none of the plan's names remains on any disposable host | The sweep results and the final session lists |
| **S11** | SRE, then Quality | Assemble the receipt (the frozen plan, actual counts and denominators per floor, every record with its sha256, the evidence path, failed and INCONCLUSIVE runs kept); Quality attests against the bars | The receipt |

## Stop conditions

Any of these **stops the run at once**, fails or marks it INCONCLUSIVE as stated in the pass bar, and is never retried with a fresh id or
excused as load:

- a double launch, or any new session on a target in a request's window that the request does not explain;
- any HA fallback press on a Host-path request (the HA fallback counter must stay zero);
- any mismatch between a Host request and its HA twin;
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
