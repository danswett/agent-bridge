# Design mock-up renderer

Renders the design canvas boards in [`design/canvas`](../../design/canvas) to the PNGs in
[`docs/design/mockups`](../../docs/design/mockups), offline. It implements only the parts
of the canvas format the boards use:
- `{{holes}}`;
- `<sc-if>` and `<sc-for>`;
- `<dc-import>` (child boards with props);
- a `DCLogic` class with `props` and `renderVals()`.

Interactivity is ignored.

```
cd tools/design-mockups
npm install
npx playwright install chromium   # once, if this machine has no Playwright browser
npm run render
```

**Fonts are stand-ins**, because the real faces can't be bundled or aren't installed on CI
machines:
- Inter for Segoe UI and SF Pro, and for the generic `system-ui`, `-apple-system` and
  `sans-serif`: the renderer rewrites those to an embedded alias in the rendered page,
  because `@font-face` can't redefine a generic family and the machine's own font would
  otherwise change line wrapping. The canvas source keeps its stacks;
- Roboto Flex for Roboto;
- Cascadia Mono for the fallback monospace faces. Slipgates Mono itself is real, loaded from `design/fonts/slipgates-mono`.
- Inter Tight is real too (`@fontsource-variable/inter-tight`): it is the interim wordmark face
  in docs/design/brand.md, so the lockup boards show true letterforms.

So the images are right about layout, colour, sizes and spacing, but not about the exact
letterforms. Every OS now uses the same fonts, but rasterization still differs slightly, so
re-rendering on another OS changes pixels without changing layout. Commit only the boards
whose source or renderer changed.

Phone boards render at 2×, and everything else at 1×. A board's `canvas.json` entry can set
`dpr` to render at another device-pixel ratio (1.5, say).

A board can load its own font files in its `<helmet>` CSS by a path relative to
`design/canvas` (`src: url(../../docs/design/proposals/544-custom-sans/fonts/woff2/X.woff2)`);
the renderer inlines them. A proposal uses this to show faces that aren't the design system's.

## Proposals

Boards named `Proposal-*` belong to a proposal (docs/design/proposals), and `npm run render`
leaves them out. Render one proposal's boards beside its page by naming their prefix:

```
node render.js ../../design/canvas ../../docs/design/proposals/253-attention Proposal-253-
```

Proposal #536 (the Slipgates brand) renders the same way, then tiles its boards into a contact
sheet:

```
node render.js ../../design/canvas ../../docs/design/proposals/536-slipgates-brand Proposal-536-
node contact-sheet.js ../../docs/design/proposals/536-slipgates-brand proposal-536-
```

Proposal #384 (first-run setup and Settings) draws most of its boards from one parametric
board, `Proposal-384-Windows.dc.html` (props `theme` = dark, light or hc; `screen`; `width` =
1440, or 720 for the compact layout). Each `Proposal-384-<Screen>[-Light].dc.html` is a
one-line `<dc-import>` of it, and only those wrappers are in `canvas.json`'s order. The phone
board and the states matrix are their own boards. Re-render them, then the contact sheet:

```
node render.js ../../design/canvas ../../docs/design/proposals/384-first-run Proposal-384-
node contact-sheet.js ../../docs/design/proposals/384-first-run proposal-384- "--note=decided by the owner on 2026-11-03 · ui.md is the spec"
```

`contact-sheet.js <dir> <prefix> [out.png] [columns] [--note=<text>]` tiles every
`<prefix>*.png` in a folder, sorted by name, into `contact-sheet.png` beside them. It uses the
same Playwright and Inter as the renderer, so it runs offline too. Its subtitle says "proposal
mockups, not approved" unless `--note` replaces it, as #384's decided note above does.

## Motion preview

`npm run motion` records [`docs/design/motion-preview.html`](../../docs/design/motion-preview.html)
to `motion-preview.gif`, and again under reduced motion to `motion-preview-reduced.gif`, beside
it (docs/design/ui.md §5.3). The page is timed by the generated `--ab-motion-*` tokens, so
re-record it after changing `design/tokens.json` `motion`. It serves the repository on a
loopback port, since a page opened from `file://` can't use the sprite. It steps the page's
own clock 30 ms a frame rather than recording in real time, so a slow machine records the
same frames. It uses the machine's `system-ui` font, not the stand-ins above.
