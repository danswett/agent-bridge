# Design canvas source

A copy of the owner's design canvas, "Agent Bridge — Session Workbench", as of
2026-11-01:
- `canvas.json` is the board layout;
- each `*.dc.html` file is one board, in the canvas's Design Component format.

**The canvas is a sketchpad, not the spec.** The spec is
[docs/design/ui.md](../../docs/design/ui.md), and it wins wherever they disagree.

These files are kept so that:
- the boards can be re-rendered with [`tools/design-mockups`](../../tools/design-mockups);
- a future design session can start from them if the live canvas is ever lost.

Some boards are shared components or thin wrappers:
- `Glyph.dc.html` and `Icon.dc.html` are the glyph and icon components the other boards
  import;
- `*-Light.dc.html` and `*-Dark.dc.html` are wrappers that import a platform board with
  its `theme` prop flipped;
- `PWA-Desktop.dc.html` imports `Windows.dc.html` with `chrome="web"`.

`Proposal-<issue>-*.dc.html` boards belong to a proposal in
[docs/design/proposals](../../docs/design/proposals). They render beside the proposal, not
into `docs/design/mockups`. Once the owner has decided, they stay as the record of the
decision and as the reference for states the platform boards don't draw. #253's and #384's are
decided, and ui.md §10 points to them. #661's draw the decided delegate events and history
(#292), all from one component, `Proposal-661-Session.dc.html`, by its `form` and `state` props.

Colours and glyph paths inside these files are copies of
[`design/tokens.json`](../tokens.json) and [`design/glyphs`](../glyphs). If they drift,
`design/` is right.
