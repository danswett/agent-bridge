#!/usr/bin/env python3
"""Finish an Iosevka build of Agent Bridge Mono: credit every copyright holder, then
write the TTFs and WOFF2s that ship. Usage: postprocess.py <iosevka dist/AgentBridgeMono> <out dir>

Iosevka writes only its own copyright into the name table. The SIL OFL requires keeping
that notice; we add the IoskeleyMono configuration's and ours beside it."""
import calendar, pathlib, sys
from fontTools.ttLib import TTFont

# Fixed timestamps (the pinned Iosevka release's date) so a rebuild of an unchanged plan
# produces byte-identical files; Iosevka stamps the build time into head.created.
STAMP = calendar.timegm((2026, 10, 24, 0, 0, 0)) + 2082844800  # seconds since 1904

COPYRIGHT = ("Copyright 2015-2026, Renzhi Li (aka. Belleve Invis, belleve@typeof.net). "
             "Copyright 2025, Ahmed Hatem (IoskeleyMono configuration). "
             "Copyright 2026, Dan Swett (Agent Bridge Mono modifications).")

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
