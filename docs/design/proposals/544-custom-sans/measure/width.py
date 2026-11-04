"""Is Slipgates Sans too condensed? Per-em widths for the #544 width addendum (§11).

    python width.py <comparison fonts dir> <repo root>

avg/em is the mean advance of a-z over the em; x/em the height of x's outline; width/x is
avg/em over x/em, the proportion the eye reads as wide or narrow. o/x is the o's drawn width
over the x-height, the round letters the owner reads as narrow. Variable faces are taken at the
named instance each platform draws; the comparison faces are the google/fonts files width.mjs
names; Segoe UI, Segoe UI Variable and Verdana are this machine's own. Vietnamese counts the 104
precomposed letters (ă â đ ê ô ơ ư and U+1EA0-1EF9) and the five combining tone marks.
"""
import json
import string
import sys
from pathlib import Path

from fontTools.pens.boundsPen import BoundsPen
from fontTools.ttLib import TTFont
from fontTools.varLib.instancer import instantiateVariableFont

CMP = Path(sys.argv[1])
REPO = Path(sys.argv[2])
SANS = REPO / "design/fonts/slipgates-sans/ttf/SlipgatesSans.ttf"
# The shipping TTF is pinned at the text cut (opsz 14), so the optical-size rows read the
# proposal's unpinned variable font, the same tuned B with its opsz axis kept.
SANS_OPSZ = REPO / "docs/design/proposals/544-custom-sans/fonts/woff2/SlipgatesSansB[opsz,wght].woff2"
WIN = Path(r"C:\Windows\Fonts")

# (face, regular file, its location, SemiBold file or None for the same file at wght 600)
FACES = [
    ("Segoe UI (the PWA today on Windows: Chromium's system-ui)", WIN / "segoeui.ttf", {}, WIN / "seguisb.ttf"),
    ("Segoe UI Variable Text (the Windows app today)", WIN / "SegUIVar.ttf", {"opsz": 10.5}, None),
    ("Roboto (the PWA today on Android)", CMP / "Roboto.ttf", {"wdth": 100}, None),
    ("Slipgates Sans B, text cut (opsz 14): what ships on Windows and the web", SANS, {"opsz": 14}, None),
    ("B at opsz 20 (Apple/Android variable TTF, a 20 pt title)", SANS_OPSZ, {"opsz": 20}, None),
    ("B at opsz 32 (the display cut, a 34 pt Large Title)", SANS_OPSZ, {"opsz": 32}, None),
    ("Inter Tight (the interim wordmark and brand-board headings, #558)", CMP / "InterTight.ttf", {}, None),
    ("IBM Plex Sans", CMP / "IBMPlexSans.ttf", {"wdth": 100}, None),
    ("Noto Sans", CMP / "NotoSans.ttf", {"wdth": 100}, None),
    ("Atkinson Hyperlegible Next", CMP / "AtkinsonHyperlegibleNext.ttf", {}, None),
    ("Verdana (a wide reference)", WIN / "verdana.ttf", {}, WIN / "verdanab.ttf"),
]
VIET = "ăâđêôơưĂÂĐÊÔƠƯ" + "".join(chr(c) for c in range(0x1EA0, 0x1EFA))
TONES = [0x0300, 0x0301, 0x0303, 0x0309, 0x0323]


def instance(path, loc, wght):
    f = TTFont(path)
    if "fvar" not in f:
        return f
    # Advances and outlines are all this needs; the instancer can't merge some faces' GPOS.
    for t in ("GPOS", "GDEF", "GSUB", "MVAR"):
        if t in f:
            del f[t]
    axes = {a.axisTag for a in f["fvar"].axes}
    want = {k: v for k, v in loc.items() if k in axes}
    want["wght"] = wght
    return instantiateVariableFont(f, want)


def metrics(f):
    upm = f["head"].unitsPerEm
    cmap = f.getBestCmap()
    gs = f.getGlyphSet()

    def ink(ch):
        bp = BoundsPen(gs)
        gs[cmap[ord(ch)]].draw(bp)
        return bp.bounds

    avg = sum(f["hmtx"][cmap[ord(c)]][0] for c in string.ascii_lowercase) / 26 / upm
    xh = ink("x")[3] / upm
    o = ink("o")
    return avg, xh, (o[2] - o[0]) / upm / xh


rows = []
for name, path, loc, sb_path in FACES:
    avg, xh, ox = metrics(instance(path, loc, 400))
    sb = metrics(TTFont(sb_path)) if sb_path else metrics(instance(path, loc, 600))
    cmap = TTFont(path).getBestCmap()
    rows.append({
        "face": name,
        "avg_em": round(avg, 3), "x_em": round(xh, 3), "width_x": round(avg / xh, 3), "o_x": round(ox, 3),
        "semibold_avg_em": round(sb[0], 3), "semibold_width_x": round(sb[0] / sb[1], 3),
        "vietnamese": f"{sum(ord(c) in cmap for c in VIET)}/{len(VIET)}",
        "tone_marks": f"{sum(t in cmap for t in TONES)}/{len(TONES)}",
    })
print(json.dumps(rows, indent=1, ensure_ascii=False))
