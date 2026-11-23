# Release feed Worker (#927)

A Cloudflare Worker at `releases.slipgates.com` that caches GitHub's release list so clients stop hitting the
unauthenticated GitHub rate limit. It serves one route only:

`GET /repos/danswett/agent-bridge/releases?per_page=N&page=N` (per_page 1-100, page 1-1000)

- Fresh for 300 seconds at the edge without a token, or 60 seconds when the optional `GITHUB_TOKEN`
  Worker secret is present; keyed on both `per_page` and `page`. The key is the public zone URL
  (`https://releases.slipgates.com/repos/danswett/agent-bridge/releases?per_page=N&page=N`), so a Cloudflare
  purge-by-URL of that URL can clear an entry (#3870). Whether a purge clears a Cache API entry under a zone-host key
  is not proven here; it needs one live check by the owner or a Cloudflare session.
- Every list answer carries a content-based `ETag` and `Age`: 0 after a successful GitHub fetch or
  revalidation, otherwise the cache age in seconds (#1163). `If-None-Match` matching the current list
  returns 304 with no body and the same `ETag` and real `Age`, including when serving stale.
- Expired copies are revalidated with GitHub's own ETag when available. GitHub's 304 renews freshness;
  a failed refresh does not. Only authenticated GitHub 304s are exempt from its primary quota.
- If GitHub rejects the Worker token with 401, the Worker retries once without Authorization or the
  upstream ETag. A successful anonymous list is cached for 300 seconds, even while the secret remains
  set. A failed retry keeps the stale copy's real Age, or returns 502 without a copy. Other statuses
  and network errors do not trigger this retry.
- If the GitHub refresh fails, a copy up to 24 hours old is served with its real `Age`. With no copy, the
  answer is 502. A failed refresh never returns an empty list as a success.
- Packages and `releases.<channel>.json` are not proxied. Clients get them from GitHub download URLs, which
  GitHub does not count against the limit. Clients must accept only asset URLs under
  `https://github.com/danswett/agent-bridge/releases/download/`.
- No required secrets, no logging of request data, no R2, no CI token. The optional read-only
  `GITHUB_TOKEN` secret is sent only to GitHub in the upstream Authorization header, never to clients.
  Incoming client credentials are not forwarded.

Risk: Cloudflare egress IPs are shared, so GitHub may rate limit the refresh. Stale-on-error covers short
gaps. Creating the optional read-only token secret remains an owner decision; this code does not
create a credential or deploy anything. Without it, freshness stays at 300 seconds.
After owner approval, the deployment operator can run `wrangler secret put GITHUB_TOKEN` from this
directory and enter the read-only token at the prompt; never put its value in `wrangler.toml`.
The relay remains untrusted transport: client `CdnProblem` validation is unchanged, and clients
still reject lists older than one hour using `Age`.

Test: `node infra/cloudflare/release-feed/worker.test.mjs`.

Deploy (owner-approved on #927, done by the Cloudflare session on the owner's machine): create the Worker from
`worker.js` and the route `releases.slipgates.com/*` on the `slipgates.com` zone, with a proxied (orange
cloud) DNS record for `releases` so the Worker receives traffic. This is the one proxied record on the zone.
`wrangler.toml` records the intended settings.

Redeploy after a Worker change: run the **Release feed deploy** workflow (`.github/workflows/release-feed-deploy.yml`)
from main (Actions tab, Release feed deploy, Run workflow); SRE approves. Nobody runs `wrangler login` or `wrangler deploy`
by hand. The workflow:

- **test**: runs the Worker's tests (`worker.test.mjs`), with no secret.
- **deploy**: waits for SRE's approval of the `release-feed-deploy` environment, then runs `wrangler deploy` (an exact,
  pinned wrangler) with that environment's Cloudflare API token. It reads `wrangler.toml` (name `agent-bridge-release-feed`,
  route `releases.slipgates.com/*`, `workers_dev = false`) and publishes `worker.js`. It sets no secret and changes no DNS.
- **purge-check**: in the `release-purge` environment, purges the list URL by URL and checks the next answer was fetched
  after the purge, then a bodyless 304 for its ETag. That is the purge-by-URL check (#3870).

The first requests after a deploy that changes the cache key refetch from GitHub once.

Verify: `curl -i "https://releases.slipgates.com/repos/danswett/agent-bridge/releases?per_page=10&page=1"`
returns 200 JSON with an `Age` header; a second call returns a larger `Age`.
Repeat with `If-None-Match: <ETag from the first response>` to verify a bodyless 304 with its real `Age`.
