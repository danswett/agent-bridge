#!/usr/bin/env python3
"""Proposal #544, option B: Inter 4.1, tuned to echo the monospace. NOT APPROVED.

Usage: tune-inter.py <unzipped Inter-4.1 release> <out dir>

Freezes five of Inter's own alternates into the defaults, so the details that make the
monospace recognisable appear in every app without asking each client to turn on
OpenType features (WinUI's TextBlock can't, and Apple's needs code per label):
  cv05  l with a tail          the monospace's own change, so l, I and 1 stay apart
  cv02  open 4                 the monospace's semi-open 4
  cv03  open 6, cv04 open 9    the monospace's open-contour 6 and 9
  cv09  flat-top 3             the monospace's flat-top 3
  ss07  square punctuation     the monospace's square dots: . , : ; ! ? and the dots on i, j
Inter's outlines, spacing, kerning and (in the static files) hinting are otherwise
untouched: nothing is redrawn.

Freezing means two things. The cmap points each character at the alternate. And every
single-substitution lookup that maps a default glyph (tnum's four -> four.tf, case,
sups...) gets the same mapping from the alternate (four.ss01 -> four.tf.ss01), so tabular
figures and the rest keep working on the frozen shapes.

Then: subset to the monospace's ranges plus Vietnamese, rename to "Slipgates Sans B" (Inter reserves no
font name, so the OFL doesn't require this, but a modified font under its upstream name
would be mistaken for it), credit both copyright holders, and write TTF and WOFF2.
"""
import pathlib, sys
from fontTools.ttLib import TTFont
from fontTools import subset

FREEZE = ["cv05", "cv02", "cv03", "cv04", "cv09", "ss07"]
FAMILY = "Slipgates Sans B"
PS = "SlipgatesSansB"
COPYRIGHT = ("Copyright (c) 2016 The Inter Project Authors (https://github.com/rsms/inter). "
             "Copyright 2026, Dan Swett (Slipgates Sans B modifications: Inter's alternates "
             "for l, 3, 4, 6, 9 and square punctuation made the defaults).")
# The monospace's subset (design/fonts/slipgates-mono/private-build-plans.toml), so the
# two faces cover the same characters and fall back together, plus Spacing Modifier Letters
# and Latin Extended Additional (Vietnamese), which a text face needs and Segoe UI covers.
RANGES = [(32, 126), (160, 255), (256, 383), (384, 591), (688, 767), (768, 879), (7680, 7935), (880, 1023),
          (1024, 1279), (8192, 8303), (8352, 8383), (8448, 8527), (8528, 8591),
          (8592, 8703), (8704, 8959), (8960, 9215), (9472, 9599), (9600, 9631),
          (9632, 9727), (9728, 9983), (9984, 10175), (10240, 10495)]
SOURCES = {
    "InterVariable.ttf": "SlipgatesSansB[opsz,wght]",
    "InterVariable-Italic.ttf": "SlipgatesSansB-Italic[opsz,wght]",
    **{f"extras/ttf/Inter-{s}.ttf": f"SlipgatesSansB-{s}" for s in [
        "Regular", "Italic", "Medium", "MediumItalic",
        "SemiBold", "SemiBoldItalic", "Bold", "BoldItalic"]},
}


def lookups(gsub, tag=None):
    feats = gsub.FeatureList.FeatureRecord
    idx = sorted({i for fr in feats if tag is None or fr.FeatureTag == tag
                  for i in fr.Feature.LookupListIndex}) if tag else range(len(gsub.LookupList.Lookup))
    for i in idx:
        lk = gsub.LookupList.Lookup[i]
        for st in lk.SubTable:
            if lk.LookupType == 7:
                st = st.ExtSubTable
            yield st


def freeze(font):
    gsub = font["GSUB"].table
    alt = {}
    for tag in FREEZE:
        m = {}
        for st in lookups(gsub, tag):
            if hasattr(st, "mapping"):
                m.update(st.mapping)
        # Compose with what earlier features already froze (cv05 then ss07 on l-acute...).
        for g in set(font.getGlyphOrder()):
            cur = alt.get(g, g)
            if cur in m:
                alt[g] = m[cur]
    for table in font["cmap"].tables:
        for cp, g in list(table.cmap.items()):
            if g in alt:
                table.cmap[cp] = alt[g]
    for st in lookups(gsub):
        mp = getattr(st, "mapping", None)
        if not isinstance(mp, dict):
            continue
        for a, b in list(mp.items()):
            if a in alt and alt[a] not in mp:
                mp[alt[a]] = alt.get(b, b)
    return alt


def rename(font, ps_stem):
    for rec in font["name"].names:
        s = rec.toUnicode()
        if rec.nameID == 0:
            rec.string = COPYRIGHT
        elif rec.nameID in (1, 3, 4, 16, 18, 21) or rec.nameID >= 256:
            rec.string = s.replace("Inter Variable", FAMILY).replace("InterVariable", PS).replace("Inter", FAMILY)
        elif rec.nameID in (6, 20, 25):
            rec.string = s.replace("InterVariable", PS).replace("Inter", PS)
    if "fvar" in font:
        font["name"].removeNames(nameID=25)
        font["name"].setName(PS + ("Italic" if "Italic" in ps_stem else ""), 25, 3, 1, 0x409)
    if "STAT" in font and "fvar" in font:
        # Upstream Inter leaves the default optical size ("Text", opsz 14) non-elidable, so
        # DirectWrite names the variable family "<family> Text" instead of "<family>".
        # Marking it elidable (flag 0x2) gives one family name across the variable and static files.
        stat = font["STAT"].table
        tags = [a.AxisTag for a in stat.DesignAxisRecord.Axis]
        default = next((a.defaultValue for a in font["fvar"].axes if a.axisTag == "opsz"), None)
        for value in stat.AxisValueArray.AxisValue if stat.AxisValueArray else []:
            if "opsz" in tags and getattr(value, "AxisIndex", None) == tags.index("opsz") and value.Value == default:
                value.Flags |= 0x2


def main():
    src, out = pathlib.Path(sys.argv[1]), pathlib.Path(sys.argv[2])
    (out / "ttf").mkdir(parents=True, exist_ok=True)
    (out / "woff2").mkdir(parents=True, exist_ok=True)
    for rel, stem in SOURCES.items():
        font = TTFont(src / rel)
        alt = freeze(font)
        opts = subset.Options()
        opts.layout_features = ["*"]
        opts.name_IDs = ["*"]
        opts.name_languages = ["*"]
        opts.glyph_names = False
        opts.notdef_outline = True
        opts.hinting = True
        opts.legacy_kern = True
        sub = subset.Subsetter(opts)
        sub.populate(unicodes=[u for a, b in RANGES for u in range(a, b + 1)])
        sub.subset(font)
        rename(font, stem)
        font.save(out / "ttf" / f"{stem}.ttf")
        font.flavor = "woff2"
        font.save(out / "woff2" / f"{stem}.woff2")
        print("wrote", stem, "froze", len(alt), "glyphs")


if __name__ == "__main__":
    main()
