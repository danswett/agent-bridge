# S4: an untrusted relay with GitHub sign-in, end-to-end channels and a PWA

**Verdict: GO.** One relay, run by the project, can serve every user and device
without being able to read, forge or redirect anything. Sign-in is GitHub's device
flow, and the PWA runs the same channel as the .NET host. Running live on Azure
Container Apps, a host on the corporate network was reached from a second device and
from the PWA at its GitHub Pages origin, in Chromium and WebKit. The live run also
found something the design must change: **a single in-memory relay splits during a
deploy**, so the product needs a cross-replica backplane (see finding 3).

Run on 2026-10-27 with .NET 10.0.403 and Playwright 1.56.1 (Chromium and WebKit). The
live relay ran in Azure Container Apps, West US 2, on 0.25 vCPU and 0.5 GiB with one
replica. The test host was DSWETT-DEV-VM1 on the Microsoft corporate network.

## Question

The design (§3, §5) replaces Home Assistant's MQTT hub with a project-run relay that
is **not trusted**: it forwards frames between one account's devices and must learn
nothing from them. The spike asks whether that works end to end:

1. Can GitHub's device flow give hosts, apps and a browser an account, with no GitHub
   credential kept anywhere?
2. Can the relay route by device without being able to read, forge, replay or
   redirect the traffic, or cross between accounts?
3. Does S2's Noise channel work through it, including from a phone-style PWA?
4. What does it cost to run, and what breaks in practice?

## What was built

| Part | What it does |
|---|---|
| `src/S4.Protocol` | The wire format (`Wire.cs`), the relay client (`RelayClient.cs`), Noise channels multiplexed over one relay connection (`ChannelHub.cs`), and the device-flow client (`SignIn.cs`). The Noise code is S2's `Noise.cs`, linked rather than copied |
| `src/S4.Relay` | ASP.NET Core. `/v1/auth/device` and `/v1/auth/token` proxy GitHub's device flow and issue the relay's own HMAC-signed account token. `/v1/ws` admits a device and routes frames within its account |
| `src/S4.Host` | A stand-in host CLI: `sign-in`, `run` (stays connected, answers peers, reconnects with backoff) and `call` (opens a channel, takes a snapshot, measures pings) |
| `web/` | The PWA. It keeps a non-extractable P-256 key in IndexedDB, signs in, proves its key, lists the account's online devices and opens a channel to one |
| `src/S4.DevStack` | A fake GitHub, plus `check` (the relay's security and routing checks, run in CI) and `serve` (relay and demo host for the PWA test) |
| `Dockerfile` | The relay as one container |

### How a device joins

1. **Sign-in.** The relay proxies GitHub's device flow under the app's client id. On
   success it reads `/user` for the numeric id and login, revokes the GitHub token
   if it has the app's secret, and returns its own account token. Hosts keep only that.
2. **Hello and proof.** The device sends the token and its P-256 static public key.
   The relay answers with a fresh ephemeral key and a nonce. The device returns
   `HMAC(ECDH(static, ephemeral), label ‖ nonce ‖ account)`. ECDH keys cannot sign,
   and WebCrypto and TPM ECDH keys are ECDH-only, so this is a proof of possession
   that needs nothing but ECDH.
3. **Routing id.** The device id is the first 16 bytes of SHA-256 of the static key,
   so the id itself names the key.
4. **Channels.** A Noise XX handshake runs inside data frames addressed by device id.
   The prologue binds the initiator id, the responder id and a random channel id. The
   initiator checks that the responder's static key hashes to the id it addressed.

The relay sees: account id and login, device ids, the names and kinds devices report,
presence over time, and frame sizes and timing. Everything said inside a channel is
Noise ciphertext.

## Results

### Offline checks (`s4-devstack check`, in CI): 20 of 20 pass

| Check | Result |
|---|---|
| Sign-in issues a relay token; the GitHub token is revoked and never returned | Pass |
| A sign-in denied at GitHub fails | Pass |
| Two devices of one account see each other | Pass |
| A channel authenticates both ends: each key hashes to the id it was reached at | Pass |
| A request crosses end to end, and the relay sees only ciphertext: no tapped frame or log line contains the plaintext marker | Pass |
| A 60,000-byte message crosses the relay | Pass |
| Another account's device is neither listed nor reachable | Pass |
| **A relay that redirects a channel to another device is detected**: the handshake fails authentication | Pass |
| A bad token is refused (4001) | Pass |
| A device that cannot prove the key it claims is refused (4003) | Pass |
| A key that is not on the curve is refused at hello | Pass |
| A frame over the size limit closes the connection (4013) | Pass |
| Flooding frames closes the connection (4008) | Pass |
| A second connection with the same device key replaces the first (4009) | Pass |
| A device that leaves drops out of presence | Pass |
| A socket from a web origin that is not allowed is refused | Pass |
| The sign-in API answers CORS preflight for allowed origins only | Pass |
| A host bounds the channels it keeps: a full table refuses more, unfinished handshakes expire, and a departed peer's channels go | Pass |
| The relay's logs carry no tokens or payloads | Pass |
| A relay that is stopping tells connected devices to reconnect at once (1012) | Pass |

### PWA through the relay (`js/test-pwa.mjs`, in CI)

| Engine | Sign-in, key proof, channel, snapshot, echo | Handshake | 20 pings (local) |
|---|---|---|---|
| Chromium | Pass, service worker registered | 19 ms | median 1 ms |
| WebKit | Pass, service worker registered | 403 ms | median 62 ms |

No forwarded frame contained plaintext. WebKit's figures reflect Playwright's slow
Windows WebKit crypto, as in S2.

### Live, on Azure Container Apps

| Path | Relay connect | Handshake | Pings (2 relay round trips each) |
|---|---|---|---|
| Second device on the same machine → host | 790-800 ms | 310-320 ms | 50: median 282 ms, max 295 ms |
| PWA at `https://danswett.github.io` with the production CSP, Chromium | | 301 ms | 20: median 278 ms |
| Same, WebKit | | 687 ms | 20: median 340 ms |

The host signed in with a real GitHub device-flow sign-in as `danswett`. The second
device and the PWA reused that account token rather than signing in again; each still
proved its own key.

The PWA run used the exact files the Pages workflow publishes, served at their real
origin. That exercised CORS, the WebSocket origin check and the page's CSP: **0 CSP
violations**. The relay's console log showed only login, device ids, counts and byte
totals.

Round trips are dominated by the route from this corp machine to West US 2: about
140 ms each way to the relay, with negligible relay time. A host and phone in the same
region as the relay would see far less.

### Cost

Container Apps consumption at 0.25 vCPU and 0.5 GiB, always on, is about $14 a month
after the free grant, plus about $5 for a Basic container registry. A GitHub Container
Registry image would remove the second item.

## Findings

### 1. The relay can be trusted with nothing, and that is testable

Every property in the threat model (§5.1) has a test that plays the hostile relay.
The channel is bound to both routing ids, so a relay that redirects a channel breaks
the handshake. Even a relay that also forged the prologue would meet the key-to-id
check. Accounts are isolated by routing alone, and the relay never holds a key that
decrypts anything.

### 2. The PWA must not be served by the relay

A relay that serves the PWA's code can serve code that reads what the PWA decrypts,
which would make the relay trusted after all. The PWA is now published on GitHub
Pages from this repository, with a CSP that permits scripts only from that origin and
connections only to the one relay. The relay serves no app code in production
(`Relay:ServePwa=false`) and accepts browser sockets and sign-in calls only from
allowed origins. A later step is to version the PWA and pin its hash in the native
apps' "open on phone" QR code, so a substituted page is detectable.

### 3. A single in-memory relay splits during a deploy

Container Apps keeps the old replica running for about a minute after traffic moves
to the new revision. Observed live:

* The ingress dropped the host's socket when traffic shifted, with no close frame.
  The host reconnected in 3 s, **to the old replica**.
* The old replica stopped about 60 s later. With the fix below it sent **1012
  restarting**, and the host reconnected to the new replica in 2 s.

In that window, devices that reconnect land on different replicas and cannot see one
another. The spike makes the window as short as one relay allows: it closes every
socket with 1012 on shutdown, and pings with a required pong (`KeepAliveTimeout`) on
both ends, so a socket dropped without a close frame is noticed. The product relay
needs a **backplane**: shared presence plus forwarding between replicas, keyed by
account (Redis pub/sub, or an Azure Web PubSub-style service), so it can roll and run
more than one replica. Note that .NET's `KeepAliveInterval` on its own only sends
unsolicited pongs and never notices a dead connection.

### 4. A GitHub App is a good fit, and the relay keeps no GitHub credential

The registered app is a GitHub App. Its device flow works unchanged, and a user token
carries only the app's permissions, which here are none beyond identity. Without the
app's client secret the relay cannot revoke the token, so it relies on GitHub App user
tokens expiring (8 hours by default). Either way the relay discards the token after
one `/user` call and stores nothing but the account id and login inside its own signed
token. **Account tokens are bearer tokens in the spike**: 30 days, not bound to a
device. The product should bind them to the device key, so a stolen token is useless
without the key.

### 5. Proof of possession without signatures

ECDH-only keys (WebCrypto non-extractable ECDH, the TPM's ECDH keys, the Secure
Enclave) cannot sign, yet the relay still needs to know a device holds the key it
names. An HMAC over a relay nonce, keyed by ECDH with a fresh relay ephemeral, does
it with the same primitive the channel already uses. That keeps S6's TPM question to
"can it do ECDH", which it can.

### 6. Nothing a peer or client does is trusted to be tidy

Review and a flaky check found two more things the product must carry:

* **A host bounds what peers can make it hold.** A caller that exits never says so, and
  a hostile device can start handshakes it never finishes. The hub now expires
  unfinished handshakes, drops a peer's channels when it leaves presence, and ignores
  new handshakes past a cap.
* **A relay that refuses a client must let it hear why.** Ending the request right
  after a close frame reset the TCP connection while the client was still sending,
  and the reset discarded the close frame unread (the flood check failed 2 runs in 4).
  The relay now reads and discards until the client answers, for up to 2 s. That run
  then passed 8 times in 8.
* The per-account device cap is checked and taken under one lock, so concurrent
  admissions cannot both see room.

## Limitations

* **Real phone not yet tested.** The Pages build has run in desktop Chromium and
  WebKit at the real origin. The iPhone run follows once this merges and Pages deploys.
* **No device roster yet.** Any device that signs in to the account can see and reach
  every other device; pairing approval and the signed roster (§5.4) are P4. A host
  will need to refuse channels from keys not in the roster.
* **Presence is not private between your own devices**, and the device names a
  device reports are visible to the relay.
* Rate limits are per connection and per IP for sign-in; there is no per-account
  quota or abuse handling beyond the device cap.
* The live relay runs on an Azure free-trial subscription. It needs moving to a paid
  subscription before the trial ends, or it stops.

## Reproduce

```powershell
cd spikes/s4-relay
dotnet build src/S4.DevStack -c Release
dotnet src/S4.DevStack/bin/Release/net10.0/s4-devstack.dll check
npm ci; npx playwright install chromium webkit
node js/test-pwa.mjs
```

Against a live relay, using a GitHub App or OAuth app with device flow enabled:

```powershell
dotnet build src/S4.Host -c Release
$h = 'src/S4.Host/bin/Release/net10.0/s4-host.dll'
dotnet $h sign-in --relay https://<relay>
dotnet $h run --relay https://<relay> --name my-host
dotnet $h call --relay https://<relay> --profile second --to my-host
```

The relay reads `Relay__GitHubClientId`, `Relay__TokenKey` (32 random bytes, base64,
kept as a secret), `Relay__ServePwa=false` and `Relay__AllowedOrigins__0`.
