# S5: Web Push to the phone, end to end encrypted

**Verdict: pending the real-iPhone run.** Everything up to the phone passes:
- A host encrypts a question notice to the phone's own push key (RFC 8291, checked byte
  for byte against the RFC's example).
- The relay signs it with VAPID and forwards it, refusing anything off its allowlist.
- The service worker shows it, and a tap opens the app at the question.
- The answer goes back over the end-to-end channel, first answer wins.

Built on S4's relay, host and PWA (`spikes/s4-relay`); this folder holds the results.

## Question

Home Assistant's phone app is how questions reach the user today. Can the standalone
product replace it with Web Push to the PWA, without giving the relay or the push
service (Apple, Google, Mozilla) the content, and within iOS's limits?

## What iOS allows

Web Push works on iOS 16.4+ only for a page **added to the Home Screen** and opened
from there, after a permission prompt shown in response to a tap. iOS shows **no action
buttons and no text input** on web notifications. So the phone flow is: notification →
tap → the app opens at the question → answer in the app. Safari 18.4+ also accepts
*Declarative Web Push* payloads, which it can show without running the service worker;
the hosts send that shape, and the service worker reads the same fields elsewhere.
Every push must show a notification, or iOS revokes the permission.

## Design

| Step | Who | What they see |
|---|---|---|
| Subscribe | PWA | `PushManager.subscribe` with the relay's VAPID public key (`GET /v1/push/key`) |
| Register | PWA → host, over Noise | The subscription (endpoint, `p256dh`, `auth`) and the app's URL. **The relay never holds the subscription keys** |
| Ask | Host | Encrypts `{title, body, navigate}` to the subscription (RFC 8291, aes128gcm, one 4096-byte record) |
| Forward | Host → relay → push service | The relay signs a VAPID JWT (ES256, 55-minute expiry) and POSTs the ciphertext. It sees the endpoint, the size and the time |
| Show | Service worker | Decrypted by the browser; shows the notice. The tap URL is forced into the app's scope |
| Answer | PWA → host, over Noise | `answer {q, answer}`. First answer wins, as on the dashboard |

The relay's push gateway POSTs only over HTTPS, on the default port, to allowlisted push
services (`web.push.apple.com`, `fcm.googleapis.com`, `push.services.mozilla.com`,
`notify.windows.com` and their subdomains). It never follows redirects, caps bodies at
4096 bytes, and rate-limits each account (30 a minute).

## Results

`s4-devstack check` (CI): the S5 checks, alongside S4's 22.

| Check | Result |
|---|---|
| RFC 8291 encryption reproduces the RFC's example byte for byte, and decrypts | Pass |
| The relay's VAPID header verifies against its published key, for the endpoint's origin, and expires within the hour | Pass |
| A question reaches a phone as an encrypted push through the relay, and its answer comes back over the channel; a second answer is refused; neither the question nor the endpoint appears in the push body or the relay's logs | Pass |
| The relay refuses push endpoints off its allowlist without making a request (cloud metadata address, foreign host, suffix trick, non-HTTPS, garbage, oversized body) | Pass |
| Pushes are rate-limited per account, and a gone subscription (410) is dropped | Pass |

`js/test-sw.mjs` (CI) runs the service worker's handlers against a stubbed worker scope;
headless browsers refuse notification permission, so this is where they are pinned:

| Check | Result |
|---|---|
| A declarative payload shows its title, body and tag, and the tap goes to the question | Pass |
| A tap can never leave the app, whatever the payload says | Pass |
| An empty or unreadable push still shows a notification | Pass |
| A tap opens the app at the question, or moves an open app to it | Pass |

`js/test-pwa.mjs` (CI, Chromium and WebKit): a question asked by the host appears and is
answered in the app, and opening the notification's link connects to that host by itself.

## Findings

1. **A push endpoint is a capability, and the relay was logging it.** HttpClientFactory
   logs every request URL at Information, so each forwarded push wrote the full endpoint
   to the relay's log; anyone with that URL could notify that phone. The check that the
   relay's logs carry nothing sensitive caught it. The relay's outbound clients now
   have no request logging.
2. **Presence before `ready` reaches the PWA too.** Opening the app from a notification
   connects to the host as soon as it appears in presence, which can arrive before the
   relay connection can send. The PWA now acts once both are true.
3. **The PWA's origin is shared.** `danswett.github.io` is the origin, so notification
   permission and storage are shared with any other page on it. The product needs its
   own domain.

## Limitations

* Notification text appears on the lock screen. The product needs a "generic notices
  only" setting, so the question itself is only ever shown after unlocking, in the app.
* Subscriptions are per host here: each host holds the subscriptions phones have sent
  it. With several hosts, a phone registers with each one it connects to.
* Native apps (APNs/FCM directly) would add answer buttons and text input; this spike
  does not change that plan.
