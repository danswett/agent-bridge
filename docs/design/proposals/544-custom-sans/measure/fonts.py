"""Proposal #544 (not approved): font facts for the custom-sans options.

    python fonts.py <repo root> [--system <font file> ...] > results/fonts.md

Reads the proposal's WOFF2 files and the bundled mono from the repo. Reference faces that the repo
doesn't carry (Segoe UI Variable from Windows, Roboto from google/fonts) are passed with --system.
Prints Markdown: metrics, inline-mono scale, coverage, payload and the style-linking fields that
Apple and Android read when they pick a face.
"""
import io
import pathlib
import sys

from fontTools import subset
from fontTools.ttLib import TTFont
from fontTools.varLib import instancer

BLOCKS = [
    ("Basic Latin", 0x20, 0x7E),
    ("Latin-1", 0xA0, 0xFF),
    ("Latin Ext-A", 0x100, 0x17F),
    ("Latin Ext-B", 0x180, 0x24F),
    ("IPA + modifiers", 0x250, 0x2FF),
    ("Combining marks", 0x300, 0x36F),
    ("Greek", 0x370, 0x3FF),
    ("Cyrillic", 0x400, 0x4FF),
    ("Latin Ext Additional (Vietnamese)", 0x1E00, 0x1EFF),
    ("General Punctuation", 0x2000, 0x206F),
    ("Currency", 0x20A0, 0x20CF),
    ("Letterlike + Number forms", 0x2100, 0x218F),
    ("Arrows", 0x2190, 0x21FF),
    ("Math operators", 0x2200, 0x22FF),
    ("Box drawing + blocks", 0x2500, 0x259F),
    ("Geometric shapes", 0x25A0, 0x25FF),
    ("Misc symbols + dingbats", 0x2600, 0x27BF),
]
# Google Fonts' "latin" unicode-range, the slice a PWA would load first.
LATIN = [(0x0, 0xFF), (0x131, 0x131), (0x152, 0x153), (0x2BB, 0x2BC), (0x2C6, 0x2C6), (0x2DA, 0x2DA),
         (0x2DC, 0x2DC), (0x304, 0x304), (0x308, 0x308), (0x329, 0x329), (0x2000, 0x206F), (0x20AC, 0x20AC),
         (0x2122, 0x2122), (0x2191, 0x2191), (0x2193, 0x2193), (0x2212, 0x2212), (0x2215, 0x2215),
         (0xFEFF, 0xFEFF), (0xFFFD, 0xFFFD)]


def load(path):
    font = TTFont(path)
    font.flavor = None
    return font


def woff2_bytes(font, unicodes=None):
    font = TTFont(io.BytesIO(_ttf_bytes(font)))
    if unicodes is not None:
        opts = subset.Options()
        opts.layout_features = ["*"]
        opts.name_IDs = ["*"]
        opts.hinting = True
        opts.notdef_outline = True
        sub = subset.Subsetter(opts)
        sub.populate(unicodes=unicodes)
        sub.subset(font)
    font.flavor = "woff2"
    buf = io.BytesIO()
    font.save(buf)
    return len(buf.getvalue())


def _ttf_bytes(font):
    buf = io.BytesIO()
    flavor, font.flavor = font.flavor, None
    font.save(buf)
    font.flavor = flavor
    return buf.getvalue()


def avg_advance(font, text="The quick brown fox jumps over the lazy dog 0123456789"):
    cmap, hmtx = font.getBestCmap(), font["hmtx"]
    return sum(hmtx[cmap[ord(c)]][0] for c in text) / len(text) / font["head"].unitsPerEm


def main():
    sys.stdout.reconfigure(encoding="utf-8")
    root = pathlib.Path(sys.argv[1])
    system = []
    if "--system" in sys.argv:
        system = [pathlib.Path(p) for p in sys.argv[sys.argv.index("--system") + 1:]]
    proposal = root / "docs/design/proposals/544-custom-sans/fonts/woff2"
    mono = sorted((root / "design/fonts/agent-bridge-mono").rglob("*.woff2"))
    regulars = {
        "A (Slipgates Sans A)": proposal / "SlipgatesSansA-Regular.woff2",
        "B (Slipgates Sans B)": proposal / "SlipgatesSansB-Regular.woff2",
        "Mono (Agent Bridge Mono)": next(p for p in mono if p.name.endswith("-Regular.woff2")),
    }
    for p in system:
        regulars[f"System: {load(p)['name'].getDebugName(1)} ({p.name})"] = p
    fonts = {k: load(v) for k, v in regulars.items()}
    mono_x = 520 / 1000

    print("## Metrics (Regular)\n")
    print("| Face | unitsPerEm | x-height | x/em | cap/em | mono scale for x-height match | avg advance /em | hinted |")
    print("|---|---|---|---|---|---|---|---|")
    for name, f in fonts.items():
        upm, os2 = f["head"].unitsPerEm, f["OS/2"]
        x = os2.sxHeight / upm
        hinted = "yes (fpgm/prep)" if "fpgm" in f else "no"
        print(f"| {name} | {upm} | {os2.sxHeight} | {x:.3f} | {os2.sCapHeight / upm:.3f} | {x / mono_x:.3f} "
              f"| {avg_advance(f):.3f} | {hinted} |")

    print("\n## Coverage (codepoints in cmap)\n")
    names = list(fonts)
    cmaps = {n: set(f.getBestCmap()) for n, f in fonts.items()}
    print("| Block | " + " | ".join(names) + " |")
    print("|---|" + "---|" * len(names))
    for block, a, b in BLOCKS:
        cells = []
        for n in names:
            have = sum(1 for u in range(a, b + 1) if u in cmaps[n])
            cells.append(str(have))
        print(f"| {block} (U+{a:04X}–{b:04X}) | " + " | ".join(cells) + " |")
    print("| **Total codepoints** | " + " | ".join(str(len(cmaps[n])) for n in names) + " |")
    for ref in [n for n in names if n.startswith("System")]:
        latin_greek_cyr = {u for u in cmaps[ref] if u < 0x500 or 0x1E00 <= u <= 0x1EFF or 0x2000 <= u <= 0x20CF}
        for n in names:
            if n.startswith("System"):
                continue
            missing = sorted(latin_greek_cyr - cmaps[n])
            sample = "".join(chr(u) for u in missing if chr(u).isprintable() and not 0x300 <= u <= 0x36F)[:40]
            print(f"\n{n} lacks {len(missing)} of {ref}'s Latin/Greek/Cyrillic/punctuation codepoints"
                  + (f", e.g. `{sample}`" if sample else "") + ".")

    print("\n## Payload (WOFF2 bytes)\n")
    print("| Set | Files | Total | Latin-only subset of Regular |")
    print("|---|---|---|---|")
    latin = [u for a, b in LATIN for u in range(a, b + 1)]
    for label, glob in [("A statics, 4 weights × upright+italic", "SlipgatesSansA-*.woff2"),
                        ("B statics, 4 weights × upright+italic", "SlipgatesSansB-*[a-z].woff2"),
                        ("B variable, wght 100–900 + opsz, upright+italic", "SlipgatesSansB*[[]*.woff2")]:
        files = sorted(proposal.glob(glob))
        files = [f for f in files if ("[" in f.name) == ("variable" in label)]
        regular = next(f for f in files if "Italic" not in f.name and ("Regular" in f.name or "[" in f.name))
        print(f"| {label} | {len(files)} | {sum(f.stat().st_size for f in files):,} "
              f"| {woff2_bytes(load(regular), latin):,} ({regular.name}) |")
    print(f"| Mono statics (bundled today) | {len(mono)} | {sum(f.stat().st_size for f in mono):,} "
          f"| {woff2_bytes(load(regulars['Mono (Agent Bridge Mono)']), latin):,} |")
    pinned = [instancer.instantiateVariableFont(load(f), {"opsz": 14}) for f in sorted(proposal.glob("SlipgatesSansB*[[]*.woff2"))]
    print(f"| B variable, opsz pinned at 14 (wght axis only), upright+italic | 2 | "
          f"{sum(woff2_bytes(f) for f in pinned):,} | {woff2_bytes(pinned[-1], latin):,} |")

    print("\n## Style linking (what Apple's and Android's matchers read)\n")
    print("| File | name 1 / 2 | name 16 / 17 | usWeightClass | fsSelection | macStyle | italicAngle |")
    print("|---|---|---|---|---|---|---|")
    for f in sorted(proposal.glob("*.woff2")) + mono:
        t = load(f)
        n, os2 = t["name"], t["OS/2"]
        sel = os2.fsSelection
        bits = ",".join(b for b, on in (("ITALIC", sel & 1), ("BOLD", sel & 32), ("REGULAR", sel & 64)) if on)
        mac = ",".join(b for b, on in (("bold", t["head"].macStyle & 1), ("italic", t["head"].macStyle & 2)) if on)
        print(f"| {f.name} | {n.getDebugName(1)} / {n.getDebugName(2)} | {n.getDebugName(16) or '–'} / "
              f"{n.getDebugName(17) or '–'} | {os2.usWeightClass} | {bits or '–'} | {mac or '–'} "
              f"| {t['post'].italicAngle:g} |")


if __name__ == "__main__":
    main()
