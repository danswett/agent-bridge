# How the host moves sessions between machines

A session that ended on one of the account's machines can be resumed on another, as a copy
under a new id ([contract](protocol/v1-transfer.md)). Both halves are in every host:
`src/AgentBridge.Host/SessionTransfers.cs` (fetching and serving, over the relay),
`SignedCommands.cs` (the command a person's device signs), and
`src/AgentBridge.Host.Delivery/Launch/SessionBundles.cs` (packing and installing a session's
files). `LaunchService` drives it as a kind of launch. Settings are `launch.transfer` in
[host-config.md](host-config.md).

## What agent-ha-bridge does

From `docs/cross-machine-resume.md` there, `hooks/session-launch.ps1`
(`Get-BridgeSessionBundleSpec`, `New-BridgeSessionBundle`, `Install-BridgeSessionBundle`),
`hooks/daemon-launch.ps1` (`Invoke-DaemonTransferRequest`, `Receive-DaemonSessionTransfer`,
`Resolve-DaemonRemoteResume`) and `hooks/daemon-sessions.ps1` (`Get-DaemonResumableExport`):

* Each machine publishes its resumable sessions; a launch card on one lists the others'.
* Picking one asks its machine, over MQTT, signed with a **fleet secret** every machine
  shares, to pack the session (Copilot's `session-state/<id>`, Claude's project transcript,
  Codex's rollout) and send it to the asking machine.
* The receiver installs it under a **new id** and resumes it; the original stays. Bundles are
  capped at 25 MB (`newSession.transferMaxBytes`), at most 200 MB.
* Off until set up: a machine serves nothing without `newSession.transferSecret` (the same on
  every machine) and lists other machines' sessions only with `newSession.transferResumable`.
* Only transcripts move, never credentials: copying an agent's credentials elsewhere spends
  them. The target must already be signed in to that agent.
* `copilot --session-id <id>` on a machine without that session silently starts an empty one
  with that id, so an install is checked before anything is launched.

## What this host does

The same fork under a new id, with the same caps, and these differences:

* **No fleet secret.** The source acts only on a command a device of the account signed for
  it (design §5.5), once, within minutes, for that session and the machine it goes to. The
  target carries it but cannot forge or change one.
* **End to end.** The bundle is sealed for the target alone and goes through the relay's
  blob store ([blobs.md](protocol/blobs.md)); its key travels inside the Noise channel
  between the two hosts. The relay sees two small requests and ciphertext.
* **Nothing is published.** A client asks each machine for its resumable sessions when it
  needs them, over a channel; no list is kept anywhere else.
* **Where it opens is chosen here.** The session opens in a workspace approved on the
  target and picked in the launch, in a new worktree if that workspace is isolated
  (invariant 7). agent-ha-bridge opened it in the workspace's path the same way; it never
  reuses the source's path.
* **On by default** (`launch.transfer.serve` and `.receive`), with nothing to set up.
  agent-ha-bridge's setup was distributing the shared secret, which a broker anyone on it
  could publish to made necessary; here each transfer needs a press signed by a device of the
  account, for that session, and goes end to end. Either can be turned off per machine.

## In order

On the **target**, for a `session.launch` with a `transfer` from a device's channel:

1. `LaunchProtocol.Check`; `launch.transfer.receive` on; a workspace named and approved; the
   agent named and installed. Not from the local pipe.
2. The command's body is read, unsigned, and must name the device asking as the signer, this
   machine as `to`, and the session and machine the launch names.
3. Waits up to 10 seconds for the source in presence, opens a channel to it, and sends
   `session.transfer` with the command; waits up to 10 minutes.
4. Checks the result (size against its own `maxBytes`, hash, file count, launcher), fetches
   the blob into a new file in its private staging folder (`<profile>/transfers`, owner only,
   never TEMP; invariant 9), checks the SHA-256, deletes the blob, closes the channel.
5. Refuses a bundle of another agent kind than the launcher named.
6. As any launch: worktree if isolated, then installs the bundle under a new id (below) and
   resumes it with the launcher. `launched.transfer` says where it came from.

Anything that fails before step 6 leaves nothing behind, and the launch is `refused` with
the reason, the source's words included.

On the **source**, for `session.transfer` from another host's channel:

1. `launch.transfer.serve` on, launching available, connected to a relay with a blob store.
2. The command is verified ([contract](protocol/v1-transfer.md#the-signed-command)), must
   name the machine asking as `to`, and is remembered until it expires: a second use is
   refused.
3. The session must be on this machine's resume list now: ended, never live or starting,
   in an approved folder. At most two are packed at once.
4. It is packed read-only into a zip in the private staging folder. Links are never followed.
   A file that changes while it is read fails the transfer ("it is probably open somewhere").
5. Refused if larger than the smaller of `maxBytes` and what the relay's blob store takes,
   with the size and what to do (`/compact`, or raise `maxBytes` on both machines).
6. Sealed and streamed to the blob store for the target, then the zip is deleted. Packing and
   uploading must finish within 9 minutes (the target waits 10): storage that stalls would
   otherwise hold the zip and a sending slot until the host restarts.

A host that stops part way leaves only its own names in its staging folder (`<hex>.zip`,
`unpack-<hex>`), and deletes them when it next starts.

## Installing a bundle

`SessionBundles.Install` unpacks into a fresh folder in the private staging folder, then:

* accepts only the names a session of that agent has, never `..`, a rooted path or a name
  twice, and stops at `maxSourceBytes` unpacked or 20,000 files;
* requires the transcript;
* replaces the old id with the new one in the transcript and its metadata (`.jsonl`,
  `.yaml`, `.json`, `.md`), line by line;
* records the folder it now opens in where the agent and this machine's resume list read it:
  Copilot's `workspace.yaml` `cwd`, each Claude line's `cwd`, Codex's `session_meta` and
  `turn_context` `cwd`. Left as the other machine's, a folder not approved here, the copy
  would drop off the resume list once it ended;
* moves it where the agent looks: Copilot `~/.copilot/session-state/<new id>`; Claude
  `~/.claude/projects/<folder slug>/<new id>.jsonl`; Codex
  `~/.codex/sessions/<yyyy>/<MM>/<dd>/rollout-<stamp>-<new id>.jsonl`;
* checks it is where the agent looks for that id, and removes it otherwise, so a Copilot
  launch never starts an empty session under the new id.

Known gaps: Codex's version is not compared (agent-ha-bridge only warned). The Windows app does not offer transfers yet
(#49); the phone does.

## The log

`launch <id>: bringing session <id> from <machine>`, then the launch's usual lines, on the
target; `sent session <id> to <machine> for <device>: <size>, <n> file(s)` or
`transfer … refused: <why>` on the source. Ids are cut to eight characters; nothing from a
transcript is logged.
