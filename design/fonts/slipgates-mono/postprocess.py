#!/usr/bin/env python3
"""Finish an Iosevka build of Slipgates Mono: credit every copyright holder, then write
what ships. Usage: postprocess.py <iosevka dist/SlipgatesMono> <out dir>

Iosevka writes only its own copyright into the name table. The SIL OFL requires keeping
that notice; we add the IoskeleyMono configuration's and ours beside it.

Writes:
  ttf/    one hinted TTF per face (Apple, Android)
  woff2/  the same faces as WOFF2 (the PWA)
  ttc/SlipgatesMono.ttc  every hinted face in one collection (Windows). WinUI builds a font
          family from the one file its FontFamily URI names, so with a file per face
          DirectWrite saw only Regular and synthesized every bold and italic (#544).
  faces.json  each real face: weight, style, PostScript name, file and its index in the
          collection, and the advance of a probe string, which the Windows self-test uses
          to prove a weight is drawn from its real face (docs/design/ui.md section 4.2)."""
import calendar, json, pathlib, sys
from fontTools.ttLib import TTFont, TTCollection

# Fixed timestamps (the pinned Iosevka release's date) so a rebuild of an unchanged plan
# produces byte-identical files; Iosevka stamps the build time into head.created.
STAMP = calendar.timegm((2026, 10, 24, 0, 0, 0)) + 2082844800  # seconds since 1904

COPYRIGHT = ("Copyright 2015-2026, Renzhi Li (aka. Belleve Invis, belleve@typeof.net). "
             "Copyright 2025, Ahmed Hatem (IoskeleyMono configuration). "
             "Copyright 2026, Dan Swett (Slipgates Mono modifications).")
FAMILY = "Slipgates Mono"
PROBE = "Hamburgefonstiv 0123456789"
STYLES = [(400, "Regular", "Italic"), (500, "Medium", "MediumItalic"),
          (600, "SemiBold", "SemiBoldItalic"), (700, "Bold", "BoldItalic")]

src, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
for d in ("ttf", "woff2", "ttc"):
    (out / d).mkdir(parents=True, exist_ok=True)
faces, fonts = [], []
for weight, upright, italic in STYLES:
    for style, name in (("normal", upright), ("italic", italic)):
        stem = f"SlipgatesMono-{name}"
        font = TTFont(src / "TTF" / f"{stem}.ttf", recalcTimestamp=False)
        font["head"].created = font["head"].modified = STAMP
        # Iosevka's PostScript names are hyphenated (Slipgates-Mono-Semibold-Italic); Apple
        # names a face by it, so it is the file's stem, as Slipgates Sans' are.
        for rec in font["name"].names:
            if rec.nameID == 0:
                rec.string = COPYRIGHT
            elif rec.nameID == 6:
                rec.string = stem
        assert font["OS/2"].usWeightClass == weight, (stem, font["OS/2"].usWeightClass)
        font.save(out / "ttf" / f"{stem}.ttf")
        font.flavor = "woff2"
        font.save(out / "woff2" / f"{stem}.woff2")
        font = TTFont(out / "ttf" / f"{stem}.ttf", recalcTimestamp=False)
        cmap, hmtx = font.getBestCmap(), font["hmtx"].metrics
        faces.append({"weight": weight, "style": style,
                      "postscript": font["name"].getDebugName(6),
                      "file": f"ttf/{stem}.ttf", "index": len(fonts),
                      "probe": sum(hmtx[cmap[ord(c)]][0] for c in PROBE)})
        fonts.append(font)
        print("wrote", stem)
ttc = TTCollection()
ttc.fonts = fonts
ttc.save(out / "ttc" / "SlipgatesMono.ttc", shareTables=True)
manifest = {"family": FAMILY, "unitsPerEm": fonts[0]["head"].unitsPerEm, "probe": PROBE,
            "collection": "ttc/SlipgatesMono.ttc", "faces": faces}
(out / "faces.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
print("wrote ttc/SlipgatesMono.ttc and faces.json")
