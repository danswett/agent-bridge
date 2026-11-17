# Comms-lanes sender

`tools/agents/reply-session.ps1` keeps its parameters and numeric exits. Final delivery reports append `[outcome=<name>; rid=<request-id>]`; successful output still starts `sent to <node>`.

| Named outcome | Exit | Meaning |
|---|---|---|
| `sent` | 0 | This RID was acknowledged as delivered and the existing status/turn check passed. |
| `already-handled` | 2 | A duplicate RID was acknowledged; nothing was typed now. Never a new success. |
| `unconfirmed` | 2 | The receiver could not confirm delivery, including no fresh acknowledgement or a non-delivered result after the bounded retry. |
| `input-full` | 4 | Existing text blocks delivery, including a failed Enter; never retry automatically. |
| `not-delivered` | 1, 3, 5 | Pre-submit failure, initial receiver refusal, or delivered acknowledgement without a verified turn, respectively. |
| `node-gone` | 1 | No host session list contains the target; a retained registration is insufficient. |

Before each submission, the target must belong to exactly one available host session list whose online sensor is `on`. Missing, offline, ambiguous or unreadable liveness fails closed without submitting (`unconfirmed`, exit 1).

An explicit unconfirmed acknowledgement or acknowledgement timeout causes exactly one re-offer with the **same RID**, even with `-Attempts 1`. `-Attempts` still bounds pre-submit payload/stamp attempts, not acknowledgement retries. A lost submit HTTP response also waits for an acknowledgement before that bounded re-offer. Refusal, input-full, already-handled and exit 5 never cause an automatic retry.

The payload carries the expected host `instance`. The receiver checks it when consuming the payload, including while a question is open, and refuses a mismatch without typing; `instance-mismatch` takes precedence over a question's ordinary `refused` outcome. The sender reports that distinct refusal as `unconfirmed` (exit 2). This receiver-side fence covers a restart after the sender's last liveness check. Legacy payloads without `expected_instance` remain accepted as before, but have no receiver-side restart fence: after a host restart clears its RID history, resending one can deliver it twice.

The re-offer is the exact serialized payload from the first submission, including its original `at`, RID and expected host instance. It is submitted only while the host session-list sensor's `instance` marker is unchanged; a missing, changed or unreadable marker skips the retry. Because the first submission may already have reached the session, any retry-preparation failure remains `unconfirmed` (exit 2), not `not-delivered`.

The existing sender mutex remains unchanged; this cut adds no per-machine lock, host send queue, receiver FIFO or relay storage. The receiver inbox is host-local; its later relay write-through seam is separate. GitHub is not a live inbox.
