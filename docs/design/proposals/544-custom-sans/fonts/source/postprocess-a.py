#!/usr/bin/env python3
"""Proposal #544, option A: finish the Iosevka build of Slipgates Sans A. NOT APPROVED.
Usage: postprocess-a.py <iosevka dist/SlipgatesSansA> <out dir>

As design/fonts/slipgates-mono/postprocess.py: credit every copyright holder (Iosevka
writes only its own), fix the timestamps so a rebuild is byte-identical, write TTF and WOFF2."""
import calendar, pathlib, sys
from fontTools.ttLib import TTFont

STAMP = calendar.timegm((2026, 10, 24, 0, 0, 0)) + 2082844800  # seconds since 1904
COPYRIGHT = ("Copyright 2015-2026, Renzhi Li (aka. Belleve Invis, belleve@typeof.net). "
             "Copyright 2025, Ahmed Hatem (IoskeleyMono configuration). "
             "Copyright 2026, Dan Swett (Slipgates Sans A modifications).")

src, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
(out / "ttf").mkdir(parents=True, exist_ok=True)
(out / "woff2").mkdir(parents=True, exist_ok=True)
for ttf in sorted((src / "TTF").glob("*.ttf")):
    font = TTFont(ttf, recalcTimestamp=False)
    font["head"].created = font["head"].modified = STAMP
    for rec in font["name"].names:
        if rec.nameID == 0:
            rec.string = COPYRIGHT
    font.save(out / "ttf" / ttf.name)
    font.flavor = "woff2"
    font.save(out / "woff2" / ttf.with_suffix(".woff2").name)
    print("wrote", ttf.stem)
