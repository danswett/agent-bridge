# The app's address: app.slipgates.com

The production PWA moves from `https://danswett.github.io/agent-bridge/app/` to `https://app.slipgates.com/`
(#393). Nothing here is live until the cutover below is done. The relay's own move to
`relay.slipgates.com` is separate (#540); the app keeps using the relay it is built for.

## What the owner or SRE does (the cutover)

The repository's Pages site is one site, so setting its custom domain moves the whole site, spikes
included: the PWA becomes the root, and `/s2/` and `/s4/` stay where they are. Every step is
approved (owner, 2026-11-03, 4:23 PM PT). Production changes go through the usual staging, then
production approval.

1. **Relay first.** Deploy the relay with `https://app.slipgates.com` in `allowedOrigins`
   (`infra/regions/westus2.bicepparam`, see `docs/relay-deploy.md`): staging, then production. Both
   origins stay allowed, so the current app keeps working.
2. **Smoke the relay**: `pwsh infra/smoke-app-domain.ps1` fails on its last check until step 1 is
   live in production; that is the signal.
3. **In one sitting:**
   - DNS: add `app` as a CNAME to `danswett.github.io`, DNS only (no proxy).
   - Repository settings, Pages: set the custom domain `app.slipgates.com`; once the certificate is
     issued, enforce HTTPS.
   - Repository variable `PAGES_APP_AT_ROOT` = `true`, then run the Pages workflow (Actions, Pages,
     Run workflow). Without it the site would keep the PWA at `/app/`.
4. **Smoke**: `pwsh infra/smoke-app-domain.ps1`. It checks the page, manifest and service worker
   (and their types), the relay's CORS answer for the new origin, the spikes, and the `/app/`
   redirect.
5. **Install and sign in** on a real phone and a desktop (see below) before announcing the address.

### Rollback

Remove the custom domain in Pages settings, delete the DNS record, and set `PAGES_APP_AT_ROOT` to
`false` (or delete it), then run Pages again. The site returns to
`danswett.github.io/agent-bridge/` with the app at `/app/`. The relay needs no change: the old origin
was never removed from `allowedOrigins`. Anyone who moved keeps their data at the new origin, which
simply stops being reachable.

## What changes for people who already installed the app

Browsers keep a site's storage (the pairing keys and identity in IndexedDB, settings,
notification permission, push subscriptions, the installed app itself) per origin. A different
address is a different origin, so **none of it moves**. To switch:

1. Open `https://app.slipgates.com/` and install it (Add to Home Screen on iPhone; Install in
   Chrome or Edge).
2. Sign in and pair your machines again, as for a new device.
3. Remove the old installed app.

Until you do, the old app keeps working from its own address. Visiting
`danswett.github.io/agent-bridge/app/` after the cutover lands on `/app/`, which sends you to the new
app (a deep link's query and fragment are kept); that visit does not carry your pairing across.

## Why it is safe

- The manifest, service worker and asset paths are relative, so the app runs from any origin's root.
  The service worker only answers pages inside its own scope's root; `/s2/`, `/s4/` and `/app/` are
  left to the browser.
- The relay checks origins for CORS, the WebSocket handshake and blob uploads from the same list.
  Native clients do not send a browser origin and are unaffected.
