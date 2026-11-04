# Proposal #544: a custom sans to pair with the monospace (decided)

> **Decided 2026-11-03.** The owner approved **every recommended answer** (§10, recorded on
> [#557](https://github.com/danswett/agent-bridge/pull/557)): 1 (a) B everywhere, as Slipgates Sans;
> 2 (a) hinted statics on Windows, the variable pair elsewhere; 3 (a) no synthesized styles, as spec,
> with a check on each platform; 4 (a) the mono gains Medium 500 and Vietnamese, and Windows checks
> its bold and italic are real; 5 (a) the names Slipgates Sans and Slipgates Mono, with no Reserved
> Font Name; 6 (a) a true italic on Windows; 7 (a) the reading measure about 8% wider. #557 applied
> them: the font is [design/fonts/slipgates-sans](../../../design/fonts/slipgates-sans/README.md), the
> rules are in [ui.md](../ui.md) §4, and `tools/design-gen` wires both fonts into every client.
> **ui.md is the spec.** This page stays as the reasoning behind it; where the two differ, ui.md
> wins. A and C were not chosen. Their fonts, and B's proposal build, stay in
> [544-custom-sans/fonts/](544-custom-sans/fonts/) only so the boards still render; nothing ships
> or loads them.

*Written as a draft for the owner, from the design track.* Issues:
[#544](https://github.com/danswett/agent-bridge/issues/544) (this),
[#535](https://github.com/danswett/agent-bridge/issues/535) (the Slipgates rename) and
[#536](https://github.com/danswett/agent-bridge/issues/536) (the brand).

The mock-ups are in [544-custom-sans/](544-custom-sans/), with every board on one
[contact sheet](544-custom-sans/contact-sheet.png). They are drawn from boards in
[`/design/canvas`](../../../design/canvas) (`Proposal-544-*.dc.html`) with
[tools/design-mockups](../../../tools/design-mockups/README.md), in headless Chromium, using the
real font files: the two built here, the bundled monospace, and the system faces where this
machine has them. The machines are devbox, studio and laptop, and every session, time and
version is made up.

## 1. The ask

The owner likes the bundled monospace,
[Slipgates Mono](../../../design/fonts/slipgates-mono/README.md): Iosevka 34.9.1 in the
IoskeleyMono configuration, with a tailed `l`, a 600-unit width and no ligatures. Today every
other piece of text uses the platform's face: Segoe UI Variable on Windows, SF Pro on Apple,
Roboto (Flex) on Android, and `system-ui` in the PWA (ui.md §4). The ask is a custom sans for
all of that text, so that all the type is custom and related.

The owner added two requirements:

- **Every platform:** before and after on Windows, the PWA (desktop and phone), macOS, iPhone
  and Android.
- **No synthesized styles:** the family ships a real face for every weight and style the ramps
  use. No faux bold, italic or oblique, on any platform, and the same rule applies to the mono.

## 2. What the ramps need

This is an audit of `type.roles` in tokens.json per platform, with the Apple and Android
equivalents and ui.md's inline styles (Markdown emphasis in a reply, strong, code).

| Weight or style | Used by | Needed? |
|---|---|---|
| Light 300 | nothing | no |
| **Regular 400** | every row, reading and meta role | yes |
| **Medium 500** | Android Title Small (section headers), the Material tab labels; also the iOS tab bar | yes |
| **SemiBold 600** | Windows and PWA titles and section headers; status words in rows | yes |
| **Bold 700** | macOS toolbar title and section headers, iOS Large Title, Markdown `**strong**` | yes |
| Heavy 800–900 | nothing | no |
| **Italic** at 400, 500, 600 and 700 | Markdown `*emphasis*` in replies, which can sit inside any role, including strong | yes |

So the family needs **4 weights × upright and italic = 8 faces**. Both custom options ship all
eight as real faces ([weights board](544-custom-sans/proposal-544-weights.png)).

**The monospace has two gaps** under the same rule. tokens.json gives it 400, 600 and 700,
upright and italic, and it ships those six faces, so nothing in today's ramps is synthesized.
But:

1. **No Medium 500.** Inline mono inside an Android Title Small (medium) header would fall back.
   DirectWrite resolves 500 to the SemiBold face and CSS resolves it to Regular. Neither
   synthesizes, but neither is a match.
2. **No Vietnamese.** The mono covers only 3 of the 256 Latin Extended Additional codepoints
   (both new sans faces cover them; see §5).

Both are cheap to fix by rebuilding the mono with a Medium weight and that block (decision 4).

## 3. The options

### A. Same skeleton: Slipgates Sans A (built)

A custom Iosevka 34.9.1 build, **quasi-proportional, the way Iosevka Aile is built**, from the
mono's own IoskeleyMono variants: tailed `l`, flag `1`, straight `7`, open 4, 6 and 9, and
flat-top 3. It comes in Regular, Medium, SemiBold and Bold, each with a true italic, and is
hinted with ttfautohint. Width is 540 rather than 600: in quasi-proportional spacing the width
sets each letter's proportions, and 600 drew the text noticeably wide beside Segoe.

The build uses the same toolchain as the mono's `build.sh`
([build-a.sh](544-custom-sans/fonts/source/build-a.sh),
[plan](544-custom-sans/fonts/source/private-build-plans.toml)). It took about four minutes.
**Nothing was stood in:** these are the real fonts.

- **For:** it is literally the mono's skeleton, so the kinship is complete. The x-height is
  identical, so inline mono needs no scaling (×1.000). Advances are the same at every weight,
  so bolding a row never reflows it.
- **Against:** it reads as "a programmer's font". Its even, squarish rhythm is lovely in
  headings and the wordmark, but in long replies and in 12 px meta at 1× it is less comfortable
  than a humanist or grotesk text face. Iosevka builds no variable font, so the eight statics
  are the payload (1.53 MB WOFF2 in total, although the Latin subset of Regular is only
  23.8 KB).

### B. A tuned grotesk: Slipgates Sans B (built; recommended)

**Inter 4.1** was chosen over the alternatives (IBM Plex Sans, Source Sans 3, Atkinson
Hyperlegible) for four reasons:

- It was drawn for UI at 11 to 16 px, with a tall x-height and open apertures.
- It has an optical-size axis, so it switches to a Display design at title sizes.
- It ships hand-hinted statics and a variable font with a true italic.
- It already carries the alternates that echo the mono, so nothing needs redrawing.

[tune-inter.py](544-custom-sans/fonts/source/tune-inter.py) makes five of Inter's own
alternates the defaults with fontTools. It does this by remapping the cmap and every
single-substitution lookup, so tabular figures and case forms still work on the new shapes.

| Detail | Inter's default | Made the default | Echoes the mono's |
|---|---|---|---|
| `l` | plain stroke | `cv05`, tailed `l` | tailed `l`, so `Il1` stay distinct |
| 4 | closed | `cv02`, open 4 | semi-open 4 |
| 6, 9 | closed contours | `cv03`, `cv04`, open | open-contour 6 and 9 |
| 3 | round top | `cv09`, flat-top 3 | flat-top 3 |
| `. , : ; ! ?`, dots on `i` and `j` | round | `ss07`, square | square dots |

Then the script subsets to the mono's ranges plus Vietnamese, renames the family to "Slipgates
Sans B" and credits both copyright holders. No outline is redrawn. Making the alternates the
defaults matters because WinUI's `TextBlock` can't switch on OpenType features, and on Apple
each label would need code to do it.

The script builds 8 hinted statics and the variable pair: `wght` 100–900 plus `opsz`, upright
and a true italic. **Nothing was stood in.**

- **For:** it is the most legible at 12 to 14 px of everything tested, at 1×, 1.5× and 2×, in
  dark and light ([legibility](544-custom-sans/proposal-544-legibility.png)). It sits naturally
  in both Fluent and HIG. The mono details carry the family resemblance where the eye looks
  for it: IDs, numbers and punctuation.
- **Against:** the kinship is in the details, not the skeleton. Its x-height is 5% taller than
  the mono's (inline mono scales ×1.05, inside the rule's range). Its coverage is narrower than
  A's in symbols, which fall back to the mono or the system face.

### C. Brand face only (built from A)

A for the wordmark, titles, headings and first run only. Rows, replies, meta and dense UI stay
on the system face. The boards draw it with A as the brand face, because A is the face with
the mono's skeleton; B could do the same job.

- **For:** it carries the least risk and the smallest payload (two weights), and every dense
  surface stays exactly as it is today.
- **Against:** it doesn't do what was asked. Most of the type stays system type, and two text
  faces (brand and system) plus the mono is a three-way pairing that is harder to make look
  related.

Side by side: [options](544-custom-sans/proposal-544-options.png). Specimens beside the mono,
light and dark: [specimens](544-custom-sans/proposal-544-specimens.png) and
[specimens-dark](544-custom-sans/proposal-544-specimens-dark.png).

## 4. Before and after, per platform

Each board shows the system face first, then B (recommended), then A, on the same session list
and reply.

| Platform | Board | Notes |
|---|---|---|
| Windows (WinUI) | [windows](544-custom-sans/proposal-544-windows.png) | Ramp 20/14/15/12, Segoe UI Variable before |
| PWA, desktop | [pwa-desktop](544-custom-sans/proposal-544-pwa-desktop.png) | Same ramp, `system-ui` before |
| PWA, phone | [pwa-phone](544-custom-sans/proposal-544-pwa-phone.png) | Phone ramp 24/15/16/13 |
| macOS | [mac](544-custom-sans/proposal-544-mac.png) | Ramp 13/13/14/11, bold toolbar title |
| iPhone | [iphone](544-custom-sans/proposal-544-iphone.png) | Large Title 34, Body 17, Subheadline 15, Footnote 13 |
| Android (Material 3) | [android](544-custom-sans/proposal-544-android.png) | Title Large 22, Body Large 16, Title Small 14 medium, Roboto before |
| A conversation turn | [app](544-custom-sans/proposal-544-app.png) | A reply with Markdown, inline mono and a tool block |
| Scaling, iPhone | [scaling](544-custom-sans/proposal-544-scaling.png) | B at Dynamic Type Large and AX3 |
| Scaling, Android | [scaling-android](544-custom-sans/proposal-544-scaling-android.png) | B at font scale 100% and 200% (nonlinear) |

**Stand-ins:** SF Pro can't be loaded in Chromium on Windows, so the Apple "before" columns
use Inter as a labelled stand-in for SF. They look close to B, which is honest: SF and Inter
are close relatives, so on Apple B changes the details more than the texture. Roboto and
Segoe UI Variable are the real faces.

## 5. Measured results

All measured from the font files with fontTools, DirectWrite and headless Chromium
([measure/](544-custom-sans/measure/): [fonts.md](544-custom-sans/measure/results/fonts.md),
[dwrite.txt](544-custom-sans/measure/results/dwrite.txt),
[css-synthesis.md](544-custom-sans/measure/results/css-synthesis.md)).

| | **A** Slipgates Sans A | **B** Slipgates Sans B | Mono (today) | Segoe UI Variable | Roboto |
|---|---|---|---|---|---|
| x-height / em | 0.520 | 0.546 | 0.520 | 0.500 | 0.528 |
| Cap height / em | 0.690 | 0.728 | 0.690 | 0.700 | 0.711 |
| Inline mono scale for an x-height match (`type.inlineMono`) | **1.000** | **1.050** | — | 0.962 | 1.016 |
| Average advance / em | 0.494 | 0.510 | 0.600 | 0.469 | 0.475 |
| Width of a reading paragraph at 14 px, relative to Segoe | 1.091 | 1.082 | 1.387 | 1.000 | — |
| Codepoints | 3054 | 1561 | 2718 | 2417 | 927 |
| Vietnamese (U+1E00–1EFF) | 256 | 255 | **3** | 250 | 101 |
| Real faces | 8 statics | 8 statics + 2 variable | 6 statics (no 500) | variable font, upright only (no italic, 500 → Semibold) | — |
| WOFF2, whole family | 1,532,588 B (8 statics) | 813,040 B (8 statics); **551,920 B (variable pair)**; 360,084 B (variable, `opsz` pinned) | 913,380 B | system | system |
| WOFF2, Regular, Latin only | 23.8 KB | 36.8 KB static; 96.7 KB variable; 62.8 KB pinned | 22.4 KB | — | — |
| Hinting | ttfautohint | statics hinted by Inter; variable unhinted | ttfautohint | hand-hinted | hinted |
| DirectWrite: of 8 asks (4 weights × upright, italic), synthesized | **0** | **0** (statics and variable) | 0, but 500 → SemiBold | **4 per family, all italic** (12 over Small, Text and Display) | — |
| CSS (Chromium), with real face pixel- and advance-identical | 8 of 8 | 8 of 8 statics, 8 of 8 variable | 6 of 6; 500 → Regular | — | — |

The other inline-mono scales are SF Pro ×0.977 and Roboto Flex ×0.988.

What the numbers say:

- **Inline mono:** A matches exactly, and B needs ×1.05. Both are closer to 1 than Segoe's
  ×0.962, because the mono was drawn tall for Segoe.
- **Width:** set as a paragraph, both run about 8 to 9% wider than Segoe UI Variable
  (css-synthesis.md, "Reading width"). So a row truncates a few characters earlier at the same
  size, and a reply's measure holds about 8% fewer characters: 104 rather than 112 in 680 px
  at 14 px. Keeping ui.md's measure would want `max-width` raised by the same 8 to 9%, from
  about 680–712 px to about 735–775 px. Nothing else in the layouts changes.
- **Coverage:** both cover every Latin, Greek and Cyrillic letter a UI string needs, plus
  Vietnamese. B lacks most IPA, arrows, maths and box drawing, which fall back to the mono (by
  stacking it second), then the system face. That is the right order, because the mono covers
  them in a matching style.
- **Payload:** for the PWA, B's variable pair (552 KB, or 360 KB with `opsz` pinned) beats its
  eight statics (813 KB) and covers 100–900. With `unicode-range` subsets, a Latin-only page
  downloads 63 to 97 KB for the upright. Native apps bundle the files, so size matters little.
- **Synthesis:** neither custom family is ever synthesized, on any path we can execute. Today,
  **every italic on Windows is synthesized**: Segoe UI Variable has no italic, so DirectWrite
  slants it ([synthesis board](544-custom-sans/proposal-544-synthesis.png), last row). Both
  options fix that.

## 6. No synthesized styles: how each platform is held to it

The [synthesis board](544-custom-sans/proposal-544-synthesis.png) renders each face three
ways:

- through the normal API, from the full family;
- from a Regular-only control family, which forces the platform to fake it;
- overlaid on each other.

The real faces differ visibly (italic `a` and `f`, weight distribution); the fakes are slanted
or smeared Regular. The automated checks compare glyph advances and pixels against the real
face loaded alone.

| Platform | Prevention | Proof here |
|---|---|---|
| **CSS (PWA)** | `font-synthesis: none` on `:root`. A complete `@font-face` set: eight statics, each with its exact `font-weight` and `font-style`; or the variable pair with `font-weight: 100 900` and `font-style: normal` / `italic`. Subset with `unicode-range`. | [css-synthesis.md](544-custom-sans/measure/results/css-synthesis.md): `<strong>`, `<em>` and `font-weight` reach the real face, pixel- and advance-identical. The Regular-only control is synthesized at 600, 700 and italic with `font-synthesis: auto`. |
| **WinUI / DirectWrite** | Bundle all eight statics (hinted). Register every file through one `FontFamily` URI list, or a custom font collection, so DirectWrite sees the whole family. Check at startup that `IDWriteFontFace::GetSimulations()` returns `NONE` for each ramp style. | [dwrite.txt](544-custom-sans/measure/results/dwrite.txt) and [dwrite-check.cs](544-custom-sans/measure/dwrite-check.cs): 0 simulations for A and B; 6 for the control. |
| **Apple (macOS, iOS)** | List every file in `UIAppFonts` (iOS) or `ATSApplicationFontsPath` (macOS). Map weights to PostScript names: `.bold()` → `SlipgatesSansB-Bold`, `.italic()` → `-Italic`, and so on. Add a test asserting `UIFont.fontName` / `NSFont.fontName` resolve to those names, never to Regular with a trait. | Can't run here; the style-linking table in [fonts.md](544-custom-sans/measure/results/fonts.md) shows the flags Apple's matcher reads (`macStyle`, `fsSelection`, names 16 and 17) are set on every face. |
| **Android** | `res/font/slipgates_sans.xml` with eight `<font>` entries: `android:fontWeight` 400/500/600/700 × `android:fontStyle` normal/italic, in lowercase files. Rename the bracketed variable files if they are used: API 26+ can use them via `fontVariationSettings`. In Compose, set `FontSynthesis.None` in the theme's `TextStyle`s. | Can't run here; the same style-linking table applies. |

**The mono under the same rule.** It ships 400, 600 and 700, upright and italic, which covers
every weight its tokens use. In the PWA, `tokens.css` and the service worker already declare
all six faces. Two items still need checking:

- **WinUI**, which references the mono by a single-file URI (`…#Slipgates Mono`). It is
  **unverified** whether DirectWrite picks up the sibling files from that URI or simulates bold
  and italic from Regular. That needs a SelfTest comparing advances, whatever the owner decides
  here.
- **The design-mockups renderer**, whose built-in mono declares only four faces. These boards
  use their own complete set.

## 7. Platform caveats

**Windows.**

- **ClearType:** DirectWrite uses ClearType's subpixel rendering with the font's hinting at
  small sizes. A and B's statics are hinted (ttfautohint and Inter's own), so ship statics in
  WinUI. B's variable font is unhinted, which is acceptable at 1.5× and 2× but softer at 100%.
- **What these renders show:** Chromium here renders grayscale anti-aliasing, not ClearType, so
  the boards are fair to the shapes but not a ClearType proof. A one-hour check on a 100%
  display before rollout is part of the plan.
- **Text scaling:** Windows text scaling goes to 225% and applies to custom fonts the same way
  (WinUI's `IsTextScaleFactorEnabled`). Nothing changes.
- **Segoe UI Variable's optical sizes:** these go. B's `opsz` axis replaces them in the PWA, and
  WinUI picks the right static by size.

**PWA.** Respects the browser's text zoom and the OS text size, since sizes stay in `rem`.
`font-size-adjust` (from `type.inlineMono`) keeps fallback text and inline mono at the matched
x-height.

**macOS.** It has no Dynamic Type. The ramp is fixed at 13/11 pt, and custom fonts need no
scaling code. AppKit's `NSFont(name:size:)` takes the registered PostScript name.

**iOS and iPadOS.** Custom fonts must still follow Dynamic Type. Each role becomes
`UIFontMetrics(forTextStyle:).scaledFont(for:)`, or `Font.custom(_:size:relativeTo:)` in
SwiftUI. At AX3, Body goes from 17 to 40 pt, Large Title from 34 to 52, Subheadline to 36 and
Footnote to 33 ([scaling](544-custom-sans/proposal-544-scaling.png)). B holds up at those
sizes: its `opsz` axis moves to the Display design.

**Android.**

- **`sp` scaling:** sizes stay in `sp`. Android 14 scales nonlinearly up to 200%: at that
  setting 12 → 24, 14 → 26, 16 → 28, 22 → 35 and 11 → 22
  ([scaling-android](544-custom-sans/proposal-544-scaling-android.png)).
- **Packaging:** the font goes in `res/font` with the family XML above. Downloadable fonts
  aren't needed, because the APK bundles the files. Material 3's type scale is kept; only its
  `fontFamily` changes.

**Fluent and HIG.**

- **Fluent:** Fluent 2 allows a brand typeface in place of Segoe.
- **HIG:** it accepts custom fonts that support Dynamic Type. The system face remains a fair
  choice for controls the OS draws itself (menus, context menus and alerts), which we leave
  alone.
- **Fit:** B fits both conventions without looking foreign. A is more distinctive and would
  read as deliberately "ours" against native chrome.

## 8. Licence and naming

| Base | Licence | Reserved Font Names | Naming under Slipgates |
|---|---|---|---|
| Iosevka 34.9.1 | SIL OFL 1.1 | none | free to call A "Slipgates Sans" |
| IoskeleyMono configuration | SIL OFL 1.1 | none | same |
| Inter 4.1 | SIL OFL 1.1 | none (Inter's licence declares none) | free to call B "Slipgates Sans". We renamed it anyway, because a modified font under its upstream name would be mistaken for it. |

The OFL lets us modify, bundle and redistribute the fonts, including in a commercial app. It
forbids selling them alone, and it requires shipping the licence and keeping the copyright
notices. [OFL.txt](544-custom-sans/fonts/OFL.txt) carries every holder.

If we ourselves declare "Slipgates" a Reserved Font Name, others could still modify the font
but would have to rename their version. That protects the brand, and it is decision 5.

## 9. Recommendation

**B: Slipgates Sans B, renamed "Slipgates Sans", for all non-mono text on every client.** It is
the most legible at 12 to 15 px, it is a credible native citizen in Fluent, HIG and Material,
and it has a true italic. It carries the mono's tailed `l`, numerals and square punctuation,
so IDs, counts and code read the same in both faces. The payload is reasonable:

| Client | Files |
|---|---|
| Windows | hinted statics |
| PWA | the variable pair, `opsz` pinned or not, with `unicode-range` |
| Apple and Android | variable or statics |

A is the strong alternative if the owner values the skeleton kinship over reading comfort. C
is the fallback if a full rollout is too much for now.

## 10. Decisions for the owner

Each has the recommended answer first. **The owner chose (a) for every one** (2026-11-03).

1. **Which option?**
   - **(a) B everywhere (recommended).**
   - (b) A everywhere.
   - (c) C, brand only.
   - (d) Keep the system face.

   Boards: [options](544-custom-sans/proposal-544-options.png),
   [specimens](544-custom-sans/proposal-544-specimens.png),
   [specimens-dark](544-custom-sans/proposal-544-specimens-dark.png),
   [legibility](544-custom-sans/proposal-544-legibility.png) (1×),
   [1.5×](544-custom-sans/proposal-544-legibility-1_5x.png),
   [2×](544-custom-sans/proposal-544-legibility-2x.png), and every platform board in §4.
2. **Which files, per platform?**
   - **(a) Hinted statics on Windows; the variable pair, with `opsz` pinned, in the PWA;
     variable on Apple and Android (recommended).**
   - (b) Statics everywhere: simplest, larger PWA payload.
   - (c) Variable everywhere: smallest, but unhinted on Windows at 100%.

   Evidence: §5 table, [weights](544-custom-sans/proposal-544-weights.png).
3. **No faux styles: adopt the §6 rules as spec?**
   - **(a) Yes: `font-synthesis: none`, complete face sets, and a startup or test check per
     platform that no face is simulated (recommended).**
   - (b) Rules only, with no tests.

   Board: [synthesis](544-custom-sans/proposal-544-synthesis.png).
4. **Close the mono's gaps?**
   - **(a) Rebuild it with Medium 500 and Vietnamese, and add the WinUI SelfTest for its
     sibling faces (recommended).**
   - (b) Leave it.

   Board: [weights](544-custom-sans/proposal-544-weights.png) (mono row).
5. **Names?**
   - **(a) "Slipgates Sans" and "Slipgates Mono" (renaming Slipgates Mono with #536), with
     no Reserved Font Name (recommended):** simplest for contributors.
   - (b) The same names, with "Slipgates" reserved.
   - (c) Keep "Slipgates Mono".

   Board: [specimens](544-custom-sans/proposal-544-specimens.png).
6. **Italic on Windows:** today all Markdown emphasis there is Segoe's synthesized oblique.
   - **(a) Use the family's true italic for emphasis everywhere (recommended).**
   - (b) Keep emphasis upright on Windows.

   Board: [synthesis](544-custom-sans/proposal-544-synthesis.png) (last row),
   [app](544-custom-sans/proposal-544-app.png).
7. **Reading measure:**
   - **(a) Widen `max-width` by about 8% to keep the same number of characters per line
     (recommended).**
   - (b) Keep the pixel width, giving a shorter measure.

   Board: [windows](544-custom-sans/proposal-544-windows.png),
   [pwa-desktop](544-custom-sans/proposal-544-pwa-desktop.png).

What #557 did with them:

- B became [design/fonts/slipgates-sans](../../../design/fonts/slipgates-sans/README.md), built by its
  `build.py` from the Inter 4.1 release: the variable pair as TTF and WOFF2, and Inter's hinted
  statics, 400–700 upright and italic, in one collection for Windows.
- The mono was rebuilt as Slipgates Mono with Medium 500 and Vietnamese, and a collection for
  Windows.
- ui.md §4 and `type` in tokens.json name both families, the weights and styles the ramps use,
  the no-synthesis rule (§4.2), the new inline-mono x-height and the 770 px measure.
- `tools/design-gen` refuses a family that lacks a face the ramps use, and generates the PWA's
  `@font-face` set with `font-synthesis: none`, the Windows font families and self-test keys,
  `design/apple` (the font registration list and each face's PostScript name) and
  `design/android/font` (a complete family XML per font).
- The checks: a Playwright test in the PWA, and a DirectWrite and rendered-advance check in the
  Windows self-test. The Apple and Android clients don't exist yet, so their checks are
  follow-ups, as is a ClearType check on real hardware at 100% scale.
