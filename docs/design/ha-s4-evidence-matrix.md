# HA retirement S4-1: client evidence matrix

Scope: [#2383](https://github.com/danswett/agent-bridge/issues/2383), parent
[#1396](https://github.com/danswett/agent-bridge/issues/1396). Documentation only;
no client, host, permission, notification or retirement behavior changes here.
Row numbers below are the retirement inventory's, not parity.md section numbers.

## Sources and reading depth

Base: `e16dc3674e21f4974dabd51b8bdee3ebd6ad07b3`, freshly fetched main.
Read **whole**: [P: parity.md](../parity.md), [A: apple-parity.md](../apple-parity.md),
[W: app-windows.md](../app-windows.md), [H: optional HA design](ha-optional-client.md)
and [S: host-stop.md](../host-stop.md). These are documentation claims, not a new code audit.
P's audit is at `2a25120`; A's comparison is dated November 2, 2026.
Later W detail can narrow P's historical claims; neither creates a live receipt.
Read [C: #39 checklist](../parity-checklist.md) introduction, conventions and
relevant E1-E5, E9-E10, E15, E18 and rollback steps, **not the whole checklist**.
Engineering supplied the complete closed [#39](https://github.com/danswett/agent-bridge/issues/39)
text (no comments), S4 row meanings and the retirement gate text by message.
#39 delivered a harness for 53 untested-live rows, not proof those runs passed.
P's "Live: Progress" citations are historical evidence pointers; the underlying
Progress record was not available at its named repository path in this checkout.

Gate item 1: "Every row in Section 1 has its acceptance evidence recorded for every
client that Section 1a requires, and no row is DONE on a design alone or on one client alone."
The same continuous run needs 500 messages, 50 launches, **30 real approvals and
20 stops**, zero HA fallbacks; counters belong to #2384, not this document.
The HA-off rehearsal runs first on one machine, then all four, each required row
at least **three times**, zero failures, with owner confirmation of phone approvals.

## Reading the grid

Each cell separates build evidence, client-specific live evidence and remaining work.
**Built** means documented implementation, not live qualification. **Partial** names
a missing behavior; **missing** is explicit in the cited source. **Unknown** means
the sources do not establish it, never that a runtime feature certainly does not exist.
**Historical** preserves a documented live claim without promoting its client/version
scope or old rules. **Untested live** means no sufficient client-specific receipt found.
Every gap key resolves through the issue packet below; no cell is declared DONE.
PWA includes browser/installed modes; Android-via-PWA gets its own run, not a native
Android claim. Apple cells distinguish Mac from iOS/iPadOS. Android native and Linux
clients are not shipped; future shipping adds required cells, never inherited passes.
HA is included without a feature exemption. H describes an optional later client,
not shipped runtime; its S8 implementation is not a new prerequisite for HA retirement.
Legacy connector evidence is kept separate from that proposed component.

## Row 4: stop, with the host's idle-only rule

| Client | Built evidence | Live evidence | Gap keys |
|---|---|---|---|
| PWA | Partial for current rule: P section 5/S allow confirmed non-idle stops | Historical P section 5, Progress > End session; not idle-only proof | STOP, L-PWA, L-ANDROID |
| Windows | Partial: W Details uses the host's two-press rule | Historical P section 5; old question-stop run | STOP, L-WIN |
| Apple | Partial: A two-step stop against a capable remote host; Mac local host stop unavailable | Mac fixture only; Mac/iOS real-host runs untested live | STOP, L-MAC, L-IOS |
| MCP agent | Missing in P section 15's native MCP inventory; current exposure unknown | Untested live; an agent cannot borrow person authority | I-MCP, STOP, L-MCP |
| Host CLI | Unknown client stop entry point; S proves host handling, not CLI exposure | Untested live | I-CLI, STOP, L-CLI |
| Optional HA | Missing runtime: H proposes opening paired-person UI; legacy P section 18 differs | Disposable connector CI is not this component's live proof | HA, STOP |

Pass on every required client: end an idle disposable session and confirm shutdown/
removal; attempt working, waiting, background-work and stale/replayed stops and prove
the approved idle-only rule at the **host**, with process identity preserved and no
typing into a question. Keep transcript, host log and client outcome. Repeat three
times per rehearsal stage; contribute to 20 continuous-run stops. C E5-01 supplies
idle evidence; E5-02/03/04's older non-idle semantics must be reconciled, not passed as-is.

## Row 5: approvals

| Client | Built evidence | Live evidence | Gap keys |
|---|---|---|---|
| PWA | Built: P section 3 permission UI and host checks | Historical P Progress > Copilot permission prompts; no 30-approval client ledger | L-PWA, L-ANDROID |
| Windows | Built: W card/Needs you approval; approval toast is **Open only**, not Approve | Historical P section 3; old toast buttons prove neither current approval UI nor count | L-WIN |
| Apple | Built: A structured permissions on capable remote hosts; local Mac actions unavailable | Mac/iOS untested live; fixture receipts excluded | L-MAC, L-IOS |
| MCP agent | Native server missing in P section 15; current authorized surface unknown | Untested live; agents never answer trust/permission confirmations | I-MCP, L-MCP |
| Host CLI | Unknown client approval workflow; host management guards are not a person UI | Untested live | I-CLI, L-CLI |
| Optional HA | Missing runtime: H read grant cannot approve; separate person path proposed | Legacy connector CI is not component acceptance | HA |

Pass: person approves once and denies real prompts from each required client;
record matching transcript/host outcomes and stale/revoked/agent refusals without
executing the denied action. Use C E3-01/02/03/04/05, recording Codex unconfirmed
honestly and Claude's terminal-only gap. Three repetitions per client/stage, and
30 real approvals across clients in the continuous-run ledger with distribution
shown. Unsupported authorized-person paths remain gaps, not permission bypasses.

## Row 8: owner-only updates

| Client | Built evidence | Live evidence | Gap keys |
|---|---|---|---|
| PWA | Built for capable remote Windows app/host packages: P section 9, W updates | Historical P section 9: November 1, 2026, 11:06 PT, 0.1.690 -> 0.1.700 | L-PWA, L-ANDROID |
| Windows | Built: W local Velopack plus remote Check/Install/Check all | P's remote app controls tested; no complete current client live receipt found | L-WIN |
| Apple | Partial: A remote host updates built; Apple self-updates deferred, not supplied by Windows | Mac/iOS untested live | APPLE-UPDATE, L-MAC, L-IOS |
| MCP agent | Owner-only update surface not established; no agent install authority | Untested live | I-MCP, L-MCP |
| Host CLI | Unknown owner update client; W app-only management is not CLI evidence | Untested live | I-CLI, L-CLI |
| Optional HA | Missing runtime: H person update path proposed; legacy Install exists separately | P section 9 legacy HA button still untested live (#161) | HA |

Pass: owner checks, confirms and installs an available version, sees progress,
reconnect and exact installed version; agent/unapproved requests change nothing.
Run three times per required client/stage; keep before/after versions, host logs
and visible receipts. C E9-02/E18-06 are templates, not evidence of Apple self-update.

## Row 15: forget machine

| Client | Built evidence | Live evidence | Gap keys |
|---|---|---|---|
| PWA | Built: P section 9 offline roster, two-press Forget | Untested live (#50); tests are not a live run | L-PWA, L-ANDROID |
| Windows | Built: W offline list and five-second two-press confirmation | Untested live | L-WIN |
| Apple | Built: A confirmed offline Forget | Mac/iOS untested live | L-MAC, L-IOS |
| MCP agent | Unknown authorized roster/forget client; P section 15 missing native server | Untested live | I-MCP, L-MCP |
| Host CLI | Unknown client; relay permits a person's device, never a host (P section 9) | Untested live | I-CLI, L-CLI |
| Optional HA | Missing runtime: H status card opens person UI; legacy dashboard cleanup differs | Untested live | HA |

Pass: forget only the selected offline machine after confirmation, observe its
roster/cards disappear, cancel/expire safely, and refuse a machine that comes online
before confirmation. Three runs per client/stage; keep relay/client roster before/
after and refusal receipts. C E9-01 is only a starting view: it does not exercise Forget.

## Row 16: finished-turn and question push

| Client | Built evidence | Live evidence | Gap keys |
|---|---|---|---|
| PWA | P section 10 documents Web Push; current background parity unresolved by supplied brief | Historical phone question/finished/withdrawal claims in P; not every browser/OS | PWA-PUSH, L-PWA, L-ANDROID |
| Windows | Built: W tray question/finished toasts, dedupe, withdrawal and quiet behavior | Historical question toast in P; full current lifecycle untested live | L-WIN |
| Apple | Partial: A open-app notices; iOS/iPadOS APNs background transport missing | Mac/iOS OS delivery/authorization untested live | IOS-PUSH, L-MAC, L-IOS |
| MCP agent | Unknown notification/subscription client contract in read sources | Untested live | I-MCP, L-MCP |
| Host CLI | Unknown question/turn notification client | Untested live | I-CLI, L-CLI |
| Optional HA | Missing runtime: H uses paired-person notification path, not HA action events | Legacy disposable-HA notification test is not component proof | HA |

Pass: question and completed main-agent turn each notify once per session, including
the required background/locked lifecycle; answering elsewhere withdraws the question
and reconnect never reannounces old work. C E10-01, E2-01/06 and E18-04; three runs
per client/stage with screen/OS delivery evidence plus correlated host records.

## Row 17: function keys and multi-field forms

| Client | Built evidence | Live evidence | Gap keys |
|---|---|---|---|
| PWA | Built forms (P section 2); function-key parity not documented | Historical five-field PWA form; current full key policy untested live | KEYS, L-PWA, L-ANDROID |
| Windows | Built forms (W session view); larger forms open from toasts | No client-specific full-form/key live receipt found | KEYS, L-WIN |
| Apple | Built structured questions (A); native limits/open-session actions explicit | Mac/iOS real-host form/key runs untested live | KEYS, L-MAC, L-IOS |
| MCP agent | Current structured-form/function-key exposure unknown | Untested live | I-MCP, KEYS, L-MCP |
| Host CLI | Unknown form/key client workflow | Untested live | I-CLI, KEYS, L-CLI |
| Optional HA | Missing runtime: H proposes complete fields and person UI, no select workaround | Untested live | HA, KEYS |

Pass: document the explicit keep-for-all or drop decision for each function-key
behavior, never infer an exemption. Exercise every retained action and a form with
choice, boolean, Unicode text, number and multi-select; C E2-03/05/08/09 supplies
expected transcript fields, refusal and draft checks. Three runs per client/stage;
retain client screens and transcript values; a design decision alone is not DONE.

## Row 27: dashboard views

| Client | Built evidence | Live evidence | Gap keys |
|---|---|---|---|
| PWA | Built: P sections 1/9 all-machine cards, activity and machine selection | Historical P cards/machine rows; complete current view set untested live | L-PWA, L-ANDROID |
| Windows | Built: W Sessions/Machines/Launch/Usage/Settings and responsive panes | Historical P Windows cards/second host; demo screenshots excluded | L-WIN |
| Apple | Built: A workbench, detail, history, usage and machine views | Mac fixture traversal only; Mac/iOS real fleet views untested live | L-MAC, L-IOS |
| MCP agent | Current equivalent list/detail/status view coverage unknown | Untested live | I-MCP, L-MCP |
| Host CLI | Partial evidence: P section 14 status/logs/config; full view parity unknown | Untested live | I-CLI, L-CLI |
| Optional HA | Missing runtime: H specifies seven card roles, not a working dashboard | P section 18 generated legacy dashboard is not this component | HA |

Pass: compare required session/activity/question/reply/launch/usage/machine views
against the same live state, including offline, reconnect, removal and unreadable
state; C E1-01/02/03/05/08, E9-01, E18-01/02/03. Three runs per client/stage,
screens or equivalent inspectable client output plus host snapshot; no CLI/MCP exemption.

## Row 28: push opens the question

| Client | Built evidence | Live evidence | Gap keys |
|---|---|---|---|
| PWA | Documented tap-to-card; iOS Web Push has no action buttons (P section 10) | Historical verified phone tap, not current all-platform background proof | PWA-PUSH, L-PWA, L-ANDROID |
| Windows | Built: W toast opens correct session/question heading, never approval focus | Historical question buttons; current approval Open-only navigation untested live | L-WIN |
| Apple | Partial: A guarded foreground navigation/actions; iOS background missing | Mac/iOS physical-device opens untested live | IOS-PUSH, L-MAC, L-IOS |
| MCP agent | Unknown equivalent question-target navigation/notification contract | Untested live | I-MCP, L-MCP |
| Host CLI | Unknown exact-question open/inspect path from a notice | Untested live | I-CLI, L-CLI |
| Optional HA | Missing runtime: H proposes exact host/session/question person links | Legacy notification action is not the proposed link | HA |

Pass: tap from foreground, background and locked state, landing on the exact current
host/session/question; stale, answered, wrong-account and offline notices cannot
submit anything. C E2-01/06, E10-02; three runs per client/stage, screen and identity/
outcome record. No iOS buttons is an observed difference, not failed tap-to-question proof.

## Row 29: images in replies

| Client | Built evidence | Live evidence | Gap keys |
|---|---|---|---|
| PWA | Built: P section 4 paste/attach and encrypted large-file blobs | Historical small image attachment; large-photo path untested live | L-PWA, L-ANDROID |
| Windows | Built: W paste/attach, local chunks/remote blobs and sent-image preview | Historical P section 9 remote reply with image; full limits/lifecycle untested live | L-WIN |
| Apple | Built: A file/PhotosPicker/Mac paste, upload and staged removal | Mac/iOS real-host attachments untested live | L-MAC, L-IOS |
| MCP agent | Current attachment/reply client exposure unknown | Untested live | I-MCP, L-MCP |
| Host CLI | Unknown image staging/reply client workflow | Untested live | I-CLI, L-CLI |
| Optional HA | Missing runtime: H read-only grant cannot attach; person path proposed | Legacy image store is not this component's encrypted reply proof | HA |

Pass: send a small pasted/imported image and a full-size photo over 256 KiB from
each required client, prove the intended agent receives the correct image exactly
once, with explicit refusal/no silent shrinking where unsupported. C E4-05/06/07:
three runs per client/stage, transcript attachment identity, byte/size evidence and
client receipt; verify account/connection changes cannot reuse staged attachments.

## Follow-up issue packet

Engineering created the following issues from this packet; keys map to those links.
Known existing work: #1396 epic, #55 pairing blocker, #736 release gate, #1146 chain,
#1401 audit slices and #1949 load proof. Their current subissue mapping is **unknown**;
do not duplicate or claim them closed. Also check P's #45/#50/#161 and H's #1419.
Other than HA -> #1419, these older issues remain **unmapped** to individual cells.
Live-run issues are **not ready** until their gap issues are fixed.

| Key / issue | Title | Acceptance / boundary |
|---|---|---|
| L-PWA / [#2389](https://github.com/danswett/agent-bridge/issues/2389) | S4 live run: PWA row-by-row retirement evidence | Run all nine contracts above in browser/installed modes; distinguish iOS Web Push from native Apple. |
| L-WIN / [#2390](https://github.com/danswett/agent-bridge/issues/2390) | S4 live run: Windows app row-by-row retirement evidence | Run all nine contracts on real installed app, local and remote hosts; exclude demo/self-test proof. |
| L-MAC / [#2391](https://github.com/danswett/agent-bridge/issues/2391) | S4 live run: Mac client row-by-row retirement evidence | Run all nine contracts on real Mac client; report local-host limitations separately from remote-host results. |
| L-IOS / [#2392](https://github.com/danswett/agent-bridge/issues/2392) | S4 live run: iOS client row-by-row retirement evidence | Run all nine contracts on physical supported iOS/iPadOS targets; foreground, background and locked lifecycle recorded. |
| L-ANDROID / [#2393](https://github.com/danswett/agent-bridge/issues/2393) | S4 live run: Android via PWA row-by-row retirement evidence | Run all nine contracts on Android browser/installed PWA; no claim of a native Android client. |
| L-MCP / [#2394](https://github.com/danswett/agent-bridge/issues/2394) | S4 live run: MCP agent client row-by-row retirement evidence | Run all nine contracts through actual permitted client surface; record person-only refusals and missing paths as gaps. |
| L-CLI / [#2395](https://github.com/danswett/agent-bridge/issues/2395) | S4 live run: host CLI row-by-row retirement evidence | Run all nine contracts from documented CLI entry points; host handlers alone are not client exposure proof. |
| HA / [#1419](https://github.com/danswett/agent-bridge/issues/1419) | Optional Home Assistant client (existing S8 work) | Reuse existing implementation/conformance work; no separate S4 live-run issue or retirement prerequisite. |
| STOP / [#2396](https://github.com/danswett/agent-bridge/issues/2396) | S4 stop: reconcile host enforcement with the idle-only rule | Resolve S/P non-idle semantics against approved rule; one bounded enforcement cut, then all-client three-run refusal/idle proof. Check #1146 first. |
| PWA-PUSH / [#2397](https://github.com/danswett/agent-bridge/issues/2397) | S4 PWA push: close background question and turn delivery gaps | Reconcile historical Web Push with current missing-background report; one lifecycle cut, prove rows 16/28 background/locked delivery and exact-question open. |
| IOS-PUSH / [#2398](https://github.com/danswett/agent-bridge/issues/2398) | S4 iOS push: add the first background delivery slice | Cut one missing APNs registration/provider/lifecycle mechanism, at most 250 changed lines; name remaining slices; full rows 16/28 proof follows only when complete. |
| APPLE-UPDATE / [#2399](https://github.com/danswett/agent-bridge/issues/2399) | S4 Apple updates: resolve the client self-update gap | Reconcile local-development distribution decision with row 8 for Mac/iOS; record approved delivery path and first at-most-250-line cut, never label remote Windows updates Apple self-updates. |
| KEYS / [#2400](https://github.com/danswett/agent-bridge/issues/2400) | S4 function keys: record all-client keep-or-drop behavior | Inventory key actions and obtain explicit common policy; map each retained missing client mechanism to bounded work and its row 17 live contract. |
| I-MCP / [#2401](https://github.com/danswett/agent-bridge/issues/2401) | S4 MCP evidence: reconcile current operations with the parity inventory | Check #45/current docs; record actual entry points for all nine rows and exact missing mechanisms, with at-most-250-line implementation follow-ups; no new agent approval rights. |
| I-CLI / [#2402](https://github.com/danswett/agent-bridge/issues/2402) | S4 host CLI evidence: inventory missing client-row mechanisms | Record real CLI entry points/output for all nine rows, allowed-person boundaries and bounded missing-mechanism follow-ups; never infer parity from host-only handlers. |

All seven live-run issues inherit #39's exact action/expected/read-location format,
after-mark transcripts/logs, screenshots and step verdicts, including explicit failed/
manual/not-run results. Record migration and rollback side separately, restore and
repeat, and update parity evidence with date, client/OS/build and immutable run links.
Attach the three-per-row rehearsal records and continuous-run approval/stop ledger;
untested or missing cells stay open. Any implementation follow-up is recut before
exceeding 250 changed lines. HA implementation maps to existing #1419/S8, not a duplicate.
