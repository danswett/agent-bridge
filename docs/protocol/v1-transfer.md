# Bridge Protocol v1: session transfer

Bringing a session that ended on one of the account's machines to another, and resuming it
there as a session of its own, as agent-ha-bridge's cross-machine resume does
(`docs/cross-machine-resume.md` there). A person's device asks the machine the session is
going to (the **target**) with `session.launch`, carrying a command it signed for the machine
the session is on (the **source**). The target opens a channel to the source and passes the
command on with `session.transfer`; the source packs the session's transcript, seals it into
the relay's blob store for the target alone, and answers with the key. The target fetches it,
installs it under a new id and resumes it.

It extends [v1-launch.md](v1-launch.md) and [v1-sessions.md](v1-sessions.md), whose rules
hold here. The bundle travels as a blob ([blobs.md](blobs.md)). C# records are in
`src/AgentBridge.Protocol/Transfer.cs`, the signature in `src/AgentBridge.Host/SignedCommands.cs`;
the PWA signs in `pwa/js/transfer.js`. How a Windows host carries one out, and its settings,
are [host-transfer.md](../host-transfer.md). Design §9 ("Session transfer", "Fleet secret
retired"), §5.5.

**A fork, not a move.** The session arrives under a new id and the original stays where it
was, untouched, as in agent-ha-bridge. Nothing has to be reserved across machines, and either
copy can go on without the other.

**No fleet secret.** agent-ha-bridge signed transfers with a secret shared by every machine,
which proved only that the sender held it. Here the source acts only on a command a device of
the account signed for it, naming that session and the machine it goes to, once, for minutes,
and the bundle goes end to end: the relay and storage see ciphertext and sizes.

## Capabilities

On a channel through the relay only; never on the local pipe.

| Capability | Meaning |
|---|---|
| `sessions.transfer` | `session.launch` takes a `transfer`: this machine brings sessions in. Offered with `sessions.launch` while the host is connected to a relay with a blob store and `launch.transfer.receive` is on |
| `sessions.transfer.serve` | `session.transfer`: this machine hands its ended sessions on. Offered while it can launch, is connected to a relay with a blob store, and `launch.transfer.serve` is on. A client lists a machine's sessions for a transfer only where it says so |

## Listing what can be brought

There is no separate op. A client asks each other machine with `sessions.transfer.serve` for
`launch.options` over a short channel of its own, and offers its `resumable` sessions, by
machine, alongside the target's own. Those are exactly the sessions the source would resume
itself: ended, never live, in a folder approved at it ([v1-launch.md](v1-launch.md#launchoptions)).
A machine that is asleep or does not answer is left out.

## `session.launch` with a transfer

```json
{
  "request": "2b6e1f0a-7c3d-4e5f-8a9b-0c1d2e3f4a5b",
  "agent": "copilot",
  "workspace": "Bridge",
  "permissions": "ask",
  "transfer": {
    "from": "3f9a2c71b0de4a5f8c6d7e8f9a0b1c2d",
    "session": "0f3c4d5e-6a7b-4c8d-9e0f-a1b2c3d4e5f6",
    "command": { "body": "eyJ2IjoxLC…", "by": "BKzKtn9kLPn5…", "mac": "_PJ9LEydaL4_…" }
  }
}
```

| Field | Notes |
|---|---|
| `transfer.from` | The source's device id (32 lowercase hex), from presence |
| `transfer.session` | The session's id on the source: one of its `resumable` |
| `transfer.command` | The signed command below, unchanged |
| `workspace` | **Required.** The session's own folder is on the other machine; it opens in this workspace here, in a new worktree if the workspace is isolated. No default is assumed |
| `agent` | **Required.** A launcher installed here that runs the session's kind: the one it lists on the other machine, or another of the same kind. Checked against the bundle once it arrives |
| `resume` | Not with `transfer` |

Everything else (`profile`, `model`, `effort`, `context`, `permissions`, `prompt`,
`attachments`) is as for any launch. The outcomes are a launch's. While the bundle is being
fetched the launch has not started anything, so a failure there is `refused`, with the source's
reason. A started launch's `launched` says where it came from:

```json
"launched": { "agent": "copilot", "workspace": "Bridge", "resume": "<new id>", "cwd": "…", "transfer": { "from": "3f9a…", "session": "0f3c…", "bytes": 3271456 } }
```

`resume` is the session's new id here, which `session` also names. A transfer takes longer
than a launch: a client waits up to twelve minutes for its outcome before resending.

## The signed command

A person's device signs a command for one machine. Device keys are P-256 ECDH keys, a TPM's or
WebCrypto's non-extractable ones, which cannot sign, so the signature is a MAC that only the
signing device and the machine it is for can make, from static-static ECDH between them:

```
key = HKDF-SHA256(ikm:  ECDH(signer, machine),
                  salt: "agent-bridge command v1",
                  info: "<signer device id>|<machine device id>", length 32)
mac = HMAC-SHA256(key, body)
```

`body` is the command's JSON, UTF-8, sent as those exact bytes (base64url, no padding) so
neither end has to write JSON the same way. `by` is the signer's public key, uncompressed
(65 bytes, base64url); a device id is the first 16 bytes of its SHA-256, in hex. The target,
which carries a person's command, cannot change a byte of it: it is not the machine that key
is shared with.

**The MAC proves which key signed, not that a person did.** Any machine of the account can
make a key and sign a command of its own, naming itself. So the source also requires the
signer to be someone other than the machine asking, and a person's device connected to the
relay at that moment (below).

```json
{
  "v": 1,
  "action": "session.transfer",
  "request": "7d0f3a2e-1c4b-4d5e-8f60-112233445566",
  "principal": { "kind": "user", "device": "<signer device id>" },
  "target": { "machine": "<source device id>", "session": "0f3c4d5e-6a7b-4c8d-9e0f-a1b2c3d4e5f6" },
  "to": "<target device id>",
  "issuedAt": 1793300000,
  "expiresAt": 1793300300
}
```

The source acts on a command only if all of these hold, and says which did not:

* `body`, `by`, `mac` are base64url within 2048, 96 and 64 characters; `by` is a point on
  P-256; the MAC is the one it computes with its own key;
* `v` is 1, `action` is `session.transfer`, `request` is a UUID;
* `principal.kind` is `user` and `principal.device` is the id of the key that signed it;
* `target.machine` is the source itself, and `to` is the machine on the channel asking;
* the signer is neither the machine asking nor `to`, and is in the source's relay presence
  now, as a person's device (kind `pwa`, `app` or `phone`), not a `host`. The relay
  admits a device only with the account's sign-in and proof of its key, so presence says the
  key is a device of the account; the person pressing Launch is connected while they wait;
* it lasts at most 10 minutes (`expiresAt - issuedAt`), was not issued more than 2 minutes
  in the source's future, and has not expired;
* its `request` has not been acted on before. The source remembers it until it expires,
  so one press brings a session over once, however often the launch is resent.

The target checks the command's body too, unsigned, before it opens a channel: it must name
the device that sent the launch as the principal, this machine as `to`, and `transfer.from` and
`transfer.session` as its target.

The PWA signs a command once per press, for five minutes, and a resend of that launch carries
the same command. A test vector keeps the two implementations to one MAC
(`SignedCommandTests.The_same_bytes_and_keys_give_the_MAC_the_PWA_computes`,
`pwa/test/unit/transfer.test.mjs`).

**What this does not stop yet.** A device's kind in presence is what it said when it connected.
Until a device roster exists (design §5.4), anything holding the account's sign-in, including
a compromised machine of the account, can connect a new key calling itself `pwa` and sign with
it. With a roster, a source will require the signer to be an approved person's device.

## `session.transfer`

Host to host, on a channel the target opens to the source through the relay. Request `data`:
`{ "command": { "body", "by", "mac" } }`. Nothing else: everything the source acts on is in
the command. Result:

```json
{
  "blob": { "id": "<ticket>", "key": "<base64url, 32 bytes>" },
  "size": 3271456,
  "sha256": "<base64url>",
  "agent": "copilot",
  "launcher": "agency",
  "title": "Fix the flaky test - bridge",
  "files": 37
}
```

| Field | Notes |
|---|---|
| `blob` | The bundle, sealed for the target alone ([blobs.md](blobs.md)). The key travels only here, inside the channel |
| `size`, `sha256` | The bundle's bytes and their SHA-256. The target refuses one over its own `maxBytes` before fetching it, and one whose hash does not match after |
| `agent`, `launcher` | The session's kind, and what it ran in there. A launch naming an agent of another kind is `refused` |
| `title` | As `resumable` lists it |
| `files` | How many files the bundle holds, at most 20,000 |
| `agentVersion` | Optional, reserved: the agent's version there. Not sent yet |

An error is the op's usual `{ ok: false, error }`, its reason in words, which the target
passes on in its `refused` reason. The source refuses a session that is not on its resume
list, or is running or starting there; one whose files changed while being packed; one over
the size limits; and while two other transfers are being packed.

The target deletes the blob from the store as soon as it has the file, whatever the outcome,
and the source deletes its packed copy as soon as it is uploaded. Storage deletes any blob a
day after it was written regardless.

## The bundle

A zip of the session's files. Entry names are `/`-separated and relative to the session, not
to the agent's home; the receiving host decides where each goes:

| Agent | Entry names | From, on the source (agent home) |
|---|---|---|
| Copilot (and Agency) | `events.jsonl` (required), `workspace.yaml`, `checkpoints/…`, `files/…` | `session-state/<id>/` |
| Claude | `<id>.jsonl`, the one entry | `projects/<project>/<id>.jsonl` |
| Codex | `rollout-<stamp>-<id>.jsonl`, the one entry | `sessions/<yyyy>/<MM>/<dd>/` |

Links are never followed when packing, and each file is read only to the length it had when
packing began. Installing accepts only those names, refuses `..`, rooted paths, drive letters,
backslashes and duplicates, counts what it unpacks against a limit, requires the transcript,
and rewrites transcript lines of at most 16 MiB each (a longer line is refused). The old id is replaced by the new one in the transcript and its metadata, and the
folder it now opens in replaces the other machine's where each agent records it (Copilot's
`workspace.yaml`, Claude's `cwd` on each line, Codex's `session_meta` and `turn_context`).

## Limits

| Limit | Default | At most |
|---|---|---|
| Bundle, compressed (`launch.transfer.maxBytes`, each machine) | **25 MiB** | **200 MiB** |
| Read from disk to pack one (`launch.transfer.maxSourceBytes`) | 10 × `maxBytes` | 20 GiB |
| Unpacked on install (`maxSourceBytes` of the target) | 10 × `maxBytes` | 20 GiB |
| Files | 20,000 | |
| Command lifetime | 5 minutes (PWA) | 10 minutes |

Each side applies its own `maxBytes`, and the relay's largest blob bounds both. A session over
the limit is refused with its size and what to do: `/compact` it first, or raise
`launch.transfer.maxBytes` on both machines.
