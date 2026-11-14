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
into events, and sends each event to the session that owns it. **Ask (Chief, for the owner):** record
the decision that the relay is the one sanctioned GitHub reader for PR events; until then, and for
everything else, the rule of at most one conditional check per 10 minutes stays.

- Builders and leads never poll; they wait for messages, as the rules already say.
- Targets, measured per kind in the soak: a PR state, comment or submitted review reaches its owner
  within 2 minutes of GitHub showing it; a `gate` within 2 minutes while CI runs and 4 minutes on a
  finished head; a review dismissal within 4 minutes; `ci` within 5 minutes of the last check
  finishing, 9 after a re-run on a finished head. On a draft PR, state, comment and submitted review
  targets are the same; a dismissal and a `gate` arrive within 16 minutes and `ci` within 18 (a
  draft's reviews and head checks and statuses are read every 15 minutes, then every 60 s while its
  checks run; `ci` is raised at once when the last `completed_at` is already 3 minutes old).
- Fewer than 300 counted REST calls an hour when quiet, about 500 with 8 PRs in CI (see Rate), on a
  budget of its own.
- No silent loss: every event the relay observes is delivered, or journaled as undeliverable and
  reported to a lead. **Limit:** the relay sees states, not every transition. Two head moves
  between reads arrive as one `head` event naming the last seen and the new SHA; a comment edited
  twice arrives at its latest text; a comment created and deleted between reads is never seen.
  Comments, reviews and check runs are complete (they are listed, not sampled).

## Trust: the repository is public
Anyone can comment, review or open a PR from a fork, and the relay types into sessions that hold
repository-write tokens. So text from GitHub is trusted only by its author's login:

- **Allow-list** (in the teams file): the shared owner account the sessions post from, the merge-rule
  App, and named bots (Copilot reviewer, `github-actions`). `author_association` is not enough.
- Comments, reviews, `[to:team]` tags and `[admit]` lines from anyone else are not delivered and
  change nothing. Quality gets one line per such item: PR number, login and a link, no text.
- A PR from a fork, or opened by a login not on the list, is not routed: Quality gets a link-only
  `opened` line and nothing else for it. Its `Owner-Session:` line and title are ignored.
- Even trusted text is passed on as a quote, `quoted from <login>: "..."`, after the #2568 escaping,
  so a bracketed header or an instruction inside it never reads as the relay's own words.

## Shape: where it runs, when a node leaves, where state lives
```
GitHub REST (ETag) -> reader -> state commit (If-Match) -> router -> sender -> session
                                 protected store: state blob, journal, lease, App key
```
Owner rule (contributor-workflow, "nothing tied to a machine"), answered:

1. **Where it runs.** `tools/agents/pr-relay.ps1` (PowerShell 7) runs as a standby runner on every
   fleet node the owner enrols, two or more. Only the runner holding the lease reads and sends.
2. **When that node leaves.** The lease (60 s, renewed every 20 s) lapses, and another runner takes
   it, loads the state blob and carries on from it. Nothing lives only on the old node.
3. **Where state lives.** In a protected per-component store in the Slipgates relay service, off
   every machine. **Ask (Engineering):** that store: a private blob with lease and conditional
   writes, encrypted at rest, an append-only journal, and a secret slot for the App key, reachable
   only by enrolled nodes. The relay does not go live without it; slice 1 runs only in CI.

**Fencing and where a send is admitted.** Every state commit is a write conditioned on the blob's
ETag (`If-Match`) under the current lease; a runner whose write fails has lost the lease and stops.
The admission point for a send is its own `sending` commit: a send runs only after that commit
succeeds. Every attempt, including a retry and the one re-send after `dropped`, is a new `sending`
commit with its attempt number and the same request id; an attempt whose outcome is unknown is never
sent again. Neither the host nor HA checks lease
generations, so runners can overlap in time: one that pauses after its commit may resume after a
takeover and finish that one send. The guarantees are: a stale runner completes only sends it had
already committed, at most one per recipient and PR (the order barrier below), and its next commit fails;
no event goes out twice, since the new runner never re-sends a `sending` record and the host keeps
one entry per request id; per-PR order holds, since later events wait behind the open record. As a
local guard, a runner also abandons a send whose `sending` commit is more than 30 s old, before
calling `reply-session.ps1`. If it still holds the lease it commits that attempt as `not sent`
(proof nothing went out) and makes a new attempt; if it cannot commit, the open record is an
unknown outcome to the next runner and follows the `unconfirmed` path (Sending and the outbox).

## What it reads
All reads use `gh api -i` with `If-None-Match`; an authorized 304 does not count against the primary
rate limit. Lists use `per_page=100`. The PR list follows `Link: next`; the comment feeds page by
key. A list that fails part way follows the rules below.

| Read | Every | Gives |
|---|---|---|
| `pulls?state=all&sort=updated&direction=desc`, pages until `updated_at` is older than the cursor | 30 s | opened, head moved, draft or ready, labels, closed, merged |
| `issues/comments?sort=updated&direction=asc&since=<since>`, one page per request (see below) | 30 s | PR comments, `[to:team]` tags on PRs and hub issues, `[admit]` lines |
| `pulls/comments?sort=updated&direction=asc&since=<since>`, one page per request | 60 s | review-thread comments |
| `pulls/<n>/reviews` | when the PR's `updated_at` changes, and every 3 min for open non-draft PRs, 15 min for drafts | reviews submitted or dismissed |
| `commits/<head>/check-runs` and `commits/<head>/statuses` | 60 s while CI runs on the head (drafts too), 3 min for other open non-draft heads, 15 min for other drafts | CI finished, re-runs, `merge-rule`, `independent-review`, `independent-review-2` |

**Comment feeds page by key, not by page number.** Each request is page 1 of the list, oldest
first, with `since` as the key. The design does not rely on whether GitHub's `since` includes its
own timestamp (the soak checks it): every request uses `since` = key minus 1 second, so every
comment at the key's timestamp is read again, and repeats are removed by comment id plus
`updated_at`. A scan's first key is the committed cursor `C` minus 10 minutes (late-indexed
comments); each next key is the newest `updated_at` on the page just read. A deleted comment cannot
shift a later one past the reader, and a comment edited mid-scan moves to the end, where it is read.
If a full page brings nothing new (more than 100 comments within that second), the scan takes pages
2, 3 ... of that key until a page is short, then repeats that key's pass until a whole pass finds
nothing new and nothing missing; a deletion or edit during a pass shows as a missing comment and
forces another pass. The scan ends at a short page. Each page is one state commit: its events
(oldest first, so order holds across pages), its ETag, its newest `updated_at` and count, and the
next key; `C` moves to the last key once a page is short. A crash or failure resumes from the
committed key. A 304 page is not the end of a scan: the scan follows that page's saved next key, so
a comment beyond a full first page is still read, and only a short page ends it. Issue comments are
kept only on PRs, or on issues when they carry a `[to:team]` tag.
Reviews are re-read on a timer because GitHub does not promise a dismissal changes the PR's
`updated_at`; checks and statuses likewise, as a re-run, a receipt or `merge-rule` can change
without it.

**PR-list scans and the watermark.** The PR list has no `since`, so it is read newest first until
an `updated_at` older than its committed cursor `C`. A scan notes page 1's newest `updated_at` as
its pending top `T`; each page is one state commit of its events, its ETag, its oldest `updated_at`
and `T`, so a 304 page can still be judged against `C`. `C` becomes `T` only when one scan, from
page 1, reaches `C` or the last page. A partial scan (throttled, failed, cut short or
a restart) keeps `C`, and the next scan starts again from page 1: staged events are already in the
outbox and repeats are removed by event id, so a later page 1 304 cannot hide them and nothing on a
later page is skipped. Each event is about one PR, so order across pages does not matter (per-PR
order is kept by transition numbers). **Limit:** a PR GitHub deletes (spam) during a scan can
shift one PR past the reader; its next change is still seen.

**Rate.** Only 200 responses count, so cost follows changes, not cadence. Estimate: the comment and
PR feeds about 150 an hour; a CI run about 45 (a check-run list changes when a check starts or
ends); a finished head's 3-minute reads mostly 304. With 8 PRs in CI that is about 500 an hour. If
the hour's projection passes the ceiling it cuts, in this order: finished heads to 15 minutes,
review-thread comments to 5 minutes, statuses of running heads read only when their check runs
change, running heads to 2 minutes. It honours `Retry-After` and secondary limits (backs off 1, 2,
4 ... up to 15 minutes). Below 1,000 calls left it keeps only the first page of the PR list (the
watermark rule above applies) and the checks and statuses of open heads; comment and review reads
pause and later resume from their cursors. Below 300 it stops reading until the reset and tells the
Chief of Staff once. A fine-grained token would share its
user's 5,000-an-hour budget, so it isolates nothing. **Ask (owner):** a GitHub App installation,
read-only on contents, pull requests, issues, checks, statuses and Actions of
`danswett/agent-bridge`, with a budget of its own. Until then the relay keeps a hard ceiling of 600
counted calls an hour, and the soak runs under the App or that ceiling.

## Events
Each event has an id that is stable across retries and restarts and distinct for each occurrence.
Every id starts with the state blob's epoch, a random value made when the blob is created, so a
counter that restarts after state loss can never repeat an earlier id:

| Kind | Raised when | Id key |
|---|---|---|
| `head` | head SHA differs from the last seen (A to B to A counts twice) | transition number plus from and to SHAs |
| `unadmitted` | a `head` whose SHA has no admission (see Admission) | as its `head` |
| `ci` | `quality-gate` and every other check run on the head have finished, and no new run appeared for 3 minutes; again after a re-run | head SHA plus the sorted check-run ids and attempts |
| `gate` | `merge-rule`, `independent-review` or `independent-review-2` changes state | status id |
| `review` | a trusted review is submitted or dismissed | review id plus state |
| `comment`, `tag` | a trusted comment, new or edited; `tag` when it carries `[to:<team>]` or `[to:all]` | comment id plus `updated_at`, plus the recipient for a targeted tag, so an edit or a newly named team arrives; `[to:all]` by comment id only, never re-broadcast |
| `ready`, `draft`, `opened`, `closed`, `merged`, `label` | state changes; `label` only for `hold`, `do-not-merge`, `security`, `fixes-main` | transition number plus old and new state |

Message: one line plus a link, for example `PR 2573 head moved 2ea1f1dd -> 77ee2a75 (merge of main)`,
`PR n CI on 77ee2a75: failed: build-x64, pwa`, `PR n merged as b11b4610 at 19:03 PT`, or a trusted
comment's first 300 characters as a quote. Times are Pacific.

## Routing: who owns a PR
Recipients are resolved by session **name** when sending, never a stored node id (onboarding §1).
Only trusted PRs (see Trust) are routed.

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
| Quality | `head`, `unadmitted`, `ci`, `gate`, `label`, `merged`, `closed` on every non-draft PR; `[to:quality]`; untrusted-item links |
| Engineering | `ready`, and `head` on a PR labelled `security` or touching sensitive paths; `[to:engineering]` |
| Other leads | their `[to:<team>]` tags; PRs they own by rule 1 or 2 |
| Chief of Staff | undeliverable reports, relay health, and everything Engineering gets while it is away |

The **roster** is the agent sessions whose names start with a team prefix in the teams file; the
owner's own interactive sessions are never in it. `[to:all]` goes to every live roster session, as
onboarding §1 says. The owning session receives every event on its PR. All sessions comment from one
account, so the relay cannot tell which session wrote a comment and delivers every trusted comment,
the writer's own included. Each recipient gets an event at most once, even when two rules name it.

## Admission
Quality admits exact SHAs by message and, as its adopted practice, records each admission as a PR
comment `[admit] <full sha>`.
The relay raises `unadmitted` when a head has no trusted admission, which would have caught PR 2573
at 18:25 PT, not 23:04. On start it reads the open PRs' comments once to learn earlier admissions.
**Limit:** sessions share one account, so a trusted `[admit]` line shows which account wrote it, not
which session; `unadmitted` is a warning that helps Quality, never a gate.

## Sending and the outbox
Messages go through `tools/agents/reply-session.ps1` with the agent token, label `pr-relay`, one
event per message, never batched into one turn. Under #2568 they are agent messages: queueable,
never urgent, and the envelope adds sender, id and receipt time. The request id is derived from the
event id and recipient, so a retried send is the same request, and the text carries
`[pr-relay id=<event id>]` so a receiver can ignore a repeat it already saw.

- **One commit.** New outbox entries, each PR's last head, transition number and state, and the
  cursors and ETags that produced them are saved in one conditional write of the state blob. A crash
  before it re-reads the same data; after it, nothing is lost. A send is recorded as `sending` (with
  recipient and generation) in the blob before `reply-session.ps1` runs.
- **Dedupe** checks both the outbox and the journal by event id, so a re-read after a crash never
  sends an event already delivered and removed from the outbox.
- **Outcome per recipient:** `queued` (the #2568 ack, not durable), then `delivered`, `dropped` or
  `unconfirmed` from #2568's by-id confirmation. Only `dropped` (proof nothing reached the CLI) is
  re-sent, once, with the same id. `unconfirmed`, and a `sending` record found after a takeover, are
  never re-sent: they stay open, with their text kept in the outbox, a later #2568 sighting promotes
  them to `delivered`, and after 30 minutes the PR's team lead gets one line, `unconfirmed id=<id>`
  plus the event's own line and link, so nothing is lost even if the send never ran. `queued` is
  never counted as sent. A `queued` item at a session that is in a turn is normal work: it is
  reported only once the session has been idle 10 minutes with it still queued, or after 2 hours.
  Sending is switched on only once #2568's by-id confirmation (its slice 4) has landed.
- **Order:** per recipient and PR, in the order observed. A later event for the same PR and recipient
  waits behind one that is retrying, `sending` or still `queued`, until it is delivered or dropped and
  re-sent, or it is reported; events sent after that report carry `after <id> (unconfirmed)`.
  An event that falls back to a lead is marked `late (observed <time>)`. Different PRs do not wait.
- **Rate:** at most 6 messages a minute to one session. Extra events wait in the outbox, in order;
  nothing is summarised away.
- **Retry:** a definite failure retries at 1, 2 and 5 minutes, with the same request id, so the host
  keeps one entry: HA refusing the request (an HTTP error status or a refused connection, so nothing
  was accepted) or the host's busy refusal. A timeout or a reply with no outcome is unknown, not a
  failure: it follows the `unconfirmed` path and is never retried. After the retries the event is
  `undeliverable`: the PR's team lead gets it once,
  and if that fails too, the Chief of Staff gets one line. A session not in the roster is not
  retried; its events go to rule 3 or 4.
- Once an event is delivered, its text is removed from the outbox. The journal keeps event id, kind,
  PR, recipient, outcome and time, and never a comment body.

## Keys and state custody
- **GitHub:** the App's private key sits in the store's secret slot, released only to the runner
  holding the lease and kept in memory. One installation token (an hour) is reused until 5 minutes
  before expiry; the replaced token is revoked (`DELETE /installation/token`). Until the App exists
  the shared account's token comes from the keyring of the leaseholder's node, in memory only.
- **HA:** the agent token comes from the environment `reply-session.ps1` already uses; never in the
  state blob.
- No token or key is ever printed, logged, journaled or put in a message. Rotation: the owner rotates
  the App key in GitHub's App settings and SRE replaces it in the store; the old key is deleted the
  same day.
- The state blob holds quoted comment text until delivery, so only enrolled nodes can read it.
- **Outsider notices** to Quality are capped at 5 per login per hour; beyond that, one count line an
  hour per login.

## Restart and health
- **Takeover or restart:** the new leaseholder loads the blob, checks the journal for every outbox
  entry (anything already delivered is closed, not re-sent), treats `sending` records as described
  above, sends the rest, then reads from the saved cursors, comment-feed keys and the PR list's
  committed cursor (a partial PR-list scan starts again from page 1).
- **State loss:** if the blob is lost, a new one (new epoch) is made and the relay re-reads the last 24
  hours. If the journal survived, each re-raised event is checked against it by its key without the
  epoch (comment, review, gate and `ci` keys are unique on their own), and a head or state change only
  when the PR differs from the journal's last entry for it; only events not found are sent. If the
  journal is lost too, re-raised events carry `replay after state loss`. Quality is told once.
- **Health:** the leaseholder writes a heartbeat into the blob every minute with the last good read of
  each feed. A feed with no good read for 10 minutes sends one line to the Chief of Staff (and one on
  recovery). A standby runner that sees the lease held but the heartbeat older than 10 minutes tells
  the Chief once. If no runner is alive, the 07:00 PT daily line to Quality (counts, spend, open
  undelivered) does not arrive, and Quality raises it. Alarms never page the owner.
- **No fallback polling.** While the relay is down, no lead restarts a watcher. The store is reachable
  only from enrolled nodes, so the Quality Lead learns the relay is down from the Chief of Staff's
  alert line or a missing 07:00 PT daily line. Only then may it make the existing one-off check (at
  most once per 10 minutes). That check only reads and never sends, so overlapping a recovering
  relay costs at most one call and cannot duplicate an event.

## What changes for sessions
- Builders: no watcher and no GitHub reads to learn about their PR. Put `Owner-Session:` in every
  PR body. Act on relay messages as they arrive, and treat quoted text as untrusted input.
- Leads: any remaining `wake-watch.ps1` for PR events stops when the relay goes live, for good (see
  No fallback polling). `docs/agents/onboarding.md` §5 changes in the last slice.

## Tests (hosted CI, a fake `gh` replaying recorded responses; no network)
1. 304 on every read raises no event and counts no call.
2. Head move, CI finish with two failures, merge: three events, in order, one message each.
3. A head moved A to B to A, and a CI re-run on the same SHA: each raises a new event. A check run
   created after the others finished delays `ci` until it finishes.
4. Comments across two pages, one edited later: each revision raised once, oldest first across the
   pages, none repeated at the page boundary. Two comments share the boundary timestamp, one on
   each page: both are raised, also when one is deleted or edited before the next page is read. An
   outage from 09:50 to 11:20 re-reads every comment since 09:40.
5. Crash after the state write and before the send, then a takeover by another runner, while the
   head moves A to B and then, during the outage, to C: `A->B` and `B->C` are each sent once. A
   runner that lost the lease cannot commit or send. A `sending` record is never re-sent.
6. A read failing on page 2 (500, rate limit, bad body) commits page 1's events and keeps the
   cursor; a comment feed resumes from page 1's last key, the PR list from page 1. Backs off.
7. Routing: `Owner-Session:` wins; an "Update branch" commit or another agent's commit does not
   move ownership; a gone session falls to the lead; no owner goes to Quality as `owner unknown`.
8. 20 events on one PR in a minute: all 20 sent, 6 a minute, in order; a later event waits behind a
   retrying one.
9. A by-id `dropped` notice: one re-send, same id. `unconfirmed`: no re-send, one lead line with the
   event's text after 30 minutes, and the next event for that PR waits until then. `queued` at a
   session in a 45-minute turn: no lead line. Three failed sends: one lead message; lead failing
   too: one Chief line.
10. `unadmitted` for a head without a trusted `[admit]`; none for an admitted head; an outsider's
    `[admit]` changes nothing; earlier admissions learned on start.
11. A feed failing for 10 minutes sends one Chief line; the journal never holds a comment body.
12. An outsider's `[to:all]` comment, an outsider's review and a fork PR with an `Owner-Session:`
    line reach no session; Quality gets one link-only line each, at most 5 per login per hour.
    Editing a trusted `[to:all]` comment does not re-broadcast it; editing a `[to:quality]` comment
    reaches Quality again, and adding `[to:engineering]` in an edit reaches Engineering once.
13. State loss: a new epoch; no new id equals an old one. With the journal kept, nothing already
    delivered is sent again; with it lost, replays are marked.
14. A runner pauses after its `sending` commit, another takes over, the first resumes: that send
    arrives once, the new runner does not re-send it, and the first runner's next commit fails. A
    runner paused before its commit sends nothing.
15. While throttled, 130 PRs change and one on page 2 closes: after recovery the `closed` event is
    raised once, though page 1 returns 304.
16. Page 1 of the PR list holds 100 changed PRs, page 2 fails, and the runner restarts: page 1 then
    returns 304, yet each page 1 and page 2 event is sent exactly once and the cursor moves only
    after one scan reaches it. The same with 150 new changes before the resume. For both comment
    feeds: pages 1 and 2 are committed, the read of page 3 fails, then a page-2 comment is deleted
    (in a second run a page-1 comment is edited, in a third a comment tied with the page-2 boundary
    is deleted); on resume each revision of every surviving comment is raised exactly once, oldest
    first. With 130 comments in the 10-minute lookback, a new comment on the second key page is
    read although the first page returns 304. (The same with a takeover is slice 2's test 5.)

## Live soak (SRE, 24 hours, enrolled disposable nodes)
The relay sends live only to two disposable sessions and logs every other event dry-run; no watcher
runs beside it. Baseline: once, after the window ends, a read of the soak window's evidence, as an
approved part of the soak (**Ask (Chief):** approve it), compared with the relay's journal: no
event missing, none twice. Evidence per kind: state, head, PR comments and reviews from each PR's
`issues/<n>/timeline`; review-thread comments from `pulls/comments` for the window; `ci` and `gate`
from `check-runs?filter=all` and statuses of each head SHA in those timelines (their completion
times); hub `[to:team]` tags from the hub issues' comments for the window. Cost: one call per 100
timeline entries per PR, per 100 check runs and per 100 statuses per head SHA, per 100
review-thread comments and per 100 comments per hub issue; for 40 PRs with about 4 heads each,
about 400 counted calls, paid from the App's budget, or without the App from the 600-an-hour
ceiling in hours after the soak while the relay is stopped. The soak also confirms whether `since`
includes its own timestamp. Measured: counted calls an hour, and time from GitHub to message for
each kind against the targets in Goal.

## Slices (docs first; each at most 250 changed lines)
| Slice | Content |
|---|---|
| 0 | This document. |
| 1 | Reader, paging, trust filter, state commit, outbox and journal against a fake store; dry-run log only. Tests 1-4, 6, 12, 15, 16. |
| 2 | Store client, lease, fencing, takeover and state loss (needs the Engineering store). Tests 5, 13, 14. |
| 3 | Routing, team file, sending, outcomes, order, rate and retry (needs #2568 slice 4). Tests 7-9. |
| 4 | `unadmitted`, heartbeat, feed health, daily line, onboarding §5. Tests 10-11. |

## Overlap and out of scope
- **#2568** (message delivery): relay messages ride its queueable lane, envelope, escaping and by-id
  confirmation; nothing here changes how text is typed into a session.
- **#728** (agent messaging on the Slipgates relay): when it replaces the HA route, only the sender
  changes.
- **#2228/#2278** (release relay ETag reads): the same conditional-read pattern, a different feed.
- **Webhooks** would see every transition and end polling, but need a public endpoint and a secret;
  a later option, not this design.
- **Out of scope:** merging, admitting, reviewing or re-running anything. The relay reads and tells.

## Asks
1. Owner: a read-only GitHub App installation for the relay (its own budget), and which fleet nodes
   to enrol as runners.
2. Chief, for the owner: record the relay as the one sanctioned PR-event reader, and approve the
   soak's one end-of-window evidence read (Live soak).
3. Engineering: the protected per-component store with lease in the relay service (Shape, point 3).
4. SRE: run the 24-hour soak. Engineering (or the Chief while it is away): accept this design and
   name who builds slices 1-4.