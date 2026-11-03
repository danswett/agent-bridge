# Apple / .NET interoperability probe

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
