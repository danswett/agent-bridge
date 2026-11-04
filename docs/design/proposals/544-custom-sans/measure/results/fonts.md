## Metrics (Regular)

| Face | unitsPerEm | x-height | x/em | cap/em | mono scale for x-height match | avg advance /em | hinted |
|---|---|---|---|---|---|---|---|
| A (Slipgates Sans A) | 1000 | 520 | 0.520 | 0.690 | 1.000 | 0.494 | yes (fpgm/prep) |
| B (Slipgates Sans B) | 2048 | 1118 | 0.546 | 0.728 | 1.050 | 0.510 | yes (fpgm/prep) |
| Mono (Slipgates Mono) | 1000 | 520 | 0.520 | 0.690 | 1.000 | 0.600 | yes (fpgm/prep) |
| System: Segoe UI Variable (SegUIVar.ttf) | 2048 | 1024 | 0.500 | 0.700 | 0.962 | 0.469 | yes (fpgm/prep) |
| System: Roboto (Roboto.ttf) | 2048 | 1082 | 0.528 | 0.711 | 1.016 | 0.475 | yes (fpgm/prep) |

## Coverage (codepoints in cmap)

| Block | A (Slipgates Sans A) | B (Slipgates Sans B) | Mono (Slipgates Mono) | System: Segoe UI Variable (SegUIVar.ttf) | System: Roboto (Roboto.ttf) |
|---|---|---|---|---|---|
| Basic Latin (U+0020–007E) | 95 | 95 | 95 | 95 | 95 |
| Latin-1 (U+00A0–00FF) | 96 | 95 | 96 | 96 | 96 |
| Latin Ext-A (U+0100–017F) | 128 | 127 | 128 | 128 | 128 |
| Latin Ext-B (U+0180–024F) | 208 | 207 | 208 | 208 | 18 |
| IPA + modifiers (U+0250–02FF) | 95 | 80 | 15 | 176 | 12 |
| Combining marks (U+0300–036F) | 112 | 66 | 112 | 112 | 6 |
| Greek (U+0370–03FF) | 121 | 105 | 121 | 135 | 75 |
| Cyrillic (U+0400–04FF) | 256 | 248 | 256 | 256 | 255 |
| Latin Ext Additional (Vietnamese) (U+1E00–1EFF) | 256 | 255 | 3 | 250 | 101 |
| General Punctuation (U+2000–206F) | 94 | 77 | 94 | 56 | 38 |
| Currency (U+20A0–20CF) | 26 | 28 | 26 | 32 | 15 |
| Letterlike + Number forms (U+2100–218F) | 121 | 74 | 121 | 26 | 10 |
| Arrows (U+2190–21FF) | 112 | 28 | 112 | 9 | 0 |
| Math operators (U+2200–22FF) | 256 | 18 | 256 | 20 | 12 |
| Box drawing + blocks (U+2500–259F) | 160 | 0 | 160 | 48 | 0 |
| Geometric shapes (U+25A0–25FF) | 96 | 23 | 96 | 16 | 4 |
| Misc symbols + dingbats (U+2600–27BF) | 187 | 22 | 187 | 23 | 0 |
| **Total codepoints** | 3054 | 1561 | 2718 | 2417 | 927 |

A (Slipgates Sans A) lacks 135 of System: Segoe UI Variable (SegUIVar.ttf)'s Latin/Greek/Cyrillic/punctuation codepoints, e.g. `ɐɑɒɓɔɕɗɘəɚɛɜɝɞɠɡɢɣɤɥɦɧɨɩɪɬɮɯɰɱɲɴɵɶɷɸɺɼɿʀ`.

B (Slipgates Sans B) lacks 244 of System: Segoe UI Variable (SegUIVar.ttf)'s Latin/Greek/Cyrillic/punctuation codepoints, e.g. `ŉǄɐɑɒɓɔɕɖɗɘəɚɛɜɝɞɟɠɡɢɣɤɥɦɧɨɩɪɫɬɭɮɯɰɱɲɳɴɵ`.

Mono (Slipgates Mono) lacks 462 of System: Segoe UI Variable (SegUIVar.ttf)'s Latin/Greek/Cyrillic/punctuation codepoints, e.g. `ɐɑɒɓɔɕɗɘəɚɛɜɝɞɠɡɢɣɤɥɦɧɨɩɪɬɮɯɰɱɲɴɵɶɷɸɺɼɿʀ`.

A (Slipgates Sans A) lacks 16 of System: Roboto (Roboto.ttf)'s Latin/Greek/Cyrillic/punctuation codepoints, e.g. `ə⁺⁻⁼⁽⁾ⁿ₊₋₌₍₎`.

B (Slipgates Sans B) lacks 43 of System: Roboto (Roboto.ttf)'s Latin/Greek/Cyrillic/punctuation codepoints, e.g. `ŉəѺѻѼѽѾѿҞҟ⁰⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾ⁿ₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎`.

Mono (Slipgates Mono) lacks 128 of System: Roboto (Roboto.ttf)'s Latin/Greek/Cyrillic/punctuation codepoints, e.g. `əʼˆˇˉ˘˙˚˛˜˝˳ḀḁḾḿẀẁẂẃẄẅẞẠạẢảẤấẦầẨẩẪẫẬậẮắẰ`.

## Payload (WOFF2 bytes)

| Set | Files | Total | Latin-only subset of Regular |
|---|---|---|---|
| A statics, 4 weights × upright+italic | 8 | 1,532,588 | 23,788 (SlipgatesSansA-Regular.woff2) |
| B statics, 4 weights × upright+italic | 8 | 813,040 | 36,832 (SlipgatesSansB-Regular.woff2) |
| B variable, wght 100–900 + opsz, upright+italic | 2 | 551,920 | 96,656 (SlipgatesSansB[opsz,wght].woff2) |
| Mono statics (bundled today) | 6 | 913,380 | 22,404 |
| B variable, opsz pinned at 14 (wght axis only), upright+italic | 2 | 360,084 | 62,776 |

## Style linking (what Apple's and Android's matchers read)

| File | name 1 / 2 | name 16 / 17 | usWeightClass | fsSelection | macStyle | italicAngle |
|---|---|---|---|---|---|---|
| SlipgatesSansA-Bold.woff2 | Slipgates Sans A / Bold | Slipgates Sans A / Bold | 700 | BOLD | bold | 0 |
| SlipgatesSansA-BoldItalic.woff2 | Slipgates Sans A / Bold Italic | Slipgates Sans A / Bold Italic | 700 | ITALIC,BOLD | bold,italic | -9 |
| SlipgatesSansA-Italic.woff2 | Slipgates Sans A / Italic | Slipgates Sans A / Italic | 400 | ITALIC | italic | -9 |
| SlipgatesSansA-Medium.woff2 | Slipgates Sans A Medium / Regular | Slipgates Sans A / Medium | 500 | REGULAR | – | 0 |
| SlipgatesSansA-MediumItalic.woff2 | Slipgates Sans A Medium / Italic | Slipgates Sans A / Medium Italic | 500 | ITALIC | italic | -9 |
| SlipgatesSansA-Regular.woff2 | Slipgates Sans A / Regular | Slipgates Sans A / Regular | 400 | REGULAR | – | 0 |
| SlipgatesSansA-SemiBold.woff2 | Slipgates Sans A Semibold / Regular | Slipgates Sans A / Semibold | 600 | REGULAR | – | 0 |
| SlipgatesSansA-SemiBoldItalic.woff2 | Slipgates Sans A Semibold / Italic | Slipgates Sans A / Semibold Italic | 600 | ITALIC | italic | -9 |
| SlipgatesSansB-Bold.woff2 | Slipgates Sans B / Bold | – / – | 700 | BOLD | bold | 0 |
| SlipgatesSansB-BoldItalic.woff2 | Slipgates Sans B / Bold Italic | – / – | 700 | ITALIC,BOLD | bold,italic | -9.39999 |
| SlipgatesSansB-Italic.woff2 | Slipgates Sans B / Italic | – / – | 400 | ITALIC | italic | -9.39999 |
| SlipgatesSansB-Italic[opsz,wght].woff2 | Slipgates Sans B / Italic | – / – | 400 | ITALIC | italic | -9.39999 |
| SlipgatesSansB-Medium.woff2 | Slipgates Sans B Medium / Regular | Slipgates Sans B / Medium | 500 | REGULAR | – | 0 |
| SlipgatesSansB-MediumItalic.woff2 | Slipgates Sans B Medium / Italic | Slipgates Sans B / Medium Italic | 500 | ITALIC | italic | -9.39999 |
| SlipgatesSansB-Regular.woff2 | Slipgates Sans B / Regular | – / – | 400 | REGULAR | – | 0 |
| SlipgatesSansB-SemiBold.woff2 | Slipgates Sans B SemiBold / Regular | Slipgates Sans B / SemiBold | 600 | REGULAR | – | 0 |
| SlipgatesSansB-SemiBoldItalic.woff2 | Slipgates Sans B SemiBold / Italic | Slipgates Sans B / SemiBold Italic | 600 | ITALIC | italic | -9.39999 |
| SlipgatesSansB[opsz,wght].woff2 | Slipgates Sans B / Regular | – / – | 400 | REGULAR | – | 0 |
| SlipgatesMono-Bold.woff2 | Slipgates Mono / Bold | Slipgates Mono / Bold | 700 | BOLD | bold | 0 |
| SlipgatesMono-BoldItalic.woff2 | Slipgates Mono / Bold Italic | Slipgates Mono / Bold Italic | 700 | ITALIC,BOLD | bold,italic | -12 |
| SlipgatesMono-Italic.woff2 | Slipgates Mono / Italic | Slipgates Mono / Italic | 400 | ITALIC | italic | -12 |
| SlipgatesMono-Regular.woff2 | Slipgates Mono / Regular | Slipgates Mono / Regular | 400 | REGULAR | – | 0 |
| SlipgatesMono-SemiBold.woff2 | Slipgates Mono Semibold / Regular | Slipgates Mono / Semibold | 600 | REGULAR | – | 0 |
| SlipgatesMono-SemiBoldItalic.woff2 | Slipgates Mono Semibold / Italic | Slipgates Mono / Semibold Italic | 600 | ITALIC | italic | -12 |
