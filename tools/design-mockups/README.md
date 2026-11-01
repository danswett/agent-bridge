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
- Cascadia Mono for the fallback monospace faces. Agent Bridge Mono itself is real, loaded from `design/fonts/agent-bridge-mono`.

So the images are right about layout, colour, sizes and spacing, but not about the exact
letterforms. Every OS now uses the same fonts, but rasterization still differs slightly, so
re-rendering on another OS changes pixels without changing layout. Commit only the boards
whose source or renderer changed.

Phone boards render at 2×, and everything else at 1×.
