# The app's address: app.slipgates.com

The production PWA moved from `https://danswett.github.io/agent-bridge/app/` to `https://app.slipgates.com/`
(#393). It has been served there since 2026-11-07 (the cutover below is done; what was checked is
[recorded at the end](#what-was-checked-on-the-live-address-2026-11-07-pt)). The relay's own move to
`relay.slipgates.com` is separate (#540); the app keeps using the relay it is built for
(`relay.bad-duck.com` today).

## What the owner or SRE did (the cutover, kept for the rollback)

The repository's Pages site is one site, so setting its custom domain moves the whole site, spikes
included: the PWA becomes the root, and `/s2/` and `/s4/` stay where they are. Every step is
approved (owner, 2026-11-03, 4:23 PM PT). Production changes go through the usual staging, then
production approval.

**Before starting:** the change that makes `PAGES_APP_AT_ROOT` do anything (#1071, in
`.github/workflows/pages.yml`) and the relay allow-list change (#1066) must both be on `main`.
Without #1071 the variable is ignored and the new address would show the spikes' index page.

1. **Relay first.** Deploy the relay with `https://app.slipgates.com` in `allowedOrigins`
   (`infra/regions/westus2.bicepparam`, see `docs/relay-deploy.md`): staging, then production. Both
   origins stay allowed, so the current app keeps working.
2. **Check the relay is ready:** `pwsh infra/smoke-app-domain.ps1 -RelayOnly`. It asks only whether
   the relay's CORS answer allows `https://app.slipgates.com`, which works before the address
   exists. It fails until step 1 is live in production.
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

Do these in this order, so the site is never half-moved:

1. Set `PAGES_APP_AT_ROOT` to `false` (or delete it) and run the Pages workflow again. The site is
   back to the PWA at `/app/` and the spikes' index at the root.
2. Remove the custom domain in Pages settings.
3. Delete the DNS record.

The site is then at `danswett.github.io/agent-bridge/` with the app at `/app/`. The relay needs no
change: the old origin was never removed from `allowedOrigins`. Anyone who moved keeps their data at
the new origin, which simply stops being reachable.

## If you installed the app from the old address

This is for anyone who opened `https://danswett.github.io/agent-bridge/app/` (or the older
`https://danswett.github.io/agent-bridge/`) and added Slipgates to a home screen, installed it in
Chrome or Edge, or just signed in there in a browser tab. That address now sends you on to
`https://app.slipgates.com/`, but **the move does not carry your sign-in with it**.

**Why.** A browser keeps a web app's saved data separately for each address, and
`app.slipgates.com` is a different address from `danswett.github.io`. Nothing is copied between
them, and we do not offer a way to copy it. In particular we make **no promise that your sign-in,
your device's keys or its approval transfer**: they stay with the old address.

**What you do.**

1. Open `https://app.slipgates.com/` in your browser.
2. Install it again: Add to Home Screen on iPhone or iPad, **Install** in Chrome or Edge on a
   computer.
3. Sign in with GitHub, as you would on a phone you had never used before
   ([the beta guide](beta-guide.md#connect-your-phone)).
4. Turn notifications on again, from the new home-screen icon (on iPhone and iPad, notifications
   only work from the installed app).
5. Once the new one works, remove the old home-screen icon or installed app. The old copy may
   still be listed under **Your devices** on your PC; remove it there if so.

**What you lose when you switch.** Everything below stayed in the old app and starts again, empty or
at its default, in the new one:

- Your sign-in and this device's identity: the new app is a new device and has to sign in itself.
- The machines you had open and which one you last looked at.
- Settings: text size, pane sizes, list order, and your notification choices.
- Notification permission and the notifications you had subscribed to. Until you turn them on
  again from the new app, nothing from it reaches your phone.
- Text you were typing and had not sent is held only while the page is open, so there is nothing
  saved to bring across, with this move or without it.

Your sessions and their history are not affected: they live on your PC, not in the app. You only
lose what the old app was keeping for itself.

**You can take your time.** The relay still accepts the old address, so a copy you have not
replaced keeps working as before from its own address. We have not tested what an already
installed old copy does when the old address starts sending visitors on: it may keep opening from
its saved copy and never show the move, so do not wait for it to tell you. What a notification
tapped on the old copy opens is also untested.

A visit to `danswett.github.io/agent-bridge/app/` lands on `/app/` at the new address, which keeps
the query and fragment of a deep link and sends you to `/`. That visit only moves you to the new
address; it does not carry your sign-in across.

## Why it is safe

- The manifest, service worker and asset paths are relative, so the app runs from any origin's root.
  The service worker only answers pages inside its own scope's root; `/s2/`, `/s4/` and `/app/` are
  left to the browser.
- The relay checks origins for CORS, the WebSocket handshake and blob uploads from the same list.
  Native clients do not send a browser origin and are unaffected.
- The service worker never handles a request to another address, a request that is not a GET, or
  a path containing `/v1/`, so the relay's traffic is never stored by the app. It does keep other
  same-origin GETs inside its scope (the app's own files, and a stale-while-revalidate copy of any
  other file it asks for); that is static content from Pages, never an API answer.

## What was checked on the live address (2026-11-07, PT)

Run between about 11:40 and 12:00 PT against `https://app.slipgates.com/`, which was serving the
release whose service worker carries version `9515798571568acd` (files last modified 11:29 PT).
Only read-only requests (GETs, and `fetch` calls from inside the page to the app's own address)
were made, to `app.slipgates.com` and nowhere else. Nothing was signed in, paired or sent to the relay.

**curl, one GET per path** (status, content type):

| Path | Result |
|---|---|
| `/`, `/index.html`, `/?demo=1`, `/?demo=1#/sessions`, `/#/settings` | 200 `text/html; charset=utf-8` |
| `/manifest.webmanifest` | 200 `application/manifest+json; charset=utf-8` |
| `/sw.js`, `/text-size-boot.js`, `/design/brand.js`, `/js/main.js`, `/config.js` | 200 `application/javascript; charset=utf-8` |
| `/app.css`, `/design/tokens.css` | 200 `text/css; charset=utf-8` |
| `/icon.svg`, `/design/sprite.svg` | 200 `image/svg+xml` |
| `/icons/icon-192.png`, `/icons/maskable-512.png`, `/icons/apple-touch-icon.png` | 200 `image/png` |
| `/design/fonts/SlipgatesSans.woff2` | 200 `font/woff2` |
| `/app`, `/s2` (no trailing slash) | 301 to `https://app.slipgates.com/app/` and `/s2/` |
| `/app/`, `/app/redirect.js` | 200; the page says "Slipgates has moved" and its script is `location.replace('/' + location.search + location.hash)` |
| `/s2/`, `/s4/` | 200 (the spikes, untouched) |
| `/sessions`, `/sessions/`, `/v1/push/key`, `/health`, `/nope.js` | 404 (GitHub Pages' page; the app has no server routes) |
| `http://app.slipgates.com/` | 301 to `https://app.slipgates.com/` |

`Cache-Control: max-age=600` on the files looked at, as Pages sets it; the worker reads the shell with
`cache: 'reload'` so a 10-minute-old copy is never installed as a new release. The manifest has
`id`, `start_url` and `scope` all `./`, which is `/` here, with the seven icons it lists all served.

**Chromium through Playwright**, with every request that was not to `app.slipgates.com` aborted:
[`pwa/test/live-origin-check.mjs`](../pwa/test/live-origin-check.mjs), run as
`node test/live-origin-check.mjs` from `pwa/` (after `npm ci`). All 15 checks passed on the final
run, and it reported no request leaving the app's address:

- the service worker registers with scope `https://app.slipgates.com/` and takes control after one reload;
- the manifest's `id`, `start_url` and `scope` each resolve to `https://app.slipgates.com/`;
- loading and reloading `/`, `/index.html`, `/?demo=1`, `/?demo=1#/settings` and `/#/machines` each
  shows the app. The app's own routes are fragments (`#/sessions`, `#/machines`, `#/settings`), so a
  refresh of any of them is a refresh of `/`; a real path such as `/sessions` is Pages' 404 and is
  not answered with the app shell;
- `/s2/` loads the spike and not the app; `/app/` is the "moved" page;
- from inside the page, `fetch('/v1/push/key')`, `/v1/anything` and `/health`, with an
  `authorization` header made up for the test, returned Pages' 404 and none of it was cached;
- the app's caches held 34 or 35 entries (the count moves by one with the record the worker keeps
  of its releases), none for another address, none under `/v1/` and none for `/sessions` or
  `/health`. A separate one-off look, after visiting `/s2/` and `/s4/` in the same browser, found
  no `/s2/`, `/s4/` or `/app/` entry in the app's own cache (`/s4/` keeps its own, under its own worker);
- offline, a reload of `/` came back as the app from the worker.

**What the code does that the checks lean on** (`pwa/sw.js`): the fetch handler returns without
handling any non-GET request, any request to another origin (so the relay at `relay.bad-duck.com`
is never touched), any path containing `/v1/`, and any navigation other than the app's root. Other
same-origin GETs inside its scope are served from, or copied into, the app's cache, which is how
the app's own files are kept; nothing the relay or an API says goes through that. Push notification
targets are limited to the worker's own scope.

**Not verified**, and by whom it is left:

- The app installed on an iPhone or iPad (Add to Home Screen, first launch, notification entry from
  cold and warm start). The owner has put the iPad check on hold.
- The app installed as a Windows app from Edge or Chrome (install, launch from Start, window
  scope, notification entry). A headless browser does not do this.
- Signing in, pairing, the relay connection, WebSocket and blob traffic, and a real push, from the
  new address. These need a signed-in person and were not attempted.
- The relay's CORS answer for `https://app.slipgates.com` (`pwsh infra/smoke-app-domain.ps1`
  sends a request to the relay, so it was not run here). #1117 records that the script passed
  when the address went live.
- How the old address behaves: not requested from here. The redirect from
  `danswett.github.io/agent-bridge/` is as the cutover left it, and what an old installed copy does
  with it is untested (see above).
- That a new release reaches an open app on the new address (the update prompt). It was not
  exercised against production.

**Notes for other tracks.** The page's Content-Security-Policy allows the relay only at
`relay.bad-duck.com` and the blob host, so the relay move (#540) has to add `relay.slipgates.com`
to `connect-src` in the same change that points the app at it. GitHub Pages sends
`Access-Control-Allow-Origin: *` on the app's static files, and no `Strict-Transport-Security`
header was seen; both are GitHub Pages' behaviour, not something the app sets.
