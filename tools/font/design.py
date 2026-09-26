"""Turns the glyph designs into finished outlines with spacing, for one weight."""
import importlib
from geom import *
import glyphs

WEIGHTS = [
    glyphs.Weight("Regular", 400, 88, 0.0),
    glyphs.Weight("Medium", 500, 102, 0.17),
    glyphs.Weight("SemiBold", 600, 118, 0.37),
    glyphs.Weight("Bold", 700, 134, 0.58),
    glyphs.Weight("ExtraBold", 800, 170, 1.0),
]

SIDES = {"S": (60, 40), "R": (42, 28), "D": (12, 6)}
FIG_ADV = (0, 0)


def side(g, kind):
    if isinstance(kind, str):
        a, b = SIDES[kind]
        return g.lerp(a, b)
    return kind * (1 - 0.3 * g.t)


def build(g, name, info=None):
    info = info or glyphs.REG[name]
    shape, W = info["fn"](g)
    if info.get("fig"):
        adv = glyphs.fig_w(g) + g.lerp(96, 60)
        lsb = (adv - W) / 2
        return move(soften(shape, g.r), lsb, 0), adv
    lsb, rsb = side(g, info["l"]), side(g, info["r"])
    shape = soften(shape, g.r)
    return move(shape, lsb, 0), lsb + W + rsb
