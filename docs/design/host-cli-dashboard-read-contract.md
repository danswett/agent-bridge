# Host CLI dashboard read scope and output vectors (C10)

Docs/output contract for [#2498](https://github.com/danswett/agent-bridge/issues/2498),
the C10 cut in [the S4 inventory](host-cli-s4-evidence.md).
**Sensitive: account/person boundary.** No new dashboard command, write operation,
credential, provider probe, account transport or claim of dashboard/action parity.
Source baseline: `8639839ca352c4430739ddfd3cf770d9a67f5219`.

## Existing readers and reusable semantics

| Source | Reuse and limit |
| --- | --- |
| [Program](../../src/AgentBridge.Host/Program.cs), [SessionSource](../../src/AgentBridge.Host.Core/SessionSource.cs) | `sessions`, `get`, `watch`: local readable roots, session/question records, revision and discovery holds. No account roster or machine selector. |
| [LocalClient](../../src/AgentBridge.Local/LocalClient.cs) | Selected local profile's hello/status and served read requests; pipe access is not an account-scoped person credential. |
| [HostConnection / SessionStore](../../src/AgentBridge.App.Core/HostConnection.cs) | Hello, records, subscriptions, revisions, matching `sessions.synced`, gap recovery and host-run identity. Reuse these rules, not ad hoc JSON event concatenation. |
| [Machine / Fleet / RelayFleetLink](../../src/AgentBridge.App.Core/Machines.cs) | App's account-scoped presence/roster, per-host channels and current-versus-offline rows. Existing App device/authentication is not a grant to a new CLI. |
| [UsageService](../../src/AgentBridge.Host/UsageService.cs) | `usage.get`/`usage.subscribe` serve the running monitor's last report; optional `usage.shared` adds retained peer readings. Reading a report is not a fresh vendor query. |
| [UsageMerge](../../src/AgentBridge.App.Core/Machines.cs), [UsageCache](../../src/AgentBridge.App.Core/UsageCache.cs) | Allowance grouping, original reader, measurement/error times and account/member filtering. Existing App cache persistence is not a new CLI cache or write authorization. |
| [FleetList / RelayFleetRoster](../../src/AgentBridge.Host/Fleet/FleetList.cs) | `fleet.list`/`fleet.find` on an authenticated agent connection: restricted account-scoped registry metadata, not session/question/usage dashboard records. |

The one-shot CLI `usage` command is different from served `usage.get`: it runs
local collectors/probes, can contact providers, and may include additional vendor
accounts configured in that profile. Do not run it for this docs cut or describe it
as a passive all-machine read. Publish-off/empty output is not evidence of zero use.
Local transcript reads are OS-file-access reads, not an anti-agent security boundary.

## Views: local versus account-scoped

| View | Local evidence | Account-scoped person view / retained gap |
| --- | --- | --- |
| Machines | A running local host's `hello`/`host.status` identifies that host/profile/run. Direct `sessions` JSON alone does not supply account membership. | Needs authenticated account presence plus roster, then each selected host's hello. The App has these readers; no person CLI account transport is established here. |
| Sessions | `SessionSource` list/get or running-host `sessions.list`/`sessions.subscribe`, with holds and revision. | Per-host session records after membership and channel checks; an aggregate must disclose machines whose snapshot is missing/incomplete. Registry metadata is not the full list. |
| Questions | The current local Session question, where present, includes its identity and refusal/availability flags. | Exact machine/session/question from a current host snapshot, not a name or cached notice. Reading fields is neither a form editor nor authority to answer. |
| Usage | Local profile/vendor report; a served report may include bounded shared readings with original `readBy`. | Merge only records of the authenticated account's permitted machines, retaining allowance identity, measurement/error time and origin. Shared readings do not prove full fleet coverage. |

A vendor `account`/`accountKey` groups usage allowances; it is not the relay account
identity or proof of fleet membership. Do not merge data solely by agent name.
Keyless readings stay with their original reader rather than collapsing unrelated
machines into one allowance. Preserve [v1 usage](../protocol/v1-usage.md) semantics.

The missing person CLI route must be specified and implemented separately with
approved account/device authorization. Do not fill it by reading `host.relay.account`,
copying a host/App key, accepting an account supplied in request JSON, promoting an
agent principal or adding a device kind. C10 implements none of those mechanisms.
Local JSON and [agent fleet metadata](../protocol/agent-operations.md) remain their
own surfaces, not a fallback that silently claims person dashboard completeness.

## Presence, completeness and age are separate axes

Every proposed rendered view identifies its source/scope and observation time when
known. Display text is not a machine/session ID. Unknown timestamps stay unknown;
never invent a fresh observation or `lastSeen` from the renderer's current time.
These are output-review requirements, not new wire fields or CLI verbs.

| Axis / condition | Required rendering |
| --- | --- |
| Online | Current authenticated account presence says the host is online; this does not by itself make its session snapshot readable/current. |
| Offline | A known account machine is absent from a current complete presence view. Show offline and supplied last-seen information; retained rows are last-reported, not live. |
| Unknown | Account scope/presence unreadable, signed out, connecting/refused, missing identity or incomplete coverage. Do not convert absence of evidence to offline, empty or zero. |
| Stale / last-reported | A measurement stale under its source rule, disconnected/gapped snapshot, changed host run or discovery hold prevents a current/full-coverage claim. This can coexist with online presence. |
| Known empty | Only a complete current snapshot for its explicit scope supports zero sessions/no current question. A partial fleet or held local scan does not. |

For a subscribed list, require the response to the current subscription followed by
its matching `sessions.synced`, without a sequence gap. Resume only for the matching
host instance/revision; otherwise replace from a full snapshot. A gap, old-channel
event or changed instance cannot turn cached rows into a current list.
Discovery holds must remain visible even after a snapshot synchronizes.
Do not sum partial host counts into a complete fleet total.

Usage age follows the existing protocol: a reading older than an hour is stale;
`source: cache` alone does not imply stale. Missing windows/no reading is not 0%.
A fresh, valid measured 0% remains 0%. A reset window shows Reset, not its old percent
or an invented zero. Keep a newer failure distinct from the older fallback reading.
Existing retained usage is account-bound, origin-marked and expires after 14 days;
sign-out/account changes and forgotten/excluded machines retain their current rules.
Do not relabel an unconfirmed cached scope as the newly selected account.
This cut does not change the agent registry's existing legacy offline fallback.

## Output vectors (review-only)

`M1`, `S1`, `Q1` and account scopes A/B below are fixture labels, not wire IDs,
real account names, serialized protocol additions or newly executed receipts.
All examples are read views; none supplies action authority.

| Vector | Input / observation | Required output distinction |
| --- | --- | --- |
| L1 | Local direct-source scan: two sessions, no hold; no relay scope read | `scope=local`, two local sessions; fleet presence/coverage unknown, not "two fleet sessions". |
| L2 | Local scan empty while discovery held/unreadable | Explicit hold/unknown completeness; not "no sessions" or successful full inventory. |
| L3 | Complete current snapshot, explicit machine scope, no sessions | Zero sessions for that scope only; no inferred total for other machines. |
| M1 | Current account roster knows a machine; current presence says offline | Offline, supplied lastSeen or unknown time; retained session count not current. |
| M2 | Presence says online; host channel times out/refuses | Online presence with unavailable/unknown snapshot, not offline or zero sessions. |
| M3 | Relay signed out, scope missing, or roster/presence read fails | Account view unavailable/unknown with reason; not an empty successful fleet. |
| M4 | One host current, another missing/truncated | Partial coverage with that gap; do not present the first host's count as complete fleet count. |
| S1 | Cached session rows after disconnect/event gap | Last-reported rows with incomplete/current-unknown status until a valid new snapshot. |
| S2 | Old-channel synced/event or new host instance | No promotion of old rows; require matching subscription/full resync. |
| Q1 | Old Q1 shown while host is offline, gapped or held | "Last-reported question; current state unknown", not a live answerable question or no question. |
| Q2 | Current snapshot shows Q1 answered/replaced | Show current identity/state; no stale notice retargeting, form submission or answer permission. |
| U1 | No usage reading/windows, publish off or capability absent | No data/unavailable with the supplied reason, not 0% consumed. |
| U2 | Fresh valid window measured 0% | Show measured 0%, distinct from U1; preserve measurement time/source. |
| U3 | Two-hour-old cached window plus newer failure | Stale fallback and newer error/attempt time, not fresh usage or a zeroed bar. |
| U4 | Cached reading measured two minutes ago; window not reset | Fresh reading with cache source, not stale merely because it is cached. |
| U5 | Window reset; or retained reading older than 14 days | Reset/no current percent; expired retained reading omitted, never converted to zero. |
| U6 | Same allowance from two permitted machines; keyless readings from two readers | Keyed allowance groups by identity/time; keyless readings do not collapse by agent alone. |
| A1 | Account A data retained, account B selected or scope unresolved | Do not label A data as B/current; preserve account-change/invalidation and unknown-scope distinctions. |
| A2 | Shared usage lacks some machines or retains an offline reader | Show supplied readings/origins/age and partial coverage, not full current fleet usage. |
| A3 | Agent `fleet.list`/`find` succeeds, including names | Metadata-only scope; no prompt/question/usage expansion or person dashboard/action parity claim. |

Example render labels, not product output or a new JSON schema:

```jsonl
{"scope":"local","sessions":2,"fleetPresence":"unknown","coverage":"local-only"}
{"scope":"account","machine":"M1","presence":"offline","snapshot":"last-reported","sessionCount":null}
{"scope":"account","machine":"M1","presence":"online","snapshot":"unknown","reason":"event-gap","sessionCount":null}
```

A later read-only CLI runs in the invoking terminal, not on a designated fleet node.
Relay account presence/roster remains relay-owned; session/usage evidence stays with
its source host or already authorized cache. Invocation buffers add no durable
service or credential store. When that reader leaves, it supplies no continuing
freshness/background receipt. No new cache, provider refresh or write is added here.

Review checks L1-L3, M1-M4, S1-S2, Q1-Q2, U1-U6 and A1-A3.
No executable changes: red-first runtime proof is not applicable, and no local
test/build/provider command was run. Hosted `docs-links` remains Quality's proof.
Both model families read both changed files whole. This closes only C10's docs
contract, not machine/session/question/usage dashboard or S4 live acceptance.
