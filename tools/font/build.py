"""Builds the Fermata Sans TTF files from the designs in glyphs.py into src/Fermata/Assets/Fonts.

    python3 -m venv .venv && .venv/bin/pip install -r tools/font/requirements.txt
    .venv/bin/python tools/font/build.py
"""
import math
import os
import tempfile
import unicodedata

import skia
from fontTools.cu2qu import curve_to_quadratic
from fontTools.feaLib.builder import addOpenTypeFeaturesFromString
from fontTools.fontBuilder import FontBuilder
from fontTools.pens.ttGlyphPen import TTGlyphPointPen, TTGlyphPen

import design
import glyphs
from geom import bounds, move, scale, soften, union, rect

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..", "src", "Fermata", "Assets", "Fonts")
UPM = 1000
ASC, DESC = 950, -290   # Atkinson Hyperlegible's line, so swapping fonts keeps baselines

# ---------------------------------------------------------------- outlines to TrueType


def conic_to_cubic(p0, p1, p2, w):
    k = 4 * w / (3 * (1 + w))
    c1 = (p0[0] + (p1[0] - p0[0]) * k, p0[1] + (p1[1] - p0[1]) * k)
    c2 = (p2[0] + (p1[0] - p2[0]) * k, p2[1] + (p1[1] - p2[1]) * k)
    return c1, c2


def contours_of(path):
    """Returns closed contours as lists of (x, y, on_curve) points in quadratic form."""
    out, cur = [], None
    it = skia.Path.Iter(path, True)
    while True:
        verb, pts = it.next()
        if verb == skia.Path.kDone_Verb:
            break
        P = [(p.x(), p.y()) for p in pts]
        if verb == skia.Path.kMove_Verb:
            cur = [(P[0][0], P[0][1], True)]
            out.append(cur)
        elif verb == skia.Path.kLine_Verb:
            cur.append((P[1][0], P[1][1], True))
        elif verb == skia.Path.kQuad_Verb:
            cur += [(P[1][0], P[1][1], False), (P[2][0], P[2][1], True)]
        elif verb in (skia.Path.kCubic_Verb, skia.Path.kConic_Verb):
            if verb == skia.Path.kConic_Verb:
                c1, c2 = conic_to_cubic(P[0], P[1], P[2], it.conicWeight())
                cub = [P[0], c1, c2, P[2]]
            else:
                cub = P
            q = curve_to_quadratic(cub, 0.6)
            for pt in q[1:-1]:
                cur.append((pt[0], pt[1], False))
            cur.append((q[-1][0], q[-1][1], True))
    clean = []
    for c in out:
        pts = [(round(x), round(y), on) for x, y, on in c]
        if len(pts) > 1 and pts[0][:2] == pts[-1][:2]:
            pts.pop()
        dedup = []
        for p in pts:
            if dedup and dedup[-1][:2] == p[:2] and dedup[-1][2] == p[2]:
                continue
            dedup.append(p)
        # drop on-curve points that sit on a straight line between two other on-curve points
        changed = True
        while changed and len(dedup) > 3:
            changed = False
            for i in range(len(dedup)):
                a, b, c_ = dedup[i - 1], dedup[i], dedup[(i + 1) % len(dedup)]
                if a[2] and b[2] and c_[2]:
                    cross = (b[0] - a[0]) * (c_[1] - a[1]) - (b[1] - a[1]) * (c_[0] - a[0])
                    if cross == 0 and min(a[0], c_[0]) <= b[0] <= max(a[0], c_[0]) and min(a[1], c_[1]) <= b[1] <= max(a[1], c_[1]):
                        dedup.pop(i)
                        changed = True
                        break
        if len(dedup) >= 3 and area(dedup) != 0:
            clean.append(dedup)
    return clean


def area(c):
    s = 0
    for i in range(len(c)):
        x1, y1 = c[i][:2]
        x2, y2 = c[(i + 1) % len(c)][:2]
        s += x1 * y2 - x2 * y1
    return s / 2


def inside(pt, c):
    x, y = pt
    n, hit = len(c), False
    for i in range(n):
        x1, y1 = c[i][:2]
        x2, y2 = c[(i + 1) % n][:2]
        if (y1 > y) != (y2 > y):
            if x < x1 + (y - y1) * (x2 - x1) / (y2 - y1):
                hit = not hit
    return hit


def orient(contours):
    """Outer contours clockwise and holes anticlockwise, as TrueType expects."""
    out = []
    for i, c in enumerate(contours):
        probe = next(((x, y) for x, y, on in c if on), c[0][:2])
        probe = (probe[0] + 0.01, probe[1] + 0.013)
        depth = sum(1 for j, o in enumerate(contours) if j != i and inside(probe, o))
        a = area(c)
        want_cw = depth % 2 == 0
        if (a < 0) != want_cw:
            c = list(reversed(c))
        out.append(c)
    return out


def tt_glyph(path):
    pen = TTGlyphPointPen(None)
    for c in orient(contours_of(path)):
        pen.beginPath()
        n = len(c)
        for i, (x, y, on) in enumerate(c):
            if on:
                prev_on = c[i - 1][2]
                pen.addPoint((x, y), segmentType="line" if prev_on else "qcurve")
            else:
                pen.addPoint((x, y))
        pen.endPath()
    return pen.glyph()


# ---------------------------------------------------------------- glyph set

COMBINING = {
    0x300: "grave", 0x301: "acute", 0x302: "circumflex", 0x303: "tilde", 0x304: "macron", 0x306: "breve",
    0x307: "dotaccent", 0x308: "dieresis", 0x30A: "ring", 0x30B: "hungarumlaut", 0x30C: "caron",
    0x326: "commaaccent", 0x327: "cedilla", 0x328: "ogonek", 0x352: "fermata",
}
BELOW = {"cedilla", "ogonek", "commaaccent"}
SPACING_MARKS = {
    0x60: "grave", 0xB4: "acute", 0xA8: "dieresis", 0xAF: "macron", 0xB8: "cedilla", 0x2C6: "circumflex",
    0x2C7: "caron", 0x2D8: "breve", 0x2D9: "dotaccent", 0x2DA: "ring", 0x2DB: "ogonek", 0x2DC: "tilde",
    0x2DD: "hungarumlaut",
}
# Latvian and Romanian letters that decompose with a cedilla but are drawn with a comma
COMMA_BELOW = {0x122, 0x136, 0x137, 0x13B, 0x13C, 0x145, 0x146, 0x156, 0x157}
CARON_ALT = {0x10F, 0x13D, 0x13E, 0x165}
EXTRA = [0x218, 0x219, 0x21A, 0x21B]


def base_name_for(cp):
    for name, info in glyphs.REG.items():
        if info["uni"] == cp:
            return name
    return None


def decompositions():
    """(codepoint, base glyph, [mark names]) for every accented letter we compose."""
    out = []
    for cp in list(range(0xC0, 0x180)) + EXTRA:
        d = unicodedata.decomposition(chr(cp))
        if not d or d.startswith("<"):
            continue
        parts = [int(x, 16) for x in d.split()]
        base, marks = parts[0], parts[1:]
        if any(m not in COMBINING for m in marks):
            continue
        names = [COMBINING[m] for m in marks]
        if cp in COMMA_BELOW:
            names = ["commaturned" if cp == 0x123 else "commaaccent"]
        if cp == 0x123:
            names = ["commaturned"]
        if cp in CARON_ALT:
            names = ["caronalt"]
        bname = base_name_for(base)
        if bname is None:
            continue
        if bname == "i" and not any(n in BELOW for n in names):
            bname = "dotlessi"
        if bname == "j":
            bname = "dotlessj"
        out.append((cp, bname, names))
    return out


def glyph_name(cp):
    try:
        n = unicodedata.name(chr(cp))
    except ValueError:
        return "uni%04X" % cp
    return "uni%04X" % cp if cp > 0xFF or True else n


class FontData:
    def __init__(self, g):
        self.g = g
        self.paths, self.adv, self.cmap = {}, {}, {}
        self.composites = {}          # name -> [(component, dx, dy)]
        self.anchors_top, self.anchors_bottom, self.anchors_ogonek = {}, {}, {}

    def add(self, name, path, adv, cp=None):
        self.paths[name] = path
        self.adv[name] = round(adv)
        if cp is not None:
            self.cmap[cp] = name

    def add_comp(self, name, parts, adv, cp=None):
        self.composites[name] = parts
        self.adv[name] = round(adv)
        if cp is not None:
            self.cmap[cp] = name


def lc_gap(g):
    return g.lerp(110, 92)


def uc_gap(g):
    return g.lerp(62, 52)


def build_font_data(g):
    fd = FontData(g)
    # .notdef
    nd = union(rect(50, 0, 450, 700))
    from geom import diff
    fd.add(".notdef", diff(nd, rect(50 + 60, 60, 450 - 60, 700 - 60)), 500)
    for name, info in glyphs.REG.items():
        p, adv = design.build(g, name)
        fd.add(name, p, adv, info["uni"])
    # proportional figures
    for name in glyphs.FIGS:
        p = fd.paths[name]
        x0, _, x1, _ = bounds(p)
        sb = g.lerp(46, 30)
        fd.add(name + ".pnum", move(p, sb - x0), (x1 - x0) + 2 * sb)
    # superior and inferior figures, and ordinals, built from a heavier small copy
    small = glyphs.Weight("small", g.css, g.V * 1.34, g.t)
    sc = 0.6
    for name, cp_sup, cp_inf in [("one", 0xB9, None), ("two", 0xB2, None), ("three", 0xB3, None), ("four", None, None)]:
        p, adv = design.build(small, name)
        sp = scale(p, sc)
        fd.add(name + "superior", move(sp, 0, g.cap - small.cap * sc), adv * sc, cp_sup)
        fd.add(name + "inferior", move(sp, 0, -g.lerp(20, 20)), adv * sc)
    pa, aa = design.build(small, "a")
    po, ao = design.build(small, "o")
    bar_y = g.cap - small.xh * sc - g.lerp(80, 70)
    for nm, cp, (p, a) in [("ordfeminine", 0xAA, (pa, aa)), ("ordmasculine", 0xBA, (po, ao))]:
        body = move(scale(p, sc), 0, g.cap - small.xh * sc)
        b = bounds(body)
        fd.add(nm, union(body, rect(b[0], bar_y - g.H * 0.8, b[2], bar_y)), a * sc, cp)
    fw = g.c(160)
    fd.add("fraction", glyphs.hband(0, 0, fw, g.cap, g.D * 0.8, 0, g.cap), fw, 0x2044)
    fd.paths["fraction"] = soften(fd.paths["fraction"], g.r)
    for cp, (num, den) in {0xBC: ("one", "four"), 0xBD: ("one", "two"), 0xBE: ("three", "four")}.items():
        n_adv = fd.adv[num + "superior"]
        overlap = g.lerp(60, 50)
        fd.add_comp(glyphs_name(cp), [(num + "superior", 0, 0), ("fraction", n_adv - overlap, 0),
                                      ({"two": "twoinferior", "four": "fourinferior"}[den], n_adv + fw - 2 * overlap, 0)],
                    n_adv + fw - 2 * overlap + fd.adv[{"two": "twoinferior", "four": "fourinferior"}[den]], cp)
    # marks: lowercase and capital sizes, drawn centred on x = 0
    lc_h, uc_h = g.lerp(150, 160), g.lerp(112, 124)
    for m, fn in glyphs.MARKS.items():
        for suffix, h in (("", lc_h), (".case", uc_h)):
            p = fn(g, h)
            if m not in ("caronalt", "ogonek", "cedilla"):
                x0, _, x1, _ = bounds(p)
                p = move(p, -(x0 + x1) / 2)
            p = soften(p, g.r * 0.8)
            fd.add(m + "comb" + suffix, p, 0)
    for cp, m in COMBINING.items():
        fd.cmap[cp] = m + "comb"
    # spacing accents
    for cp, m in SPACING_MARKS.items():
        p = fd.paths[m + "comb"]
        x0, _, x1, _ = bounds(p)
        sb = 40
        dy = 0 if m in BELOW else g.xh + lc_gap(g)
        fd.add_comp(glyphs_name(cp), [(m + "comb", sb - x0, dy)], (x1 - x0) + 2 * sb, cp)
    # accented letters
    for cp, base, marks in decompositions():
        parts = [(base, 0, 0)]
        bx0, by0, bx1, by1 = bounds(fd.paths[base]) if base in fd.paths else (0, 0, fd.adv[base], g.xh)
        is_cap = by1 > g.xh + 80 and unicodedata.category(chr(cp)) == "Lu"
        adv = fd.adv[base]
        stack = by1
        for m in marks:
            suffix = ".case" if is_cap and m not in BELOW else ""
            mp = fd.paths[m + "comb" + suffix]
            mx0, my0, mx1, my1 = bounds(mp)
            cx = (bx0 + bx1) / 2
            if base in TOP_X:
                cx = TOP_X[base](g, fd, bx0, bx1)
            if m == "caronalt":
                if base in ("d",):
                    x, y = bx1 - g.lerp(62, 70) + g.lerp(40, 30), g.asc
                elif base in ("l",):
                    x, y = bx0 + g.V + g.lerp(40, 30), g.asc
                elif base == "L":
                    x, y = bx0 + g.V + g.lerp(40, 30), g.cap
                else:  # t
                    x, y = bx0 + g.c(84) + g.V + g.lerp(30, 24), g.xh + g.lerp(150, 120)
                parts.append((m + "comb", x, y))
                adv = max(adv, x + mx1 + g.lerp(20, 10))
                continue
            if m in BELOW:
                if m == "ogonek":
                    ox = bx1 - g.V * 0.5 if base not in ("e", "E") else (bx0 + bx1) / 2 + (bx1 - bx0) * 0.12
                    if base in ("i", "I", "dotlessi"):
                        ox = (bx0 + bx1) / 2 + g.V * 0.1
                    parts.append((m + "comb", ox, 0))
                else:
                    parts.append((m + "comb", cx, 0))
                continue
            if m == "commaturned":
                parts.append((m + "comb", cx, g.xh + lc_gap(g) - 20))
                stack = g.xh + lc_gap(g) + my1
                continue
            if is_cap:
                y = g.cap + uc_gap(g)
            elif by1 > g.xh + 80:
                y = g.asc + uc_gap(g)
                suffix = ".case"
            else:
                y = g.xh + lc_gap(g)
            if stack > y - 10 and len(parts) > 1:
                y = stack + g.lerp(30, 30)
            if m == "ring" and base == "A":
                y = g.cap + uc_gap(g) * 0.6
            parts.append((m + "comb" + suffix, cx, y))
            stack = y + my1
        fd.add_comp(glyphs_name(cp), parts, adv, cp)
    # a few letters built from others
    fd.add_comp("IJ", [("I", 0, 0), ("J", fd.adv["I"] - g.lerp(20, 10), 0)], fd.adv["I"] + fd.adv["J"] - g.lerp(20, 10), 0x132)
    fd.add_comp("ij", [("i", 0, 0), ("j", fd.adv["i"] - g.lerp(10, 6), 0)], fd.adv["i"] + fd.adv["j"] - g.lerp(10, 6), 0x133)
    pc = fd.paths["periodcentered"]
    px0, _, px1, _ = bounds(pc)
    fd.add_comp("Ldot", [("L", 0, 0), ("periodcentered", fd.adv["L"] * 0.55 - px0, g.cap * 0.5 - g.xh * 0.47)], fd.adv["L"], 0x13F)
    fd.add_comp("ldot", [("l", 0, 0), ("periodcentered", fd.adv["l"] - px0 - g.lerp(10, 10), g.xh * 0.1)],
                fd.adv["l"] + (px1 - px0) + g.lerp(30, 20), 0x140)
    fd.add_comp("napostrophe", [("quoteright", 0, 0), ("n", fd.adv["quoteright"] - g.lerp(20, 10), 0)],
                fd.adv["quoteright"] + fd.adv["n"] - g.lerp(20, 10), 0x149)
    return fd


def _d_top(g, fd, x0, x1):
    x1 -= g.lerp(62, 70)  # the spur
    return x0 + (x1 - x0 - g.V) / 2


def _b_top(g, fd, x0, x1):
    return x0 + g.V + (x1 - x0 - g.V) / 2


TOP_X = {
    "l": lambda g, fd, x0, x1: x0 + g.V / 2,
    "d": _d_top,
    "b": _b_top,
    "t": lambda g, fd, x0, x1: x0 + g.c(84) + g.V / 2,
    "a": lambda g, fd, x0, x1: (x0 + x1) / 2 - g.V * 0.1,
    "G": lambda g, fd, x0, x1: (x0 + x1) / 2 - g.V * 0.1,
}


def glyphs_name(cp):
    return "uni%04X" % cp


# ---------------------------------------------------------------- features

def kerning(fd):
    g = fd.g
    rev = {}
    for cp, n in fd.cmap.items():
        rev.setdefault(n, cp)

    def with_accents(*bases):
        out = []
        for b in bases:
            if b in fd.adv:
                out.append(b)
        for cp, base, marks in decompositions():
            if base in bases or (base == "dotlessi" and "i" in bases):
                if "caronalt" in marks:
                    continue
                out.append(glyphs_name(cp))
        return sorted(set(out))

    L = {
        "T": with_accents("T") + ["Tbar"], "V": with_accents("V", "W"), "Y": with_accents("Y"),
        "A": with_accents("A"), "L": with_accents("L") + ["Lslash"], "P": ["P", "Thorn"], "F": ["F"],
        "R": with_accents("R"), "r": with_accents("r"), "v": with_accents("v", "w", "y"), "f": ["f"],
        "o": with_accents("o", "b", "p", "e", "thorn") + ["oslash", "ae", "oe"], "k": with_accents("k", "x"),
        "quote": ["quoteright", "quotedblright", "quotesingle", "quotedbl"],
        "dot": ["period", "comma", "ellipsis", "quotesinglbase", "quotedblbase"],
        "O": with_accents("O", "D", "Q") + ["Oslash", "Eth", "Dcroat"], "K": with_accents("K", "X"),
        "hy": ["hyphen", "endash", "emdash"],
    }
    R = {
        "o": with_accents("a", "c", "d", "e", "g", "o", "q") + ["oslash", "ae", "oe", "eth"],
        "A": with_accents("A") + ["AE"], "V": with_accents("V", "W"), "T": with_accents("T"), "Y": with_accents("Y"),
        "v": with_accents("v", "w", "y"), "dot": ["period", "comma", "ellipsis"],
        "O": with_accents("O", "C", "G", "Q") + ["Oslash", "OE"], "quote": ["quoteleft", "quotedblleft", "quotesingle", "quotedbl", "quoteright", "quotedblright"],
        "u": with_accents("u", "m", "n", "p", "r", "s", "z", "x") + ["dotlessi"], "J": with_accents("J"),
        "hy": ["hyphen", "endash", "emdash"], "s": with_accents("s"),
    }
    pairs = [
        ("T", "o", -80), ("T", "A", -64), ("T", "dot", -90), ("T", "u", -60), ("T", "v", -44), ("T", "hy", -60), ("T", "J", -50), ("T", "s", -70),
        ("V", "o", -46), ("V", "v", -22), ("Y", "v", -34), ("L", "o", -16), ("F", "u", -10), ("P", "u", -8), ("V", "A", -56), ("V", "dot", -80), ("V", "u", -28), ("V", "hy", -30),
        ("Y", "o", -76), ("Y", "A", -64), ("Y", "dot", -90), ("Y", "u", -50), ("Y", "hy", -60), ("Y", "s", -60),
        ("A", "T", -64), ("A", "V", -56), ("A", "Y", -64), ("A", "v", -30), ("A", "O", -14), ("A", "quote", -60),
        ("L", "T", -84), ("L", "V", -70), ("L", "Y", -84), ("L", "quote", -100), ("L", "v", -40), ("L", "O", -20), ("L", "hy", -50),
        ("P", "A", -56), ("P", "dot", -110), ("P", "o", -20), ("P", "J", -40),
        ("F", "A", -40), ("F", "dot", -90), ("F", "o", -20),
        ("R", "T", -20), ("R", "V", -20), ("R", "Y", -30), ("R", "o", -12),
        ("r", "dot", -64), ("r", "hy", -24), ("r", "o", -8),
        ("v", "dot", -54), ("v", "o", -12), ("v", "A", -30),
        ("o", "v", -10), ("o", "T", -70), ("o", "V", -40), ("o", "Y", -70), ("o", "quote", -20),
        ("k", "o", -20), ("f", "quote", 40), ("f", "dot", -40), ("f", "o", -10),
        ("quote", "A", -60), ("quote", "o", -40), ("quote", "dot", -90),
        ("dot", "quote", -90), ("dot", "T", -80), ("dot", "V", -70), ("dot", "Y", -84), ("dot", "v", -44),
        ("O", "A", -14), ("O", "V", -30), ("O", "Y", -40), ("O", "T", -24), ("O", "dot", -30),
        ("K", "O", -24), ("K", "o", -24), ("K", "v", -34),
        ("hy", "T", -60), ("hy", "Y", -60), ("hy", "V", -30), ("hy", "A", -20),
    ]
    fea = []
    for k, v in L.items():
        v = [x for x in v if x in fd.adv]
        fea.append("@L_%s = [%s];" % (k, " ".join(v)))
    for k, v in R.items():
        v = [x for x in v if x in fd.adv]
        fea.append("@R_%s = [%s];" % (k, " ".join(v)))
    fea.append("lookup kernpairs {")
    s = 1 - 0.12 * g.t
    for a, b, v in pairs:
        fea.append("  pos @L_%s @R_%s %d;" % (a, b, round(v * s)))
    fea.append("} kernpairs;")
    return "\n".join(fea)


def mark_feature(fd):
    g = fd.g
    lines = []
    above = [m for m in COMBINING.values() if m not in BELOW]
    for m in above:
        lines.append("markClass %scomb <anchor 0 %d> @TOP;" % (m, -round(lc_gap(g))))
    for m in BELOW:
        if m == "ogonek":
            lines.append("markClass ogonekcomb <anchor 0 0> @OGONEK;")
        else:
            lines.append("markClass %scomb <anchor 0 0> @BOTTOM;" % m)
    lines.append("lookup marktop {")
    bases = [n for n in glyphs.REG if n in fd.paths and (len(n) == 1 and n.isalpha() or n in ("dotlessi", "dotlessj"))]
    for n in bases:
        x0, y0, x1, y1 = bounds(fd.paths[n])
        cx = TOP_X[n](g, fd, x0, x1) if n in TOP_X else (x0 + x1) / 2
        top = g.cap if n.isupper() else (g.asc if y1 > g.xh + 80 else g.xh)
        lines.append("  pos base %s <anchor %d %d> mark @TOP;" % (n, round(cx), round(top)))
    for n in ["a"]:
        pass
    lines.append("} marktop;")
    lines.append("lookup markbottom {")
    for n in bases:
        x0, y0, x1, y1 = bounds(fd.paths[n])
        lines.append("  pos base %s <anchor %d 0> mark @BOTTOM <anchor %d 0> mark @OGONEK;" % (n, round((x0 + x1) / 2), round(x1 - g.V * 0.5)))
    lines.append("} markbottom;")
    return "\n".join(lines)


def features(fd):
    figs = list(glyphs.FIGS)
    above = " ".join(m + "comb" for m in COMBINING.values() if m not in BELOW)
    return f"""
languagesystem DFLT dflt;
languagesystem latn dflt;

@ABOVE = [{above}];

lookup dotless {{
  sub i by dotlessi;
  sub j by dotlessj;
}} dotless;

feature ccmp {{
  sub [i j]' lookup dotless @ABOVE;
}} ccmp;

feature pnum {{
  sub [{' '.join(figs)}] by [{' '.join(f + '.pnum' for f in figs)}];
}} pnum;

feature tnum {{
  sub [{' '.join(f + '.pnum' for f in figs)}] by [{' '.join(figs)}];
}} tnum;

{kerning(fd)}

feature kern {{
  lookup kernpairs;
}} kern;

{mark_feature(fd)}

feature mark {{
  lookup marktop;
  lookup markbottom;
}} mark;
"""


# ---------------------------------------------------------------- assembling


def build(g):
    fd = build_font_data(g)
    order = [".notdef"] + [n for n in fd.adv if n != ".notdef"]
    tt = {}
    for n in order:
        if n in fd.composites:
            pen = TTGlyphPen(tt)
            for comp, dx, dy in fd.composites[n]:
                pen.addComponent(comp, (1, 0, 0, 1, round(dx), round(dy)))
            tt[n] = pen.glyph()
        else:
            tt[n] = tt_glyph(fd.paths[n])
    fb = FontBuilder(UPM, isTTF=True)
    fb.setupGlyphOrder(order)
    fb.setupCharacterMap(fd.cmap)
    fb.setupGlyf(tt)
    glyf = fb.font["glyf"]
    metrics = {}
    for n in order:
        gl = glyf[n]
        gl.recalcBounds(glyf)
        metrics[n] = (fd.adv[n], getattr(gl, "xMin", 0) if gl.numberOfContours else 0)
    fb.setupHorizontalMetrics(metrics)
    fb.setupHorizontalHeader(ascent=ASC, descent=DESC, lineGap=0)
    family = "Fermata Sans"
    style = g.name
    ribbi = style in ("Regular", "Bold")
    names = {
        "copyright": "Copyright (c) 2026 Chase Brower",
        "familyName": family if ribbi else f"{family} {style}",
        "styleName": style if ribbi else "Regular",
        "uniqueFontIdentifier": f"FermataSans-{style};0.100",
        "fullName": f"{family} {style}",
        "psName": f"FermataSans-{style}",
        "version": "Version 0.100",
        "licenseDescription": "Licensed under the MIT License, as part of Fermata.",
        "typographicFamily": family,
        "typographicSubfamily": style,
    }
    fb.setupNameTable(names)
    fsSel = {"Regular": 0x40, "Bold": 0x20}.get(style, 0) | 0x80
    fb.setupOS2(
        usWeightClass=g.css, usWidthClass=5, fsSelection=fsSel, achVendID="FRMT",
        sTypoAscender=ASC, sTypoDescender=DESC, sTypoLineGap=0,
        usWinAscent=ASC, usWinDescent=-DESC, sxHeight=g.xh, sCapHeight=g.cap,
        ulUnicodeRange1=0b111, version=4, fsType=0,
    )
    fb.setupPost(keepGlyphNames=True)
    fb.setupHead(unitsPerEm=UPM, macStyle=1 if style == "Bold" else 0)
    from fontTools.misc.timeTools import timestampNow
    fb.font["head"].fontRevision = 0.1
    fb.font["head"].created = fb.font["head"].modified = timestampNow()
    addOpenTypeFeaturesFromString(fb.font, features(fd))
    os.makedirs(OUT, exist_ok=True)
    path = os.path.join(OUT, f"FermataSans-{style}.ttf")
    with tempfile.TemporaryDirectory() as scratch:
        raw = os.path.join(scratch, f"FermataSans-{style}.ttf")
        fb.save(raw)
        hint(raw, path)
    return os.path.normpath(path), len(order), len(fd.cmap)


def hint(src, dst):
    """Adds TrueType hints with ttfautohint: blue zones at the x-height, cap height and baseline, and stems snapped to whole pixels."""
    from ttfautohint import ttfautohint
    from ttfautohint.options import StemWidthMode
    ttfautohint(in_file=src, out_file=dst, hint_composites=True, hinting_range_min=8, hinting_range_max=48,
                increase_x_height=14, default_script="latn", no_info=True,
                gray_stem_width_mode=StemWidthMode.NATURAL, gdi_cleartype_stem_width_mode=StemWidthMode.QUANTIZED,
                dw_cleartype_stem_width_mode=StemWidthMode.QUANTIZED)


if __name__ == "__main__":
    for g in design.WEIGHTS:
        print(build(g))
