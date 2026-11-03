# Browser session renewal (proposed)

The installed PWA runs on a Pages origin. The relay serves sign-in and sockets on a separate origin. Today the PWA puts a relay-issued bearer in localStorage and the default access lifetime is 30 days. This design gives the browser an access token that expires in ten minutes and can be renewed only by its registered device. Native account tokens keep their existing protocol until those clients opt in.

## Enrollment and storage

A client requests the browser-session capability with the device-code exchange. After GitHub approval, the relay returns a one-time enrollment ticket valid for two minutes, bound to the approved account and the submitted device public key. It becomes a session only after the client proves possession of the matching private key. The enrollment ticket is consumed atomically. Do not issue the old long-lived bearer to a client requesting this capability.

The relay records a random session-family ID, device ID, account ID, hashed renewal handle, generation, last-used time and absolute expiry in a durable store. Keep the access token only in page memory. Keep an opaque renewal handle and public account label in the PWA's IndexedDB alongside the existing non-extractable device key. A handle alone does not authorize a connection. Requiring neither ambient credentials nor third-party cookies works across the independent origins. Existing bearer tokens in localStorage are discarded on explicit migration, followed by fresh sign-in. A relay without the capability must label the legacy mode explicitly.

## Refresh, rotation and revocation

1. Request a one-use challenge over HTTPS with the handle and device ID. Check the configured PWA origin. Use the same response for unknown or revoked handles, cap outstanding challenges per IP/device and expire challenges after 30 seconds.
2. The relay sends its ephemeral P-256 public key and a cryptographically random nonce. The client derives an ECDH secret with the stored key and authenticates a length-delimited transcript including the distinct domain label `agent-bridge-browser-renew-v1`, normalized relay origin, account and family IDs, generation, both public keys and nonce. Never reuse the websocket proof label. Reject wrong-origin, expired, consumed and mismatched challenges before issuing credentials.
3. In one transaction, consume the challenge, compare-and-swap the handle generation and return a fresh handle plus a ten-minute access token bound to this device and family. The WebSocket handshake checks the bound device ID. Expiration or revocation closes established sockets in that family, including those on another replica. No plaintext handles or credential values enter logs.
4. Coordinate tabs with the existing single-owner mechanism. Persist the rotated handle before reporting refresh complete. If the confirmation or storage write is lost, require fresh device sign-in. A spent handle never creates another session. On deliberate sign-out or device removal, revoke the family and close connected sockets; locally clear the handle, in-memory access token, and account-scoped session state. An offline sign-out clears local state immediately and must distinguish pending server revocation. Provide account-side revocation from another signed-in device.
5. Require full sign-in after 30 days of inactivity, 90 days absolute, or loss of the private key. Background suspension can cross access expiry; refresh before reconnect. Rate-limit failed renewal separately from ordinary sign-in.

The threat addressed is replay of copied persistent browser data outside its device. Active script in the PWA origin could still ask the device to prove possession. Keep the independently published client, CSP and origin checks.

## Compatibility and acceptance

The cross-origin browser test in `pwa/test/e2e/e2e.spec.mjs` reloads the installed identity, reconnects to a separate relay origin, and verifies that its non-extractable key survives without relay cookies. It exercises prerequisites of this proposal. Renewal endpoints, durable family revocation and token rotation must pass replay, lost-response, two-tab, sign-out, storage eviction and replica restart tests before changing production token defaults. Roll out relay capability discovery first, then browser opt-in.
