# agent-bridge

A standalone app for watching and driving AI coding agent sessions (GitHub Copilot
CLI, Claude Code, Codex) across your machines and your phone. It is the successor to
[agent-ha-bridge](https://github.com/danswett/agent-ha-bridge), without the need for
Home Assistant.

> **Status: P0 spikes.** Nothing here is installable yet. agent-ha-bridge remains the
> supported product, and nothing in this repository changes or replaces it until a
> machine is explicitly migrated.

## Planned shape

Nothing depends on Windows: the host, hook, relay, protocol and all product logic are
shared on Windows, macOS and Linux, and each desktop gets a native app of equal
standing.

* **Bridge Host** (C#/.NET, Windows, macOS and Linux): one per machine. It does what
  the agent-ha-bridge daemon does today: picks up hook events, tails transcripts, and
  delivers answers, replies, launches and stops. It holds all the product logic.
* **Agent Bridge apps**: WinUI 3 on Windows and SwiftUI on macOS. Both are thin native
  shells over the host, providing the desktop UI, notifications, and the installer and
  manager for the host and agent hooks. Linux runs the host only, for now.
* **Relay**: an untrusted, end-to-end-encrypted rendezvous so your machines and phone
  can reach each other from anywhere. It can be self-hosted, and there are LAN-only
  options.
* **Phone**: a PWA first, native apps later.

The protocol between them is specified in [docs/protocol](docs/protocol/v1-sessions.md).

## Ground rules while this is being built

* Releases of this product are published **only here**, never in agent-ha-bridge,
  whose installed bridges update from that repository's latest release.
* This product installs under its own paths and names, leaves agent hook registrations
  alone, and never writes to an existing Home Assistant setup until a machine is
  explicitly migrated.

## Spikes

| Spike | Question | Result |
|---|---|---|
| [S1](spikes/s1-console-injection/RESULTS.md) | Can a console-less .NET process type into agent sessions? | GO, through an injector worker process |
| [S2](spikes/s2-noise-interop/RESULTS.md) | Can .NET and browser WebCrypto share one end-to-end encrypted channel? | GO: byte-identical in Node, Chromium and WebKit. [Run it on your phone](https://danswett.github.io/agent-bridge/s2/) |
| [S3](spikes/s3-winui-velopack/RESULTS.md) | Can an unpackaged WinUI 3 app install, update and uninstall itself with Velopack, and take answers from toasts? | GO, with OS toast activation instead of Windows App SDK's, and `EnableMsixTooling` for publish |
| [S4](spikes/s4-relay/RESULTS.md) | Can one untrusted relay serve every user, with GitHub sign-in and end-to-end channels from hosts and a PWA? | GO, live on Azure Container Apps. Needs a cross-replica backplane to deploy without splitting. [PWA](https://danswett.github.io/agent-bridge/s4/) |
| [S5](spikes/s5-web-push/RESULTS.md) | Can a host notify the phone PWA through Web Push without the relay or push service reading it? | GO on a real iPhone: lock-screen notice, tap, answer back at the host in 10 s |
| [S6](spikes/s6-tpm-keys/RESULTS.md) | Can a device's long-term key live non-exportable in the TPM and still do Noise, the relay proof and the device id? | GO, live with the relay and the iPhone. Needs NCrypt directly; .NET's wrappers fail with the TPM provider |
