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

Colours and glyph paths inside these files are copies of
[`design/tokens.json`](../tokens.json) and [`design/glyphs`](../glyphs). If they drift,
`design/` is right.
