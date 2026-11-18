# Owner paging: host contract (slice 1)

`notify-owner` is local-pipe-only, after `agent.hello`. Only a live script credential
explicitly minted by the person's `scripts add <name> --notify-owner` may call it.
Existing scripts, session credentials, person classification and `launch:fleet`
give no paging authority. `scripts revoke <name>` revokes both rotations.

Request: `{request: UUID, audience: "owner", target: "policy", title, body}`.
Only the authenticated owner's own app/PWA devices are eligible; no recipient,
endpoint or account override is accepted. `policy` reserves [notification policy](https://github.com/danswett/agent-bridge/issues/913) selection, not a bypass.
Limits: 4096 UTF-8 request bytes, 120 title bytes, 1024 body bytes, six valid attempts
per script principal per minute. Unknown/duplicate fields, controls and malformed Unicode refuse.

Reply: `{request, clients: [{client: "app"|"pwa", outcome: "delivered"|"queued"|"refused", reason}]}`.
Both client kinds are always represented. Stable refusals: `callerNotAuthorized`,
`badRequest`, `payloadTooLarge`, `audienceRefused`, `targetRefused`, `rateLimited`,
`auditUnwritable`, `deliveryUnavailable`; delivery's `noSubscribedClient` is reserved.
Every call/refusal is audited without title/body; an unwritable audit fails closed.
CLI: `agent-bridge-host notify-owner <title> <body> [--profile <name>]`, using the
process's script token, prints per-kind JSON and exits nonzero on any refusal.

**No client delivery in this slice.** Both kinds currently refuse `deliveryUnavailable`.
App/PWA admission, #913 selection and delivery/subscription acknowledgments require
a separate slice to stay below 400 WHOLE. This does not yet unblock Fleet N1.
No silent manage, host-gone, relay-only send, HA or N2 path is reused or changed.
