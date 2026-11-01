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
npm run render                    # Linux or macOS; on Windows run render.js directly
```

**Fonts are stand-ins**, because the real faces can't be bundled or aren't installed on CI
machines:
- Inter for Segoe UI and SF Pro;
- Roboto Flex for Roboto;
- Cascadia Mono for the fallback monospace faces. Agent Bridge Mono itself is real, loaded from `design/fonts/agent-bridge-mono`.

So the images are right about layout, colour, sizes and spacing, but not about the exact
letterforms. `fonts.conf` maps `system-ui` and `sans-serif` to Inter for the PWA boards
(Linux only).

Phone boards render at 2×, and everything else at 1×.
