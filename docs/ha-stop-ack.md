# Home Assistant Stop acknowledgements

The Stop button's state remains its last press timestamp, never evidence that a
session ended. A successful Home Assistant service call is not end evidence either.
The host derives a distinct request UUID from the session and observed press
timestamp; the button's `last_rid` attribute exposes it. It is correlation, not
authorization. This does not add a caller-supplied rid or change Stop authority.

The node's activity attributes carry `end_ack` and `end_ack_history` (the last
eight distinct acknowledged rids, oldest result first). Each result contains
`rid`, `node`, `outcome`, `state_before`, `killed`, UTC ISO `at`, and `reason`.
Later activity and transient notices preserve these results. The latest result
can belong to an earlier press: match `rid`, not its timestamp or array position.
History is bounded in-memory connector state, not a durable audit log.

Host `Ended` maps to `ended`; `Stale` maps to `already_ended` when the host knew
the session at dispatch, otherwise `not_found`. `Busy`, including a press during
an in-flight end, maps to `ending` without dispatching another end. Confirmation
requests, refusals and generic failures map to `refused`, retaining the reason;
an explicit timeout exception maps to `timeout`. No non-ended host result is
promoted to `ended`, and only the host's `Forced` result sets `killed`.

The existing host Stop service still owns deadlines and process control. Results
are published before normal card retirement; no registry retention/removal
contract changes here. Registry readback, end-verification tooling and its exit
codes remain separate work. An absent acknowledgement is unconfirmed, not success.
The recorded discovery fixture includes the two additive Stop attribute fields.
