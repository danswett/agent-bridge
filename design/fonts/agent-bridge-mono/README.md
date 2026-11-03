# Agent Bridge Mono

The monospace for every Agent Bridge client: commands, paths, agent IDs, tool calls and
code blocks ([ui.md](../../../docs/design/ui.md) §4.1). Text everywhere else uses the
platform's system face.

![Agent Bridge Mono in dark and light](specimen.png)

## What it is

A modified build of [Iosevka](https://github.com/be5invis/Iosevka) 34.9.1, using the
[IoskeleyMono](https://github.com/ahatem/IoskeleyMono) configuration, which gives it a
Berkeley Mono-like character. One change is ours: **a tailed lowercase `l`**, so `l`, `1`
and `I` stay distinct in agent IDs, hashes and paths.

**Width: 600 units,** IoskeleyMono's own and Iosevka's Extended. The first build was 560,
between Iosevka's 500 and IoskeleyMono's 600, to fit a little more per line. Beside Segoe UI
Variable it looked condensed and clashed with the text, so the owner had it rebuilt at a
normal width (#295). What the eye compares is the advance over the x-height, since inline
mono is scaled to the text's x-height (ui.md §4.2): 560 gave 1.08, narrower than Consolas
(1.12) and Cascadia Mono (1.13); 600 gives 1.15, close to theirs. The x-height itself is
unchanged (520), so inline mono's scale is too.

Ligatures are off, because code in agent output should show the characters typed.

| Style | Weight | Files |
|---|---|---|
| Regular, Italic | 400 | `ttf/`, `woff2/` |
| SemiBold, SemiBold Italic | 600 | `ttf/`, `woff2/` |
| Bold, Bold Italic | 700 | `ttf/`, `woff2/` |

- **Format:** the TTFs are hinted with ttfautohint, for crisp rendering on Windows.
- **Coverage:** subset to the characters agent output uses: Latin, Greek, Cyrillic,
  punctuation, arrows, maths, box drawing, blocks, geometric shapes, ⌘ ⌥ ⏎, ✓ ✗ and the
  Braille spinners. Anything else falls back to the platform font.
- **Size:** each TTF is about 450 KB, and each WOFF2 about 145 KB.

## Using it

- **PWA:** `@font-face` from `woff2/`, family `"Agent Bridge Mono"`, with the stack
  `"Agent Bridge Mono", "Cascadia Mono", "SF Mono", "Roboto Mono", ui-monospace, monospace`.
- **Windows app:** bundle `ttf/` and reference it as
  `ms-appx:///Fonts/AgentBridgeMono-Regular.ttf#Agent Bridge Mono` (or the unpackaged
  equivalent).
- **macOS and iOS:** add `ttf/` to the bundle and list the files under `UIAppFonts` /
  `ATSApplicationFontsPath`.
- **Android:** use `res/font/`.

The design-tokens generator (#177) owns wiring this into the clients. Don't copy the
files by hand.

## License

The fonts are under the **SIL Open Font License 1.1** ([OFL.txt](OFL.txt)), not the
repository's AGPL-3.0-only license. The OFL lets us:
- bundle and embed them in the apps and the PWA;
- keep them in this public repository;
- modify them.

Its conditions:
- **Not sold on their own.** They may ship inside the apps.
- **Modified versions stay under the OFL.**
- **Credit is kept.** Every copyright notice (Iosevka, IoskeleyMono and ours) is kept in
  the files and in OFL.txt.

None of the upstream projects reserves a font name, and ours is distinct anyway.

## Rebuilding

```
./build.sh
```

The script clones Iosevka at the pinned tag, builds from `private-build-plans.toml`, then
runs `postprocess.py`. That step writes all three copyright notices into the name table
and produces `ttf/` and `woff2/`.

- **Needs:** git, Node.js 18+, Python 3 with `fonttools` and `brotli`, and `ttfautohint`.
  `pip install ttfautohint-py` is enough; the script wraps it.
- **On Windows,** run it from Git Bash, with `python3` on the `PATH` (a one-line script
  that runs the Python holding `fonttools`) and, ahead of it, ttfautohint-py's package
  folder, which holds a real `ttfautohint.exe`: Node on Windows can't start the script's
  shell wrapper. The 600 build was made this way. Rebuilding the old 560 plan the same way
  matched the committed 560 files in all but 7 of 3,409 glyphs: one-unit roundings and a
  few rare symbols (ℭ ↖ ⇖ ♧).
- **Time:** about four minutes on four cores (five on Windows).

To change a glyph, edit the `variants.design` table in `private-build-plans.toml`. The
names are listed in Iosevka's
[custom-build.md](https://github.com/be5invis/Iosevka/blob/v34.9.1/doc/custom-build.md).
Then rebuild, regenerate `specimen.png`, and say what changed in the PR. `specimen.png` is
`specimen.html` (beside `woff2/`) captured at 1000×625 CSS pixels and 2× scale, for example
with the PWA's Playwright. `tools/design-gen`'s tests check that every TTF and WOFF2 is the
build plan's width, so a stale build can't ship.
