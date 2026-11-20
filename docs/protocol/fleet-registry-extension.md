# Session registry process starts and totals

Extension to [the session registry](roster.md#session-registry-each-machines-sessions),
S-N2b of [the registry design](../design/fleet-session-registry.md).

Session entries may include `startedAt`: a host-observed process start in exactly
`YYYY-MM-DDTHH:mm:ssZ` form, UTC whole seconds. Omit unknown starts; explicit null,
offsets, fractions and non-calendar values are refused. Legacy entries without it
remain accepted. No other session field becomes allowed.

An optional `total` is accepted only with `truncated: true` and must be an integer
strictly greater than the number of published sessions, not necessarily greater
than 64. Byte fitting may publish 59 of 64 usable sessions, for example.
The 64-session, 16 KiB record and 24 KiB reply-frame limits do not change.
Acceptance does not establish verification, enable name selection or authorize deployment.
