# UI implementation plan

How to build [ui.md](ui.md) and [subagents.md](subagents.md), split into slices an agent
can pick up with no other context. Each slice is one issue. A slice may become one PR, or
a PWA PR and an app PR.

Read [AGENTS.md](../../AGENTS.md) first: worktrees, PR and merge rules, tests, and "only
the coordinator merges". Read [status.md](../status.md) for where this sits among the
project's priorities (§4 step 6 and §5).

## 0. Before anything starts

| Gate | Who | Blocks |
|---|---|---|
| The owner signs off ui.md and subagents.md, and answers subagents.md §8 | Owner | Every slice in track B. S1 may start before it (see below) |
| #181: the monospace | Owner | Decided 2026-11-01: Agent Bridge Mono, in `design/fonts`. #177 bundles it |

**S1 may start before sign-off.** Its model and protocol are additive and change nothing a
current client shows. Its fixtures also feed the shadow gate (#40). If sign-off changes
§5 of subagents.md, S1 follows.

## 1. Slices

### Track A: sub-agents in the host (#84)

| Slice | Issue | What | Depends on | Touches |
|---|---|---|---|---|
| S1 | #173 | Sub-agent tree from Copilot transcripts; `subagents[]`, `activity.own` and `source`, `waitingOn`, Question `agent`, `session.get { agent }`; budget; precedence rule 2 | none | `Host.Core/Parsing`, `BackgroundWork`, `Protocol`, `SessionBudget`, `docs/protocol/v1-sessions.md`, `ShadowWitness` |
| S2 | #174 | Claude and Codex sources, from recorded transcripts first; otherwise counts only (rule 8) | S1 | `ClaudeParser`, Codex discovery and parsing, `tests/fixtures` |
| S5 | #175 | HA connector publishes `own` and a `subagents` attribute; shadow classification | S1 | `AgentBridge.HomeAssistant`, `docs/host-shadow.md` |

### Track B: the clients, in the order agreed on 2026-11-01

| Slice | Issue | What | Depends on | Touches |
|---|---|---|---|---|
| U1 | #164 | Your order: stored per device, pins, Ended group, activity dot | sign-off | `pwa/js/sessions-store.js`, `pwa/js/ui/views.js`; `App.Core` view models, `MainTests` |
| U2 | #165 | Needs you lane, with inline approve/deny for approvals, and badges | U1 | `pwa/js/ui/*`, `answers.js`; app `QuestionView`, tray badge |
| U3 | #177 | Generator: `design/` to WinUI `ThemeDictionaries` and `PathIcon`s, and to CSS variables and an SVG sprite; switch both clients to the colour roles | sign-off | new `tools/design-gen`, `pwa/app.css`, `App.xaml`, CI |
| U4 | #178 | Status glyphs and icons replace edges and glows; status text from ui.md §2.2 | U3 | `pwa/js/ui/session-card.js`, `app.css`; `SessionCard.xaml` |
| U5 | #179 | PWA layout: panes by width, web app bar, rail, phone navigation, row anatomy, type ramp | U1, U2, U4 | `pwa/` broadly |
| U6 | #180 | Windows app layout: Mica, title bar, `NavigationView`, panes, resizable, row anatomy, type ramp | U1, U2, U4 | `src/AgentBridge.App` broadly, `App.Core` |
| U7 | #168 | PWA platform layer: shortcut hints, safe areas, back navigation | U5 | `pwa/js/platform.js` (new) |
| U8 | #176 | Delegates in both clients: strip, tab, inspector, sheet, attributed questions | S1, U5, U6 | `pwa/`, `src/AgentBridge.App` |

**Later, outside this plan:** the macOS app (P5) and native phone apps (P8) build on ui.md
§7. They extend the generator with SwiftUI and Compose outputs when they start.

### What can run at the same time

```
S1 ──► S2
  └──► S5
  └──────────────────────────────► U8
U1 ──► U2 ──┐
U3 ──► U4 ──┴─► U5 ──► U7
            └─► U6 ──────────────► U8
```

- **S1, U1 and U3 can start together.** They touch different code.
- **U5 and U6 can run together.** They touch different clients, but each is a large PR, so
  keep each one to its own client.
- **U1 and U2 both touch the list. Do them in order.** Put their logic in the store or view
  model, not the markup, because U5 and U6 rewrite the markup. Keep the visual change in
  the old layout minimal.

## 2. How to do a slice

1. **Claim the issue.** Comment "taking this in <worktree/branch>", and make a worktree
   from fresh `origin/main` (AGENTS.md, Git).
2. **Read the spec sections the issue names, and nothing else, as the source of truth.**
   The owner's design canvas is private and is not the spec: if you can't find something
   in ui.md, subagents.md or `design/`, ask on the issue rather than guessing from
   screenshots.
3. **Values come from `design/tokens.json`.** Until U3 lands, copy a value by hand and
   leave a `TODO(#177)` beside it. After U3, never write a colour, size or radius that the
   generator owns.
4. **Keep the PWA and the app at parity.** A slice that touches both opens a PR per
   client, links them both ways, and lands them close together. The second PR's
   description lists any behaviour it doesn't match yet, with an issue for each.
5. **Tests:** the issue's acceptance list is the minimum. Name tests as sentences, make
   each one fail without the change, and never weaken an existing test (AGENTS.md, Tests).
   - PWA: unit tests and Playwright (`pwa/README.md`), at 390, 900 and 1440 px wherever
     the layout matters.
   - App: view-model tests in `App.Core` (they run on Linux CI), plus a manual run on
     Windows.
   - Visual: light, dark, and high contrast (`forced-colors` in the PWA). Check contrast
     against ui.md §9.
6. **Live check** where the issue asks for one: on a disposable VM, or on ULTRA with the
   owner's say-so (status.md §2, Machines), never anywhere else.
7. **PR description:** what changed, which ui.md sections it implements, before-and-after
   screenshots in both themes, and exactly what was run.
8. **Report** "#<n> ready at <sha>" to the coordinator. Only the coordinator merges.
9. **Docs in the same PR:**
   - Implementation reveals the spec is wrong or silent: update ui.md, subagents.md or
     `design/` in the same PR and say so.
   - Protocol changes: update `docs/protocol/v1-sessions.md`.
   - The slice closes a step: update status.md §5.

## 3. Done

The track is done when:

- **#164–#168, #173–#180 and #84** are closed.
- **Both clients** show every surface in ui.md §6, in all three themes, at all three
  widths.
- **Status in both clients** follows ui.md §2.2, with tests named after each rule.
- **The main agent's text** never shows a delegate's line, anywhere: PWA, app, HA,
  notifications.
- **No colour, glyph or icon** is defined outside `design/` or its generated files.
- **status.md** says so, and its decision log records anything decided along the way.
