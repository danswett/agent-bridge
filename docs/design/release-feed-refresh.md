# Keeping the release-list CDN current

Status: proposed, design only (R8b, Nov 23, 2026 PT). No code, workflow, Worker change, secret or
deployment is made or authorized by this document. Sensitive: update trust and the release path; two
independent model families through Quality.

Tracking: the owner's ask, "we should be able to update the CDN immediately on a new release", under
[#2225](https://github.com/danswett/agent-bridge/issues/2225) (releases and updates); the relay is
[#927](https://github.com/danswett/agent-bridge/issues/927). Related: the halt policy's relay renewal,
merged in [#3778](https://github.com/danswett/agent-bridge/pull/3778) (see
[halt-policy-without-github-api.md](halt-policy-without-github-api.md)), and its follow-up
[#3862](https://github.com/danswett/agent-bridge/pull/3862), still open at the time of writing: it widens
the relay fallback to any failed GitHub policy read and moves the policy list to `per_page=10`.

## What happened

On the owner's VM (host 0.1.7436), a person-pressed Check for updates read the relay's list when its copy
was 52 minutes old and offered nothing, though a newer release existed. A later read showed the relay
serving the new version with `Age: 88`. Nothing was wrong with the client's checks: the relay's copy was
valid and within the hour the client allows, and it was simply behind GitHub.

## How the copies refresh today

Only the release **list** goes through the relay. Feeds (`releases.<channel>.json`) and packages come from
GitHub download URLs, which are not rate limited and not proxied, so there is no feed copy to keep fresh.

The Worker ([infra/cloudflare/release-feed](../../infra/cloudflare/release-feed/README.md)) keeps each list
page in the Cache API under a synthetic key, per data centre:

- fresh for 300 seconds (60 with its optional read-only token); the key includes `per_page` and `page`;
- past that, revalidated with GitHub's ETag on the next request; a failed refresh is not an answer, so the
  copy is **served stale, up to 24 hours, with its real `Age`**, or 502 when there is no copy;
- Cloudflare's egress addresses are shared, so GitHub's unauthenticated limit can refuse the Worker's own
  refresh. That is the case the stale-on-error rule exists for, and it is when a copy becomes old.

Nothing tells the Worker a release was just published. A new release is seen only when a copy expires and a
refresh succeeds, so after a publish the lag is the fresh window plus, whenever GitHub refuses the Worker, the
age of its last good copy.

## What the clients accept

The relay is untrusted transport; every bound is the client's, set by the `Age` header the Worker sends
(`ReleaseDiscovery`, [app-windows.md](../app-windows.md)):

| Reader | Accepts a relay copy | Why |
|---|---|---|
| Background update check | `Age` at most 1 hour | `CdnOldest`; an older copy goes to GitHub |
| Kept relay copy, re-judged per check | `Age` at fetch plus time since, at most 1 hour | a copy taken just under the hour is not served past it |
| Person's press | proposed: at most 5 minutes, read first, bypassing any kept copy | the update-check slice paused behind this design and #3862; today a press asks GitHub first |
| Halt policy (#3778, #3862) | `Age` at most 1 hour, only while the relay grace is open | a renewal is stamped no later than the grace end less six hours |
| GitHub revalidation | at least every 6 hours; relay grace at most 24 hours from GitHub's last answer | so a compromised relay cannot freeze machines on an old list |

The accepted lag is therefore up to an hour for background checks, and the gap above (a stale copy with an
honest `Age`) is within it: the client behaved as designed, and the relay was not fresh enough to be useful.

## The gap

Two things are missing, and they are different problems:

1. **A publish does not reach the relay.** A pressed check wants the newest release now; the relay can only
   promise what its last successful refresh saw.
2. **The relay depends on GitHub to refresh itself.** When GitHub is refusing (the situation #3778 and #3862
   exist for), the Worker cannot refresh either, so its copy ages to the client's one-hour limit and the
   fallback stops helping. A refresh that does not need the Worker to call GitHub would keep it useful.

## Options

**A. Purge on publish (recommended first).** After a release is published, the publisher purges the list pages
at the edge and then checks that the relay serves the new newest tag.

- The Worker's cache key moves to the zone's own URL (`releases.slipgates.com/...`), so Cloudflare's
  purge-by-URL clears it in every data centre. Today's synthetic key cannot be purged through the API; this
  is a Worker change, deployed by the Cloudflare session, and the claim that a purge clears a Cache API entry
  under a zone-host key is **not yet proven**: one live check by that session settles it.
- The publisher (the release train, and `ci.yml`'s publish job while it exists) runs the step last: purge
  `releases?per_page=10&page=1` and any other size a client pins; poll the relay until its newest release is
  the one just published, within a bounded wait (about a minute), and **fail the run loudly** if it is not.
  A failed refresh never unpublishes a release.
- The next request after the purge makes the Worker fetch from GitHub, so the new copy is GitHub's own list
  at `Age: 0`: no new trust is placed in CI or in the relay.
- Needs: a Cloudflare API token limited to the `slipgates.com` zone with only Cache Purge, stored as a GitHub
  Actions secret in an environment limited to `main`, plus the zone id as a variable. Created by the owner or
  his operator; this document creates none.

**B. Push the list to the Worker.** The publisher sends the new list to an authenticated Worker route, which
stores it at `Age: 0`. This also removes the Worker's need to call GitHub right after a publish, so it works
while GitHub is refusing. It needs a Worker secret shared with CI, an owner decision on where the trusted
bytes come from (the relay must not be able to invent a list, and clients still check every field of it), and
a signature or digest story. Larger than A and not needed to close the first gap; kept as the answer to the
second if A leaves it open.

**C. Shorter freshness.** Lower the Worker's fresh window or give it the token everywhere. Cheap and already
possible (the token is an owner decision, #927), but it trades the shared per-IP limit for more refreshes and
never reaches "immediately".

**D. Client only.** The press reading the relay first with a five-minute bound narrows what a person sees but
does not make the relay current, so on its own it only moves the staleness.

## Fit with the halt policy fallback (#3778, #3862)

- The policy's carrier list and the release list are the **same relay pages** once #3862 moves the policy to
  `per_page=10`, so one purge refreshes both, and a release's `halts.json` is not newer than its list.
- A purge after a publish is **not** what the halt fallback needs. That path runs while GitHub refuses; the
  Worker's copy then ages up to the one-hour `Age` bound and the policy cannot renew from it. Only B (or an
  open per-IP window) helps there. The design keeps that distinction: A fixes a person's stale answer, B would
  extend the fallback's life under a GitHub outage.
- Every client bound above is unchanged by A. A halt can still hide for up to the relay grace on a
  compromised or stale relay, as the owner accepted on Nov 22 (#3778); a purge never lengthens that.

## Verification and rollout

1. Cloudflare session: prove the purge clears an entry keyed on the zone host in every data centre (curl a
   few PoPs through `cf-cache-status` and `Age`), then redeploy the Worker with the zone-keyed cache key.
2. A publish-time step in the train that purges, polls and fails loudly (a workflow change, delivered to
   Quality as a patch like the others). `ci.yml`'s publish job gets the same step until it is removed.
3. A first dry run after a real release, comparing the time to the new tag at the relay with today's.

## Risks and open questions

- The purge token is a new secret. Scope it to Cache Purge on one zone and keep it out of any job that builds
  or runs source.
- Cloudflare may rate limit purges; hourly releases are far below it.
- Whether the owner wants B at all; whether the Worker should get the optional read-only token regardless (an
  owner decision, #927).
- A purge only helps if the release list GitHub returns already shows the new release; the publish step runs
  after the release is published, never before.
- None of this changes what a person sees when GitHub and the relay both fail. That is #3553's wording work.
