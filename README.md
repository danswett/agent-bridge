# agent-bridge

A standalone app for watching and driving AI coding agent sessions (GitHub Copilot
CLI, Claude Code, Codex) across your machines and your phone. It is the successor to
[agent-ha-bridge](https://github.com/danswett/agent-ha-bridge), without the need for
Home Assistant.

> **Status: P0 spikes.** Nothing here is installable yet. agent-ha-bridge remains the
> supported product, and nothing in this repository changes or replaces it until a
> machine is explicitly migrated. How far each of agent-ha-bridge's features has come,
> with the evidence, and what must be true before a machine migrates:
> [docs/parity.md](docs/parity.md).

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

## Product code so far

| Path | What it is |
|---|---|
| `src/AgentBridge.Protocol` | Bridge Protocol v1 records and their JSON |
| `src/AgentBridge.Host.Core` | Discovers the live Copilot, Claude and Codex sessions on a machine and keeps each as a protocol `Session`, strictly read-only: it never writes, locks, renames or deletes anything the agents or agent-ha-bridge own. Also reads each agent's allowance (Copilot, Claude, Codex) at agent-ha-bridge's pace |
| `src/AgentBridge.Host` | `agent-bridge-host`: `sign-in --relay <url>` then `serve` shows the machine's sessions to the user's devices through the relay (Protocol v1), reconnecting on its own, types their answers to pending questions (`session.answer`) and their replies (`session.reply`), with files and images sent ahead (`attachment.*`), ends sessions (`session.stop`), notifies the account's phones by Web Push when a session asks or finishes (`push`), reports each agent's usage (`usage`, [v1-usage.md](docs/protocol/v1-usage.md)), and launches or resumes sessions in workspaces approved at the machine (`session.launch`, [v1-launch.md](docs/protocol/v1-launch.md)); `sessions`, `get <id>`, `watch` and `usage` check what the core sees, `workspace` and `config` edit the profile's `config.json` ([docs/host-config.md](docs/host-config.md)), and `shadow` compares what it shows with agent-ha-bridge's daemon, read-only, for a machine's cutover ([docs/host-shadow.md](docs/host-shadow.md)) |
| `src/AgentBridge.Host.Delivery` | Answering a question, sending a reply, ending a session and launching one: the keys for each question shape (pure and unit-tested), the transcript checks before and after typing, the two-press rule and process identity for ending, agent-ha-bridge detection, and the injector worker client. See [docs/host-answers.md](docs/host-answers.md), [docs/host-replies.md](docs/host-replies.md), [docs/host-stop.md](docs/host-stop.md) and [docs/host-launch.md](docs/host-launch.md) |
| `src/AgentBridge.Injector` | `agent-bridge-injector`: the NativeAOT worker the host starts to type into an agent's console, so a console closing mid-answer can never take the host down |
| `src/AgentBridge.Transport` | The end-to-end channel (Noise XX over P-256), the relay client, channels with requests, responses and events, sign-in, device keys (TPM, DPAPI or an owner-only file) and Web Push encryption. Promoted from spikes S2, S4, S5 and S6 |
| `src/AgentBridge.Relay` | The relay (ASP.NET Core), promoted from S4. `docker build -f src/AgentBridge.Relay/Dockerfile .` from the repository root. Deployed to Azure Container Apps from `infra/` (Bicep) by `.github/workflows/relay.yml`: staging, a smoke test, then production on approval. See [docs/relay-deploy.md](docs/relay-deploy.md) |
| `src/AgentBridge.Local` | The local API between a desktop app and its host: Protocol v1 over a named pipe (a Unix socket elsewhere) that only the same user at the same elevation can use, plus local-only host management ops. `serve` always opens it, and goes to the relay only once signed in. [docs/protocol/local-pipe.md](docs/protocol/local-pipe.md) |
| `src/AgentBridge.App` | The Windows app (WinUI 3, unpackaged, Velopack): this machine's sessions as cards at parity with the PWA, usage, toasts that take answers, and installing, starting, signing in and updating the host it ships with. `--demo` runs it against a pretend host. [docs/app-windows.md](docs/app-windows.md); `build/pack-app.ps1` builds the package |
| `src/AgentBridge.App.Core` | Everything in the app that is not drawing (connection, view models, toast plans, host lifecycle), platform-neutral and tested on Linux too |
| `src/AgentBridge.HostLauncher` | `agent-bridge-hostw`: starts the host with no console window, for the logon task |
| `src/AgentBridge.HomeAssistant` | The Home Assistant connector: a migrated machine's sessions, liveness, version and usage on the existing agent-ha-bridge dashboard, in agent-ha-bridge's own per-machine MQTT contract (status only so far; off by default). [docs/ha-connector.md](docs/ha-connector.md); `tools/ha-contract` records the contract fixtures from agent-ha-bridge's source |
| `tools/AgentBridge.RelaySmoke` | `relay-smoke --relay <url>`: checks a deployed relay from outside, and with its token key connects two devices over a Noise channel |
| `tests/` | `dotnet test AgentBridge.slnx`, on Windows and Linux in CI |

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
