# Isolated Apple and .NET interoperability

`tools/AgentBridge.AppleInterop` runs the product relay, Noise transport, session
parser, session service and Unix socket server together. Its transcripts and
process observations are synthetic. It never starts an agent, types into a
terminal, installs hooks, opens the default profile or contacts Home Assistant.
The identity-provider boundary is a loopback-only stub; the product relay still
performs its real device-code exchange, token issuance and device-key proof.

This supplements the offline Apple UI fixture. It does not establish real agent
delivery, real GitHub sign-in, physical-device networking or production relay
acceptance.

## Run on a development Mac

Requires the .NET 10 SDK and a currently valid development PFX with a private key
and a `localhost` DNS subject alternative name. Keep its password in a private
file. Trust for native Apple clients is an explicit workstation setup step; the
harness does not install certificates or weaken application TLS validation.

```sh
dotnet build tools/AgentBridge.AppleInterop -c Release --nologo
dotnet tools/AgentBridge.AppleInterop/bin/Release/net10.0/agent-bridge-apple-interop.dll \
  --certificate /path/to/development-server.pfx \
  --certificate-password-file /path/to/private-password-file \
  --manifest /path/to/private-directory/new-manifest.json
```

The manifest must not already exist. It is created with mode `0600` and contains
`relayUrl`, `localSocketPath`, `accountID`, `login`, `clientToken`, `hostDeviceID`,
`fixtureRoot`, `sessionID`, `delegateID`, `initialMarker`, `updatedMarker`,
`updateTriggerPath` and the public certificate fingerprint/expiry. Treat the
manifest as a secret; do not print or commit it. All session discovery roots and
the socket live inside a fresh `0700` temporary directory. Shutdown deletes both
the fixture directory and the manifest.

The advertised relay is HTTPS on an ephemeral loopback port. A separate HTTP
loopback listener lets the harness's .NET peer join the same relay without
changing OS certificate trust. That internal listener is not an Apple client
setting. Ambient relay/Kestrel settings and proxy configuration cannot redirect
this executable to a cloud service or add a public listener. The relay's blob
backend is its existing bounded in-memory development backend; no Azure account
is used. Native app clients retain their ordinary certificate checks.

The local API retains the product's caller guard. It offers session reads and
refuses privileged host management. No answer, reply, attachment, stop or launch
capability is fabricated. Existing real blob routes can be exercised separately
through the relay, without claiming that a synthetic session accepted a file.

## Drive a session change

Send one line on standard input: `advance` updates the transcript; `remove` removes
its synthetic process; `reconnect` reconnects the .NET host under the same device
identity; `quit` shuts down. A probe can also atomically rename a file containing
`advance` to the manifest's `updateTriggerPath`. The product parser then produces
the normal live session events. Standard output contains only redacted readiness
and command acknowledgements. Ctrl-C and the 30-minute lifetime bound also shut
the harness down.

The existing Mac app finds local sockets through profile names. An integration
probe can use `LocalBridge.connect(path:)` with `localSocketPath` directly. A
physical iPhone cannot reach a Mac's loopback listener; use a separately authorized
real relay for that acceptance step.

## Verify

```sh
dotnet test tests/AgentBridge.AppleInterop.Tests -c Release --nologo
```

The focused tests exercise real local and Noise channels, the product device flow,
snapshot/live updates, history/delegates, reconnect, read-only refusals, a supplied
TLS certificate, a 1 MiB encrypted blob round trip, private manifest creation and
cleanup after a late update trigger. The tests pin their generated certificate
only in their own observer; they do not change global certificate trust.

## Swift interoperability probe

`swift run --package-path apple agent-bridge-interop /absolute/path/to/manifest.json`
uses the public production Swift local transport, sign-in flow, WebSocket relay,
Noise channel, session decoder and subscription reducer against a separately
running disposable .NET stack. It opens no browser, accesses no Keychain and
keeps the relay token and device key in memory. JSON-lines output includes check
names, outcomes and numeric error codes only, never server bodies or credentials.

The manifest must be a regular file no larger than 32 KiB with owner-only
permissions. `fixtureRoot` must be a private directory under `/tmp` or this user's
temporary directory; both the socket and update
trigger must resolve inside it. The relay must be an HTTPS loopback origin with
a normally trusted certificate. No insecure transport or certificate override
exists in the probe or production APIs.

```json
{
  "relayUrl": "https://localhost:8443",
  "localSocketPath": "/private/tmp/apple-interop-run/host.sock",
  "accountID": "9001",
  "hostDeviceID": "0123456789abcdef0123456789abcdef",
  "fixtureRoot": "/private/tmp/apple-interop-run",
  "sessionID": "interop-session",
  "initialMarker": "interop-before",
  "updatedMarker": "interop-after",
  "updateTriggerPath": "/private/tmp/apple-interop-run/advance",
  "delegateID": "delegate-1"
}
```

The .NET stack must serve the same synthetic session through `LocalServer` and
`ChannelHub` using the actual `SessionService` and `SessionSource`. Its local
GitHub substitute auto-approves a disposable account, reports an interval of at
least one second, and returns `https://github.com/login/device` as its verification
URL. The probe never opens that URL or contacts GitHub. Production `RelayApp`
performs the normal token issuance, device proof and routing.

The probe verifies both snapshots and `session.get`, optional delegate detail,
advertised read-only capabilities and a refused synthetic `session.reply`. Once
both subscriptions are established it exclusively creates the private trigger
file containing `advance\n`. The harness must react by updating only its synthetic
transcript so `SessionSource` emits a response containing `updatedMarker`. Both
live feeds must observe the change; fresh local and Noise channels must obtain it
again after reconnect. Use a fresh fixture directory for each run. Blob integrity
is explicitly skipped because this read-only host offers no attachment backend.

This establishes interoperability of the real wire implementations and host
services. It does not establish native UI onboarding, Keychain persistence,
physical-device networking, GitHub's external device approval, or typing into a
real agent. Those require separate acceptance runs with the actual applications.
No installed host, agent registration, Home Assistant, MQTT or production relay
is needed or modified by this probe.
