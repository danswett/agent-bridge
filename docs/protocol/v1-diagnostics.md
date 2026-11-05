# Protocol v1: diagnostics (`diagnostics.get`)

"Diagnose this machine" (#802): a person's phone or browser asks one of their machines how it is,
reads the answer and copies it (for example, into an issue they are writing) only after they have
reviewed it. The host answers with its content-free journal (#737) and a closed snapshot of its
health. Nothing is sent anywhere else: there is no automatic upload, issue, telemetry or inbox.

The read is served by `RemoteDiagnostics` (src/AgentBridge.Host). The wire records are in
`src/AgentBridge.Protocol/Diagnostics.cs`, and the journal's entry records are in
`src/AgentBridge.Protocol/Journal.cs`.

## Who may ask

| Rule | Why |
|---|---|
| Only through the relay's end-to-end channel (a Noise channel), never on the machine's own pipe | Agents, the MCP server and the Home Assistant connector use the pipe |
| Only a device pinned at this machine with the `person` role (`RemoteAuthorization.IsPinnedPerson`) | The role is the locally pinned grant. It is never the kind the relay lists, the device's introduction, or anything in the request. A service, connector, host or agent pin gets neither the capability nor the read. Revoking the pin closes the channel |
| Not while trusted-peer pins are off | Then only the relay's word for a device's kind would remain, which is not enough for this |
| The request names `maxEntries` only | Any other property is refused rather than ignored. That includes a principal, origin or role a device might claim |

It is read-only. It reads in-memory state and the journal's bounded in-memory snapshot. It runs no
command, and it reads no setting, credential, environment or log file. It never restarts or
repairs anything.

## Capability

hello's `capabilities` include `diagnostics` only on a channel this host would serve it to.

A client with no `diagnostics` from a host does not ask. Its Diagnose button then says the machine
cannot be diagnosed from there and should be updated. It sends no request and does not retry.

## `diagnostics.get`

Request `data`: `{}` or `{ "maxEntries": n }`. `n` is a whole number from 1. It is clamped to 200,
and the default is 100. The request is at most 256 bytes.

Result: a `DiagnosticsSnapshot`, written in camelCase with enum names in camelCase.

| Field | What |
|---|---|
| `schema` | `1` |
| `at` | When it was read |
| `host` | `version` and `dev`: a validated version, or `null` with `versionMissing`. Also `startedAt` and `uptimeSeconds` |
| `lastRestart` | `at` is this process's start. `causeMissing: "notRecorded"`: the cause is not recorded yet |
| `relay` | `state` is one of `off`, `signedOut`, `connecting`, `connected` or `refused`, plus `since`. The relay's address and detail are never included |
| `holds` | `readOnly` is one of `none`, `configReadOnly`, `configProblem`, `agentHaBridge` or `other`. `discoveryHeld` lists the agent kinds whose sessions are being held. Reason text stays on the machine. Whether agent-ha-bridge is running comes from the last check something else made (a hello, the hello watch every 10 seconds, or an action). A read never makes a check of its own and never waits on one. With no check in the last 30 seconds, and no read-only setting, `readOnly` is `null` with `readOnlyMissing: "notStarted"`, never `none`. When the last check could not tell, it is `null` with `readOnlyMissing: "failed"`, never `agentHaBridge` |
| `readiness` | `checkedAt`, and up to 8 `agents`. Each has an `id` (`copilot`, `claude`, `codex` or `other`), a validated `version`, the states of `installation`, `versionCheck`, `hooks`, `authentication` and `activation`, and `lastHookHandledAt`. `omitted` counts any agents beyond 8. Labels, folders, executables and details are never included |
| `journal` | See below |

Every field in the table is always sent. One with no value (a `version`, `since`, `checkedAt`,
`lastHookHandledAt`, `discoveryHeld`, `readOnly`, or `lastRestart`'s `at` or `causeMissing`) is `null`,
never left out, and a client may refuse a snapshot that leaves one out. The `missing` codes below
are left out when there is nothing missing.

Missing information is never filled in with a healthy default. A part the host does not have is
given with `missing` (or `versionMissing`, `causeMissing`, `discoveryMissing` or `readOnlyMissing`) set to one of these codes:

- `notRecorded`: the host keeps no record of it.
- `notStarted`: the source has not started or reported yet.
- `notConfigured`: the host runs without it.
- `invalid`: it failed validation and is withheld.
- `failed`: reading it failed.

A client reads a code it does not know as `unknown`.

### `journal`

`entries` holds the journal's entries, newest first, each in `JournalCodec`'s canonical JSON form
(Journal.cs). Their enum names are as declared in Journal.cs, not camelCase.

Before an entry is sent, the host writes it and reads it back strictly with `JournalCodec.TryRead`,
which is the journal's one sanitizer. An entry that does not read back is dropped and counted in
`invalid`.

Each entry carries:

- Closed codes and numbers: family, kind, outcome, code, duration and stages.
- A numeric version.
- An exception type and frames, taken only from the journal's closed, trusted sets, or
  `<unrecognized>` / `<external>` otherwise.
- An `omitted` flag set for anything it had to leave out. An entry whose exception type reads as
  `<unrecognized>` always has `ExceptionTypeUnrecognized` set, however it arrived.

Entries never hold a message, prompt, title, path, account, URL, token or stack text.

The counters say what is not shown, so a short list never reads as a quiet machine:

- `omittedByLimit`: entries beyond the request's limit or the journal's byte limit.
- `omittedBySize`: entries the reply's size limit left out.
- The journal's own counters: `evictedByCount`, `evictedByAge`, `evictedByBytes`, `rejectedOversized`,
  `rejectedMalformed`, `droppedOnLoad`, `loadFailed` and `persistFailures`.

What the journal covers is only what its producers record into the host's own journal file. The
Windows app's crash records go to the app's own journal file, which is not part of this snapshot.

## Bounds

| Bound | Value |
|---|---|
| Journal entries | At most 200 (the default is 100) |
| Journal bytes | 32 KiB of canonical entries |
| Whole reply | 40 KiB, within one channel message without compression. The oldest entries are dropped first and counted in `omittedBySize` |
| Readiness agents | 8 |
| Concurrent reads | One per host. Another read at the same time is refused |
| Per device | One read every 2 seconds. Asking sooner gets a refusal, never another read |
| Time | 5 seconds on the host, whatever the client waits. A read is also given up when its channel has closed or its caller cancels |

The read is in memory, with no file or network work. Its only wait is for the journal, while a
producer appends to the journal file. That wait is cancelled at the deadline, so a given-up read
leaves nothing running and frees the one read slot at once. A channel that closes is noticed
between the read's steps, so within the same 5 seconds. A given-up read still counts as that
device's read for the 2-second limit. The relay's state is the last one published, never one
waited for behind the host's status (which reads its settings and looks at agent-ha-bridge). The
host's own log line for a read (its size, and the journal failure's type, never its text) is
written only after the reply is sent, since the log waits on a console and a file; reads given
up are counted and that count goes in the next served read's line.

Refusals:

- `diagnostics are for a person's device approved at this machine, through the relay`
- `diagnostics were read moments ago; try again shortly`
- `diagnostics could not be read in time; try again shortly`

## In the clients

The PWA and the Windows app show it the same way (ui.md §6.6).

- A machine whose hello offers `diagnostics` has a quiet, secondary **Diagnose** in its row. One
  whose hello does not shows no button, but the line "Can't be diagnosed from this device · update
  Slipgates on <machine>", and is never asked.
- The snapshot is shown as plain text (the PWA wraps it on a narrow screen; the app's box
  scrolls). It starts with
  "Slipgates diagnostics · <machine> · <time>". Times are shown in Pacific time, with PST or
  PDT; on the wire they stay UTC.
- The person ticks "I have read this and it is fine to share". Until then **Copy** is disabled and
  copies nothing. **Copy** puts the text on the clipboard and nothing else. Attaching it to an
  issue is the person's own paste; nothing is submitted for them.
- **Close**, or Escape in the report or on its **Copy** or **Close**, closes it and puts focus back
  on **Diagnose**.
- A refusal is said in the person's words, never the host's:

  | Host's reason | Shown |
  |---|---|
  | Not a person's device approved here | "Approve this device at <machine> to diagnose it" |
  | Read moments ago | "Diagnosed moments ago · try again shortly" |
  | Not read in time | "<machine> didn't answer in time · try again" |
  | Anything else, or no answer | "<machine> couldn't be reached · try again" |

- A reply is read strictly. A missing section or field, a wrong type, a `null` where the contract
  has none (an agent in `agents`, say) or a time that does not parse makes the whole reply
  unreadable: the row says "<machine> sent diagnostics this app can't read", shows nothing of it,
  and Diagnose can be pressed again. A version is shown only if it passes the journal's version
  rule (numbers, and a prerelease label of `alpha`, `beta`, `rc`, `preview`, `dev` or `ci`).
- An entry that does not read (a malformed time or sequence) is counted as unreadable and not
  shown.

In the PWA, an exception type is shown only if it is on the journal's closed list of runtime
exceptions (`JournalIdentifiers.RuntimeExceptions`), else `<unrecognized>`. Its frames are given
only as a count, because the product's frames can be checked only against its assemblies.

## In the Windows app

Another machine's row on the Sessions page has **Diagnose** when that machine is connected and
offers it. This machine's row has none, because its host is reached over the local pipe, which
never serves `diagnostics.get`.

The app reads each entry with `JournalCodec.TryReadFromAnotherProcess`. That read is as strict as
`TryRead`, with one difference: an exception type or frame that this app's trusted assemblies do
not list reads as `<unrecognized>` or `<external>`, with `ExceptionTypeUnrecognized` or
`FrameUnrecognized` set, instead of losing the whole entry. Such a name is never shown. Any other
field outside the contract (a version, code, stage, omission bit or unknown property) still makes
the entry unreadable, counted and not shown. The host captures its own executable's frames as
`<external>`, so the shared Host.Core and Hooks frames are the ones both sides keep.
