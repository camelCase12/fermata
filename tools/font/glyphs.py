"""The designs of Fermata Sans, as functions of one weight's parameters."""
import math
from geom import *


class Weight:
    """The parameters of one weight. t runs from 0 (Regular) to 1 (ExtraBold)."""

    def __init__(self, name, css, V, t):
        self.name, self.css, self.V, self.t = name, css, V, t
        self.H = V * (0.86 - 0.12 * t)          # horizontal strokes
        self.D = V * (0.94 - 0.02 * t)          # diagonal strokes
        self.cs = 1.1 - 0.13 * t                  # how wide counters are, relative to Regular's design
        self.r = V * 0.17                       # corner radius
        self.xh, self.cap, self.asc, self.desc = 530, 700, 750, -215
        self.os = 12                             # overshoot of round shapes
        self.k = 0.60                            # squareness of bowls
        self.dot = V * (1.12 - 0.06 * t)         # size of dots

    def lerp(self, a, b):
        return a + (b - a) * self.t

    def c(self, w):
        """A counter width designed for Regular, scaled for this weight."""
        return w * self.cs


REG = {}


def glyph(name, uni=None, l="S", r="S"):
    def deco(fn):
        REG[name] = dict(fn=fn, uni=uni, l=l, r=r)
        return fn
    return deco


# ---------------------------------------------------------------- shared parts

def ring(g, x0, y0, x1, y1, V=None, H=None, k=None, shift=0.0, ik=None, ext=0.0):
    """An oval stroke. shift moves the counter right (positive); ext widens it to the right only."""
    V = g.V if V is None else V
    H = g.H if H is None else H
    k = g.k if k is None else k
    outer = sqel(x0, y0, x1, y1, k)
    inner = sqel(x0 + V + shift, y0 + H, x1 - V + shift + ext, y1 - H, (ik if ik is not None else k + 0.02))
    return diff(outer, inner)


def bowl(g, x0, y0, x1, y1, H=None):
    """A bowl whose right side merges into a stem at x1-V..x1, thinned where it meets the stem."""
    jb = g.V * g.lerp(0.05, 0.3)
    return ring(g, x0, y0, x1 - jb, y1, H=H, ext=g.V * g.lerp(0.35, 0.55) + jb)


def centre(x0, y0, x1, y1):
    return (x0 + x1) / 2, (y0 + y1) / 2


def arch(g, xs, xr, top, ry=None):
    """The shoulder of n, m and h: rises from a stem at xs..xs+V into a right leg at xr-V..xr."""
    V, H = g.V, g.H
    yt = top + g.os
    ry = ry or top * 0.52
    cy = yt - ry
    ox0 = xs + V * g.lerp(0.08, 0.5)
    ix0 = xs + V * g.lerp(0.42, 0.6)
    outer = sqel(ox0, cy - ry, xr, yt, g.k, g.k + 0.04)
    inner = sqel(ix0, cy - ry + H, xr - V, yt - H, g.k + 0.02, g.k + 0.06)
    shoulder = inter(diff(outer, inner), rect(xs + V * 0.5, cy, xr + 50, yt + 50))
    return union(shoulder, rect(xr - V, 0, xr, cy + 1)), cy


def dot(g, cx, y0, s=None):
    s = g.dot if s is None else s
    return rrect(cx - s / 2, y0, cx + s / 2, y0 + s, s * 0.3)


def idot(g, cx):
    """The dot of i and j, sitting between the x-height and the ascender."""
    s = g.dot
    top = g.asc - g.lerp(10, 0)
    y0 = max(g.xh + g.lerp(95, 70), top - s)
    return dot(g, cx, y0, s)


def hook_down(g, x0, x1, ybot, h, keep=(200, 360), V=None):
    """A bowl bottom from x0 to x1 whose lower edge is at ybot, height 2h, cut to the given angle range."""
    ring_ = ring(g, x0, ybot, x1, ybot + 2 * h, V=V)
    cx, cy = (x0 + x1) / 2, ybot + h
    return inter(ring_, wedge(cx, cy, *keep)), cy


def hook_up(g, x0, x1, ytop, h, keep=(0, 160), V=None):
    ring_ = ring(g, x0, ytop - 2 * h, x1, ytop, V=V)
    cx, cy = (x0 + x1) / 2, ytop - h
    return inter(ring_, wedge(cx, cy, *keep)), cy


# ---------------------------------------------------------------- lowercase

def w_n(g):
    return 2 * g.V + g.c(222)


def w_o(g):
    return 2 * g.V + g.c(272)


@glyph("n", 0x6E)
def g_n(g):
    W = w_n(g)
    a, _ = arch(g, 0, W, g.xh)
    return union(rect(0, 0, g.V, g.xh), a), W


@glyph("h", 0x68)
def g_h(g):
    W = w_n(g)
    a, _ = arch(g, 0, W, g.xh)
    return union(rect(0, 0, g.V, g.asc), a), W


@glyph("m", 0x6D)
def g_m(g):
    c = g.c(172) * (1 + 0.1 * g.t)
    W = 3 * g.V + 2 * c
    a1, _ = arch(g, 0, 2 * g.V + c, g.xh)
    a2, _ = arch(g, g.V + c, W, g.xh)
    return union(rect(0, 0, g.V, g.xh), a1, a2), W


@glyph("u", 0x75)
def g_u(g):
    p, W = g_n(g)
    return rotate180(p, W / 2, g.xh / 2), W


@glyph("r", 0x72, r=18)
def g_r(g):
    Wf = w_n(g) * 0.96
    a, cy = arch(g, 0, Wf, g.xh)
    W = g.V + g.c(152)
    cut = wedge(g.V * 0.5, cy, 25, 180)
    shoulder = inter(diff(a, rect(Wf - g.V - 5, -10, Wf + 10, cy + 2)), cut)
    shoulder = inter(shoulder, leftof(W))
    return union(rect(0, 0, g.V, g.xh), shoulder), W


@glyph("o", 0x6F, "R", "R")
def g_o(g):
    W = w_o(g)
    return ring(g, 0, -g.os, W, g.xh + g.os), W


@glyph("c", 0x63, "R", 20)
def g_c(g):
    W = w_o(g) - g.c(34)
    x1 = W + g.c(20)
    o = ring(g, 0, -g.os, x1, g.xh + g.os)
    cx, cy = x1 / 2, g.xh / 2
    return inter(o, wedge(cx, cy, 44, 318)), W


@glyph("e", 0x65, "R", 30)
def g_e(g):
    W = w_o(g) - g.c(8)
    o = ring(g, 0, -g.os, W, g.xh + g.os)
    cx, cy = W / 2, g.xh / 2
    yb = g.xh * g.lerp(0.47, 0.45) - g.H * 0.45
    bar = rect(g.V * 0.5, yb, W - 2, yb + g.H * g.lerp(0.92, 0.86))
    atop = math.degrees(math.atan2(yb - cy, W / 2))
    o = diff(o, wedge(cx, cy, -40, atop))
    return union(o, bar), W


def _bowl_stem(g, y0, y1):
    W = w_o(g) - g.c(12)
    return union(bowl(g, 0, -g.os, W, g.xh + g.os), rect(W - g.V, y0, W, y1)), W


@glyph("d", 0x64, "R", 16)
def g_d(g):
    """d carries a spur at the baseline, so it is not a mirror of b."""
    p, W = _bowl_stem(g, 0, g.asc)
    sp = g.lerp(62, 70)
    return union(p, rect(W - 2, 0, W + sp, g.H * 0.95)), W + sp


@glyph("b", 0x62, "S", "R")
def g_b(g):
    p, W = _bowl_stem(g, 0, g.asc)
    return mirror_x(p, W), W


@glyph("q", 0x71, "R", 16)
def g_q(g):
    """q carries a spur at the x-height, so it is not a mirror of p."""
    p, W = _bowl_stem(g, g.desc, g.xh)
    sp = g.lerp(62, 70)
    return union(p, rect(W - 2, g.xh - g.H * 0.95, W + sp, g.xh)), W + sp


@glyph("p", 0x70, "S", "R")
def g_p(g):
    p, W = _bowl_stem(g, g.desc, g.xh)
    return mirror_x(p, W), W


@glyph("g", 0x67, "R", "S")
def g_g(g):
    W = w_o(g) - g.c(12)
    bowl_ = bowl(g, 0, -g.os, W, g.xh + g.os)
    hh = g.lerp(150, 160)
    hook, cy = hook_down(g, g.V * 0.1, W, g.desc - g.os, hh, keep=(208, 360))
    return union(bowl_, hook, rect(W - g.V, cy, W, g.xh)), W


@glyph("a", 0x61, 30, "S")
def g_a(g):
    W = 2 * g.V + g.c(196)
    top = g.xh + g.os
    hh = g.xh * 0.34
    hook, cy = hook_up(g, g.V * 0.02, W, top, hh, keep=(0, 152))
    yb = g.xh * g.lerp(0.58, 0.63)
    b = inter(bowl(g, 0, -g.os, W, yb, H=g.H * 0.96), leftof(W - g.V / 2))
    return union(hook, b, rect(W - g.V, 0, W, cy + 1)), W


@glyph("i", 0x69)
def g_i(g):
    return union(rect(0, 0, g.V, g.xh), idot(g, g.V / 2)), g.V


@glyph("dotlessi", 0x131)
def g_dotlessi(g):
    return rect(0, 0, g.V, g.xh), g.V


def _j(g):
    W = g.V + g.c(96)
    hh = g.lerp(130, 140)
    x0 = W - 2 * (W - g.V * 0.5) - g.V * 0.1
    hook, cy = hook_down(g, x0, W, g.desc - g.os, hh, keep=(232, 360))
    return union(hook, rect(W - g.V, cy, W, g.xh)), W


@glyph("dotlessj", 0x237, 6, "S")
def g_dotlessj(g):
    return _j(g)


@glyph("j", 0x6A, 6, "S")
def g_j(g):
    p, W = _j(g)
    return union(p, idot(g, W - g.V / 2)), W


@glyph("l", 0x6C, "S", 20)
def g_l(g):
    W = g.V + g.c(96)
    hh = g.lerp(120, 130)
    tail, cy = hook_down(g, 0, W + g.c(84), -g.os, hh, keep=(180, 296))
    return union(rect(0, cy - 1, g.V, g.asc), tail), W


@glyph("t", 0x74, 16, 18)
def g_t(g):
    s = g.c(84)                        # overhang of the crossbar to the left
    W = s + g.V + g.c(142)
    hh = g.lerp(120, 130)
    tail, cy = hook_down(g, s, s + 2 * (g.V + g.c(110)), -g.os, hh, keep=(180, 290))
    stem = rect(s, cy - 1, s + g.V, g.xh + g.lerp(140, 110))
    bar = rect(0, g.xh - g.H, W, g.xh)
    return union(tail, stem, bar), W


@glyph("f", 0x66, 16, 10)
def g_f(g):
    s = g.c(84)
    W = s + g.V + g.c(150)
    hh = g.lerp(130, 140)
    x1 = s + 2 * (g.V + g.c(116))
    hook, cy = hook_up(g, s, x1, g.asc + g.os, hh, keep=(52, 180))
    stem = rect(s, 0, s + g.V, cy + 1)
    bar = rect(0, g.xh - g.H, W, g.xh)
    return union(hook, stem, bar), W


@glyph("k", 0x6B, "S", "D")
def g_k(g):
    W = g.V + g.c(290)
    D = g.D
    ax0, ay0 = g.V * 0.5, g.xh * 0.22
    ax1, ay1 = W - D * 0.62, g.xh
    arm = inter(hband(ax0, ay0, ax1, ay1, D, 0, g.xh), rightof(g.V * 0.5))
    f = 0.36
    lx, ly = ax0 + (ax1 - ax0) * f, ay0 + (ay1 - ay0) * f
    leg = hband(lx, ly, W - D * 0.6, 0, D, 0, ly)
    return union(rect(0, 0, g.V, g.asc), arm, leg), W


def _v(g, W, h, D=None):
    D = g.D if D is None else D
    L = hband(D * 0.6, h, W / 2, 0, D, 0, h)
    R = hband(W - D * 0.6, h, W / 2, 0, D, 0, h)
    return union(L, R)


@glyph("v", 0x76, "D", "D")
def g_v(g):
    W = g.c(360) + g.V * 1.35
    return _v(g, W, g.xh), W


@glyph("w", 0x77, "D", "D")
def g_w(g):
    D = g.D * 0.94
    W = g.c(560) + g.V * 2.6
    q = W / 4
    parts = [
        hband(D * 0.58, g.xh, q + D * 0.05, 0, D, 0, g.xh),
        hband(W / 2, g.xh, q + D * 0.05, 0, D, 0, g.xh),
        hband(W / 2, g.xh, 3 * q - D * 0.05, 0, D, 0, g.xh),
        hband(W - D * 0.58, g.xh, 3 * q - D * 0.05, 0, D, 0, g.xh),
    ]
    return union(*parts), W


@glyph("y", 0x79, "D", "D")
def g_y(g):
    W = g.c(360) + g.V * 1.35
    D = g.D
    L = hband(D * 0.6, g.xh, W / 2 + D * 0.05, 0, D, 0, g.xh)
    # the tail continues the right stroke straight down to the descender
    x2 = W - D * 0.6
    dx = (W / 2 - x2) / g.xh
    R = hband(x2, g.xh, x2 + dx * (g.xh - g.desc), g.desc, D, g.desc, g.xh)
    return union(L, R), W


@glyph("x", 0x78, "D", "D")
def g_x(g):
    W = g.c(330) + g.V * 1.35
    D = g.D
    return union(hband(D * 0.55, g.xh, W - D * 0.55, 0, D, 0, g.xh),
                 hband(W - D * 0.55, g.xh, D * 0.55, 0, D, 0, g.xh)), W


@glyph("z", 0x7A, 30, 30)
def g_z(g):
    W = g.c(310) + g.V * 1.1
    H = g.H
    top = rect(0, g.xh - H, W - 4, g.xh)
    bot = rect(0, 0, W, H)
    diag = hband(W - g.D * 0.72, g.xh - H, g.D * 0.72, H, g.D, H - 1, g.xh - H + 1)
    return union(top, bot, diag), W


def s_path(g, W, h, t, top_in=0.03, lean=0.0):
    """The spine of s and S in a box W wide and h tall, for a stroke of width t."""
    a, b = t / 2, t / 2
    X = lambda u: a + (W - 2 * a) * u
    Y = lambda v: b + (h - 2 * b) * v
    return curve(
        (X(0.97 - top_in), Y(0.80)),
        [(X(0.90 - top_in), Y(0.95)), (X(0.73), Y(1.0)), (X(0.50), Y(1.0))],
        [(X(0.22), Y(1.0)), (X(0.03 + top_in), Y(0.90)), (X(0.03 + top_in), Y(0.73))],
        [(X(0.03 + top_in), Y(0.56)), (X(0.22), Y(0.535)), (X(0.50), Y(0.48 + lean))],
        [(X(0.80), Y(0.425)), (X(0.985), Y(0.37)), (X(0.985), Y(0.24))],
        [(X(0.985), Y(0.08)), (X(0.78), Y(0.0)), (X(0.50), Y(0.0))],
        [(X(0.24), Y(0.0)), (X(0.07), Y(0.05)), (X(0.0), Y(0.21))],
    )


@glyph("s", 0x73, 36, 36)
def g_s(g):
    W = 2 * g.V + g.c(176)
    t = (g.V + g.H) / 2 * 0.98
    sp = s_path(g, W, g.xh + 2 * g.os, t)
    return move(stroke(sp, t), 0, -g.os), W


# ---------------------------------------------------------------- capitals

def dshape(x0, y0, x1, y1, rx, k):
    """A box whose right side is a half oval of horizontal radius rx."""
    rx = max(rx, 1)
    return union(rect(x0, y0, x1 - rx + 1, y1), inter(sqel(x1 - 2 * rx, y0, x1, y1, k), rightof(x1 - rx)))


def dbowl(g, x0, y0, x1, y1, rx, H=None):
    """The bowl of D, P, B and R: a stroke from a stem at x0 around a right side ending at x1."""
    H = g.H if H is None else H
    outer = dshape(x0, y0, x1, y1, rx, g.k)
    inner = dshape(x0 - 50, y0 + H, x1 - g.V, y1 - H, max(rx - g.V * 0.9, 20), g.k + 0.04)
    return diff(outer, inner)


def w_H(g):
    return 2 * g.V + g.c(392)


@glyph("H", 0x48)
def g_H(g):
    W = w_H(g)
    y = g.cap * 0.5
    return union(rect(0, 0, g.V, g.cap), rect(W - g.V, 0, W, g.cap), rect(g.V - 1, y - g.H / 2 + 8, W - g.V + 1, y + g.H / 2 + 8)), W


@glyph("I", 0x49)
def g_I(g):
    return rect(0, 0, g.V, g.cap), g.V


@glyph("E", 0x45, "S", 30)
def g_E(g):
    W = g.V + g.c(300)
    y = g.cap * 0.5 + 8
    return union(rect(0, 0, g.V, g.cap), rect(0, g.cap - g.H, W, g.cap), rect(0, y - g.H / 2, W - g.c(24), y + g.H / 2),
                 rect(0, 0, W, g.H)), W


@glyph("F", 0x46, "S", 24)
def g_F(g):
    W = g.V + g.c(290)
    y = g.cap * 0.5 + 8
    return union(rect(0, 0, g.V, g.cap), rect(0, g.cap - g.H, W, g.cap), rect(0, y - g.H / 2, W - g.c(28), y + g.H / 2)), W


@glyph("L", 0x4C, "S", 22)
def g_L(g):
    W = g.V + g.c(282)
    return union(rect(0, 0, g.V, g.cap), rect(0, 0, W, g.H)), W


@glyph("T", 0x54, 18, 18)
def g_T(g):
    W = g.V + g.c(476)
    return union(rect(0, g.cap - g.H, W, g.cap), rect((W - g.V) / 2, 0, (W + g.V) / 2, g.cap)), W


def w_O(g):
    return 2 * g.V + g.c(478)


@glyph("O", 0x4F, "R", "R")
def g_O(g):
    W = w_O(g)
    return ring(g, 0, -g.os, W, g.cap + g.os, k=g.k - 0.02), W


@glyph("Q", 0x51, "R", "R")
def g_Q(g):
    W = w_O(g)
    o = ring(g, 0, -g.os, W, g.cap + g.os, k=g.k - 0.02)
    tail = hband(W * 0.56, g.H * 1.4, W * 0.9, -g.lerp(150, 140), g.D, -g.lerp(150, 140), g.H * 1.6)
    return union(o, tail), W


@glyph("C", 0x43, "R", 14)
def g_C(g):
    W = w_O(g) - g.c(40)
    x1 = W + g.c(28)
    o = ring(g, 0, -g.os, x1, g.cap + g.os, k=g.k - 0.02)
    return inter(o, wedge(x1 / 2, g.cap / 2, 42, 318)), W


@glyph("G", 0x47, "R", "S")
def g_G(g):
    W = w_O(g) - g.c(14)
    o = ring(g, 0, -g.os, W, g.cap + g.os, k=g.k - 0.02)
    cx, cy = W / 2, g.cap / 2
    yb = g.cap * 0.46
    o = inter(o, wedge(cx, cy, 38, 360))
    o = diff(o, inter(rightof(cx), rect(-BIG, yb, BIG, cy + 1)))
    bar = rect(W * 0.54, yb - g.H, W, yb)
    side = rect(W - g.V, 0, W, yb)
    return union(inter(o, union(leftof(W - g.V + 2), below(yb))), bar, side), W


@glyph("D", 0x44, "S", "R")
def g_D(g):
    W = g.V + g.c(420)
    return union(rect(0, 0, g.V, g.cap), dbowl(g, g.V / 2, 0, W, g.cap, W * 0.62)), W


@glyph("P", 0x50, "S", 34)
def g_P(g):
    W = g.V + g.c(330)
    y0 = g.cap * 0.40
    return union(rect(0, 0, g.V, g.cap), dbowl(g, g.V / 2, y0 - g.H / 2, W, g.cap, (g.cap - y0) * 0.56)), W


@glyph("R", 0x52, "S", "D")
def g_R(g):
    W = g.V + g.c(352)
    y0 = g.cap * 0.42
    bw = W - g.c(20)
    bowl = dbowl(g, g.V / 2, y0 - g.H / 2, bw, g.cap, (g.cap - y0) * 0.56)
    leg = hband(g.V + g.c(150), y0, W - g.D * 0.6, 0, g.D, 0, y0)
    return union(rect(0, 0, g.V, g.cap), bowl, leg), W


@glyph("B", 0x42, "S", "R")
def g_B(g):
    W = g.V + g.c(356)
    ym = g.cap * 0.54
    top = dbowl(g, g.V / 2, ym - g.H, W - g.c(34), g.cap, (g.cap - ym) * 0.6)
    bot = dbowl(g, g.V / 2, 0, W, ym, ym * 0.6)
    return union(rect(0, 0, g.V, g.cap), top, bot), W


@glyph("U", 0x55)
def g_U(g):
    W = w_H(g) - g.c(10)
    h = g.lerp(250, 270)
    bowl, cy = hook_down(g, 0, W, -g.os, h, keep=(180, 360))
    return union(bowl, rect(0, cy - 1, g.V, g.cap), rect(W - g.V, cy - 1, W, g.cap)), W


@glyph("J", 0x4A, 20, "S")
def g_J(g):
    W = g.V + g.c(260)
    h = g.lerp(200, 220)
    bowl, cy = hook_down(g, 0, W, -g.os, h, keep=(206, 360))
    return union(bowl, rect(W - g.V, cy - 1, W, g.cap)), W


@glyph("S", 0x53, 34, 34)
def g_S(g):
    W = 2 * g.V + g.c(296)
    t = (g.V + g.H) / 2 * 1.0
    sp = s_path(g, W, g.cap + 2 * g.os, t, top_in=0.035)
    return move(stroke(sp, t), 0, -g.os), W


@glyph("A", 0x41, "D", "D")
def g_A(g):
    W = g.c(520) + g.V * 1.3
    D = g.D
    apex = g.cap + g.lerp(40, 70)
    L = hband(D * 0.6, 0, W / 2, apex, D, 0, g.cap)
    R = hband(W - D * 0.6, 0, W / 2, apex, D, 0, g.cap)
    yb = g.cap * 0.27
    bar = inter(rect(0, yb, W, yb + g.H), rect(D, -10, W - D, g.cap))
    return union(L, R, bar), W


@glyph("V", 0x56, "D", "D")
def g_V(g):
    W = g.c(520) + g.V * 1.3
    D = g.D
    low = -g.lerp(40, 70)
    L = hband(D * 0.6, g.cap, W / 2, low, D, 0, g.cap)
    R = hband(W - D * 0.6, g.cap, W / 2, low, D, 0, g.cap)
    return union(L, R), W


@glyph("W", 0x57, "D", "D")
def g_W(g):
    D = g.D * 0.94
    W = g.c(800) + g.V * 2.4
    q = W / 4
    low = -g.lerp(20, 40)
    mid = g.cap * 0.78
    parts = [
        hband(D * 0.58, g.cap, q, low, D, 0, g.cap),
        hband(W / 2, mid, q, low, D, 0, mid),
        hband(W / 2, mid, 3 * q, low, D, 0, mid),
        hband(W - D * 0.58, g.cap, 3 * q, low, D, 0, g.cap),
    ]
    return union(*parts), W


@glyph("M", 0x4D)
def g_M(g):
    W = 2 * g.V + g.c(480)
    D = g.D * 0.94
    L = hband(g.V * 0.5, g.cap + g.lerp(30, 50), W / 2, -g.lerp(10, 30), D, 0, g.cap)
    R = hband(W - g.V * 0.5, g.cap + g.lerp(30, 50), W / 2, -g.lerp(10, 30), D, 0, g.cap)
    v = inter(union(L, R), rect(g.V / 2, 0, W - g.V / 2, g.cap))
    return union(rect(0, 0, g.V, g.cap), rect(W - g.V, 0, W, g.cap), v), W


@glyph("N", 0x4E)
def g_N(g):
    W = 2 * g.V + g.c(386)
    D = g.D
    d = hband(g.V * 0.5, g.cap + g.lerp(20, 40), W - g.V * 0.5, -g.lerp(20, 40), D, 0, g.cap)
    d = inter(d, rect(g.V / 2, 0, W - g.V / 2, g.cap))
    return union(rect(0, 0, g.V, g.cap), rect(W - g.V, 0, W, g.cap), d), W


@glyph("K", 0x4B, "S", "D")
def g_K(g):
    W = g.V + g.c(396)
    D = g.D
    ax0, ay0 = g.V * 0.5, g.cap * 0.24
    ax1, ay1 = W - D * 0.62, g.cap
    arm = inter(hband(ax0, ay0, ax1, ay1, D, 0, g.cap), rightof(g.V * 0.5))
    f = 0.36
    lx, ly = ax0 + (ax1 - ax0) * f, ay0 + (ay1 - ay0) * f
    leg = hband(lx, ly, W - D * 0.6, 0, D, 0, ly)
    return union(rect(0, 0, g.V, g.cap), arm, leg), W


@glyph("X", 0x58, "D", "D")
def g_X(g):
    W = g.c(490) + g.V * 1.3
    D = g.D
    return union(hband(D * 0.55, g.cap, W - D * 0.55, 0, D, 0, g.cap),
                 hband(W - D * 0.55, g.cap, D * 0.55, 0, D, 0, g.cap)), W


@glyph("Y", 0x59, "D", "D")
def g_Y(g):
    W = g.c(500) + g.V * 1.3
    D = g.D
    ym = g.cap * 0.42
    L = hband(D * 0.58, g.cap, W / 2, ym - D * 0.3, D, ym - 100, g.cap)
    R = hband(W - D * 0.58, g.cap, W / 2, ym - D * 0.3, D, ym - 100, g.cap)
    return union(inter(union(L, R), above(ym)), rect((W - g.V) / 2, 0, (W + g.V) / 2, ym + 2)), W


@glyph("Z", 0x5A, 30, 30)
def g_Z(g):
    W = g.c(430) + g.V * 1.1
    H = g.H
    diag = hband(W - g.D * 0.75, g.cap - H, g.D * 0.75, H, g.D, H - 1, g.cap - H + 1)
    return union(rect(0, g.cap - H, W - 6, g.cap), rect(0, 0, W, H), diag), W


# ---------------------------------------------------------------- figures (tabular by default)

def fig_w(g):
    return 2 * g.V + g.c(268)


def fig_t(g):
    return (g.V + g.H) / 2


FIGS = {}


def figure(name, uni):
    def deco(fn):
        FIGS[name] = fn
        REG[name] = dict(fn=fn, uni=uni, l="F", r="F", fig=True)
        return fn
    return deco


@figure("zero", 0x30)
def g_zero(g):
    W = fig_w(g)
    o = ring(g, 0, -g.os, W, g.cap + g.os, k=g.k + 0.04)
    s = g.D * 0.78
    cx, cy = W / 2, g.cap / 2
    dx, dy = (W - 2 * g.V) * 0.22, g.cap * 0.17
    slash = band((cx - dx, cy - dy), (cx + dx, cy + dy), s)
    return union(o, slash), W


@figure("one", 0x31)
def g_one(g):
    W = fig_w(g)
    x = W / 2 + g.c(18) - g.V / 2
    fx, fy = x - g.c(160), g.cap - g.c(160) * 0.58
    flag = band((x + g.V * 0.5, g.cap - g.D * 0.3), (fx, fy), g.D * 0.9, ext=0)
    flag = inter(flag, below(g.cap))
    return union(rect(x, 0, x + g.V, g.cap), inter(flag, above(0))), W


@figure("two", 0x32)
def g_two(g):
    W = fig_w(g)
    t = fig_t(g)
    a = t / 2
    h = g.cap + g.os
    sp = curve((a + 2, h - a - g.cap * 0.22),
               [(a + 10, h - a - g.cap * 0.04), (W * 0.3, h - a), (W / 2, h - a)],
               [(W * 0.78, h - a), (W - a, h - a - g.cap * 0.08), (W - a, h - a - g.cap * 0.25)],
               [(W - a, h - a - g.cap * 0.44), (W * 0.62, g.cap * 0.36), (a + 2, g.H * 0.9)])
    body = union(stroke(sp, t, join="round"), rect(0, 0, W, g.H))
    return inter(body, above(0)), W


@figure("three", 0x33)
def g_three(g):
    W = fig_w(g)
    ym = g.cap * 0.55
    up, _ = hook_up(g, W * 0.05, W - g.c(14), g.cap + g.os, (g.cap + g.os - ym + g.H) / 2, keep=(-90, 148))
    lo_top = ym
    lo = ring(g, 0, -g.os, W, lo_top)
    lo = inter(lo, wedge(W / 2, lo_top / 2, -150, 90))
    bar = rect(W * 0.34, ym - g.H, W * 0.5, ym)
    return union(up, lo, bar), W


@figure("four", 0x34)
def g_four(g):
    W = fig_w(g)
    xs = W - g.V - g.c(34)
    yb = g.cap * 0.26
    D = g.D
    diag = hband(xs + g.V * 0.5, g.cap, D * 0.4, yb + g.H * 0.5, D, yb, g.cap)
    diag = inter(diag, leftof(xs + g.V))
    return union(rect(xs, 0, xs + g.V, g.cap), rect(0, yb, W, yb + g.H), diag), W


@figure("five", 0x35)
def g_five(g):
    W = fig_w(g)
    yt = g.cap * 0.61
    bowl = ring(g, 0, -g.os, W, yt)
    bowl = inter(bowl, wedge(W / 2, yt / 2, -150, 104))
    xv = W * 0.08
    stem = rect(xv, yt - g.H * 1.2, xv + g.V, g.cap)
    stem = inter(stem, union(above(yt - g.H * 0.2), rect(-BIG, -BIG, BIG, BIG)))
    top = rect(xv, g.cap - g.H, W - g.c(16), g.cap)
    link = rect(xv, yt - g.H, W / 2, yt)
    return union(bowl, rect(xv, yt - g.H, xv + g.V, g.cap), top, link), W


@figure("six", 0x36)
def g_six(g):
    W = fig_w(g)
    yt = g.cap * 0.62
    bowl = ring(g, 0, -g.os, W, yt)
    t = fig_t(g)
    a = g.V / 2
    sp = curve((a, yt * 0.5),
               [(a, g.cap * 0.84), (W * 0.26, g.cap + g.os - t / 2), (W * 0.58, g.cap + g.os - t / 2)],
               [(W * 0.74, g.cap + g.os - t / 2), (W * 0.84, g.cap - t * 0.2), (W - t * 0.3, g.cap - t * 0.9)])
    stem = stroke(sp, g.V)
    return union(bowl, stem), W


@figure("seven", 0x37)
def g_seven(g):
    W = fig_w(g)
    D = g.D
    diag = hband(W - D * 0.55, g.cap - g.H, W * 0.3, 0, D, 0, g.cap - g.H + 2)
    return union(rect(0, g.cap - g.H, W, g.cap), diag), W


@figure("eight", 0x38)
def g_eight(g):
    W = fig_w(g)
    ym = g.cap * 0.56
    lo = ring(g, 0, -g.os, W, ym)
    up = ring(g, W * 0.05, ym - g.H, W * 0.95, g.cap + g.os, V=g.V * 0.94)
    return union(lo, up), W


@figure("nine", 0x39)
def g_nine(g):
    p, W = g_six(g)
    return rotate180(p, W / 2, g.cap / 2), W


# ---------------------------------------------------------------- punctuation

def ds(g):
    return g.dot


@glyph("space", 0x20, 0, 0)
def g_space(g):
    return skia.Path(), g.lerp(236, 214)


@glyph("nbspace", 0xA0, 0, 0)
def g_nbspace(g):
    return g_space(g)


@glyph("period", 0x2E, 44, 44)
def g_period(g):
    return dot(g, ds(g) / 2, 0), ds(g)


def comma_shape(g, y=0, flip=False):
    s = ds(g)
    tail = hband(s * 0.64, s * 0.5, s * 0.12, -g.lerp(150, 160), s * 0.56, -g.lerp(150, 160), s * 0.5)
    p = union(dot(g, s / 2, 0), tail)
    if flip:
        b = bounds(p)
        p = rotate180(p, s / 2, (b[1] + b[3]) / 2)
    return move(p, 0, y), s


@glyph("comma", 0x2C, 38, 44)
def g_comma(g):
    return comma_shape(g)


@glyph("colon", 0x3A, 44, 44)
def g_colon(g):
    s = ds(g)
    return union(dot(g, s / 2, 0), dot(g, s / 2, g.xh - s)), s


@glyph("semicolon", 0x3B, 38, 44)
def g_semicolon(g):
    p, s = comma_shape(g)
    return union(p, dot(g, s / 2, g.xh - s)), s


@glyph("exclam", 0x21, 50, 50)
def g_exclam(g):
    s = ds(g)
    w = g.V * 0.96
    return union(rect((s - w) / 2, s + g.lerp(110, 90), (s + w) / 2, g.cap), dot(g, s / 2, 0)), s


@glyph("exclamdown", 0xA1, 50, 50)
def g_exclamdown(g):
    p, s = g_exclam(g)
    return move(rotate180(p, s / 2, g.cap / 2), 0, -(g.cap - g.xh) - 4), s


@glyph("question", 0x3F, 28, 34)
def g_question(g):
    W = 2 * g.V + g.c(250)
    y0 = g.cap * 0.33
    top, cy = hook_up(g, 0, W, g.cap + g.os, (g.cap + g.os - y0) / 2, keep=(-90, 160))
    cx = W / 2
    s = ds(g)
    stem = rect(cx - g.V / 2, s + g.lerp(110, 90), cx + g.V / 2, y0 + g.H)
    return union(top, stem, dot(g, cx, 0)), W


@glyph("questiondown", 0xBF, 34, 28)
def g_questiondown(g):
    p, W = g_question(g)
    return move(rotate180(p, W / 2, g.cap / 2), 0, -(g.cap - g.xh) - 4), W


@glyph("quotesingle", 0x27, 50, 50)
def g_quotesingle(g):
    w = g.V * 0.94
    return rect(0, g.cap - g.lerp(230, 250), w, g.cap + 10), w


@glyph("quotedbl", 0x22, 50, 50)
def g_quotedbl(g):
    w = g.V * 0.94
    gap = g.lerp(80, 70)
    h = g.lerp(230, 250)
    return union(rect(0, g.cap - h, w, g.cap + 10), rect(w + gap, g.cap - h, 2 * w + gap, g.cap + 10)), 2 * w + gap


def quote(g, n, y, flip):
    p, s = comma_shape(g, y, flip)
    gap = g.lerp(64, 56)
    if n == 2:
        p = union(p, move(p, s + gap))
        return p, 2 * s + gap
    return p, s


@glyph("quoteright", 0x2019, 44, 38)
def g_quoteright(g):
    return quote(g, 1, g.cap - ds(g) + 10, False)


@glyph("quoteleft", 0x2018, 38, 44)
def g_quoteleft(g):
    return quote(g, 1, g.cap - ds(g) + 10 - g.lerp(150, 160) + 0, True)


@glyph("quotedblright", 0x201D, 44, 38)
def g_quotedblright(g):
    return quote(g, 2, g.cap - ds(g) + 10, False)


@glyph("quotedblleft", 0x201C, 38, 44)
def g_quotedblleft(g):
    return quote(g, 2, g.cap - ds(g) + 10 - g.lerp(150, 160), True)


@glyph("quotesinglbase", 0x201A, 38, 44)
def g_quotesinglbase(g):
    return quote(g, 1, 0, False)


@glyph("quotedblbase", 0x201E, 38, 44)
def g_quotedblbase(g):
    return quote(g, 2, 0, False)


def axis(g):
    return g.xh * 0.5 + 12


@glyph("hyphen", 0x2D, 40, 40)
def g_hyphen(g):
    W = g.c(210) + g.V * 0.4
    y = g.xh * 0.47
    return rect(0, y - g.H * 0.48, W, y + g.H * 0.48), W


@glyph("softhyphen", 0xAD, 40, 40)
def g_softhyphen(g):
    return g_hyphen(g)


@glyph("endash", 0x2013, 20, 20)
def g_endash(g):
    W = 460
    y = g.xh * 0.47
    return rect(0, y - g.H * 0.46, W, y + g.H * 0.46), W


@glyph("emdash", 0x2014, 20, 20)
def g_emdash(g):
    W = 960
    y = g.xh * 0.47
    return rect(0, y - g.H * 0.46, W, y + g.H * 0.46), W


@glyph("underscore", 0x5F, 10, 10)
def g_underscore(g):
    W = 480
    return rect(0, -g.lerp(150, 170), W, -g.lerp(150, 170) + g.H * 0.9), W


def paren(g):
    t = g.V * 0.94
    W = g.c(150) + t
    top, bot = g.cap + 90, -g.lerp(150, 140)
    h = top - bot
    sp = curve((W - t * 0.1, top),
               [(t * 0.2, top - h * 0.18), (t * 0.5, top - h * 0.3), (t * 0.5, (top + bot) / 2)],
               [(t * 0.5, bot + h * 0.3), (t * 0.2, bot + h * 0.18), (W - t * 0.1, bot)])
    return inter(stroke(sp, t), rect(-BIG, bot, W, top)), W


@glyph("parenleft", 0x28, 50, 16)
def g_parenleft(g):
    return paren(g)


@glyph("parenright", 0x29, 16, 50)
def g_parenright(g):
    p, W = paren(g)
    return mirror_x(p, W), W


def bracket(g):
    t = g.V * 0.94
    W = t + g.c(120)
    top, bot = g.cap + 90, -g.lerp(150, 140)
    return union(rect(0, bot, t, top), rect(0, top - g.H, W, top), rect(0, bot, W, bot + g.H)), W


@glyph("bracketleft", 0x5B, "S", 20)
def g_bracketleft(g):
    return bracket(g)


@glyph("bracketright", 0x5D, 20, "S")
def g_bracketright(g):
    p, W = bracket(g)
    return mirror_x(p, W), W


@glyph("slash", 0x2F, 10, 10)
def g_slash(g):
    W = g.c(280) + g.V * 0.6
    bot, top = -g.lerp(120, 110), g.cap + 60
    return hband(g.D * 0.5, bot, W - g.D * 0.5, top, g.D * 0.9, bot, top), W


@glyph("backslash", 0x5C, 10, 10)
def g_backslash(g):
    p, W = g_slash(g)
    return mirror_x(p, W), W


@glyph("bar", 0x7C, 70, 70)
def g_bar(g):
    w = g.V * 0.9
    return rect(0, -g.lerp(180, 170), w, g.asc + 50), w


@glyph("brokenbar", 0xA6, 70, 70)
def g_brokenbar(g):
    w = g.V * 0.9
    m = (g.asc - 170) / 2 + 20
    return union(rect(0, -g.lerp(180, 170), w, m - 60), rect(0, m + 60, w, g.asc + 50)), w


def mathw(g):
    return g.c(400) + g.V * 0.4


@glyph("plus", 0x2B, 40, 40)
def g_plus(g):
    W = mathw(g)
    t = g.V * 0.92
    y = axis(g)
    return union(rect(0, y - t / 2, W, y + t / 2), rect((W - t) / 2, y - W / 2, (W + t) / 2, y + W / 2)), W


@glyph("minus", 0x2212, 40, 40)
def g_minus(g):
    W = mathw(g)
    t = g.V * 0.92
    y = axis(g)
    return rect(0, y - t / 2, W, y + t / 2), W


@glyph("equal", 0x3D, 40, 40)
def g_equal(g):
    W = mathw(g)
    t = g.V * 0.9
    y = axis(g)
    d = g.lerp(100, 120)
    return union(rect(0, y + d - t / 2, W, y + d + t / 2), rect(0, y - d - t / 2, W, y - d + t / 2)), W


@glyph("plusminus", 0xB1, 40, 40)
def g_plusminus(g):
    W = mathw(g)
    t = g.V * 0.92
    y = axis(g) + 60
    plus = union(rect(0, y - t / 2, W, y + t / 2), rect((W - t) / 2, y - W * 0.42, (W + t) / 2, y + W * 0.42))
    return union(plus, rect(0, 0, W, t)), W


@glyph("multiply", 0xD7, 50, 50)
def g_multiply(g):
    W = mathw(g) * 0.8
    t = g.V * 0.92
    y = axis(g)
    c = W / 2
    a = band((0, y - c), (W, y + c), t)
    b = band((0, y + c), (W, y - c), t)
    return inter(union(a, b), rect(0, y - c, W, y + c)), W


@glyph("divide", 0xF7, 40, 40)
def g_divide(g):
    W = mathw(g)
    t = g.V * 0.92
    y = axis(g)
    s = ds(g) * 0.95
    d = g.lerp(170, 190)
    return union(rect(0, y - t / 2, W, y + t / 2), dot(g, W / 2, y + d - s / 2, s), dot(g, W / 2, y - d - s / 2, s)), W


def chevron(g, W, h, y, t):
    a = hband(W - t * 0.5, y + h / 2, t * 0.3, y, t, y, y + h / 2)
    b = hband(W - t * 0.5, y - h / 2, t * 0.3, y, t, y - h / 2, y)
    return inter(union(a, b), rightof(0))


@glyph("less", 0x3C, 40, 40)
def g_less(g):
    W = mathw(g)
    return chevron(g, W, W * 1.0, axis(g), g.D * 0.9), W


@glyph("greater", 0x3E, 40, 40)
def g_greater(g):
    p, W = g_less(g)
    return mirror_x(p, W), W


@glyph("asciicircum", 0x5E, 30, 30)
def g_asciicircum(g):
    W = mathw(g)
    p = chevron(g, W * 0.5, W, 0, g.D * 0.9)
    p = transform(p, skia.Matrix.MakeAll(0, 1, 0, -1, 0, 0, 0, 0, 1))
    b = bounds(p)
    return move(p, -b[0], g.cap - b[3]), W


@glyph("asciitilde", 0x7E, 40, 40)
def g_asciitilde(g):
    W = mathw(g)
    t = g.V * 0.9
    y = axis(g)
    a = W * 0.12
    sp = curve((t * 0.5, y - a * 0.6), [(W * 0.22, y + a * 1.6), (W * 0.38, y + a), (W / 2, y)],
               [(W * 0.62, y - a), (W * 0.78, y - a * 1.6), (W - t * 0.5, y + a * 0.6)])
    return stroke(sp, t), W


@glyph("logicalnot", 0xAC, 40, 40)
def g_logicalnot(g):
    W = mathw(g)
    t = g.V * 0.92
    y = axis(g) + 40
    return union(rect(0, y - t / 2, W, y + t / 2), rect(W - t, y - 150, W, y + t / 2)), W


@glyph("numbersign", 0x23, 20, 20)
def g_numbersign(g):
    W = g.c(430) + g.V * 0.6
    t = g.V * 0.9
    h = g.cap
    bars = union(rect(0, h * 0.64 - t / 2, W, h * 0.64 + t / 2), rect(0, h * 0.34 - t / 2, W, h * 0.34 + t / 2))
    sl = W * 0.1
    v1 = hband(W * 0.33 - sl, 0, W * 0.33 + sl, h, t, 0, h)
    v2 = hband(W * 0.69 - sl, 0, W * 0.69 + sl, h, t, 0, h)
    return union(bars, v1, v2), W


@glyph("percent", 0x25, 30, 30)
def g_percent(g):
    Vs, Hs = g.V * 0.8, g.H * 0.8
    w = g.c(170) + 2 * Vs
    h = g.cap * 0.42
    W = w * 2 + g.c(150)
    o1 = ring(g, 0, g.cap - h, w, g.cap + 6, V=Vs, H=Hs)
    o2 = ring(g, W - w, -6, W, h, V=Vs, H=Hs)
    sl = hband(W * 0.2, -10, W * 0.8, g.cap + 10, g.D * 0.82, 0, g.cap)
    return union(o1, o2, sl), W


@glyph("asterisk", 0x2A, 30, 30)
def g_asterisk(g):
    L = g.c(160)
    t = g.V * (0.86 - 0.2 * g.t)
    cx, cy = L, g.cap - L - 10
    p = skia.Path()
    for i in range(5):
        a = math.radians(90 + i * 72)
        p = union(p, band((cx, cy), (cx + L * math.cos(a), cy + L * math.sin(a)), t))
    return p, 2 * L


@glyph("dollar", 0x24, 34, 34)
def g_dollar(g):
    p, W = g_S(g)
    t = g.V * 0.9
    cx = W / 2
    stubs = union(rect(cx - t / 2, g.cap, cx + t / 2, g.cap + 110), rect(cx - t / 2, -110, cx + t / 2, 0))
    return union(p, stubs), W


@glyph("cent", 0xA2, "R", 26)
def g_cent(g):
    p, W = g_c(g)
    t = g.V * 0.9
    cx = W / 2 + 10
    return union(p, rect(cx - t / 2, g.xh, cx + t / 2, g.xh + 110), rect(cx - t / 2, -110, cx + t / 2, 0)), W


@glyph("sterling", 0xA3, 30, 30)
def g_sterling(g):
    W = g.c(360) + g.V * 1.2
    t = g.V
    xs = W * 0.22
    top, cy = hook_up(g, xs, W + g.c(20), g.cap + g.os, g.lerp(150, 170), keep=(18, 180))
    stem = rect(xs, g.H, xs + g.V, cy + 1)
    bar = rect(0, g.cap * 0.42 - g.H / 2, W * 0.72, g.cap * 0.42 + g.H / 2)
    base = rect(0, 0, W, g.H)
    return union(top, stem, bar, base), W


@glyph("Euro", 0x20AC, 20, 26)
def g_euro(g):
    p, W = g_C(g)
    p = move(p, g.c(40))
    y1, y2 = g.cap * 0.58, g.cap * 0.4
    bars = union(rect(0, y1 - g.H * 0.44, W * 0.62, y1 + g.H * 0.44), rect(0, y2 - g.H * 0.44, W * 0.58, y2 + g.H * 0.44))
    return union(p, bars), W + g.c(40)


@glyph("yen", 0xA5, "D", "D")
def g_yen(g):
    p, W = g_Y(g)
    y1, y2 = g.cap * 0.36, g.cap * 0.18
    bars = union(rect(W * 0.16, y1 - g.H * 0.44, W * 0.84, y1 + g.H * 0.44), rect(W * 0.16, y2 - g.H * 0.44, W * 0.84, y2 + g.H * 0.44))
    return union(p, bars), W


@glyph("periodcentered", 0xB7, 40, 40)
def g_periodcentered(g):
    s = ds(g)
    return dot(g, s / 2, g.xh * 0.47 - s / 2), s


@glyph("bullet", 0x2022, 40, 40)
def g_bullet(g):
    s = ds(g) * 1.9
    return dot(g, s / 2, g.xh * 0.47 - s / 2, s), s


@glyph("ellipsis", 0x2026, 50, 50)
def g_ellipsis(g):
    s = ds(g)
    gap = g.lerp(150, 110)
    return union(dot(g, s / 2, 0), dot(g, s * 1.5 + gap, 0), dot(g, s * 2.5 + gap * 2, 0)), 3 * s + 2 * gap


def guil(g, flip=False):
    t = g.D * 0.86
    W = g.c(160) + t
    h = g.lerp(300, 320)
    p = chevron(g, W, h, g.xh * 0.47, t)
    return (mirror_x(p, W) if flip else p), W


@glyph("guilsinglleft", 0x2039, 30, 30)
def g_guilsinglleft(g):
    return guil(g)


@glyph("guilsinglright", 0x203A, 30, 30)
def g_guilsinglright(g):
    return guil(g, True)


@glyph("guillemotleft", 0xAB, 30, 30)
def g_guillemotleft(g):
    p, W = guil(g)
    d = W + g.lerp(40, 20)
    return union(p, move(p, d)), W + d


@glyph("guillemotright", 0xBB, 30, 30)
def g_guillemotright(g):
    p, W = guil(g, True)
    d = W + g.lerp(40, 20)
    return union(p, move(p, d)), W + d


@glyph("degree", 0xB0, 40, 40)
def g_degree(g):
    s = g.c(230) + g.V * 0.4
    t = g.V * 0.78
    return ring(g, 0, g.cap - s, s, g.cap, V=t, H=t, k=0.62), s


@glyph("copyright", 0xA9, 40, 40)
def g_copyright(g):
    t = g.V * 0.72
    W = g.cap + 30
    o = ring(g, 0, -15, W, g.cap + 15, V=t, H=t, k=0.56)
    cw = W * 0.42
    c = ring(g, W / 2 - cw / 2, g.cap / 2 - cw * 0.58, W / 2 + cw / 2, g.cap / 2 + cw * 0.58, V=t, H=t * 0.94)
    c = inter(c, wedge(W / 2, g.cap / 2, 48, 312))
    return union(o, c), W


@glyph("registered", 0xAE, 40, 40)
def g_registered(g):
    t = g.V * 0.72
    W = g.cap + 30
    o = ring(g, 0, -15, W, g.cap + 15, V=t, H=t, k=0.56)
    x0, y0, h = W * 0.34, g.cap * 0.24, g.cap * 0.52
    bw = W * 0.3
    bowl = diff(dshape(x0, y0 + h * 0.4, x0 + bw, y0 + h, h * 0.3, 0.6),
                dshape(x0 - 50, y0 + h * 0.4 + t * 0.9, x0 + bw - t, y0 + h - t * 0.9, h * 0.3 - t, 0.62))
    leg = hband(x0 + bw * 0.5, y0 + h * 0.42, x0 + bw, y0, t, y0, y0 + h * 0.42)
    return union(o, rect(x0, y0, x0 + t, y0 + h), bowl, leg), W


@glyph("trademark", 0x2122, 30, 30)
def g_trademark(g):
    t = g.V * 0.72
    h = g.cap * 0.4
    y0 = g.cap - h
    tw = h * 0.9
    T = union(rect(0, g.cap - t * 0.9, tw, g.cap), rect(tw / 2 - t / 2, y0, tw / 2 + t / 2, g.cap))
    mx = tw + 60
    mw = h * 1.05
    M = union(rect(mx, y0, mx + t, g.cap), rect(mx + mw - t, y0, mx + mw, g.cap),
              inter(union(hband(mx + t / 2, g.cap + 20, mx + mw / 2, y0 + h * 0.18, t * 0.95, y0, g.cap),
                          hband(mx + mw - t / 2, g.cap + 20, mx + mw / 2, y0 + h * 0.18, t * 0.95, y0, g.cap)),
                    rect(mx, y0 + h * 0.1, mx + mw, g.cap)))
    return union(T, M), mx + mw


@glyph("mu", 0xB5)
def g_mu(g):
    p, W = g_u(g)
    return union(p, rect(0, g.desc, g.V, g.xh * 0.5)), W


# ---------------------------------------------------------------- marks
# Marks are drawn centred on x = 0. Marks above sit on y = 0; marks below hang from y = 0.

MARKS = {}


def mark(name):
    def deco(fn):
        MARKS[name] = fn
        return fn
    return deco


def mt(g):
    return g.V * (0.9 - 0.06 * g.t)


@mark("acute")
def m_acute(g, h):
    w = h * 0.62 + mt(g) * 0.5
    return hband(-w / 2 + mt(g) * 0.55, 0, w / 2, h, mt(g) * 0.98, 0, h)


@mark("grave")
def m_grave(g, h):
    return mirror_x(m_acute(g, h), 0)


@mark("circumflex")
def m_circumflex(g, h):
    t = mt(g) * 0.92
    w = h * 1.05 + t * 1.4
    return inter(union(hband(-w / 2 + t * 0.5, 0, 0, h + t * 0.3, t, 0, h),
                       hband(w / 2 - t * 0.5, 0, 0, h + t * 0.3, t, 0, h)), rect(-w, 0, w, h))


@mark("caron")
def m_caron(g, h):
    return mirror_y(m_circumflex(g, h), h / 2)


@mark("dieresis")
def m_dieresis(g, h):
    s = g.dot * 0.94
    gap = g.lerp(84, 64)
    return union(dot(g, -(s + gap) / 2, 0, s), dot(g, (s + gap) / 2, 0, s))


@mark("dotaccent")
def m_dotaccent(g, h):
    return dot(g, 0, 0, g.dot * 0.94)


@mark("ring")
def m_ring(g, h):
    s = h * 1.1 + g.V * 0.3
    t = g.V * 0.72
    return ring(g, -s / 2, 0, s / 2, s, V=t, H=t, k=0.6)


@mark("tilde")
def m_tilde(g, h):
    t = mt(g) * g.lerp(0.9, 0.72)
    h = max(h, t * 1.9)
    w = h * 1.35 + t
    a = h * 0.5 - t / 2
    y = h / 2
    sp = curve((-w / 2 + t / 2, y - a * 0.8), [(-w * 0.3, y + a * 1.5), (-w * 0.14, y + a), (0, y)],
               [(w * 0.14, y - a), (w * 0.3, y - a * 1.5), (w / 2 - t / 2, y + a * 0.8)])
    return inter(stroke(sp, t), rect(-w, 0, w, h))


@mark("macron")
def m_macron(g, h):
    w = h * 1.3 + g.V
    return rect(-w / 2, 0, w / 2, g.H * 0.92)


@mark("breve")
def m_breve(g, h):
    t = mt(g) * 0.9
    w = h * 1.3 + t
    o = ring(g, -w / 2, 0, w / 2, h * 2, V=t, H=t * 0.94, k=0.58)
    return inter(o, below(h))


@mark("hungarumlaut")
def m_hungarumlaut(g, h):
    a = m_acute(g, h)
    d = h * 0.46 + mt(g) * 0.5
    return union(move(a, -d), move(a, d))


@mark("cedilla")
def m_cedilla(g, h):
    t = mt(g) * 0.84
    sp = curve((0, 20), (0, -50),
               [(90, -50), (120, -110), (90, -150)],
               [(70, -180), (0, -190), (-80, -190)])
    return stroke(sp, t)


@mark("ogonek")
def m_ogonek(g, h):
    t = mt(g) * 0.84
    sp = curve((0, 20), [(-80, -40), (-100, -110), (-60, -160)], [(-40, -185), (0, -190), (60, -180)])
    return stroke(sp, t)


@mark("commaaccent")
def m_commaaccent(g, h):
    p, s = comma_shape(g)
    p = scale(p, g.lerp(0.95, 0.72))
    s *= g.lerp(0.95, 0.72)
    b = bounds(p)
    return move(p, -s / 2, -g.lerp(44, 40) - b[3])


@mark("commaturned")
def m_commaturned(g, h):
    p, s = comma_shape(g, flip=True)
    b = bounds(p)
    return move(p, -s / 2, -b[1])


@mark("caronalt")
def m_caronalt(g, h):
    p, s = comma_shape(g)
    b = bounds(p)
    return move(p, -b[0], -b[3])


def fermata_sign(g, w):
    """The fermata: an arch thicker at its crown, with a dot under it, as in Fermata's app icon."""
    th = g.lerp(w * 0.125, w * 0.2)
    ts = th * 0.68
    r = w / 2
    outer = sqel(-r, -r, r, r, 0.552)
    inner = sqel(-r + ts, -(r - th), r - ts, r - th, 0.552)
    arch_ = inter(diff(outer, inner), above(0))
    dr = w * g.lerp(0.105, 0.13)
    d = sqel(-dr, -dr, dr, dr, 0.552)
    return union(arch_, move(d, 0, dr - w * 0.02))


@mark("fermata")
def m_fermata(g, h):
    return fermata_sign(g, g.lerp(560, 600))


# ---------------------------------------------------------------- more Latin letters

def crossbar(g, x0, x1, y):
    return rect(x0, y - g.H * 0.42, x1, y + g.H * 0.42)


@glyph("Eth", 0xD0, 30, "R")
def g_Eth(g):
    s = g.c(80)
    p, W = g_D(g)
    return union(move(p, s), crossbar(g, 0, s + g.V + g.c(130), g.cap * 0.5)), W + s


@glyph("Dcroat", 0x110, 30, "R")
def g_Dcroat(g):
    return g_Eth(g)


@glyph("dcroat", 0x111, "R", 16)
def g_dcroat(g):
    p, W = g_d(g)
    y = g.asc - g.lerp(110, 120)
    return union(p, crossbar(g, W - g.V - g.c(110), W + g.c(60), y)), W + g.c(60)


@glyph("Hbar", 0x126, 30, 30)
def g_Hbar(g):
    s = g.c(56)
    p, W = g_H(g)
    return union(move(p, s), crossbar(g, 0, W + 2 * s, g.cap * 0.76)), W + 2 * s


@glyph("hbar", 0x127, 16, "S")
def g_hbar(g):
    s = g.c(70)
    p, W = g_h(g)
    return union(move(p, s), crossbar(g, 0, s + g.V + g.c(120), g.asc - g.lerp(110, 120))), W + s


@glyph("Tbar", 0x166, 18, 18)
def g_Tbar(g):
    p, W = g_T(g)
    return union(p, crossbar(g, W * 0.24, W * 0.76, g.cap * 0.46)), W


@glyph("tbar", 0x167, 16, 18)
def g_tbar(g):
    p, W = g_t(g)
    s = g.c(84)
    return union(p, crossbar(g, s - g.c(50), s + g.V + g.c(80), g.xh * 0.5)), W


@glyph("Lslash", 0x141, 24, 22)
def g_Lslash(g):
    s = g.c(50)
    p, W = g_L(g)
    y = g.cap * 0.46
    d = g.c(130) + g.V * 0.4
    sl = band((s + g.V / 2 - d, y - d * 0.62), (s + g.V / 2 + d, y + d * 0.62), g.D * 0.86)
    return union(move(p, s), sl), W + s


@glyph("lslash", 0x142, 16, 16)
def g_lslash(g):
    s = g.c(60)
    p, W = g_l(g)
    y = g.xh * 0.72
    d = g.c(115) + g.V * 0.4
    sl = band((s + g.V / 2 - d, y - d * 0.62), (s + g.V / 2 + d, y + d * 0.62), g.D * 0.86)
    return union(move(p, s), sl), W + s


@glyph("Oslash", 0xD8, "R", "R")
def g_Oslash(g):
    p, W = g_O(g)
    sl = hband(-10, -40, W + 10, g.cap + 40, g.D * 0.86, -40, g.cap + 40)
    return union(p, sl), W


@glyph("oslash", 0xF8, "R", "R")
def g_oslash(g):
    p, W = g_o(g)
    sl = hband(-6, -36, W + 6, g.xh + 36, g.D * 0.86, -36, g.xh + 36)
    return union(p, sl), W


@glyph("AE", 0xC6, "D", 30)
def g_AE(g):
    D = g.D
    xe = g.c(360) + D * 0.6
    E, We = g_E(g)
    W = xe + We
    L = hband(D * 0.6, 0, xe + g.V * 0.6, g.cap + 10, D, 0, g.cap)
    L = inter(L, leftof(xe + g.V))
    yb = g.cap * 0.27
    bar = rect(D, yb, xe + 2, yb + g.H)
    top = rect(xe, g.cap - g.H, W, g.cap)
    return union(L, bar, move(E, xe), top), W


@glyph("ae", 0xE6, 34, 30)
def g_ae(g):
    a, Wa = g_a(g)
    e, We = g_e(g)
    x = Wa - g.V
    eo = move(sqel(0, -g.os, We, g.xh + g.os, g.k), x)
    a = inter(a, union(leftof(x + g.V * 0.5), eo))
    return union(a, move(e, x)), x + We


@glyph("OE", 0x152, "R", 30)
def g_OE(g):
    Wo = w_O(g) - g.c(40)
    o = ring(g, 0, -g.os, Wo, g.cap + g.os, k=g.k - 0.02)
    xs = Wo * 0.56
    o = inter(o, leftof(xs + g.V * 0.5))
    E, We = g_E(g)
    return union(o, move(E, xs)), xs + We


@glyph("oe", 0x153, "R", 30)
def g_oe(g):
    o, Wo = g_o(g)
    e, We = g_e(g)
    x = Wo - g.V
    eo = move(sqel(0, -g.os, We, g.xh + g.os, g.k), x)
    o = inter(o, union(leftof(x + g.V * 0.5), eo))
    return union(o, move(e, x)), x + We


@glyph("Thorn", 0xDE, "S", 34)
def g_Thorn(g):
    W = g.V + g.c(330)
    y0 = g.cap * 0.2
    y1 = g.cap * 0.8
    return union(rect(0, 0, g.V, g.cap), dbowl(g, g.V / 2, y0, W, y1, (y1 - y0) * 0.56)), W


@glyph("thorn", 0xFE, "S", "R")
def g_thorn(g):
    p, W = g_p(g)
    return union(p, rect(0, g.xh - 10, g.V, g.asc)), W


@glyph("eth", 0xF0, "R", "R")
def g_eth(g):
    W = w_o(g) - g.c(10)
    o = ring(g, 0, -g.os, W, g.xh * 0.9 + g.os)
    t = g.V
    sp = curve((W - t / 2, g.xh * 0.45),
               [(W - t / 2, g.xh * 0.95), (W * 0.62, g.asc * 0.92), (W * 0.26, g.asc - t * 0.2)])
    bar = band((W * 0.28, g.asc * 0.72), (W * 0.84, g.asc * 0.94), g.D * 0.8)
    return union(o, stroke(sp, t), bar), W


@glyph("eng", 0x14B)
def g_eng(g):
    W = w_n(g)
    a, cy = arch(g, 0, W, g.xh)
    hh = g.lerp(130, 140)
    hook, hcy = hook_down(g, W - 2 * (g.V + g.c(90)), W, g.desc - g.os, hh, keep=(232, 360))
    return union(rect(0, 0, g.V, g.xh), inter(a, above(hcy)), rect(W - g.V, hcy, W, cy + 1), hook), W


@glyph("Eng", 0x14A)
def g_Eng(g):
    p, W = g_N(g)
    hh = g.lerp(130, 140)
    hook, hcy = hook_down(g, W - 2 * (g.V + g.c(100)), W, g.desc - g.os, hh, keep=(232, 360))
    return union(p, rect(W - g.V, hcy, W, 10), hook), W


@glyph("kgreenlandic", 0x138, "S", "D")
def g_kgreenlandic(g):
    W = g.V + g.c(290)
    D = g.D
    ax0, ay0 = g.V * 0.5, g.xh * 0.22
    ax1, ay1 = W - D * 0.62, g.xh
    arm = inter(hband(ax0, ay0, ax1, ay1, D, 0, g.xh), rightof(g.V * 0.5))
    f = 0.36
    lx, ly = ax0 + (ax1 - ax0) * f, ay0 + (ay1 - ay0) * f
    leg = hband(lx, ly, W - D * 0.6, 0, D, 0, ly)
    return union(rect(0, 0, g.V, g.xh), arm, leg), W


@glyph("longs", 0x17F, 16, 0)
def g_longs(g):
    s = g.c(20)
    W = s + g.V + g.c(130)
    hh = g.lerp(130, 140)
    x1 = s + 2 * (g.V + g.c(116))
    hook, cy = hook_up(g, s, x1, g.asc + g.os, hh, keep=(52, 180))
    return union(hook, rect(s, 0, s + g.V, cy + 1)), W


@glyph("fermata", 0x1D110, 50, 50)
def g_fermata(g):
    w = g.lerp(640, 680)
    p = fermata_sign(g, w)
    return move(p, w / 2, g.xh * 0.42), w


# ---------------------------------------------------------------- symbols drawn with more care

def brace(g):
    t = g.V * 0.9
    W = t * 1.6 + g.c(150)
    top, bot = g.cap + 90, -g.lerp(150, 140)
    mid = (top + bot) / 2
    xn = W * 0.5                      # centre of the neck
    r = W - xn - 6                    # radius of the turn at the top
    r2 = g.lerp(120, 110)             # length of the turn into the point
    sp = curve((W, top - t / 2), (xn + r * 0.9, top - t / 2),
               [(xn + r * 0.3, top - t / 2), (xn, top - t / 2 - r * 0.4), (xn, top - t / 2 - r)],
               (xn, mid + r2),
               [(xn, mid + r2 * 0.4), (xn - (xn - t * 0.3) * 0.5, mid + t * 0.02), (t * 0.3, mid)])
    half = inter(stroke(sp, t), above(mid))
    half = inter(half, leftof(W))
    return union(half, mirror_y(half, mid)), W


@glyph("braceleft", 0x7B, 30, 20)
def g_braceleft(g):
    return brace(g)


@glyph("braceright", 0x7D, 20, 30)
def g_braceright(g):
    p, W = brace(g)
    return mirror_x(p, W), W


@glyph("ampersand", 0x26, 30, 16)
def g_ampersand(g):
    W = g.c(440) + g.V * 1.35
    t = (g.V + g.H) / 2
    a = t / 2
    h = g.cap + 2 * g.os
    X = lambda u: a + (W - 2 * a) * u
    Y = lambda v: a + (h - 2 * a) * v - g.os
    body = curve((X(0.93), Y(0.47)),
                 [(X(0.87), Y(0.18)), (X(0.68), Y(0.0)), (X(0.42), Y(0.0))],
                 [(X(0.17), Y(0.0)), (X(0.0), Y(0.12)), (X(0.0), Y(0.29))],
                 [(X(0.0), Y(0.45)), (X(0.13), Y(0.53)), (X(0.32), Y(0.63))],
                 [(X(0.50), Y(0.72)), (X(0.61), Y(0.78)), (X(0.61), Y(0.875))],
                 [(X(0.61), Y(0.96)), (X(0.52), Y(1.0)), (X(0.40), Y(1.0))],
                 [(X(0.28), Y(1.0)), (X(0.19), Y(0.95)), (X(0.19), Y(0.86))],
                 [(X(0.19), Y(0.77)), (X(0.26), Y(0.70)), (X(0.34), Y(0.62))])
    leg = hband(X(0.34), Y(0.62), X(1.0) - g.D * 0.2, 0, g.D * 0.94, 0, Y(0.62))
    return inter(union(stroke(body, t), leg), above(0)), W


@glyph("at", 0x40, 34, 34)
def g_at(g):
    t = g.V * 0.84
    H = g.H * 0.9
    W = g.c(660) + t * 2
    bot, top = -g.lerp(160, 150), g.cap + 10
    cy = (bot + top) / 2
    outer = ring(g, 0, bot, W, top, V=t, H=H, k=g.k - 0.02)
    outer = diff(outer, wedge(W / 2, cy, -50, 0))
    iw = W * 0.42
    ix1 = W * 0.5 + iw * 0.44
    ix0 = ix1 - iw
    ib, it = cy - W * 0.23, cy + W * 0.23
    jb = t * g.lerp(0.05, 0.3)
    inner = ring(g, ix0, ib, ix1 - jb, it, V=t, H=H, ext=t * 0.45 + jb)
    xs = ix1 - t / 2
    tail = curve((xs, it), (xs, ib + 90),
                 [(xs, ib - 10), (xs + 30, ib - 30), (xs + 90, ib - 30)],
                 [(W - t / 2 - 40, ib - 30), (W - t / 2, cy - 90), (W - t / 2, cy + 2)])
    return union(outer, inner, stroke(tail, t)), W


@glyph("section", 0xA7, 34, 34)
def g_section(g):
    W = 2 * g.V + g.c(200)
    bot, top = -g.lerp(120, 110), g.cap + g.os
    h = top - bot
    mh = h * 0.44                      # height of the middle loop
    my0 = bot + (h - mh) / 2
    mid = ring(g, 0, my0, W, my0 + mh)
    hh = (top - (my0 + mh - g.H)) / 2  # half height of the hooks
    hook, cy = hook_up(g, W * 0.06, W * 0.94, top, hh, keep=(28, 270))
    lower = rotate180(hook, W / 2, my0 + mh / 2)
    return union(mid, hook, lower), W


@glyph("paragraph", 0xB6, 30, 40)
def g_paragraph(g):
    t = g.V * 0.9
    gap = g.c(70)
    W = g.c(250) + 2 * t + gap
    s2 = W - t                          # left edge of the right stem
    s1 = s2 - gap - t                   # left edge of the left stem
    bot, top = -g.lerp(120, 110), g.cap
    yb = g.cap * 0.36
    bw = s1 + t / 2                     # the bowl reaches into the left stem
    bowl_ = union(inter(sqel(0, yb, 2 * bw, top, g.k), leftof(bw)), rect(bw - 1, yb, s1 + t, top))
    return union(bowl_, rect(s1, bot, s1 + t, top), rect(s2, bot, s2 + t, top), rect(s1, top - g.H, W, top)), W


@glyph("germandbls", 0xDF, "S", "R")
def g_germandbls(g):
    W = g.V * 1.4 + g.c(320)
    a = g.V / 2
    t = (g.V + g.H) / 2
    at = t / 2
    top = g.asc + g.os - at
    sp = curve((a, 0), (a, g.asc * 0.66),
               [(a, g.asc * 0.92), (W * 0.22, top), (W * 0.47, top)],
               [(W * 0.71, top), (W * 0.84, g.asc * 0.9), (W * 0.84, g.asc * 0.78)],
               [(W * 0.84, g.asc * 0.65), (W * 0.62, g.xh * 0.94), (W * 0.5, g.xh * 0.76)],
               [(W * 0.9, g.xh * 0.73), (W - at, g.xh * 0.56), (W - at, g.xh * 0.34)],
               [(W - at, g.xh * 0.1), (W * 0.8, at - g.os), (W * 0.56, at - g.os)],
               (W * 0.36, at - g.os))
    stem = rect(0, 0, g.V, g.asc * 0.66)
    return union(stroke(sp, t), stem), W


@glyph("currency", 0xA4, 30, 30)
def g_currency(g):
    W = g.c(400) + g.V * 0.4
    t = g.V * 0.84
    y = axis(g) + 20
    r = W * 0.3
    o = ring(g, W / 2 - r, y - r, W / 2 + r, y + r, V=t, H=t, k=0.58)
    spokes = skia.Path()
    for a in (45, 135, 225, 315):
        c = math.radians(a)
        spokes = union(spokes, band((W / 2 + r * 0.9 * math.cos(c), y + r * 0.9 * math.sin(c)),
                                    (W / 2 + W * 0.52 * math.cos(c), y + W * 0.52 * math.sin(c)), t))
    return union(o, inter(spokes, rect(0, y - W / 2, W, y + W / 2))), W
