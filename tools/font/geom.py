"""Shape primitives for Fermata Sans, built on Skia paths in font units (y up)."""
import math
import skia

OP = skia.PathOp
BIG = 6000


def rect(x0, y0, x1, y1):
    p = skia.Path()
    p.addRect(skia.Rect(min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1)))
    return p


def rrect(x0, y0, x1, y1, r):
    p = skia.Path()
    p.addRRect(skia.RRect.MakeRectXY(skia.Rect(min(x0, x1), min(y0, y1), max(x0, x1), max(y0, y1)), r, r))
    return p


def poly(pts):
    p = skia.Path()
    p.moveTo(*pts[0])
    for q in pts[1:]:
        p.lineTo(*q)
    p.close()
    return p


def sqel(x0, y0, x1, y1, k=0.6, kt=None):
    """A superellipse-like oval in the box, drawn with cubic quadrants.

    k is the handle length as a fraction of the radius (0.552 is a circle).
    kt, when given, is used for the handles along the top and bottom so they can be flatter than the sides.
    """
    kt = k if kt is None else kt
    cx, cy = (x0 + x1) / 2, (y0 + y1) / 2
    a, b = (x1 - x0) / 2, (y1 - y0) / 2
    p = skia.Path()
    p.moveTo(cx + a, cy)
    p.cubicTo(cx + a, cy + b * k, cx + a * kt, cy + b, cx, cy + b)
    p.cubicTo(cx - a * kt, cy + b, cx - a, cy + b * k, cx - a, cy)
    p.cubicTo(cx - a, cy - b * k, cx - a * kt, cy - b, cx, cy - b)
    p.cubicTo(cx + a * kt, cy - b, cx + a, cy - b * k, cx + a, cy)
    p.close()
    return p


def op(a, b, kind):
    r = skia.Op(a, b, kind)
    if r is None:
        raise RuntimeError("path op failed")
    return r


def union(*ps):
    ps = [p for p in ps if p is not None]
    out = skia.Path()
    for p in ps:
        out = op(out, p, OP.kUnion_PathOp)
    return out


def diff(a, *bs):
    for b in bs:
        a = op(a, b, OP.kDifference_PathOp)
    return a


def inter(a, b):
    return op(a, b, OP.kIntersect_PathOp)


def band(p1, p2, t, ext=0):
    """A straight stroke of perpendicular thickness t along p1-p2, extended by ext at both ends."""
    (x1, y1), (x2, y2) = p1, p2
    L = math.hypot(x2 - x1, y2 - y1)
    dx, dy = (x2 - x1) / L, (y2 - y1) / L
    nx, ny = -dy * t / 2, dx * t / 2
    ax, ay = x1 - dx * ext, y1 - dy * ext
    bx, by = x2 + dx * ext, y2 + dy * ext
    return poly([(ax + nx, ay + ny), (bx + nx, by + ny), (bx - nx, by - ny), (ax - nx, ay - ny)])


def hband(xa, ya, xb, yb, t, y0=None, y1=None):
    """A diagonal stroke between two centre points, cut flat at y0 and y1 (default: the end heights)."""
    lo = min(ya, yb) if y0 is None else y0
    hi = max(ya, yb) if y1 is None else y1
    return inter(band((xa, ya), (xb, yb), t, ext=2000), rect(-BIG, lo, BIG, hi))


def wedge(cx, cy, a1, a2):
    """The angular region from a1 to a2 degrees (counter-clockwise from +x) around a centre."""
    if a2 < a1:
        a2 += 360
    n = max(2, int(math.ceil((a2 - a1) / 20)) + 1)
    pts = [(cx, cy)]
    for i in range(n):
        a = math.radians(a1 + (a2 - a1) * i / (n - 1))
        pts.append((cx + BIG * math.cos(a), cy + BIG * math.sin(a)))
    return poly(pts)


def above(y):
    return rect(-BIG, y, BIG, BIG)


def below(y):
    return rect(-BIG, -BIG, BIG, y)


def leftof(x):
    return rect(-BIG, -BIG, x, BIG)


def rightof(x):
    return rect(x, -BIG, BIG, BIG)


def transform(p, m):
    out = skia.Path()
    p.transform(m, out)
    return out


def move(p, dx, dy=0):
    return transform(p, skia.Matrix.Translate(dx, dy))


def mirror_x(p, width):
    """Mirror left-right within an advance of the given width."""
    return transform(p, skia.Matrix.MakeAll(-1, 0, width, 0, 1, 0, 0, 0, 1))


def mirror_y(p, cy):
    return transform(p, skia.Matrix.MakeAll(1, 0, 0, 0, -1, 2 * cy, 0, 0, 1))


def rotate180(p, cx, cy):
    return transform(p, skia.Matrix.MakeAll(-1, 0, 2 * cx, 0, -1, 2 * cy, 0, 0, 1))


def scale(p, sx, sy=None, ox=0, oy=0):
    sy = sx if sy is None else sy
    return transform(p, skia.Matrix.MakeAll(sx, 0, ox * (1 - sx), 0, sy, oy * (1 - sy), 0, 0, 1))


def skew(p, sx):
    return transform(p, skia.Matrix.MakeAll(1, sx, 0, 0, 1, 0, 0, 0, 1))


def stroke(path, w, cap="butt", join="round"):
    caps = {"butt": skia.Paint.kButt_Cap, "round": skia.Paint.kRound_Cap, "square": skia.Paint.kSquare_Cap}
    joins = {"round": skia.Paint.kRound_Join, "miter": skia.Paint.kMiter_Join, "bevel": skia.Paint.kBevel_Join}
    paint = skia.Paint(Style=skia.Paint.kStroke_Style, StrokeWidth=w, StrokeCap=caps[cap], StrokeJoin=joins[join])
    out = skia.Path()
    paint.getFillPath(path, out, None, 1)
    return union(out)


def curve(*segs):
    """An open path: curve((x,y), [(c1),(c2),(end)], (x,y) line-to, ...)."""
    p = skia.Path()
    p.moveTo(*segs[0])
    for s in segs[1:]:
        if isinstance(s, list):
            (a, b), (c, d), (e, f) = s
            p.cubicTo(a, b, c, d, e, f)
        else:
            p.lineTo(*s)
    return p


def soften(shape, r):
    """Rounds every convex corner of a shape by radius r: shrink by r, then grow back with round joins."""
    shape = union(shape)
    if r <= 0:
        return shape
    edge = stroke(shape, 2 * r)
    core = diff(shape, edge)
    return union(core, stroke(core, 2 * r))


def bounds(p):
    b = p.computeTightBounds()
    return b.left(), b.top(), b.right(), b.bottom()
