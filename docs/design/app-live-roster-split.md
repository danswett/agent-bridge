# Live app roster
Issue: https://github.com/danswett/agent-bridge/issues/2857; parent: https://github.com/danswett/agent-bridge/issues/1396.
Authorized stack: plain-merge A2 `008a4bde2e829ced4f144316bb6c170a4887ded5`, then A1 R3 `7c4aeb347e321e03a3d612c4507dcbe4b0775c68`.
Both clients already use per-host session stores, not HA entities, through existing
`sessions.subscribe`, `session.upsert`, `session.remove`, and `sessions.synced`.
This slice removes their Ended archives, including snapshot omissions, while preserving
silent-host stale rows, ordering, pin cleanup, and dynamic relay-discovered membership; compatibility Ended fields remain empty.
RED: each client's four cases produced two expected failures and two passes; GREEN: all eight pass.
Focused ordering regressions pass; HA-blocked multi-host acceptance and the owner's 241-row cause remain unproven.
Landing depends on https://github.com/danswett/agent-bridge/issues/2642 and https://github.com/danswett/agent-bridge/issues/2643; hosted acceptance remains separate.
