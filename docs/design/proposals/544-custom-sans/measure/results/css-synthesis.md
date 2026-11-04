# CSS synthesis check (headless Chromium, DPR 2, 40 px)

Each row renders the text through ordinary CSS on the full family and compares it with the reference: the real face for that weight and style, loaded alone. "Real face" means identical pixels and advance width.

## Slipgates Sans A (8 statics)

| Asked | Via | font-synthesis | Result | Width (px) | Reference width |
|---|---|---|---|---|---|
| 400 normal | font-weight/font-style | none | real face (SlipgatesSansA-Regular.woff2) | 496.81 | 496.81 |
| 500 normal | font-weight/font-style | none | real face (SlipgatesSansA-Medium.woff2) | 496.81 | 496.81 |
| 600 normal | font-weight/font-style | none | real face (SlipgatesSansA-SemiBold.woff2) | 496.81 | 496.81 |
| 700 normal | &lt;strong> | none | real face (SlipgatesSansA-Bold.woff2) | 496.81 | 496.81 |
| 400 italic | &lt;em> | none | real face (SlipgatesSansA-Italic.woff2) | 496.81 | 496.81 |
| 500 italic | font-weight/font-style | none | real face (SlipgatesSansA-MediumItalic.woff2) | 496.81 | 496.81 |
| 600 italic | font-weight/font-style | none | real face (SlipgatesSansA-SemiBoldItalic.woff2) | 496.81 | 496.81 |
| 700 italic | &lt;strong>&lt;em> | none | real face (SlipgatesSansA-BoldItalic.woff2) | 496.81 | 496.81 |

## Slipgates Sans B (8 statics)

| Asked | Via | font-synthesis | Result | Width (px) | Reference width |
|---|---|---|---|---|---|
| 400 normal | font-weight/font-style | none | real face (SlipgatesSansB-Regular.woff2) | 489.77 | 489.77 |
| 500 normal | font-weight/font-style | none | real face (SlipgatesSansB-Medium.woff2) | 496.70 | 496.70 |
| 600 normal | font-weight/font-style | none | real face (SlipgatesSansB-SemiBold.woff2) | 503.55 | 503.55 |
| 700 normal | &lt;strong> | none | real face (SlipgatesSansB-Bold.woff2) | 510.44 | 510.44 |
| 400 italic | &lt;em> | none | real face (SlipgatesSansB-Italic.woff2) | 491.83 | 491.83 |
| 500 italic | font-weight/font-style | none | real face (SlipgatesSansB-MediumItalic.woff2) | 498.88 | 498.88 |
| 600 italic | font-weight/font-style | none | real face (SlipgatesSansB-SemiBoldItalic.woff2) | 505.83 | 505.83 |
| 700 italic | &lt;strong>&lt;em> | none | real face (SlipgatesSansB-BoldItalic.woff2) | 512.80 | 512.80 |

## Slipgates Sans B (2 variable files)

| Asked | Via | font-synthesis | Result | Width (px) | Reference width |
|---|---|---|---|---|---|
| 400 normal | font-weight/font-style | none | real face (SlipgatesSansB[opsz,wght].woff2 at wght 400) | 449.80 | 449.80 |
| 500 normal | font-weight/font-style | none | real face (SlipgatesSansB[opsz,wght].woff2 at wght 500) | 460.42 | 460.42 |
| 600 normal | font-weight/font-style | none | real face (SlipgatesSansB[opsz,wght].woff2 at wght 600) | 470.95 | 470.95 |
| 700 normal | &lt;strong> | none | real face (SlipgatesSansB[opsz,wght].woff2 at wght 700) | 481.58 | 481.58 |
| 400 italic | &lt;em> | none | real face (SlipgatesSansB-Italic[opsz,wght].woff2 at wght 400) | 451.39 | 451.39 |
| 500 italic | font-weight/font-style | none | real face (SlipgatesSansB-Italic[opsz,wght].woff2 at wght 500) | 462.05 | 462.05 |
| 600 italic | font-weight/font-style | none | real face (SlipgatesSansB-Italic[opsz,wght].woff2 at wght 600) | 472.48 | 472.48 |
| 700 italic | &lt;strong>&lt;em> | none | real face (SlipgatesSansB-Italic[opsz,wght].woff2 at wght 700) | 483.11 | 483.11 |

## Agent Bridge Mono (6 statics, as bundled)

| Asked | Via | font-synthesis | Result | Width (px) | Reference width |
|---|---|---|---|---|---|
| 400 normal | font-weight/font-style | none | real face (AgentBridgeMono-Regular.woff2) | 600.00 | 600.00 |
| 500 normal | font-weight/font-style | none | no 500 normal face: CSS matching picks AgentBridgeMono-Regular.woff2; not synthesized | 600.00 | – |
| 600 normal | font-weight/font-style | none | real face (AgentBridgeMono-SemiBold.woff2) | 600.00 | 600.00 |
| 700 normal | &lt;strong> | none | real face (AgentBridgeMono-Bold.woff2) | 600.00 | 600.00 |
| 400 italic | &lt;em> | none | real face (AgentBridgeMono-Italic.woff2) | 600.00 | 600.00 |
| 500 italic | font-weight/font-style | none | no 500 italic face: CSS matching picks AgentBridgeMono-Italic.woff2; not synthesized | 600.00 | – |
| 600 italic | font-weight/font-style | none | real face (AgentBridgeMono-SemiBoldItalic.woff2) | 600.00 | 600.00 |
| 700 italic | &lt;strong>&lt;em> | none | real face (AgentBridgeMono-BoldItalic.woff2) | 600.00 | 600.00 |

## Control: Slipgates Sans A, Regular file only

| Asked | Via | font-synthesis | Result | Width (px) | Reference width |
|---|---|---|---|---|---|
| 400 normal | font-weight/font-style | none | real face (SlipgatesSansA-Regular.woff2) | 496.81 | 496.81 |
| 500 normal | font-weight/font-style | none | no 500 normal face: CSS matching picks SlipgatesSansA-Regular.woff2; not synthesized | 496.81 | – |
| 600 normal | font-weight/font-style | none | no 600 normal face: CSS matching picks SlipgatesSansA-Regular.woff2; not synthesized | 496.81 | – |
| 700 normal | &lt;strong> | none | no 700 normal face: CSS matching picks SlipgatesSansA-Regular.woff2; not synthesized | 496.81 | – |
| 400 italic | &lt;em> | none | no 400 italic face: CSS matching picks SlipgatesSansA-Regular.woff2; not synthesized | 496.81 | – |
| 500 italic | font-weight/font-style | none | no 500 italic face: CSS matching picks SlipgatesSansA-Regular.woff2; not synthesized | 496.81 | – |
| 600 italic | font-weight/font-style | none | no 600 italic face: CSS matching picks SlipgatesSansA-Regular.woff2; not synthesized | 496.81 | – |
| 700 italic | &lt;strong>&lt;em> | none | no 700 italic face: CSS matching picks SlipgatesSansA-Regular.woff2; not synthesized | 496.81 | – |
| 400 normal | font-weight/font-style | auto | real face (SlipgatesSansA-Regular.woff2) | 496.81 | 496.81 |
| 500 normal | font-weight/font-style | auto | no 500 normal face: CSS matching picks SlipgatesSansA-Regular.woff2; not synthesized | 496.81 | – |
| 600 normal | font-weight/font-style | auto | no 600 normal face: SYNTHESIZED (differs from SlipgatesSansA-Regular.woff2, the face CSS matching picks) | 496.81 | – |
| 700 normal | &lt;strong> | auto | no 700 normal face: SYNTHESIZED (differs from SlipgatesSansA-Regular.woff2, the face CSS matching picks) | 496.81 | – |
| 400 italic | &lt;em> | auto | no 400 italic face: SYNTHESIZED (differs from SlipgatesSansA-Regular.woff2, the face CSS matching picks) | 496.81 | – |
| 500 italic | font-weight/font-style | auto | no 500 italic face: SYNTHESIZED (differs from SlipgatesSansA-Regular.woff2, the face CSS matching picks) | 496.81 | – |
| 600 italic | font-weight/font-style | auto | no 600 italic face: SYNTHESIZED (differs from SlipgatesSansA-Regular.woff2, the face CSS matching picks) | 496.81 | – |
| 700 italic | &lt;strong>&lt;em> | auto | no 700 italic face: SYNTHESIZED (differs from SlipgatesSansA-Regular.woff2, the face CSS matching picks) | 496.81 | – |

## Reading width at 14 px

One paragraph of 203 characters, set on one line at 14 px, Regular.

| Face | Width (px) | Relative to Segoe UI Variable | Characters in 680 px |
|---|---|---|---|
| Segoe UI Variable (Text, wght 400) | 1229.1 | 1.000 | 112 |
| Slipgates Sans A | 1340.6 | 1.091 | 103 |
| Slipgates Sans B | 1329.6 | 1.082 | 104 |
| Agent Bridge Mono | 1705.2 | 1.387 | 81 |

