# Native APNs registration contract (#2438)

This specifies `push.apns`, separately from browser `push`. See the accepted
[native delivery design](../design/ios-push-path.md). Hosts with notifications
enabled advertise enrollment on secure relay channels. No native sender or
delivery proof is added.

| Operation | Request data | Result |
|---|---|---|
| `push.apns.subscribe` | `token`, `target`, `keyId`, `keys`; optional `device` | `PushSubscribeResult`, native subscription count |
| `push.apns.unsubscribe` | `target` | `PushUnsubscribeResult`, this channel device/target only |
| `push.apns.list` | `{}` | `PushListResult`, native entries only |

These are exact, case-sensitive shapes. Reject unknown operations, unknown or
duplicate fields (including nested keys), missing/null required values and wrong
types. Requests are at most 4096 UTF-8 bytes of JSON. `device` is only a display
name, at most 64 UTF-8 bytes without controls; absent/null uses the channel name.
Caller account/device IDs, kinds, endpoints, app URLs, topic and credentials
confer no authority and are not accepted fields.

`token` is 2-1024 ASCII hexadecimal characters, even length: bounded variable-size
OS bytes, not a hard-coded 32-byte assumption. Never log it. `target` is 1-64 ASCII
characters, starts with a lowercase letter, then lowercase letters/digits/hyphens.
It selects deployment configuration, never a URL or caller-supplied APNs topic.
`keyId` is 32 opaque random bytes. `keys.p256dh` is a valid uncompressed 65-byte
P-256 point; `keys.auth` is 16 bytes. All three are canonical unpadded base64url.
The app's background encryption material is distinct from identity/provider keys.

Only `ApnsPushRequest.Read` constructs a validated command, including for list
and unsubscribe. Enrollment requires the channel device to belong to the host's
current account; with pinning enabled, its full authenticated key must have a
current person grant. Caller identity fields never substitute for that key.
Registrations replace by channel device/target, persist separately from browser
subscriptions, and are cleared on account change or loss of the person grant.
An in-flight enrollment stays bound to its starting account generation: switching
accounts discards it, even if the host switches back before enrollment completes.
The same generation check gates unsubscribe and list: an old request cannot
remove a newer binding or read the new account's registration metadata.
Native storage is independently bounded to 16 registrations, evicting the oldest
other registration. Unsubscribe affects only the caller's device/target.
List entries reuse `PushDevice`, with
`service` = `apns:<target>`; no token, key ID or keys are returned.
Unsupported hosts/clients refuse native operations, never downgrade to Web Push.
Browser request/result shapes and their capability remain unchanged.
Target-to-provider configuration and sending remain later slices; accepting
registration is not evidence that a target can deliver.

[Shared operation vectors](examples/push-apns.json) use markers for generated
synthetic token/key material, never OS tokens or credentials. C# replays them in
`ApnsPushContractTests`; future native clients must replay the same cases.
The parser-only class deliberately is not an automatically deserializable record.
