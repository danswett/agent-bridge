# Release feed Worker (#927)

A Cloudflare Worker at `releases.slipgates.com` that caches GitHub's release list so clients stop hitting the
unauthenticated GitHub rate limit. It serves one route only:

`GET /repos/danswett/agent-bridge/releases?per_page=N&page=N` (per_page 1-100, page 1-1000)

- Fresh for 5 minutes at the edge, keyed on both `per_page` and `page`.
- Every answer carries `Age`: 0 for a fresh fetch from GitHub, otherwise the cache age in seconds (#1163).
- If the GitHub refresh fails, a copy up to 24 hours old is served with its real `Age`. With no copy, the
  answer is 502. A failed refresh never returns an empty list as a success.
- Packages and `releases.<channel>.json` are not proxied. Clients get them from GitHub download URLs, which
  GitHub does not count against the limit. Clients must accept only asset URLs under
  `https://github.com/danswett/agent-bridge/releases/download/`.
- No secrets, no logging of request data, no R2, no CI token.

Risk: Cloudflare egress IPs are shared, so GitHub may rate limit the refresh. Stale-on-error covers short
gaps. If it recurs, add a read-only token as a Worker secret (owner decision).

Test: `node infra/cloudflare/release-feed/worker.test.mjs`.

Deploy (owner-approved on #927, done by the Cloudflare session on DSWETT-HOME): create the Worker from
`worker.js` and the route `releases.slipgates.com/*` on the `slipgates.com` zone, with a proxied (orange
cloud) DNS record for `releases` so the Worker receives traffic. This is the one proxied record on the zone.
`wrangler.toml` records the intended settings.

Verify: `curl -i "https://releases.slipgates.com/repos/danswett/agent-bridge/releases?per_page=10&page=1"`
returns 200 JSON with an `Age` header; a second call returns a larger `Age`.
