# agent-bridge

A standalone app for watching and driving AI coding agent sessions (GitHub Copilot
CLI, Claude Code, Codex) across your machines and your phone. It is the successor to
[agent-ha-bridge](https://github.com/danswett/agent-ha-bridge), without the need for
Home Assistant.

> **Status: P0 spikes.** Nothing here is installable yet. agent-ha-bridge remains the
> supported product, and nothing in this repository changes or replaces it until a
> machine is explicitly migrated.

## Planned shape

* **Bridge Host** (C#/.NET, Windows and macOS): one per machine. It does what the
  agent-ha-bridge daemon does today: picks up hook events, tails transcripts, and
  delivers answers, replies, launches and stops.
* **Agent Bridge app** (WinUI 3): the desktop UI, notifications, and the installer and
  manager for the host and agent hooks.
* **Relay**: an untrusted, end-to-end-encrypted rendezvous so your machines and phone
  can reach each other from anywhere. It can be self-hosted, and there are LAN-only
  options.
* **Phone**: a PWA first, native apps later.

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
