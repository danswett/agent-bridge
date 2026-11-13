# PWA push: reconciliation and lifecycle rehearsal (#2397)

Web Push exists: the PWA registers subscriptions with hosts, the host encrypts
notifications, the relay forwards them, and the worker displays them and hands taps
to the page. Parity section 10 records historical phone question, finished-turn and
withdrawal observations, not current all-platform or background/locked-screen proof.

## One startup cut

The page formerly dropped a worker tap received before asynchronous device identity
loading finished. Keep only the latest early link in page memory, bound to the
stored-account token at receipt. Consume it once when that same account starts;
discard it at sign-in boundaries and explicit sign-out. The queue stays in memory only.
The existing machine synchronization and exact-question checks still own focus.
A tap opens a card and focuses its heading, never an answer or submission.
There is no notification-policy, wire-format or signing change.

The `startup push handoff` browser regressions hold the real IndexedDB device read
and dispatch worker messages through the page's actual listener. They cover latest
tap wins, stale-question replacement, cleared/replaced credential-transition
boundaries, and no host answer request after Enter or Space.
Credential-transition boundaries are not second-account authorization proof.
The separate-account proof owners checked in source, not run for this note, are:
`PushNotifierTests.Only_a_device_of_the_hosts_own_account_may_register_list_or_remove`
and `PushNotifierTests.Subscriptions_saved_for_another_account_are_neither_loaded_nor_kept`
in `tests/AgentBridge.Host.Tests/PushNotifierTests.cs`, and
`RelayTests.Another_accounts_device_is_neither_listed_nor_reachable`
in `tests/AgentBridge.Transport.Tests/RelayTests.cs`.

## Rows 16 and 28: evidence still required

Current finished-turn notices are silent, collapsed for ten minutes per session,
and suppressed while the device is in use. Do not change that policy here.
Under Product's ruling on [#2397](https://github.com/danswett/agent-bridge/issues/2397),
relayed by Engineering, suppression and replacement before display do not count.
Each separately displayed notice counts once, even if later replaced; silent is fine.

For each shipped notifying client away from the home network, record ten separately
displayed finished-turn notices and ten separately displayed questions. Keep the
device out of use; use a different session per finished turn or gaps longer than
the ten-minute collapse window. Questions count actual displays by the same rule.
Retain client-side service-worker or toast display evidence for each notice, screen
and OS delivery evidence, and correlated host records; host send logs alone do not
prove display (E10-01, E2-01/06, E18-04).

For foreground, background and locked-screen taps, prove the exact current machine,
session and question opens. Stale, answered, wrong-account and offline notices must
submit nothing (E10-02). Answer elsewhere and prove withdrawal; reconnect and prove
old work is not reannounced. Android uses the PWA; iOS Web Push is PWA evidence, not
native Apple evidence (#1882). iOS has no notification action buttons.
Synthetic worker messages do not certify OS delivery or complete either inventory row.

## Read boundary and follow-ups

Read whole: PWA registrar, worker and reading lifecycle; host notifier and push store;
protocol push payloads, relay push gateway and Web Push encryption; existing PWA
registrar/worker tests. Read main-page startup, link routing, notification and
sign-out wiring, not unrelated page wiring. The full S4 matrix is absent from this
base; its row 16/28 text and canonical issue were supplied by Engineering. Linked
issues #55, #736, #1146, #1401, #1949, #45, #50, #161 and #1419 were not read in full.
Physical off-network background and locked-screen rehearsal remains a follow-up.
Subscription renewal and delivery-failure cuts are follow-ups only if that proof
finds a separate reproducible defect; neither is claimed fixed or changed here.
