# HA retirement S4-1: client evidence matrix

Scope: [#2383](https://github.com/danswett/agent-bridge/issues/2383), parent
[#1396](https://github.com/danswett/agent-bridge/issues/1396). Documentation only;
no client, host, permission, notification or retirement behavior changes here.
Row numbers below are the retirement inventory's, not parity.md section numbers.

## Sources and reading depth

Base: `9a0acf32e28fca6e03542c0b7b35a20498789581`, freshly fetched main.
Earlier whole-source reads were at `e16dc3674e21f4974dabd51b8bdee3ebd6ad07b3`;
those source files are unchanged at this base.
Read **whole**: [P: parity.md](../parity.md), [A: apple-parity.md](../apple-parity.md),
[W: app-windows.md](../app-windows.md), [H: optional HA design](ha-optional-client.md)
and [S: host-stop.md](../host-stop.md). These are documentation claims, not a new code audit.
P's audit is at `2a25120`; A's comparison is dated November 2, 2026.
Later W detail can narrow P's historical claims; neither creates a live receipt.
Read [C: #39 checklist](../parity-checklist.md) introduction, conventions and
relevant E1-E5, E9-E10, E15, E18 and rollback steps, **not the whole checklist**.
Engineering supplied the complete closed [#39](https://github.com/danswett/agent-bridge/issues/39)
text (no comments), S4 row meanings and the retirement gate text by message.
Also read the whole local fleet retirement inventory (file commit `839a09f6dd0eaaffb212354f8d53fc9d1869b44b`); its Section 1 supplies the acceptance minima below, except the superseded idle-only wording for row 4.
Row 4 follows the [Product approval on #2396](https://github.com/danswett/agent-bridge/issues/2396#issuecomment-6843557043),
relayed by Engineering on November 12, 2026; the fleet correction belongs to F-Chain.
#39 delivered a harness for 53 untested-live rows, not proof those runs passed.
P's "Live: Progress" citations are historical evidence pointers; the underlying
Progress record was not available at its named repository path in this checkout.

Gate item 1: "Every row in Section 1 has its acceptance evidence recorded for every
client that Section 1a requires, and no row is DONE on a design alone or on one client alone."
Retirement requires those passing row/client records plus [#1949 load-parity proof](https://github.com/danswett/agent-bridge/issues/1949),
per the owner decision on [#1396](https://github.com/danswett/agent-bridge/issues/1396)
(November 11, 2026, 00:07 PT), relayed by Engineering; the former fixed-count
continuous-run quota is not a separate gate. Counters belong to [#2384](https://github.com/danswett/agent-bridge/issues/2384)
and serve #1949 and the per-row runs, not additional retirement quotas.
The HA-off rehearsal runs first on one machine, then all four, each required row
at least **three times**, zero failures or HA fallbacks, with owner confirmation of phone approvals.

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
The Mac/iOS work in #2391/#2392/#2398/#2399 remains required future parity but is parked last under [#1882](https://github.com/danswett/agent-bridge/issues/1882), per Engineering's supplied complete issue text on November 12, 2026, and does not block the HA-off gate until Product reopens #1882 or rules otherwise; fleet inventory row 13 says "Two Apple machines on HA; out for now (Mac and iOS clients are in scope under 1a)", a disagreement whose gate wording Product or the Chief must confirm.

## Row 4: stop, idle one press; non-idle confirmation

| Client | Built evidence | Live evidence | Gap keys |
|---|---|---|---|
| PWA | Built normal stop: P section 5/S; guarded-idle host gap tracked in #2411 | Historical P section 5, Progress > End session; full current client contract untested live | STOP, L-PWA, L-ANDROID |
| Windows | Built normal stop: W Details/S; guarded-idle host gap tracked in #2411 | Untested live at Windows/current-rule scope; P section 5's generic historical claim identifies no client | STOP, L-WIN |
| Apple | Built remote stop: A/S; guarded-idle host gap tracked in #2411; Mac local host stop unavailable | Mac fixture only; Mac/iOS real-host runs untested live | STOP, L-MAC, L-IOS |
| MCP agent | Missing in P section 15's native MCP inventory; current exposure unknown | Untested live; an agent cannot borrow person authority | I-MCP, STOP, L-MCP |
| Host CLI | Unknown client stop entry point; S proves host handling, not CLI exposure | Untested live | I-CLI, STOP, L-CLI |
| Optional HA | Missing runtime: H proposes opening paired-person UI; legacy P section 18 differs | Disposable connector CI is not this component's live proof | HA, STOP |

Product [ruling 2 on #2396](https://github.com/danswett/agent-bridge/issues/2396#issuecomment-6843606276)
requires two presses, then no `/exit`, for idle-reported unreadable-transcript,
launch-not-ready or transcript-only question states. Engineering's reported
`StopService.cs` lines 251-253/280 one-press path is a **known gap against the Product ruling, fix tracked in [#2411](https://github.com/danswett/agent-bridge/issues/2411)**.

Pass (Product wording, not live proof) per required client, for person and system stops:
idle ends on one press; working, waiting or idle-with-background-work first arms,
then ends only on a second press carrying its confirmation token within 10 seconds.
Expired or replayed first presses do not count as confirmation; preserve PID/start
time and show shutdown/removal of the correct session. Where safe, type `/exit`,
then force if still running after 12 seconds; for a question,
launch-not-ready or unreadable transcript, confirm then terminate without typing
`/exit`. Refuse elevated/other-user processes, unreadable start time, the host
itself and no-live-PID targets. Keep transcript, host log, client outcome and
confirmation/refusal receipts, including stale/reused-PID checks. Repeat three
times per rehearsal stage; record 20 row-4 stops across all four Windows
machines, all with correct targets. C E5-01/02/03/04 and S supply the action/read
locations; historical runs alone do not qualify every client.

## Row 5: approvals

| Client | Built evidence | Live evidence | Gap keys |
|---|---|---|---|
| PWA | Built: P section 3 permission UI and host checks | Historical P Progress > Copilot permission prompts; no 30-approval client ledger | L-PWA, L-ANDROID |
| Windows | Built: W card/Needs you approval; approval toast is **Open only**, not Approve | Untested live at Windows scope; P section 3's generic historical claim identifies no client; toast buttons are not approval proof | L-WIN |
| Apple | Built: A structured permissions on capable remote hosts; local Mac actions unavailable | Mac/iOS untested live; fixture receipts excluded | L-MAC, L-IOS |
| MCP agent | Native server missing in P section 15; current authorized surface unknown | Untested live; agents never answer trust/permission confirmations | I-MCP, L-MCP |
| Host CLI | Unknown client approval workflow; host management guards are not a person UI | Untested live | I-CLI, L-CLI |
| Optional HA | Missing runtime: H read grant cannot approve; separate person path proposed | Legacy connector CI is not component acceptance | HA |

Pass: person approves once and denies real prompts from each required client;
record matching transcript/host outcomes and stale/revoked/agent refusals without
executing the denied action. Use C E3-01/02/03/04/05, recording Codex unconfirmed
honestly and Claude's terminal-only gap. Three repetitions per client/stage, and
30 real approvals across shipped clients in the row-5 ledger with distribution
shown, each reaching its session within 10 seconds, away from the home network; retain tap-to-transcript timings. Unsupported authorized-person paths remain gaps, not permission bypasses.

## Row 8: owner-only updates

| Client | Built evidence | Live evidence | Gap keys |
|---|---|---|---|
| PWA | Built for capable remote Windows app/host packages: P section 9, W updates | Historical P section 9: November 1, 2026, 11:06 PT, 0.1.690 -> 0.1.700 | L-PWA, L-ANDROID |
| Windows | Built: W local Velopack plus remote Check/Install/Check all | P's remote app controls tested; no complete current client live receipt found | L-WIN |
| Apple | Built: A controls capable remote Windows host updates; Apple app self-updates are outside row 8 | Mac/iOS remote-host update controls untested live | APPLE-UPDATE, L-MAC, L-IOS |
| MCP agent | Owner-only update surface not established; no agent install authority | Untested live | I-MCP, L-MCP |
| Host CLI | Unknown owner update client; W app-only management is not CLI evidence | Untested live | I-CLI, L-CLI |
| Optional HA | Missing runtime: H person update path proposed; legacy Install exists separately | P section 9 legacy HA button still untested live (#161) | HA |

Pass: owner presses Check/Install through Slipgates for one full update of all four
Windows machines, sees progress, reconnect and exact installed versions; agent/
unapproved requests change nothing. Record client-to-machine coverage and three
row exercises per required client/stage; keep before/after versions, host logs
and visible receipts (C E9-02/E18-06). Apple client self-update is not a prerequisite.

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
before confirmation, then forget and re-pair once per machine type, retaining the code-approval evidence and any #55 blocker.
Three runs per client/stage; keep relay/client roster before/after and refusal receipts. C E9-01 is only a starting view: it does not exercise Forget or re-pair.

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
and reconnect never reannounces old work. Inventory minimum: ten finished-turn notices and ten questions reach each shipped notifying client away from the home network.
C E10-01, E2-01/06 and E18-04; three row runs per client/stage plus that minimum, with screen/OS delivery evidence and correlated host records.

## Row 17: native forms and legacy f1-f12 dropdowns

Inventory Section 1 explicitly says f1-f12 are HA per-field dropdowns, **not function keys**, and drops that workaround in favor of native named-field forms; Section 1a's older "Function keys" label does not create a keyboard prerequisite (Engineering's corrected #2400 scope).

| Client | Built evidence | Live evidence | Gap keys |
|---|---|---|---|
| PWA | Built forms (P section 2); HA dropdown workaround dropped | Historical five-field PWA form; full current form run untested live | KEYS, L-PWA, L-ANDROID |
| Windows | Built forms (W session view); larger forms open from toasts | No client-specific full-form live receipt found | KEYS, L-WIN |
| Apple | Built structured questions (A); native limits/open-session actions explicit | Mac/iOS real-host form runs untested live | KEYS, L-MAC, L-IOS |
| MCP agent | Current structured-form exposure unknown | Untested live | I-MCP, KEYS, L-MCP |
| Host CLI | Unknown form client workflow | Untested live | I-CLI, KEYS, L-CLI |
| Optional HA | Missing runtime: H proposes complete fields and person UI, no select workaround | Untested live | HA, KEYS |

Pass: row 17 includes one native multi-field form per client, with no Other value;
verify named choice, boolean, Unicode text, number and multi-select fields, without
HA's twelve-dropdown workaround. C E2-03/05/08/09 supplies expected transcript
fields, refusal and draft checks. Three runs per client/stage;
retain client screens and transcript values; a design decision alone is not DONE.

## Row 27: dashboard views

| Client | Built evidence | Live evidence | Gap keys |
|---|---|---|---|
| PWA | Built: P sections 1/9 all-machine cards, activity and machine selection | Historical P cards/machine rows; complete current view set untested live | L-PWA, L-ANDROID |
| Windows | Built: W Sessions/Machines/Launch/Usage/Settings and responsive panes | Historical P Windows cards/second host; demo screenshots excluded | L-WIN |
| Apple | Built: A workbench, detail, history, usage and machine views | Mac fixture traversal only; Mac/iOS real fleet views untested live | L-MAC, L-IOS |
| MCP agent | Current equivalent list/detail/status view coverage unknown | Untested live | I-MCP, L-MCP |
| Host CLI | Unknown client view coverage: P section 14's status/logs row describes app Settings, not CLI dashboard exposure | Untested live | I-CLI, L-CLI |
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
outcome record, including ten question-open taps per shipped client away from home. No iOS buttons is an observed difference, not failed tap-to-question proof.

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
These older issues remain **unmapped** to individual cells; #1419 is closed historical design, not an open B1-B9 tracker.
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
| HA / [#2407](https://github.com/danswett/agent-bridge/issues/2407) | Optional Home Assistant client: B1-B9 and S8 live run | Open, not-ready tracker owns all nine build slices and component live proof; #1419 is closed historical design, not their tracker; no retirement prerequisite. |
| STOP / [#2396](https://github.com/danswett/agent-bridge/issues/2396), [#2411](https://github.com/danswett/agent-bridge/issues/2411) | S4 stop: reconcile inventory wording with the parity stop rule and prove all clients | #2396 reconciles inventory wording and designs all-client proof: idle one press, non-idle confirmation, listed refusals; person/system parity. #2411 owns the guarded-idle code fix required by Product ruling 2. Three runs per client/stage and 20 correct-target stops; check #1146 first. |
| PWA-PUSH / [#2397](https://github.com/danswett/agent-bridge/issues/2397) | S4 PWA push: close background question and turn delivery gaps | Reconcile historical Web Push with current missing-background report; one lifecycle cut, prove rows 16/28 background/locked delivery and exact-question open. |
| IOS-PUSH / [#2398](https://github.com/danswett/agent-bridge/issues/2398) | S4 iOS push: add the first background delivery slice | Cut one missing APNs registration/provider/lifecycle mechanism, at most 400 changed lines including tests; name remaining slices; full rows 16/28 proof follows only when complete. |
| APPLE-UPDATE / [#2399](https://github.com/danswett/agent-bridge/issues/2399) | S4 Apple updates: verify remote-host update controls | Retitled by Engineering: parked under #1882; prove Mac/iOS person controls update all four capable Windows hosts, with progress/version/refusal receipts and three row runs per stage. Apple app distribution/self-update is outside row 8. |
| KEYS / [#2400](https://github.com/danswett/agent-bridge/issues/2400) | S4 row 17: native forms and dropped HA dropdowns | Engineering corrected the scope: f1-f12 are not function keys, HA dropdowns dropped, all-client multi-field forms retained; use the row 17 live contract. |
| I-MCP / [#2401](https://github.com/danswett/agent-bridge/issues/2401) | S4 MCP evidence: reconcile current operations with the parity inventory | Check #45/current docs; record actual entry points for all nine rows and exact missing mechanisms, with at-most-400-line implementation follow-ups including tests; no new agent approval rights. |
| I-CLI / [#2402](https://github.com/danswett/agent-bridge/issues/2402) | S4 host CLI evidence: inventory missing client-row mechanisms | Record real CLI entry points/output for all nine rows, allowed-person boundaries and bounded missing-mechanism follow-ups; never infer parity from host-only handlers. |

All seven live-run issues inherit #39's exact action/expected/read-location format,
after-mark transcripts/logs, screenshots and step verdicts, including explicit failed/
manual/not-run results. Record migration and rollback side separately, restore and
repeat, and update parity evidence with date, client/OS/build and immutable run links.
Attach the three-per-row rehearsal records, per-row approval/stop ledger and
the #1949 load-parity proof link;
untested or missing cells stay open. Any implementation follow-up is recut before
exceeding 400 changed lines including tests. HA implementation/live proof maps to open #2407/S8, not closed #1419 or a retirement prerequisite.
