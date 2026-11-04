# Proposal fonts for #544 (superseded)

These are proposal material for [544-custom-sans.md](../../544-custom-sans.md), kept only so its
boards still render. They are **not** the design system, and nothing in `design/tokens.json`,
the generated outputs or any client loads them. The owner chose B, which now lives, rebuilt, in
[design/fonts/slipgates-sans](../../../../../design/fonts/slipgates-sans/README.md) as Slipgates
Sans; A (and C, which was A for headings only) was not chosen.

Both faces are under the SIL Open Font License 1.1 ([OFL.txt](OFL.txt)). None of the upstream
fonts declares a Reserved Font Name.

| Face | What it is | Files in `woff2/` |
|---|---|---|
| **Slipgates Sans A** | A custom Iosevka 34.9.1 build, quasi-proportional (the way Iosevka Aile is built), using the monospace's IoskeleyMono variants: tailed `l`, flag `1`, open 4/6/9, flat-top 3. Width 540. | 8 statics: Regular, Medium, SemiBold, Bold, each with its true italic. Hinted with ttfautohint. |
| **Slipgates Sans B** | Inter 4.1 with five alternates frozen into the defaults (`cv05` tailed l, `cv02` open 4, `cv03`/`cv04` open 6 and 9, `cv09` flat-top 3, `ss07` square punctuation), subset and renamed. No outline is redrawn. | 8 statics (hinted), plus the variable pair `SlipgatesSansB[opsz,wght]` and `SlipgatesSansB-Italic[opsz,wght]` (unhinted, as Inter ships them). |

A covers the monospace's whole character set plus Vietnamese (Latin Extended Additional) and
Spacing Modifier Letters. B covers the same Latin, Greek and Cyrillic text plus Vietnamese,
but Inter has few of the mono's symbols (arrows, maths, box drawing), so those fall back to
the mono ([fonts.md](../measure/results/fonts.md)).

## Rebuilding

Only WOFF2 is committed; `ttf/` is ignored.

- **A:** `bash source/build-a.sh`. It needs Node 20+, Python 3 with fontTools and brotli, and
  ttfautohint (or the `ttfautohint-py` package), as `design/fonts/slipgates-mono/build.sh`
  does. It clones Iosevka v34.9.1, builds `source/private-build-plans.toml` and runs
  `source/postprocess-a.py`, which writes `ttf/` and `woff2/`. It takes about four minutes.
- **B:** download and unzip the Inter 4.1 release from github.com/rsms/inter. Then run
  `python source/tune-inter.py <unzipped release> ..` from `source/`.
- **TTF from WOFF2:** if you need a TTF without rebuilding, run
  `python -c "from fontTools.ttLib import TTFont; f=TTFont('woff2/X.woff2'); f.flavor=None; f.save('X.ttf')"`.
  WOFF2 is lossless for these fonts.
