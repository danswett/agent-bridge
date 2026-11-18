# Owner paging: host contract and delivery

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
Unknown or absent client kinds/outcomes never acknowledge delivery.
Both client kinds are always represented. Stable refusals: `callerNotAuthorized`,
`badRequest`, `payloadTooLarge`, `audienceRefused`, `targetRefused`, `rateLimited`,
`auditUnwritable`, `deliveryUnavailable`, `noSubscribedClient`, `policySuppressed`,
`tooManyClients`, `deliveryUnconfirmed`.
Every call/refusal is audited without title/body; an unwritable audit fails closed.
CLI: `agent-bridge-host notify-owner <title> <body> [--profile <name>]`, using the
process's script token, prints per-kind JSON and exits nonzero on any refusal.

App clients opt in with `sessions.subscribe {ownerPages:true}` and receive the
typed request as a `notify-owner` event. Only the freshly classified local app or
a current pinned-person relay app qualifies; service/host/session/script peers do not.
PWA delivery uses existing encrypted Web Push subscriptions with current person pins;
pinning off or an uncertain route refuses. The most recently active eligible device
(visible, input within two minutes) alerts; while away, eligible devices may alert.
Policy, script authority and person pins are rechecked before a queued push sends.
`delivered` means app-channel acceptance, not OS presentation or an owner read receipt;
`queued` means the volatile, five-minute push outbox accepted it, not browser delivery.
Pages replace the previous owner-page tag, keep 30-second burst replacements quiet,
and open only the machine, without approval controls.
Intent is audited before dispatch; terminal audit failure refuses acknowledgment and
cancels remaining queued work, but cannot retract a page already sent.
No silent manage, host-gone, relay-only send, HA or N2 path is reused or changed.
