# PR event relay: one reader, messages to the owning session

Status: design only, no code until it is accepted. Interim owner: the #2568 delivery builder, at the
Chief's request (2026-11-13, 23:19 PT), while the Engineering Lead is unavailable. **Ask** marks a
point that needs a ruling.

Not the Slipgates relay service (`src/AgentBridge.Relay`): a small agent tool beside `wake-watch.ps1`
that only reads GitHub and sends session messages.

## Problem
Every builder and lead learns about its PRs by watching GitHub itself: `wake-watch.ps1` per
session, or ad hoc `gh` calls. About a dozen watchers on one shared account used up the 5,000 REST
calls an hour on 11-05, and the GraphQL budget ran out another afternoon. Builders are now told not
to poll at all, so they hear about a moved head, a red CI run or a merge only when a lead messages
them. Leads are the bottleneck, and a lead going quiet (as tonight) leaves builders blind.
Tonight's PR 2573 shows the cost: GitHub's "Update branch" moved its head to a commit nobody
admitted, the PR merged, and its builder found out hours later, from Quality.

## Goal
One process reads GitHub for the whole repository with conditional requests, turns what changed
into events, and sends each event to the session that owns it. Nobody else polls GitHub.

- Builders and leads never poll; they wait for messages, as the rules already say.
- An event reaches its owner within 2 minutes of GitHub showing it (target, measured in the soak).
- Steady state costs fewer than 300 counted REST calls an hour, on a budget of its own.
- No silent loss: every event the relay observes is delivered, or journaled as undeliverable and
  reported to a lead. **Limit:** the relay sees states, not every transition. Two head moves
  between reads arrive as one `head` event naming the last seen and the new SHA; a comment edited
  twice arrives at its latest text; a comment created and deleted between reads is never seen.
  Comments, reviews and check runs are complete (they are listed, not sampled).

## Shape
```
GitHub REST (ETag, 304 is free) -> reader -> outbox (durable) -> router -> sender -> session
                                     \-> cursors and ETags (saved after the outbox)  \-> undeliverable
```
One instance, run by SRE on a host it names, as a long-running PowerShell 7 script,
`tools/agents/pr-relay.ps1`, reusing `wake-watch.ps1`'s `Get-Api` (ETag cache) and state-file code.

## What it reads
All reads use `gh api -i` with `If-None-Match`; an authorized 304 does not count against the primary
rate limit. Every list is paged to the end (`per_page=100`, following `Link: next`); a list that
fails part way is retried whole and raises nothing.

| Read | Every | Gives |
|---|---|---|
| `pulls?state=all&sort=updated&direction=desc`, pages until `updated_at` is older than the cursor | 30 s | opened, head moved, draft or ready, labels, closed, merged |
| `issues/comments?since=<window>` | 30 s | comments on PRs, `[to:team]` tags, `[admit]` lines |
| `pulls/comments?since=<window>` | 60 s | review-thread comments |
| `pulls/<n>/reviews` | when the PR's `updated_at` changes | reviews submitted or dismissed |
| `commits/<head>/check-runs` and `commits/<head>/statuses` | 60 s for every open PR head, 5 min once all are finished | CI finished, re-runs, `merge-rule`, `independent-review`, `independent-review-2` |

`<window>` is the start of the current hour minus 10 minutes, so the URL, and its ETag, stays the
same for an hour; duplicates inside the window are removed by comment id plus `updated_at`. Checks
and statuses are read even after CI finished, because a re-run, a receipt or `merge-rule` can change
without the PR's `updated_at` changing. The relay honours `Retry-After` and secondary limits (backs
off 1, 2, 4 ... up to 15 minutes) and slows to half speed below 500 calls left.

**Budget.** A fine-grained token would still share its user's 5,000-an-hour budget, so it isolates
nothing. **Ask (owner):** a GitHub App installation with read-only contents, pull requests, issues,
checks, statuses and Actions on `danswett/agent-bridge`; an installation has its own budget, so the
relay cannot starve other sessions and they cannot starve it. Until then it runs on the shared
account and its health line shows what it spends.

## Events
Each event has an id that is stable across retries and restarts and distinct for each occurrence:

| Kind | Raised when | Id key |
|---|---|---|
| `head` | head SHA differs from the last seen (A to B to A counts twice) | per-PR transition number |
| `unadmitted` | a `head` whose SHA has no admission (see Admission) | as its `head` |
| `ci` | every non-gate check run on the head has finished, again after any re-run | head SHA plus the sorted check-run ids and attempts |
| `gate` | `merge-rule`, `independent-review` or `independent-review-2` changes state | status id |
| `review` | a review is submitted or dismissed | review id plus state |
| `comment`, `tag` | a new or edited comment; `tag` when it carries `[to:<team>]` or `[to:all]` | comment id plus `updated_at` |
| `ready`, `draft`, `opened`, `closed`, `merged`, `label` | state changes; `label` only for `hold`, `do-not-merge`, `security`, `fixes-main` | per-PR transition number |

Message: one line plus a link, for example `PR 2573 head moved 2ea1f1dd -> 77ee2a75 (merge of main)`,
`PR n CI on 77ee2a75: failed: build-x64, pwa`, `PR n merged as b11b4610 at 19:03 PT`, or a comment's
first 300 characters. Times are Pacific. Text carries only what GitHub already shows.

## Routing: who owns a PR
Recipients are resolved by session **name** when sending, never a stored node id (onboarding §1).

1. An `Owner-Session: <session name>` line in the PR body is authoritative. Builders add it when they
   open a PR; Quality adds it when it opens one for a builder. Only an edit of that line moves it.
2. With no such line, the first `Copilot-Session:` trailer on the PR's first commit is a hint,
   mapped to a name through the session roster. A later commit (another agent, "Update branch")
   never moves ownership. The hint and the name it resolved to are saved with the PR.
3. Otherwise the lane in the PR title or labels, mapped to that team's lead.
4. Otherwise the Quality Lead, with `owner unknown` in the message.

**Leads** subscribe in `tools/agents/pr-relay.teams.json` (team name, lead session name, kinds):

| Team | Receives |
|---|---|
| Quality | `head`, `unadmitted`, `ci`, `gate`, `label`, `merged`, `closed` on every non-draft PR; `[to:quality]` |
| Engineering | `ready`, and `head` on a PR labelled `security` or touching sensitive paths; `[to:engineering]` |
| Other leads | their `[to:<team>]` tags; PRs they own by rule 1 or 2 |
| Chief of Staff | undeliverable reports, relay health, and everything Engineering gets while it is away |

`[to:all]` goes to every live session in the roster, as onboarding §1 says. The owner receives every
event on its PR. All sessions comment from one GitHub account, so the relay cannot tell which session
wrote a comment; it delivers every comment, except ones the relay itself posted. Each recipient gets
an event at most once, even when two rules name it.

## Admission
Quality admits exact SHAs by message today, so the relay cannot see admissions. **Ask (Quality):**
record each admission on the PR as `[admit] <full sha>`. The relay then raises `unadmitted` when a
head has no admission, which would have caught PR 2573 at 18:25 PT, not 23:04. On start it reads the
open PRs' comments once to learn earlier admissions. **Limit:** every session posts from the same
account, so an `[admit]` line is not authenticated; `unadmitted` is a warning that helps Quality,
never a gate. Until Quality adopts the line, `unadmitted` is off and `head` goes to Quality.

## Sending and the outbox
Messages go through `tools/agents/reply-session.ps1` with the agent token, label `pr-relay`, one
event per message, never batched into one turn. Under #2568 they are agent messages: queueable,
never urgent, and the envelope adds sender, id and receipt time. The request id is derived from the
event id and recipient, so a retried send is the same request, and the text carries
`[pr-relay id=<event id>]` so a receiver can ignore a repeat it already saw.

- **Outbox first.** Every event is written to the outbox, with its message text and recipients,
  before the cursor or ETag that produced it is saved. A crash between the two re-reads the same
  data, and the outbox removes the duplicates by event id.
- **Outcome per recipient:** `queued` (the #2568 ack, not durable), then `delivered`, `dropped` or
  `unconfirmed`. A `queued` item stays in the outbox until #2568 confirms it by id. A by-id dropped
  or unconfirmed notice re-sends it once, with the same id. Until #2568 slice 4 lands confirmation,
  `queued` counts as sent after 30 minutes, and the journal marks it `unconfirmed`.
- **Order:** per recipient and PR, in the order observed; different PRs do not wait for each other.
- **Rate:** at most 6 messages a minute to one session. Extra events wait in the outbox, in order;
  nothing is summarised away.
- **Retry:** an HA error or a busy refusal retries at 1, 2 and 5 minutes. Then the event is
  `undeliverable`: the PR's team lead gets it once, and if that fails too, the Chief of Staff gets
  one line. A session not in the roster is not retried; its events go to rule 3 or 4.
- Once an event is delivered, its text is removed from the outbox. The journal keeps event id, kind,
  PR, recipient, outcome and time, and never a comment body.

## State, restart and health
- **State** (under `~/.copilot/pr-relay/`): outbox, journal, cursors, ETags, last head, transition
  number and resolved owner for each PR, and admissions seen. Each file is written atomically.
- **Restart:** sends what the outbox still holds, then reads from the saved cursors. If state is
  lost, it re-reads the last 24 hours, raises only events whose journal ids are absent, and tells
  Quality it did.
- **One instance:** a lock file on the host stops a second copy there. On start it sends
  `pr-relay started on <host>` to Quality, so a copy started elsewhere shows up at once.
- **Health:** every minute the relay writes a heartbeat with the last good read of each feed. A feed
  with no good read for 10 minutes sends one line to the Chief of Staff (and one on recovery).
- **Death:** a relay that has crashed cannot report itself. **Ask (SRE):** a watchdog on a second
  host that reads the heartbeat every 5 minutes and, if it is older than 10 minutes, messages the
  Chief of Staff once. Alarms never page the owner.

## What changes for sessions
- Builders: no watcher and no GitHub reads to learn about their PR. Put `Owner-Session:` in every
  PR body. Act on relay messages as they arrive.
- Leads: stop `wake-watch.ps1` for PR events once the soak passes; restart it only when the relay's
  health line says the relay is down. `docs/agents/onboarding.md` §5 changes in the last slice.

## Tests (hosted CI, a fake `gh` replaying recorded responses; no network)
1. 304 on every read raises no event and counts no call.
2. Head move, CI finish with two failures, merge: three events, in order, one message each.
3. A head moved A to B to A, and a CI re-run on the same SHA: each raises a new event.
4. Comments across two pages, one edited later: each revision raised once, none repeated at rollover.
5. Crash after the outbox write and before the cursor save, then restart: each event sent once.
6. A read failing on page 2 (500, rate limit, bad body) raises nothing, saves no cursor, backs off.
7. Routing: `Owner-Session:` wins; an "Update branch" commit or another agent's commit does not
   move ownership; a gone session falls to the lead; no owner goes to Quality as `owner unknown`.
8. 20 events on one PR in a minute: all 20 sent, 6 a minute, in order.
9. `queued` then a by-id dropped notice: one re-send, same id. Three failed sends: one lead message;
   lead failing too: one Chief line.
10. `unadmitted` for a head without `[admit]`; none for an admitted head; earlier admissions learned
    on start.
11. A feed failing for 10 minutes sends one Chief line; the journal never holds a comment body.

## Live soak (SRE, disposable host, 24 hours, beside the current watchers)
Counted calls an hour, time from GitHub to message for each kind, and whether every event the
leads' watchers saw also arrived from the relay. Then leads switch over one at a time.

## Slices (docs first; each at most 250 changed lines)
| Slice | Content |
|---|---|
| 0 | This document. |
| 1 | Reader, paging, state, outbox and journal; events to a dry-run log only, no sends. Tests 1-6. |
| 2 | Routing, team file, sending, outcomes, rate and retry. Tests 7-9. |
| 3 | `unadmitted`, heartbeat and feed health, watchdog script, onboarding §5. Tests 10-11. |

## Overlap and out of scope
- **#2568** (message delivery): relay messages ride its queueable lane, envelope and by-id
  confirmation; nothing here changes how text is typed into a session.
- **#728** (agent messaging on the Slipgates relay): when it replaces the HA route, only the sender
  changes.
- **#2228/#2278** (release relay ETag reads): the same conditional-read pattern, a different feed.
- **Webhooks** would see every transition and end polling, but need a public endpoint and a secret;
  a later option, not this design.
- **Out of scope:** merging, admitting, reviewing or re-running anything. The relay reads and tells.

## Asks
1. Owner: a read-only GitHub App installation for the relay (its own budget).
2. Quality: record admissions as `[admit] <full sha>` PR comments.
3. SRE: name the relay host and the watchdog host, and run the 24-hour soak.
4. Engineering (or the Chief while Engineering is away): accept this design, name who builds 1-3.