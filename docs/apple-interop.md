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
