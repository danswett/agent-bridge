# Design handoff: from studio to spec to code

How a design idea gets from a private design studio into the product, without the studio
becoming a second spec. Owner of this process: the VP of Design role (#477). Everything
here reuses the existing design system: [`/design`](../../design/README.md), the generator,
the mock-up renderer, and [ui.md](ui.md) as the spec.

## The five stages

| Stage | Where | What it is | Who |
|---|---|---|---|
| 1. Studio | The owner's private design tool, such as a Claude Design project or the canvas | Exploration. Never the spec, never linked from this repository | The owner, with whichever assistant they choose |
| 2. Proposal | An issue, and `docs/design/proposals/<issue>-<slug>.md` ([template](proposals/README.md)) | The problem, its evidence, options, a recommendation, the state matrix below, and mock-ups. Says **Proposed, not approved** | VP of Design (or anyone) |
| 3. Approval | A dated comment on the issue | The owner's explicit decision on each open question. Silence is not approval | The owner |
| 4. Spec | A PR changing ui.md, subagents.md or `design/` (then `node tools/design-gen/generate.mjs`) | The decision becomes canonical and versioned. The proposal page gets a "Decided" note on top | VP of Design, or the UI coordinator. A coordinator merges |
| 5. Implementation | Slices in [ui-plan.md](ui-plan.md), one PR per client | Code that follows the spec | UI coordinator's delegates. Apple through #260's owner |

A **material** change (the visual language, interaction or state semantics, §8's words,
notifications, accessibility behaviour, or parity between clients) passes through all five.
A bug, where a client doesn't do what ui.md says, skips to stage 5 with an issue. A docs fix
that changes no decision needs only a PR.

## Taking the system into a private studio

The studio can't read this repository, and nothing here can read the studio. The link is
files, carried by hand in both directions. To take the system there:

```
node tools/design-gen/export-context.mjs --out <a folder outside the repository>
```

This writes a **design-context packet**: the spec, the tokens, the glyphs and icons, the
PWA's generated CSS custom properties, the rendered mock-ups and the proposal template. It
also writes a `manifest.json` with the commit it came from and each file's SHA-256, and a
`README.md` holding the brief below.

What the export does and doesn't do:
- It copies only files the repository tracks, from a clean tree whose generated files are
  current. It refuses to write inside the repository, so a packet is never committed by
  accident.
- It's **one-way and unauthenticated**: not sync, not a connector, and no access to any
  studio. Nothing made from it comes back on its own.
- A packet is the approved spec *at one commit*. Anything drawn from it is a proposal until
  stage 3.

Claude Design can take a design system from code. Its documented import works best with a
React component library. Ours is tokens, CSS custom properties and SVGs, so attach the
packet's files (or the folder) rather than expect an import. It can't import the WinUI XAML.
`/design` and `/design-sync` are Claude Code commands, not Copilot CLI commands. A design
project is private to its owner's account, so its link never belongs in this repository: in
public text write `<private studio project>`.

## Paste-ready brief for the studio

The export writes this into the packet's `README.md` with the commit filled in. Paste it, and
attach the packet:

```
You are helping explore a design change for Agent Bridge, an app for watching and answering
AI agent sessions across machines (Windows app, PWA, and later macOS, iOS and Android).

The attached packet is the approved design system at commit <sha>:
- ui.md is the spec: principles, status language (§2.2), colour, type, layout (§5.2),
  motion (§5.3), surfaces (§6), platform shells (§7), copy (§8), accessibility (§9);
- tokens.json, glyphs/ and icons/ are the canonical values; tokens.css is the same as CSS;
- mockups/ are reference renders. Where they disagree with ui.md, ui.md wins.

Rules for this exploration:
- Use only the colour roles, type ramps, spacing, radii and glyphs in the packet. If you
  need a new one, name it as a proposed token and say why.
- Keep the status words in ui.md §2.2 and §8. Don't invent synonyms.
- Draw every state in the state matrix of the proposal template, in light, dark and high
  contrast, at 390, 900 and 1440 px wide, and say what happens under reduced motion and
  with the keyboard only.
- Everything you produce is a proposal, not a decision.

The problem: <one paragraph, with the issue number>
What I want back: <options with a recommendation, the states drawn, open questions>
```

## Bringing work back

1. Export what the studio made, as images, HTML or PDF, to your own machine.
2. Open or update the issue, and attach the images or a summary. Leave out the project's link.
3. The VP of Design turns it into a proposal (stage 2) using the template's handoff fields.
   Where a board should live on, it becomes `design/canvas/Proposal-<issue>-*.dc.html`,
   rendered with [tools/design-mockups](../../tools/design-mockups/README.md), as #302 did.
4. Approval, spec and implementation follow the stages above.

## State matrix

Every proposal that changes a surface fills this in, saying for each cell whether it's
drawn, specified in words, or unchanged. Stage 2 isn't done while a cell is blank.

| | Light | Dark | High contrast | 390 (phone) | 900 (two panes) | 1440 (three panes) | Reduced motion | Keyboard only | Screen reader |
|---|---|---|---|---|---|---|---|---|---|
| `needs`: Waiting on you, Delegate *name* asks, answer at the machine | | | | | | | | | |
| `working`, including task: Delegate *name* | | | | | | | | | |
| `delegating`: N agents, N background commands, details not reported | | | | | | | | | |
| `idle` | | | | | | | | | |
| Driven by an agent (overlay) | | | | | | | | | |
| Ending, and the Ended group | | | | | | | | | |
| Machine away: Offline, Reconnecting | | | | | | | | | |
| Error or refusal (a failed send, launch or answer) | | | | | | | | | |
| Empty and first run (no machines, no sessions) | | | | | | | | | |
| Read-only machine (`delivery.readOnly`) | | | | | | | | | |

Not every row applies to every surface. Say "n/a" and why, rather than leave a cell blank.
