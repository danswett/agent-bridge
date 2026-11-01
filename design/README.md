# Design sources

The source of truth for the Agent Bridge design language. The spec explaining every
choice is [docs/design/ui.md](../docs/design/ui.md), and the order of work is
[docs/design/ui-plan.md](../docs/design/ui-plan.md).

| Path | What it is |
|---|---|
| `tokens.json` | Colour roles (dark, light, high contrast), per-platform surfaces, type ramps, spacing, radii, breakpoints, motion |
| `glyphs/*.svg` | The status glyphs, on a 16 grid |
| `icons/*.svg` | The icon set, on a 20 grid, with a 1.5 stroke and round ends. `mark.svg` is the app mark, on a 48 grid |
| `fonts/agent-bridge-mono/` | Agent Bridge Mono, the monospace: build plan, build script, TTF and WOFF2. **SIL OFL 1.1, not MIT** (see its README) |
| `preview.png` | The glyphs, icons and colour roles rendered in dark and light. Regenerate it whenever a glyph, icon or role changes |
| `canvas/` | The design canvas boards' source, rendered to [docs/design/mockups](../docs/design/mockups/README.md) by [tools/design-mockups](../tools/design-mockups/README.md) |

## Generated copies

`tools/design-gen` reads this folder and writes every client's copy. Each generated file says
so in its header. CI fails when one is stale, and its tests hold every theme to the contrast
rules in `tokens.json` (`color.contrast`).

```
node tools/design-gen/generate.mjs               # after changing anything here; commit the result
node tools/design-gen/generate.mjs --check       # what CI runs: names any stale file
node --test "tools/design-gen/test/*.test.mjs"   # contrast, the SVG subset, the outputs
node tools/design-gen/preview.mjs                # preview.png (needs pwa's npm ci and Playwright's Chromium)
```

| Client | File | What it holds |
|---|---|---|
| PWA | `pwa/design/tokens.css` | `@font-face` for each face of Agent Bridge Mono (`font-display: swap`), and `--ab-*` custom properties: roles and surfaces in light, dark (`prefers-color-scheme`) and high contrast (`forced-colors`), the mono stack, radii, spacing. `pwa/tools/build-site.mjs` copies the WOFF2s and `OFL.txt` into the site's `design/fonts/` |
| PWA | `pwa/design/sprite.svg` | A `<symbol>` per glyph (`glyph-<name>`) and icon (`icon-<name>`). A glyph with a moving part also has `glyph-<name>--still` and `glyph-<name>--spin` or `--orbit`, so a client can turn that part alone |
| PWA | `pwa/index.html`, `pwa/manifest.webmanifest` | Only the `theme-color` and manifest colours, from the shell |
| Windows | `src/AgentBridge.App/Design/DesignResources.xaml` | `ThemeDictionaries` (Light, Dark, HighContrast) of `Ab*` brushes; `MonoFontFamily` (the bundled TTF, then the fallback stack), radii and spacing; a `ContentControl` style per glyph (`AbGlyph<Name>`) and icon (`AbIcon<Name>`) |
| Windows | `src/AgentBridge.App/Design/DesignKeys.cs` | Those keys and the font files the app bundles, for the app's self-test |

Using them:
- **PWA:** `<svg><use href="design/sprite.svg#glyph-needs"/></svg>`, coloured by its `color`.
  The ink is `--ab-glyph-ink`, the knockout `--ab-glyph-knockout` (set it to the surface
  behind the glyph), and an icon's stroke `--ab-icon-stroke`. These are custom properties
  because a page's class selectors don't reach inside a `<use>`.
- **Windows:** `<ContentControl Style="{StaticResource AbGlyphNeeds}" Foreground="{ThemeResource AbNeeds}" />`.
  The knockout is the control's `Background`. The moving part is the template part `Spin` or
  `Orbit`, with a `RotateTransform` about the glyph's centre.

The SVGs stay inside a small subset the generator converts exactly: `svg`, `g`, `path`,
`circle`, `rect` and `line`; `rotate()` and `translate()`; path commands M, L, H, V, C, A and
Z; lengths as plain numbers in the viewBox's units (no `%`, `px` or `em`). It refuses
anything else, an opacity, transform or class on the root `<svg>`, a
shape that can enclose an area without saying its fill, a class not listed below, and a
hard-coded colour on a part whose class doesn't name the token that replaces it.

## Rules for these files

- **Edit here, generate everywhere.** Clients get generated copies (#167, above): XAML
  geometry and `ThemeDictionaries` for WinUI, CSS custom properties and an SVG sprite for
  the PWA, and later SwiftUI shapes and Compose `ImageVector`s. Never hand-edit a
  generated copy.
- **Colour comes from the role, never from the file.** Each glyph and icon draws in
  `currentColor`. Classes mark the parts that differ:
  - `track`: the faint ring behind `working`. It is the role colour at 30% opacity, or the
    role's `track*` token.
  - `spin`: the arc that turns while working.
  - `orbit`: the satellites that drift while delegating.
  - `ink`: the "!" inside `needs`. It takes the `needs.ink*` token, not the role colour.
  - `knockout`: the stroke around the driven diamond. It takes the surface colour behind
    the glyph.
- **Driven by an agent** is `driven-badge.svg` drawn over any glyph, with the glyph's
  active colour switched from `active` to `agent`.
- **Sizes:**
  - Glyphs are drawn at 12, 14, 16, 20 or 24.
  - Icons are drawn at 16 or 20 on desktop and 22 or 24 on phones.
  - Stroke stays 1.5 at 20. Scale it to 1.7 at 16 or below, and to 1.4 at 24 or above, so
    lines look the same weight at every size.
- **Motion:** `spin` makes one turn every 1.2 s, and `orbit` one turn every 8 s. Both stay
  still under reduced motion.

## Changing the language

Change `tokens.json` or an SVG, run the generator, and update `docs/design/ui.md` in the same
PR. Any change
to a status glyph or colour also needs the status table in ui.md §2.2 checked, because the
glyphs must stay distinguishable in greyscale.
