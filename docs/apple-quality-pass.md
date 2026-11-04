# Apple client performance, reliability and security pass

Reviewed on November 2, 2026. This pass covers the native Apple clients and their
protocol/state layers. Verification uses isolated fixtures, disposable sockets
and unsigned local builds; no installed host, hook, Home Assistant instance or
personal account is used.

## Confirmed issues corrected

- **Cancelled attachments could leave staged files or interrupt the shared
  channel.** In-flight attachment requests now settle independently of the
  composer task. Cancellation is checked before starting new work and after
  responses, and cleanup runs in an uncancelled task pinned to the original
  connection. The composer also discards a completed upload if it loses ownership
  before adding its ID. Delayed-response and final-chunk regressions cover these
  boundaries; the original cancellation-cleanup tests failed before the fix.
- **Remote blob staging was treated as synchronous.** The Windows host returns
  incomplete while fetching and verifying a blob. The client now polls the same
  attachment request at 400 ms intervals within the host's 20-minute budget,
  validates progress and connection identity, and waits for verified completion.
  The composer passes its original connection into preparation, closing a
  reconnect window between validation and starting the upload.
  Failure cleanup discards the staged attachment and relay ticket without moving
  to a replacement connection. Tests cover incomplete, full-but-unverified,
  complete, timeout, cancellation, malformed progress and connection changes.
- **Older usage/update reports could overwrite newer state.** Report responses
  and events now apply in their shared wire order. Requests and failures remain
  bound to their originating connection. Gated regressions reproduce subscription
  recovery and delayed refresh races.
- **Notification replies could consume an unrelated composer draft.** Draft
  consumption now records the mutation's origin and survives explicit retry.
  A matching text prefix alone no longer establishes ownership.
- **Question guidance could offer an unusable answer action.** The workbench and
  answer form now respect the host's read-only reason and the question's
  capability, truncation and answer-at-machine restrictions.
- **A read-only host's conflicting capabilities could enable mutations.** The
  read-only reason now overrides known action capabilities and blocks new
  attempts and retries before changing the receipt ledger. Raw capabilities,
  session/usage reads and attachment cleanup remain available. Three regressions
  failed before this guard was added.
- **Sign-in could accept an HTTP success arriving after device-code expiry.**
  The client rechecks monotonic expiry and cancellation after responses and caps
  its last polling sleep to the remaining lifetime. Isolated URLSession fixtures
  cover pending responses, interval changes, cancellation, expiry, redirect
  refusal, verification URLs, exact response limits and safe refusal messages.
- **Delegate outcome icons could hide completion or failure.** Unknown outcome
  strings now use the protocol's `done` fallback, while missing outcomes stay
  absent. Idle background delegates show their completion/failure icon without
  being treated as ended.

## Performance changes

Session ordering builds a rank map once instead of repeatedly searching the saved
order inside comparisons. An optimized 300-session fixture fell from **4.78 ms to
0.70 ms per sort**, with identical results. Unchanged Markdown can skip parsing
through an equatable rendering child, without retaining a global text cache.
See [the measured fixtures and limits](apple-reading-performance.md).

Attachment progress is capped at ten updates per second while always delivering
the first and final updates. A controlled-clock regression covers the throttle.
Mac clipboard image disk writes run on a separate actor, leaving pasteboard access
on the UI actor. Existing image and upload size limits remain in force.

## Security review and remaining acceptance

Independent review covered the changed sign-in, blob, cancellation and state
paths. HTTPS origin validation, redirect refusal, bounded response reads,
account/relay-scoped credentials, authenticated channels, original-connection
binding and explicit mutation receipts remain required. This targeted review is
not a security certification or a real-service interoperability result.

The repository's [verification script](../apple/scripts/verify.sh) runs the full
Swift test suite, formatting checks and unsigned macOS and iOS Simulator builds.
The older hosted runner's test hang was traced to blocking POSIX socket fixtures
on Swift's cooperative executor. Moving that I/O onto Dispatch queues let all
221 tests finish in under six seconds. That run exposed a lifecycle test timing
assumption, corrected with a consumed-stream marker before its exact report
assertions; assertions and time limits remain intact.
Real relay/.NET-host acceptance, minimum-supported-OS and physical-device checks,
OS notification/login behavior, and accessibility interaction remain the gaps
listed in [Apple parity](apple-parity.md). Distribution remains local development
builds. CI and exact-head review are separate PR merge requirements.
