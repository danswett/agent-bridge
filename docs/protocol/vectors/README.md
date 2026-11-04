# Protocol vectors

These are shared test vectors for Bridge Protocol v1. They give examples of every record the
protocol defines and of every signed command. Each record has valid examples, and examples a
reader or a host must refuse. Every implementation replays the same files, so a field renamed or
a limit moved in one of them fails in the others. The specs ([v1-sessions.md](../v1-sessions.md)
and the other `v1-*.md` files) stay the contract. These files are its worked examples, and they
are generated.

| File | Records |
|---|---|
| `sessions.json` | `hello`, sessions and their parts, `sessions.list`, `sessions.subscribe` and its events (`session.patch` included), answers, replies, `session.stop` |
| `attachments.json` | `attachment.start`, `attachment.chunk`, `attachment.discard` and their results |
| `push.json` | `push.subscribe`, `push.unsubscribe`, `push.list` and the notice a push carries |
| `launch.json` | `launch.options`, `session.launch`, folder-trust questions and `launch.trust.answer` |
| `transfer.json` | A launch's `transfer`, the signed command's parts, `session.transfer` (host to host) |
| `update.json` | `update.get`, `update.state`, `update.check` and `update.install` |
| `usage.json` | `usage.get`, `usage.update` and shared readings |
| `signed-command.json` | Fixed keys, the commands they sign with the MAC each gives, and commands the machine must refuse |
| `session-patches.json` | The recorded sessions in `tests/fixtures` as a subscriber that asked for patches is sent them: the snapshot's upsert, then each `session.patch` with the session the client must then hold |

## Shape

Each record file holds `records`, keyed by the C# record's name:

```jsonc
"ReplyRequest": {
  "direction": "client",            // who writes it: host, client, or host-to-host
  "check": "Replies.Check",         // the host's check after reading it, if it has one
  "valid": [
    { "name": "text", "json": { … } },
    // A valid vector with "reads" is not what a writer sends (an unknown enum value, a field
    // from a newer version, a field missing or of the wrong type that a reader reads past), but a
    // reader takes it and holds what "reads" shows.
    { "name": "…", "json": { … }, "reads": { … } }
  ],
  "refused": [
    // "parse": the reader refuses the record (for a host's, a strict field missing or of the
    // wrong JSON type; for any other, any required field missing or any field of the wrong
    // type). Otherwise the check named, with its reason word for word.
    { "name": "missing-request", "json": { … }, "refusedBy": "parse" },
    { "name": "a-slash-command", "json": { … }, "refusedBy": "Replies.Check", "reason": "a reply cannot start with /, …" }
  ]
}
```

A record a host sends a client (`"direction": "host"`, but for `push.json`'s) is read as
[v1-sessions.md, Malformed records](../v1-sessions.md#malformed-records) says (#446): a field the
reader can't read is absent and a missing required one its type's default, so most `missing-…`
and `wrong-type-…` vectors of such a record are valid, with what they read as; only a strict
field refuses it. Every other record (what a client sends, one host's to another, a push) is
read as written: any required field missing or field of the wrong type refuses it.

A vector that needs more than its record names a `context`, which the file's `contexts` holds
once. For example, the question an answer answers. A vector is named `<Record>/<name>`.

`knownDifferences` lists each place where an implementation does not yet do what a vector says,
with the issue that decides which side moves:

```json
{ "vector": "Session/missing-status", "issue": 446, "differs": "pwa", "step": "read", "csharp": "…", "pwa": "…" }
```

The expectations are what the C# records do, because they generate the files. That does not make
C# right. A replay checks that each known difference still differs, so fixing one fails the
replay until its entry goes.

## Who replays them

- **C#:** `tests/AgentBridge.Protocol.Tests/ProtocolVectorTests.cs`. Each valid vector reads into
  its record (a host's through `Received.Read`) and writes back as it `reads`. Each refused vector
  fails to read, or its check gives its reason. Every record in `AgentBridge.Protocol` has valid vectors.
  `tests/AgentBridge.Host.Tests/SignedCommandVectorTests.cs` verifies every signed command with
  `SignedCommands.Verify`, and `PushVectorTests.cs` there holds every valid `push.subscribe` to the
  host's own check (`PushNotifier.Problem`). `tests/AgentBridge.Host.Core.Tests/PatchReplayTests.cs`
  writes `session-patches.json` from the host's own replay of the recordings, and applies each
  patch with `SessionPatches.Apply`. Each `SessionPatch` in `sessions.json` names a context with the
  session the client holds (`session`, absent for none) and the `seq` of the event it came in, and,
  for a valid patch, the `result` applying it must give exactly; a refused one gives
  `SessionPatches.Apply`'s reason.
- **PWA:** `pwa/test/unit/protocol-vectors.test.mjs`. Each host record goes through the PWA's own
  readers (`protocol.js`, `launch.js`, `usage.js`, `update.js`, `host-client.js`, the service
  worker). A valid vector must keep every part the PWA uses, and a refused one must be dropped.
  `checkAnswer`, `checkReply` and `checkLaunch` must agree with the host's checks word for word.
  The answer, reply, stop, launch, list, attachment and usage requests are built by the PWA's own
  services and must equal their vectors. An attachment starts from a picked file prepared as the
  reply box prepares one, and a refused start a person could cause (a blank or long name, an
  empty or oversized file) must never be sent as it is. A chunk comes from a file of the size its
  context gives. The signed commands are signed again with the fixed keys and must give the same
  bytes and MAC. A record the PWA does not replay is listed there with the reason. Each `SessionPatch`
  is applied with `session-patch.js` as its check, and `session-patches.json`'s recordings go
  through it alone and through a subscription for patches, step by step.
- **Swift** (the `apple/` package): not yet. To adopt the folder, read each file as above. Each
  `host` record goes through the package's decoders: a valid vector keeps every field the app
  uses, and a refused one is dropped. Each `client` record goes through its encoders and any
  checks it mirrors. The signed commands are signed again with the fixed keys. A disagreement is
  then added as a known difference with `"differs": "swift"` and an issue, in
  `tests/AgentBridge.Protocol.Tests/KnownDifferences.cs`.

## Regenerating

The C# records generate the files: `ProtocolVectors.cs` and `KnownDifferences.cs` in
`tests/AgentBridge.Protocol.Tests`, `SignedCommandVectorTests.cs` in `tests/AgentBridge.Host.Tests`,
and `PatchReplayTests.cs` in `tests/AgentBridge.Host.Core.Tests`. A test in each project fails when the committed files differ
from what they generate. After a deliberate change to a record, a limit, a check's words or the
vectors themselves, write them again:

```powershell
$env:UPDATE_PROTOCOL_VECTORS = '1'
dotnet test tests/AgentBridge.Protocol.Tests --filter "FullyQualifiedName~The_committed_vectors"
dotnet test tests/AgentBridge.Host.Tests --filter "FullyQualifiedName~The_committed_signed_command_vectors"
dotnet test tests/AgentBridge.Host.Core.Tests --filter "FullyQualifiedName~The_committed_patch_sequences"
Remove-Item Env:UPDATE_PROTOCOL_VECTORS
```

(`UPDATE_PROTOCOL_VECTORS=1 dotnet test …` in bash.) Then run the PWA's unit tests. Commit the
files with the change that moved them.
