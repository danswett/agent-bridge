# Contributor workflow

This is the durable reference for claiming work, communicating and handing it off.
Start with [CONTRIBUTING.md](../CONTRIBUTING.md).
[AGENTS.md](../AGENTS.md) owns safety, Git, validation and merge requirements;
this page does not create exceptions to them.
Current role holders and their acceptance links live in
[status: running work](status.md#running-work), not in this procedure.

## Operational sources of truth

| Record | Owns |
|---|---|
| Canonical issue | Scope, acceptance criteria, priority, accepted implementation owner, dependencies and next action |
| Implementation PR | Exact head, CI, actual review, finding dispositions, unresolved threads and readiness evidence for Quality |
| GitHub Project views, once configured | Filterable indexes over those issues and PRs; not a second plan or evidence of acceptance |
| [#170 root/index](https://github.com/danswett/agent-bridge/issues/170), after verified cutover | Current roles, operating rules and links; not a repeated whole-project inventory or per-PR queue |
| Repository docs and approved designs | Durable architecture, roadmap and decisions; not a minute-by-minute activity log |

[Product, #696](https://github.com/danswett/agent-bridge/issues/696), owns the order
of work; triage records its priority decisions on canonical issues.
Keep cross-track dependencies on their canonical issues. Put readiness and
merge/rebase requests on the PR for Quality. Public issue and PR updates work
without private Project access.

## Claiming work

Before editing, read the canonical issue, recent comments, dependencies and linked
open PRs. Record the contributor or agent accepting a bounded scope and the next
deliverable there. If a scope has an accepted owner already, obtain their explicit
agreement to a split or handoff; do not take over their branch, delegates or worktree.
A proposal to change priority goes to Product, not a unilateral relabel.

A suggested owner is **unclaimed** until explicitly accepted. Link the acceptance
receipt; preserve existing delegates and contributor branch ownership. A shared
GitHub account, an unverified checkbox, silence or an old ready packet is not a new
acceptance. If the next action requires owner input, state the precise decision;
do not relabel an already-approved requirement as blocked on design.

An acceptance can be a short issue comment:

```text
I accept <bounded scope> on #<issue>; existing owner agreement: <link, if needed>.
Contributor / agent session: <identity>. Next deliverable: <result or PR>.
Dependencies and exclusions: <links and limits>.
```

Acceptance records responsibility; it does not grant machine access, change
priority or confer review or merge authority.

## Participation

**Ordinary contributors and their agents** follow the issue/PR workflow and leave
material updates and handoffs. They do not automatically become standing team
coordinators. Do not start a periodic GitHub poller, collector, reporting timer or
worker fleet merely because those exist in the project's internal coordination.
A human contributor has no imposed cadence outside an explicitly accepted work window.

**Delegates** accept a bounded assignment from a contributor or coordinator. Keep
the canonical issue, exclusions, intended branch/worktree, validation and next
recipient explicit. Use AGENTS.md's delegated PR and landing procedure.
Delegation does not transfer the contributor's permissions to the agent.

**Standing agent coordinators** explicitly accept a role and the
[Shiproom protocol](#running-shiproom); do not infer enrollment from a repository
visit or a coding assignment. Existing active coordinators retain their accepted
obligations until an explicit handoff or stand-down.

## Readiness and review evidence

Use [AGENTS.md's current merge requirements](../AGENTS.md#pull-requests). Copilot
and Codex reviews are optional extras and never required; the independent-review
receipts are the gate. This page does not
maintain a second merge checklist. A ready packet is not a merge grant.

Every head change needs fresh head-specific evidence under those requirements;
neither old CI nor an old receipt is inherited. Only an **independent**
review may use a bounded delta: the previously reviewed commit must be a proved
ancestor of the new head, and the independent reviewer must explicitly clear that
delta at the new head. A rebase without that ancestry needs a full independent
review. Safety, trust, security and required checks remain gates.
Quality's [operating receipt](https://github.com/danswett/agent-bridge/issues/170#issuecomment-6617409924)
allows ready PRs while main CI is pending; AGENTS.md defines the main-red gate and
the coordinator-controlled fix-forward exception.

On the PR, report:

```text
#<PR> ready at <full head SHA>.
Canonical issue and accepted scope: <links>.
Verification and exact-head reviews: <commands/results and links>.
Open findings, dependencies or blockers: <none, or details and needed responder>.
Next action requested from Quality: <action>.
```

## Design notes: nothing tied to a machine

Owner rule (2026-11-09): nothing in Slipgates may be tied to a specific machine. Machines
join and leave the fleet and the whole system must keep working. A component is either
distributed, so that one live node is enough, or keeps its state in the relay service,
protected, and not on a machine. No component names a particular VM as its home.

Every design note, and every PR that adds or moves a long-running component (a watcher,
scheduler, reader, cache, or state or handover file), answers three questions, and the
reviewer checks them:

1. Where does it run?
2. What happens when that node leaves?
3. Where does its state live?

A note that cannot answer all three is not ready. A component already tied to a machine
needs a migration plan, not a silent exception.

## Resolving bot-only threads

GitHub's ruleset needs 0 unresolved threads, so threads left only by the Copilot or
Codex reviewer bots would block a merge. Once the PR has its receipts on the head,
`pwsh quality/resolve-bot-threads.ps1 -Pr <n>` lists the threads whose every comment
came from those bots (a dry run); add `-Apply` to resolve them with GraphQL
`resolveReviewThread`. It refuses without the receipts, and never touches a thread a
person commented in.

## Handoffs

Before ending a session, pausing work or changing owners, update the canonical
issue and link the PR's current evidence. Do not leave critical state only in a
transcript, personal memory or private Project field.

```text
Scope and canonical issue: <link and remaining acceptance criteria>.
Outgoing contributor / agent session: <identity>.
PR, branch and full head SHA: <values, or no branch/PR yet>.
Owned worktree: <repository-relative location, or private location shared directly>.
Completed and verified: <results and exact-head evidence links>.
Remaining work: <specific next action>.
Blockers / needed responder: <details, or none>.
Incoming owner: <identity and explicit acceptance link, or unclaimed>.
```

Do not publish private filesystem details or secrets. Distinguish committed work
from uncommitted changes. A sent request is not acceptance. Until the incoming
owner accepts, describe the work as awaiting handoff; do not claim transferred
responsibility or continued background coverage. State whether any owned
background work remains and who accepted responsibility for it.

Archive old coordination snapshots with their cutoffs, keep their history and
links, and explicitly supersede old authority instructions during a handoff.
Do not remove the old tracker until all active rows have a verified destination;
a Project-permission failure is not an empty queue or a completed migration.

## Commands and code for the owner

In any question or message to the owner, put every command or code line in its own
fenced block (three backticks, one block per command) so the PWA card shows a Copy
button. The Windows app shows the same Copy button on fenced blocks. Inline
backticks and plain paragraphs get no Copy button, and nothing detects commands
automatically.

````text
```powershell
$h = "$env:LOCALAPPDATA\AgentBridgeApp\current\agent-bridge-host.exe"; & $h rollback
```
````

The owner often reads on a phone, and encoded commands don't wrap, so avoid them.

Approvals (permission prompts) are drawn as written and never get a Copy button, by
design. Put commands for the owner in a question, not in an approval.

## Conflicting or unavailable information

Historical notes explain past decisions; they do not establish today's head,
owner or permission. A private Project is an index, not a prerequisite for making
a public contribution. Missing access or failed/partial reads mean **unknown**,
not consent, idle work, an empty queue or a passed gate.

For a material conflict, stop the affected action and link both records on the
canonical issue or PR. Ask the named authority: implementation owner for scope,
TPM/triage for routing, Product for ordering, Quality for landing, or the owner for
permissions and material design decisions. Preserve the blocker and needed
responder. Unrelated accepted work need not stop. Do not select whichever account
of the rules makes the action easier or treat a newer informal comment as a
waiver of enforced requirements.

## Running Shiproom

The [public Shiproom hub](https://github.com/danswett/agent-bridge/issues/689)
links coordinator check-ins and blocker acknowledgments to the existing
[private Project](https://github.com/users/danswett/projects/1).
Shiproom owns coordination, Project health, deadlines and escalation only.
TPM retains issue triage, deduplication and the single collector; implementation,
design and Quality retain their existing authorities. Authors may still arm
their own exact-head auto-merge under AGENTS.md. Shiproom is not a merge grant,
a second implementation coordinator or completion of the #170 index cutover.

Active agent coordinators explicitly acknowledge the protocol and post a first
check-in before adoption is recorded. A coordinator acknowledges a delivered blocker
or ACK request within 5 minutes. Each check-in names lane and current session,
its permalink, the reused recurring mechanism, canonical current work, actual
blockers (or none), named needed responder, next deliverable and next due time in
Pacific. Keep one standing roll-up per lane, linking canonical issues and PRs rather
than copying the backlog. Missing owners remain pending until acceptance is proved.
Keep contributor branches and delegates with their existing owners.
Check-ins are plain English, at most eight lines; do not post content-free ACKs.
An acknowledgment required for ownership or a blocker must explicitly address it.

| Interval | Shiproom action |
|---|---|
| 5 minutes after a delivered blocker/ACK request | Obtain an explicit acknowledgment; escalate a missed deadline once |
| 15 minutes after the last explicit check-in | Coordinator check-in due |
| 20 minutes after the last explicit check-in | Mark the heartbeat stale |
| 30 minutes after the last explicit check-in | Escalate to the named responsible coordinator/TPM |
| Immediately | Escalate P0 or unsafe routing to the actual responsible authority |

Preserve the actual blocked reason and needed responder. A sent message, generic
issue `updatedAt`, mirror write or unchanged activity response is not a check-in or
acceptance. Project `Heartbeat receipt`, `Next due PT`, `Needs from` and `Ack state`
fields index this evidence; `Accepted owner` and `Owner receipt` require actual acceptance.
An inactive optional HA or Apple lane does not block unrelated native delivery.

The minute collector retains the full event journal, including failed/partial reads
as unknown. The consumer reconciles it into the canonical records and publishes
**material changes**, not repeated inventory dumps.
The dedicated Shiproom session runs a narrow one-minute SLA tick with its own
persisted control state, consuming only new retained collector events from its
activation cursor. Reuse an appropriate coordinator timer instead of duplicating
one. Inspect control/source health before lengthy research. Session-local timers
do not survive a stopped CLI or reboot: report gaps and restore coverage explicitly.
Source outages mean unknown visibility, not idle work or missed consent.
