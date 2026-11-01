# Design sources

The source of truth for the Agent Bridge design language. The spec explaining every
choice is [docs/design/ui.md](../docs/design/ui.md), and the order of work is
[docs/design/ui-plan.md](../docs/design/ui-plan.md).

| Path | What it is |
|---|---|
| `tokens.json` | Colour roles (dark, light, high contrast), per-platform surfaces, type ramps, spacing, radii, breakpoints, motion |
| `glyphs/*.svg` | The status glyphs, on a 16 grid |
| `icons/*.svg` | The icon set, on a 20 grid, with a 1.5 stroke and round ends. `mark.svg` is the app mark, on a 48 grid |
| `preview.png` | The glyphs, icons and colour roles rendered in dark and light. Regenerate it whenever a glyph, icon or role changes |
| `canvas/` | The design canvas boards' source, rendered to [docs/design/mockups](../docs/design/mockups/README.md) by [tools/design-mockups](../tools/design-mockups/README.md) |

## Rules for these files

- **Edit here, generate everywhere.** Clients get generated copies (#167): XAML
  geometry and `ThemeDictionaries` for WinUI, CSS custom properties and an SVG sprite for
  the PWA, and later SwiftUI shapes and Compose `ImageVector`s. Never hand-edit a
  generated copy. Until the generator exists, port by hand and say so in the PR.
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

Change `tokens.json` or an SVG and update `docs/design/ui.md` in the same PR. Any change
to a status glyph or colour also needs the status table in ui.md §2.2 checked, because the
glyphs must stay distinguishable in greyscale.
