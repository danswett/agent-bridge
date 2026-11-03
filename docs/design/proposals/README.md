# Design proposals

One page per proposal: `<issue>-<slug>.md`, with its mock-ups in a folder of the same name.
A proposal is stage 2 of the [design handoff](../handoff.md): it is **not the spec**, and
nothing in it applies until the owner approves it on the issue and a spec PR moves it into
[ui.md](../ui.md), [subagents.md](../subagents.md) or [`/design`](../../../design/README.md).
After that, the page keeps a "Decided" note on top and stays as the reasoning. Where it and
ui.md disagree, ui.md wins.

The first worked example is #253's, `253-attention.md`, added by #302.

## Template

Copy this into `<issue>-<slug>.md`. Never put a private studio link, a transcript, a
credential or a personal machine or network detail in it: this repository is public.

```markdown
# Proposal #<issue>: <title>

> **Status: Proposed, not approved.** <When decided: "Decided YYYY-MM-DD (PT): <the owner's
> answers>. Applied to ui.md in #<pr>.">

*Written by <role or person> on YYYY-MM-DD.* Issue: #<issue>.

## Handoff

| Field | Value |
|---|---|
| Base | `origin/main` at `<sha>`, the commit this proposal was written against |
| Spec sections | ui.md §…, subagents.md §…, `design/…` |
| Affected clients | Windows app · PWA · HA card · macOS · iOS · Android (Apple routes to #260) |
| Kind | Material (needs approval) · Bug against the spec · Docs only |
| Evidence | Product (which build) · Mock-up only · Source only. Links, screenshots, test output |
| Studio source | None, or "exported from <private studio project> on YYYY-MM-DD" (no link) |
| Related | Issues and PRs this touches or must not duplicate |
| Decision needed | The questions for the owner, each with options and the recommendation first |
| Approval | Blank until the owner answers on the issue: date, answers, link to the comment |
| Acceptance | What the implementation PRs must show to be done (below) |

## 1. Problem

What is wrong, for whom, and the evidence. Say which of it is seen in the product and which
only in mock-ups or source.

## 2. Options

Each option with what it costs, what it breaks, and which ui.md clauses it changes.
The recommendation comes first.

## 3. State matrix

The table from [handoff.md](../handoff.md#state-matrix), filled in.

## 4. Spec edits

The exact edits to ui.md, subagents.md or `design/`, ready to apply once approved.

## 5. Acceptance criteria

- Tests that fail without the change, named as sentences, for each client.
- Light, dark and high contrast; 390, 900 and 1440 px; reduced motion; keyboard only; screen
  reader names (ui.md §9).
- Parity: every client in "Affected clients", or an issue for each one not yet matched.
- Security review, if the change touches what a remote device can approve or send (AGENTS.md).

## 6. Open questions
```
