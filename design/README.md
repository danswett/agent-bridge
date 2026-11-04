# Design sources

The source of truth for the Agent Bridge design language. The spec explaining every
choice is [docs/design/ui.md](../docs/design/ui.md), and the order of work is
[docs/design/ui-plan.md](../docs/design/ui-plan.md).

| Path | What it is |
|---|---|
| `tokens.json` | Colour roles (dark, light, high contrast), per-platform surfaces, type ramps, inline mono's x-heights (`type.inlineMono`), spacing, radii, breakpoints, motion (below) |
| `glyphs/*.svg` | The status glyphs, on a 16 grid |
| `icons/*.svg` | The icon set, on a 20 grid, with a 1.5 stroke and round ends. `mark.svg` is the app mark, on a 48 grid |
| `app-icon/*.svg` | **Generated.** The app icon in each platform's container, from `tokens.json` `appIcon` and `icons/mark.svg` ([App icon](#app-icon)) |
| `fonts/slipgates-sans/` | Slipgates Sans, the text face (#544): a tuned Inter, its build script, the variable TTF and WOFF2 pair, the hinted statics in one collection, and `faces.json`. **Licensed separately under SIL OFL 1.1**, not this project's AGPL (see its README) |
| `fonts/slipgates-mono/` | Slipgates Mono, the monospace: build plan, build script, TTF, WOFF2 and a collection, `faces.json`, and its specimen. **Licensed separately under SIL OFL 1.1**, not this project's AGPL (see its README) |
| `preview.png` | The glyphs, icons and colour roles rendered in dark and light. Regenerate it whenever a glyph, icon or role changes |
| `canvas/` | The design canvas boards' source, rendered to [docs/design/mockups](../docs/design/mockups/README.md) by [tools/design-mockups](../tools/design-mockups/README.md) |

## Generated copies

`tools/design-gen` reads this folder and writes every client's copy. Each generated file says
so in its header. CI fails when one is stale, and its tests hold every theme to the contrast
rules in `tokens.json` (`color.contrast`). It refuses to generate when a bundled font lacks a real
face for a weight or style the ramps use (`type.weights` × `type.styles`), since a platform would
synthesize it (ui.md §4.2).

```
node tools/design-gen/generate.mjs               # after changing anything here; commit the result
node tools/design-gen/generate.mjs --check       # what CI runs: names any stale file
node --test "tools/design-gen/test/*.test.mjs"   # contrast, the SVG subset, the outputs
node tools/design-gen/preview.mjs                # preview.png (needs pwa's npm ci and Playwright's Chromium)
```

| Client | File | What it holds |
|---|---|---|
| PWA | `pwa/design/tokens.css` | `@font-face` for Slipgates Sans (the variable pair, `font-weight: 400 700`, upright and italic) and each face of Slipgates Mono (`font-display: swap`), `font-synthesis: none`, and `--ab-*` custom properties: roles and surfaces in light, dark (`prefers-color-scheme`) and high contrast (`forced-colors`), the sans and mono stacks, inline mono's ratio and scale per platform (`html[data-platform]`), radii, spacing, and motion (`--ab-motion-*`, with a `prefers-reduced-motion` block). `pwa/tools/build-site.mjs` copies the WOFF2s and each font's licence into the site's `design/fonts/` |
| PWA | `pwa/design/sprite.svg` | A `<symbol>` per glyph (`glyph-<name>`) and icon (`icon-<name>`). A glyph with a moving part also has `glyph-<name>--still` and `glyph-<name>--spin` or `--orbit`, so a client can turn that part alone |
| PWA | `pwa/index.html`, `pwa/manifest.webmanifest` | Only the `theme-color` and manifest colours, from the shell |
| Windows | `src/AgentBridge.App/Design/DesignResources.xaml` | `ThemeDictionaries` (Light, Dark, HighContrast) of `Ab*` brushes, and the hyperlink brushes WinUI reads (`color.accent.hyperlink`); WinUI's accent palette, `SystemAccentColor` and its Light1–3/Dark1–3 shades, as Bridge (`color.accent.windows`), so accent buttons, the selection pill, checked controls, focused fields and links take Bridge rather than the Windows accent; `SansFontFamily`, `ContentControlThemeFontFamily` and `MonoFontFamily` (each bundled collection, then the fallback stack), inline mono's scale and its size per ramp role (`AbInlineMono*`), radii, spacing and the layout sizes (`AbLayout*`, from `layout`); motion (`AbMotion*`, outside the theme dictionaries); a `TextBlock` style per role of the Windows type ramp (`AbType<Role>`, from `type.roles.windows`); a `ContentControl` style per glyph (`AbGlyph<Name>`) and icon (`AbIcon<Name>`) |
| Windows | `src/AgentBridge.App.Core/DesignLayout.cs` | The layout sizes and breakpoints as constants, for the app's view models, which decide the panes by width and run on Linux CI |
| Windows | `src/AgentBridge.App/Design/DesignKeys.cs` | Those keys, the accent palette's colours, the font files the app bundles and every real face in them, with the sans' probe advances, for the app's self-test |
| Apple | `design/apple/SlipgatesFonts.plist`, `SlipgatesFonts.swift` | Every font file to register (`UIAppFonts`; `ATSApplicationFontsPath` on macOS) and each real face's PostScript name by weight and style, so Apple clients never ask for a bold or italic the family lacks |
| Android | `design/android/font/` | Each font file under its resource name, and `slipgates_sans.xml` and `slipgates_mono.xml`: a font family with a `<font>` for every weight and style, to copy into `res/font` |
| Design | `design/app-icon/*.svg` | The app icon in each platform's container ([App icon](#app-icon)) |
| PWA | `pwa/icon.svg`, `pwa/icons/*.png` | The app icon: the favicon and the manifest's `any` icons, its maskable icons, and the `apple-touch-icon` |
| Windows | `src/AgentBridge.App/Assets/AgentBridge.ico`, `AgentBridge.png` | The app icon: the exe's, the window's, the tray's and the installer's, and the toast logo |

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
shape that can enclose an area without saying its fill, a fill or stroke opacity on a part
that has both (XAML has one opacity per path), a class not listed below, and a
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
  still under reduced motion. Every other motion is in ui.md §5.3, timed by the tokens
  below.

## Motion tokens

`tokens.json` `motion`, specified in [ui.md §5.3](../docs/design/ui.md). Durations are in
ms, easings are cubic-bézier `[x1, y1, x2, y2]`, and distances are in px (epx on Windows,
pt on Apple, dp on Android).

| Token | What it is |
|---|---|
| `limitMs` | 300. No duration, and no spring's response, may exceed it. The generator refuses one that does |
| `glyph.working.periodMs`, `glyph.delegating.periodMs` | 1200 and 8000: one turn of the arc, one orbit of the satellites. The only motions over `limitMs`, and the only loops. Still under reduced motion |
| `platform.<p>.duration.instant` | 0 |
| `platform.<p>.duration.fast` | Small, quick feedback: a press, a glyph changing, a menu. Windows 83 (WinUI's `ControlFasterAnimationDuration`), Apple 150, Android 100 (Material 3 `short2`) |
| `platform.<p>.duration.standard` | Most entrances and moves. Windows 167 (`ControlFastAnimationDuration`), Apple 250, Android 200 (`short4`) |
| `platform.<p>.duration.emphasis` | Large moves: push navigation, a sheet, a scroll. Windows 250 (`ControlNormalAnimationDuration`), Apple 300, Android 300 (`medium2`) |
| `platform.<p>.duration.crossFade` | The fade that replaces a moving entrance under reduced motion. Windows 83, Apple 150, Android 100 |
| `platform.<p>.easing.standard` | Moving from one place on screen to another. Windows Fluent 2's easy ease, Apple easeInOut, Android Material's standard |
| `platform.<p>.easing.decelerate` | Entering. Windows Fluent's Fast Out Slow In (WinUI's `ControlFastOutSlowInKeySpline`), Apple easeOut, Android standard decelerate |
| `platform.<p>.easing.accelerate` | Leaving. Windows Fluent's Slow Out Fast In, Apple easeIn, Android standard accelerate |
| `platform.apple.spring.<duration>` | SwiftUI `Spring(response:dampingFraction:)` for movement, one per duration: response 0.15, 0.25 and 0.3 s, damping 1 (no bounce) |
| `platform.pwa` | Inherits Windows: the PWA follows the Windows feel (ui.md §7) |
| `distance.nudge`, `.small`, `.medium` | 4, 12 and 24: a menu's or a disclosure's settle, a pane or a toast sliding in, a pushed screen |
| `catalogue.<entry>` | One per row of ui.md §5.3's table: its `duration` and `easing`, an optional `distance` and `exit` (the leaving half), and `reduced`, `crossFade` or `instant` |

Every easing stays within 0–1 on both axes. No curve overshoots, and WinUI's `KeySpline`
accepts nothing else.

What the clients get:
- **PWA:** `--ab-motion-<duration>` (such as `--ab-motion-fast`), `--ab-motion-ease-<easing>`,
  `--ab-motion-distance-<distance>` and `--ab-motion-glyph-<glyph>-period`. Each catalogue
  entry gets its own properties: `--ab-motion-<entry>-duration`, `-ease`, `-distance`,
  `-exit-duration` and `-exit-ease` (such as `--ab-motion-list-item-exit-duration`). The
  `prefers-reduced-motion: reduce` block sets every duration to `0ms` or, for an entry
  marked `crossFade`, the cross-fade, and every distance to `0px`. Travel no distance token
  measures, such as the sheet rising its own height, is scaled by `--ab-motion-travel`
  (`1`, and `0` under reduced motion): `translateY(calc(100% * var(--ab-motion-travel)))`.
  So a rule written with an entry's properties and `--ab-motion-travel` needs no
  reduced-motion rule of its own. A `0ms` transition never runs, and fires no
  `transitionend`, so never wait for one.
- **Windows:** the same names as `AbMotion*` resources outside the theme dictionaries,
  such as `AbMotionFast`, `AbMotionEaseDecelerate`, `AbMotionDistanceSmall`,
  `AbMotionListItemExitDuration` and `AbMotionListItemReduced` (`CrossFade` or `Instant`).
  Durations and KeySplines are `x:String`, as WinUI's own are, and `{StaticResource}`
  turns them into a `KeyTime`, `Duration` or `KeySpline`:
  `<SplineDoubleKeyFrame KeyTime="{StaticResource AbMotionStandard}" KeySpline="{StaticResource AbMotionEaseDecelerate}" Value="1" />`.
  Code reads `UISettings.AnimationsEnabled` for reduced motion; no resource changes with it.

## App icon

`tokens.json` `appIcon` holds the app icon: `icons/mark.svg` on a plate, in each platform's
container, specified in [ui.md §7](../docs/design/ui.md) (App icon). The colours are the
plate, its edge, the mark (a colour role, whose dark value is used in every theme, since the
plate is always dark) and the monochrome layer's. Each container has a `grid`, the size it is
drawn on, and on it the plate's side (`plate`, centred), its corner `radius`, the `edge`
hairline inside it, and the side of the mark's 48 box (`mark`, centred).

| Container | Grid | What it is | Generated into |
|---|---|---|---|
| `windows` | 72 | Tile: radius 14, edge 1, mark 44 | `app-icon/windows.svg`, the app's `.ico` above 24 px and its toast logo (256), and the PWA's `icon.svg` and `icons/icon-192.png`, `icon-512.png` |
| `windowsSmall` | 16 | Tile at `upTo` (24) px and under: radius 3, no edge, mark 13, so the ring stays a pixel thick | `app-icon/windows-small.svg`, the `.ico`'s 16, 20 and 24 |
| `macos` | 1024 | Apple's grid: plate 824, radius 185, mark 504 | `app-icon/macos.svg` |
| `ios` | 72 | Full bleed and opaque, mark 44; the system masks it | `app-icon/ios.svg`, the PWA's `icons/apple-touch-icon.png` (180) |
| `android` | 108 | Adaptive icon layers: the plate, the mark 40 across, and the mark in `monochrome` | `app-icon/android-background.svg`, `-foreground.svg`, `-monochrome.svg` |
| `maskable` | 72 | Full bleed, mark 35, inside the safe zone | `app-icon/maskable.svg`, the PWA's `icons/maskable-192.png`, `maskable-512.png` |

The Windows `.ico` holds 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96 and 256, every
size Windows asks an app icon for. The macOS asset catalog and the Android mipmaps come with
those apps (P5, P8), from the same SVGs.

`tools/design-gen` draws the rasters itself (`lib/raster.mjs`), without a browser: the icon is
only rounded squares and circles, so the mark must stay circles in `currentColor`, and the
generator refuses one that isn't. It uses only arithmetic IEEE rounds exactly, so every
machine draws the same pixels, and the check compares pixels, not PNG bytes, which zlib may
compress differently.

## Changing the language

Change `tokens.json` or an SVG, run the generator, and update `docs/design/ui.md` in the same
PR. Any change
to a status glyph or colour also needs the status table in ui.md §2.2 checked, because the
glyphs must stay distinguishable in greyscale.
