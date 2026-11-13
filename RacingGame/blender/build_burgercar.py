# Builds the burger car (the sixth car) from blender/burgercar_source.png, a cartoon of a burger-shaped
# car with two characters riding in it:
#
#   python3 RacingGame/blender/build_burgercar.py        (needs Pillow + numpy)
#
# The burger itself is modelled here from simple shapes (bottom bun, patty, cheese, lettuce, a ring-shaped
# top bun with sesame seeds that the riders sit in, two little decorative wheels, a licence plate cut out
# of the picture, a steering wheel and a flag on a pole). The two characters and the flag's pennant are
# cut out of the picture and "inflated" into 3D: the further a point is from the outline, the thicker
# the body is there, so their outlines round over like a plush toy. The front shows the picture; the
# back gets a copy with the faces painted over.
#
# Output (RacingGame/assets/models/):
#   burgercar.png       texture atlas for the whole car
#   burgercar.bin       the mesh: int32 vertex count, int32 index count, float32 positions (xyz),
#                       normals (xyz), texture coordinates (uv), uint32 triangle indices
#   burgercar.json      sizes, wheel placement and the ray-tracing proxy boxes
#   burgercar_icon.png  the picture without its speed lines and shadow, for the menu
#
# Model space matches the other cars: metres, x forward, y up, z right, ground at y = 0, origin halfway
# between the axles.

import json
import os
import struct
import numpy as np
from PIL import Image, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "assets", "models"))
rng = np.random.default_rng(7)

src_img = Image.open(os.path.join(HERE, "burgercar_source.png")).convert("RGBA")
src = np.asarray(src_img).astype(np.float64)
H, W = src.shape[:2]
rgb, alpha = src[..., :3], src[..., 3]
hsv = np.asarray(src_img.convert("RGB").convert("HSV")).astype(np.float64)
hue, sat, val = hsv[..., 0] * 360 / 255, hsv[..., 1] / 255, hsv[..., 2] / 255
yy, xx = np.mgrid[0:H, 0:W]

# ---------------------------------------------------------------- sizes (metres)

A, B = 2.2, 1.6              # burger: half length and half width (it's an oval seen from above)
HA, HB = 1.15, 1.08          # the hole in the top bun the riders sit in
WHEEL_R, WHEEL_W = 0.34, 0.30
WHEELBASE, TRACK = 2.6, 2.4
FLOOR_Y = 1.45               # the seat inside the top bun
RIDER_Y = 1.40               # where the bottom of the riders' cut-outs sits (hidden under the seat)
MPP = 0.0038                 # metres per picture pixel for the characters and the pennant
PICTURE_MID = 985            # picture x straight above the car's centre line

# ---------------------------------------------------------------- picture helpers


def poly(points):
    m = Image.new("L", (W, H), 0)
    ImageDraw.Draw(m).polygon(points, fill=255)
    return np.asarray(m) > 0


def dilate(m):
    o = m.copy()
    o[1:] |= m[:-1]; o[:-1] |= m[1:]; o[:, 1:] |= m[:, :-1]; o[:, :-1] |= m[:, 1:]
    return o


def erode(m):
    return ~dilate(~m)


def grow_from(seed_xy, m):
    """The connected part of mask `m` containing the seed pixel."""
    cur = np.zeros_like(m)
    cur[seed_xy[1], seed_xy[0]] = True
    cur &= m
    while True:
        nxt = dilate(cur) & m
        if (nxt == cur).all():
            return cur
        cur = nxt


def fill_holes(m):
    outside = np.zeros_like(m)
    outside[0, :] = outside[-1, :] = True
    outside[:, 0] = outside[:, -1] = True
    outside &= ~m
    while True:
        nxt = dilate(outside) & ~m
        if (nxt == outside).all():
            return ~outside
        outside = nxt


def push_pull(colour, known):
    """Fill the unknown pixels with a smooth blend of the known ones (a pyramid fill)."""
    if min(known.shape) <= 2 or known.all():
        return np.where(known[..., None], colour, (colour * known[..., None]).sum((0, 1)) / max(known.sum(), 1))
    h2, w2 = (known.shape[0] + 1) // 2, (known.shape[1] + 1) // 2
    pc = np.zeros((h2 * 2, w2 * 2, 3)); pk = np.zeros((h2 * 2, w2 * 2))
    pc[:known.shape[0], :known.shape[1]] = colour * known[..., None]
    pk[:known.shape[0], :known.shape[1]] = known
    sc = pc.reshape(h2, 2, w2, 2, 3).sum((1, 3)); sk = pk.reshape(h2, 2, w2, 2).sum((1, 3))
    coarse = push_pull(sc / np.maximum(sk, 1e-6)[..., None], sk > 0)
    up = np.stack([np.asarray(Image.fromarray(coarse[..., ch].astype(np.float32), "F")
                              .resize((w2 * 2, h2 * 2), Image.BILINEAR)) for ch in range(3)], -1)
    return np.where(known[..., None], colour, up[:known.shape[0], :known.shape[1]])


opaque = alpha > 200
blue = (hue > 185) & (hue < 235) & (sat > 0.2)                       # the flag pole
bread = (hue > 17) & (hue < 47) & (sat > 0.33) & (val > 0.35)         # bun, its brown rim and the seat

# Patrick-like starfish on the left. A white spatula (held by the other rider) crosses his side.
star_area = poly([(540, 285), (612, 285), (640, 60), (800, 60), (805, 150), (855, 150), (860, 60), (958, 60),
                  (958, 330), (990, 355), (1000, 452), (1003, 600), (700, 600), (700, 445), (540, 380)])
star_cover = poly([(972, 352), (1010, 352), (1010, 440), (985, 440)]) & star_area
star = grow_from((850, 450), star_area & opaque & ~blue & ~bread) | star_cover
star = fill_holes(star)
star_known = star & ~star_cover

# The sponge-like rider on the right. Its lower left corner is hidden behind the starfish: square it off
# and fill it in with the sponge's colours.
sponge_area = poly([(955, 160), (1290, 160), (1330, 55), (1425, 55), (1425, 160), (1300, 320), (1245, 330),
                    (1245, 400), (1212, 440), (1212, 525), (1135, 525), (1135, 530), (1072, 530), (1072, 525),
                    (1000, 525), (1000, 452), (966, 452), (966, 320), (955, 320)])
sponge_cover = poly([(966, 318), (1004, 318), (1004, 452), (966, 452)])
pinkish = ((hue < 20) | (hue > 330)) & (sat > 0.15)
sponge = grow_from((1150, 300), (sponge_area & opaque & ~(bread & (val > 0.75)) & ~(pinkish & (xx < 1004))) | sponge_cover)
sponge = fill_holes(sponge)
yellowish = (hue > 42) & (hue < 80) & (sat > 0.3)
sponge_known = sponge & (~sponge_cover | yellowish)
sponge_rgb = rgb

# The pennant at the top of the flag pole
pennant = grow_from((450, 110), poly([(320, 40), (585, 40), (585, 220), (320, 220)]) & opaque & ~blue)
pennant = fill_holes(pennant)

# ---------------------------------------------------------------- atlas

ATLAS = 2048
atlas = np.zeros((ATLAS, ATLAS, 3))
atlas[:] = (228, 162, 89)


def put(img, x, y):
    h, w = img.shape[:2]
    atlas[y:y + h, x:x + w] = img
    return (x, y, w, h)


def uv_of(region, px, py):
    """Atlas texture coordinates for pixel (px, py) inside a region placed with put()."""
    x, y, _, _ = region
    return np.stack([(x + px) / ATLAS, (y + py) / ATLAS], -1)


# Flat colours: a small square each
SOLIDS = {
    "tyre": (128, 136, 142), "hub": (176, 222, 246), "seat": (128, 70, 30), "pole": (126, 172, 222),
    "steering": (98, 104, 114), "black": (24, 22, 24), "plate": (240, 240, 236),
}
solid_uv = {}
for i, (name, c) in enumerate(SOLIDS.items()):
    x, y = 1600 + (i % 6) * 64, 600 + (i // 6) * 64
    atlas[y:y + 48, x:x + 48] = c
    solid_uv[name] = ((x + 24) / ATLAS, (y + 24) / ATLAS)


def supersampled(w, h, draw, scale=4, bg=(0, 0, 0)):
    im = Image.new("RGB", (w * scale, h * scale), bg)
    draw(ImageDraw.Draw(im), scale)
    return np.asarray(im.resize((w, h), Image.LANCZOS)).astype(np.float64)


# Top bun: u goes round the burger, v round the bun's cross-section (0 = underneath, 0.25 = outer
# side, 0.5 = top, 0.75 = inner side). Sesame seeds on the top and outer side.
BUN = np.array([228, 162, 89.0])
BUN_W, BUN_H = 2048, 384
SEED_MM_U, SEED_MM_V = 4.6, 7.0   # roughly how many mm one texel covers on the top of the bun


def draw_bun(d, s):
    d.rectangle([0, 0, BUN_W * s, BUN_H * s], fill=tuple(int(c) for c in BUN))
    for _ in range(75):
        u, v = rng.uniform(0, BUN_W), rng.uniform(0.13, 0.7) * BUN_H
        ang = rng.uniform(0, np.pi)
        a, b = 0.065 * rng.uniform(0.85, 1.15), 0.032
        t = np.linspace(0, 2 * np.pi, 24)
        ex, ey = a * np.cos(t), b * np.sin(t)
        px = (ex * np.cos(ang) - ey * np.sin(ang)) * 1000 / SEED_MM_U
        py = (ex * np.sin(ang) + ey * np.cos(ang)) * 1000 / SEED_MM_V
        for shift in (-BUN_W, 0, BUN_W):
            d.polygon([((u + shift + qx) * s, (v + qy) * s) for qx, qy in zip(px, py)], fill=(248, 232, 150))


bun_tex = supersampled(BUN_W, BUN_H, draw_bun)
v = np.linspace(0, 1, BUN_H)[:, None, None]
# darker underneath and on the inner side (the bread's inside), a brown line along the outer bottom edge
shade = 1 - 0.28 * np.clip(1 - np.abs(v - 0.0) / 0.12, 0, 1) - 0.28 * np.clip(1 - np.abs(v - 1.0) / 0.12, 0, 1) \
    - 0.22 * np.clip(1 - np.abs(v - 0.78) / 0.1, 0, 1)
bun_tex = bun_tex * shade
edge = np.clip(1 - np.abs(v - 0.13) / 0.012, 0, 1)
bun_tex = bun_tex * (1 - edge) + np.array([150, 86, 38]) * edge
bun_region = put(bun_tex, 0, 0)

# Patty: dark red with lighter curved streaks
PATTY_W, PATTY_H = 1024, 160


def draw_patty(d, s):
    d.rectangle([0, 0, PATTY_W * s, PATTY_H * s], fill=(176, 50, 40))
    for _ in range(46):
        cx, cy = rng.uniform(0, PATTY_W), rng.uniform(0.3, 0.75) * PATTY_H
        r = rng.uniform(14, 26)
        for shift in (-PATTY_W, 0, PATTY_W):
            box = [(cx + shift - r) * s, (cy - r * 0.55) * s, (cx + shift + r) * s, (cy + r * 0.55) * s]
            d.arc(box, 200, 340, fill=(238, 128, 112), width=int(3 * s))


patty_region = put(supersampled(PATTY_W, PATTY_H, draw_patty), 0, 400)

# Bottom bun: plain, with a few little pores
BBUN_W, BBUN_H = 1000, 160


def draw_bottom(d, s):
    d.rectangle([0, 0, BBUN_W * s, BBUN_H * s], fill=tuple(int(c) for c in BUN))
    for _ in range(40):
        cx, cy = rng.uniform(0, BBUN_W), rng.uniform(0.35, 0.65) * BBUN_H
        r = rng.uniform(2.5, 4)
        d.ellipse([(cx - r) * s, (cy - r * 0.7) * s, (cx + r) * s, (cy + r * 0.7) * s], outline=(150, 90, 45), width=int(1.2 * s))


bottom_tex = supersampled(BBUN_W, BBUN_H, draw_bottom)
v = np.linspace(0, 1, BBUN_H)[:, None, None]
bottom_tex *= 1 - 0.25 * np.clip(1 - v / 0.25, 0, 1)
bottom_region = put(bottom_tex, 1040, 400)

# Lettuce: green, darker towards its frilly edge (v = 1)
v = np.linspace(0, 1, 96)[:, None, None]
lettuce = np.array([168, 192, 44.0]) * np.ones((96, 1024, 1))
lettuce = lettuce * (1 - 0.25 * np.clip((v - 0.6) / 0.3, 0, 1))
rim = np.clip(1 - np.abs(v - 0.96) / 0.05, 0, 1)
lettuce = lettuce * (1 - rim) + np.array([96, 120, 22]) * rim
lettuce_region = put(lettuce, 0, 580)

# Cheese: a square slice, darker along its edges
s = np.abs(np.linspace(-1, 1, 128))
cheese_edge = np.clip((np.maximum(s[:, None], s[None, :]) - 0.88) / 0.1, 0, 1)[..., None]
cheese = np.array([247, 236, 150.0]) * (1 - cheese_edge) + np.array([214, 190, 80.0]) * cheese_edge
cheese_region = put(cheese, 1040, 580)

# Licence plate: cut out of the picture and straightened
plate = np.asarray(src_img.convert("RGB").transform((256, 140), Image.QUAD,
                   (1043, 948, 1052, 1037, 1182, 1003, 1172, 925), Image.BICUBIC)).astype(np.float64)
plate_region = put(plate, 1200, 580)


# ---------------------------------------------------------------- mesh building

class Mesh:
    def __init__(self):
        self.pos, self.nrm, self.uv, self.idx = [], [], [], []
        self.count = 0

    def add(self, P, N, T, tris):
        self.pos.append(np.asarray(P, np.float64).reshape(-1, 3))
        self.nrm.append(np.asarray(N, np.float64).reshape(-1, 3))
        self.uv.append(np.asarray(T, np.float64).reshape(-1, 2))
        self.idx.append(np.asarray(tris, np.int64) + self.count)
        self.count += self.pos[-1].shape[0]


mesh = Mesh()


def normalise(v):
    n = np.linalg.norm(v, axis=-1, keepdims=True)
    return v / np.maximum(n, 1e-12)


def surface(P, T, wrap_u=False, wrap_v=False, outward=None, double=False):
    """A grid of points P[u, v] (with texture coordinates T[u, v]) as triangles. With wrap, the last row
    joins the first (P holds no duplicate; T is given with one extra row/column for the seam).
    outward = (u index, v index, direction) says which way that spot's surface faces."""
    nu, nv = P.shape[:2]
    def d(axis, wrap):
        if wrap:
            return np.roll(P, -1, axis) - np.roll(P, 1, axis)
        g = np.gradient(P, axis=axis)
        return g
    du, dv = d(0, wrap_u), d(1, wrap_v)
    N = np.cross(du, dv)
    ln = np.linalg.norm(N, axis=-1)
    # collapsed rows (the poles of a dome): use the neighbouring row's average direction
    for j in range(nv):
        if (ln[:, j] < 1e-9).all():
            k = j + 1 if j + 1 < nv else j - 1
            avg = normalise(N[:, k]).mean(0)
            N[:, j] = avg
    N = normalise(N)
    sign = 1
    if outward is not None:
        i, j, direction = outward
        sign = 1 if np.dot(N[i, j], direction) > 0 else -1
    N = N * sign
    # seam copies
    if wrap_u:
        P = np.concatenate([P, P[:1]], 0); N = np.concatenate([N, N[:1]], 0)
    if wrap_v:
        P = np.concatenate([P, P[:, :1]], 1); N = np.concatenate([N, N[:, :1]], 1)
    nu2, nv2 = P.shape[:2]
    ii, jj = np.mgrid[0:nu2 - 1, 0:nv2 - 1]
    a = ii * nv2 + jj; b = (ii + 1) * nv2 + jj; c = (ii + 1) * nv2 + jj + 1; e = ii * nv2 + jj + 1
    if sign > 0:
        tris = np.concatenate([np.stack([a, b, c], -1).reshape(-1, 3), np.stack([a, c, e], -1).reshape(-1, 3)])
    else:
        tris = np.concatenate([np.stack([a, c, b], -1).reshape(-1, 3), np.stack([a, e, c], -1).reshape(-1, 3)])
    mesh.add(P, N, T, tris)
    if double:
        mesh.add(P, -N, T, tris[:, [0, 2, 1]])


def solid_T(shape, name):
    return np.broadcast_to(np.array(solid_uv[name]), shape + (2,)).copy()


def region_T(region, u, v):
    """Texture coordinates for (u, v) in 0..1 across an atlas region (kept half a texel inside)."""
    x, y, w, h = region
    u, v = np.broadcast_arrays(u, v)
    return np.stack([(x + 0.5 + u * (w - 1)) / ATLAS, (y + 0.5 + v * (h - 1)) / ATLAS], -1)


def superellipse(t, p):
    """cos/sin of angle t pushed towards a rounded square."""
    c, s = np.cos(t), np.sin(t)
    return np.sign(c) * np.abs(c) ** (2 / p), np.sign(s) * np.abs(s) ** (2 / p)


def arc_u(theta):
    """Fraction of the way round the burger's outline for ellipse angle theta (even spacing)."""
    t = np.linspace(0, 2 * np.pi, 2001)
    seg = np.hypot(np.diff(A * np.cos(t)), np.diff(B * np.sin(t)))
    acc = np.concatenate([[0], np.cumsum(seg)]) / seg.sum()
    return np.interp(theta, t, acc)


NT = 160
theta = np.linspace(0, 2 * np.pi, NT, endpoint=False)
theta_T = np.linspace(0, 2 * np.pi, NT + 1)
cos_t, sin_t = np.cos(theta), np.sin(theta)

# Bottom bun: a rounded puck
NV = 28
phi = np.linspace(0, np.pi, NV)                     # 0 = bottom centre, pi = top centre
cc, ss = superellipse(phi - np.pi / 2, 2.8)          # cc: radius fraction, ss: -1 bottom .. 1 top
rho = np.abs(cc)
yb = 0.44 + 0.24 * ss
P = np.stack([A * rho[None] * cos_t[:, None], np.broadcast_to(yb, (NT, NV)), B * rho[None] * sin_t[:, None]], -1)
T = region_T(bottom_region, arc_u(theta_T)[:, None], np.linspace(0, 1, NV)[None])
surface(P, T, wrap_u=True, outward=(0, NV // 2, (1, 0, 0)))

# Patty: a lumpy puck
phi = np.linspace(0, np.pi, 20)
cc, ss = superellipse(phi - np.pi / 2, 3.2)
lump = 1.035 + 0.022 * np.sin(11 * theta) + 0.012 * np.sin(27 * theta + 2)
P = np.stack([A * lump[:, None] * np.abs(cc)[None] * cos_t[:, None],
              np.broadcast_to(0.77 + 0.15 * ss, (NT, 20)),
              B * lump[:, None] * np.abs(cc)[None] * sin_t[:, None]], -1)
P[..., 1] += 0.015 * np.sin(17 * theta)[:, None] * np.abs(cc)[None]
T = region_T(patty_region, arc_u(theta_T)[:, None], np.linspace(0, 1, 20)[None])
surface(P, T, wrap_u=True, outward=(0, 10, (1, 0, 0)))

# Cheese: a square slice turned a little, its corners hanging down over the patty
n = 40
s_, t_ = np.meshgrid(np.linspace(-1, 1, n), np.linspace(-1, 1, n), indexing="ij")
rot = 0.33
nx = 0.9 * (s_ * np.cos(rot) - t_ * np.sin(rot))
nz = 0.9 * (s_ * np.sin(rot) + t_ * np.cos(rot))
r_ = np.hypot(nx, nz)
droop = 0.9 * np.clip(r_ - 0.97, 0, None) ** 1.1
P = np.stack([A * nx, 0.93 - droop, B * nz], -1)
T = region_T(cheese_region, (s_ + 1) / 2, (t_ + 1) / 2)
surface(P, T, outward=(n // 2, n // 2, (0, 1, 0)), double=True)

# Lettuce: a frilly ring sticking out from under the top bun
NL = 320
th_l = np.linspace(0, 2 * np.pi, NL, endpoint=False)
edge_r = 1.10 + 0.04 * np.sin(13 * th_l) + 0.02 * np.sin(29 * th_l + 1)
k = np.linspace(0, 1, 8)
r_ = 0.8 + (edge_r[:, None] - 0.8) * k[None]
out = np.clip(r_ - 1.0, 0, None)
yl = 0.975 - 0.9 * out ** 1.2 + 0.035 * np.sin(13 * th_l + 0.8)[:, None] * k[None] ** 2
P = np.stack([A * r_ * np.cos(th_l)[:, None], yl, B * r_ * np.sin(th_l)[:, None]], -1)
T = region_T(lettuce_region, arc_u(np.linspace(0, 2 * np.pi, NL + 1))[:, None], k[None])
surface(P, T, wrap_u=True, outward=(0, 4, (0, 1, 0)), double=True)


# Top bun: a ring. Its cross-section runs from the hole's edge (u = 0) to the outside (u = 1), round
# the loop: outer side, top, inner side, underneath.
def top_bun(th, ph):
    cu, sy = superellipse(ph, 2.5)
    u = 0.5 + 0.55 * cu
    y = 1.25 + np.where(sy > 0, 0.37, 0.30) * sy
    inner = np.stack([HA * np.cos(th), HB * np.sin(th)], -1)
    outer = np.stack([A * np.cos(th), B * np.sin(th)], -1)
    xz = inner + (outer - inner) * u[..., None]
    return np.stack([xz[..., 0], y, xz[..., 1]], -1)


NP = 48
ph = np.linspace(-np.pi / 2, 3 * np.pi / 2, NP, endpoint=False)   # starts underneath, then the outer side
TH, PH = np.meshgrid(theta, ph, indexing="ij")
P = top_bun(TH, PH)
T = region_T(bun_region, arc_u(theta_T)[:, None], np.linspace(0, 1, NP + 1)[None])
surface(P, T, wrap_u=True, wrap_v=True, outward=(0, NP // 2, (0, 1, 0)))

# The seat inside the ring
k = np.linspace(0, 1, 8)
P = np.stack([HA * k[None] * cos_t[:, None], np.full((NT, 8), FLOOR_Y), HB * k[None] * sin_t[:, None]], -1)
surface(P, solid_T((NT + 1, 8), "seat"), wrap_u=True, outward=(0, 4, (0, 1, 0)))


def cylinder(p0, p1, r0, r1, colour, segments=24, caps=True):
    axis = np.asarray(p1, float) - np.asarray(p0, float)
    a = normalise(axis)
    side = normalise(np.cross(a, [0, 1, 0] if abs(a[1]) < 0.9 else [1, 0, 0]))
    up = np.cross(side, a)
    t = np.linspace(0, 2 * np.pi, segments, endpoint=False)
    ring = np.cos(t)[:, None] * side + np.sin(t)[:, None] * up
    P = np.stack([np.asarray(p0) + ring * r0, np.asarray(p1) + ring * r1], 1)
    surface(P, solid_T((segments + 1, 2), colour), wrap_u=True, outward=(0, 0, ring[0]))
    if caps:
        disc(p1, a, 0, r1, colour, segments)
        disc(p0, -a, 0, r0, colour, segments)


def disc(centre, normal, r_in, r_out, colour, segments=32):
    nrm = normalise(np.asarray(normal, float))
    side = normalise(np.cross(nrm, [0, 1, 0] if abs(nrm[1]) < 0.9 else [1, 0, 0]))
    up = np.cross(nrm, side)
    t = np.linspace(0, 2 * np.pi, segments, endpoint=False)
    ring = np.cos(t)[:, None] * side + np.sin(t)[:, None] * up
    P = np.stack([np.asarray(centre) + ring * r_in, np.asarray(centre) + ring * r_out], 1)
    surface(P, solid_T((segments + 1, 2), colour), wrap_u=True, outward=(0, 0, nrm))


# Two little cartoon wheels stuck on the front corners of the top bun
for side in (-1, 1):
    th0, ph0 = side * 0.62, 0.32
    p = top_bun(np.array(th0), np.array(ph0))
    e = 1e-3
    du = top_bun(np.array(th0 + e), np.array(ph0)) - top_bun(np.array(th0 - e), np.array(ph0))
    dv = top_bun(np.array(th0), np.array(ph0 + e)) - top_bun(np.array(th0), np.array(ph0 - e))
    nrm = normalise(np.cross(du, dv))
    if nrm[0] < 0:
        nrm = -nrm
    cylinder(p - nrm * 0.1, p + nrm * 0.07, 0.2, 0.2, "tyre", caps=False)
    disc(p + nrm * 0.07, nrm, 0.115, 0.2, "tyre")
    disc(p + nrm * 0.07, nrm, 0.10, 0.115, "black")
    disc(p + nrm * 0.075, nrm, 0.0, 0.10, "hub")

# Licence plate on the front of the bottom bun, with a black frame behind it
px = A * 0.985
plate_y, plate_hw, plate_hh = 0.42, 0.25, 0.135
k = np.linspace(0, 1, 2)
U, V = np.meshgrid(k, k, indexing="ij")
P = np.stack([np.full_like(U, px + 0.04), plate_y + plate_hh - V * 2 * plate_hh, plate_hw - U * 2 * plate_hw], -1)
surface(P, region_T(plate_region, U, V), outward=(0, 0, (1, 0, 0)))
P2 = P.copy(); P2[..., 0] -= 0.01; P2[..., 1] = plate_y + (P2[..., 1] - plate_y) * 1.12; P2[..., 2] *= 1.07
surface(P2, solid_T((2, 2), "black"), outward=(0, 0, (1, 0, 0)))

# Steering wheel in front of the driver, and its column
WHEEL_C = np.array([0.58, 1.66, -0.44])
w_axis = normalise(np.array([-1.0, 0.9, 0]))
w_side = np.array([0, 0, 1.0])
w_up = np.cross(w_axis, w_side)
t = np.linspace(0, 2 * np.pi, 40, endpoint=False)
q = np.linspace(0, 2 * np.pi, 10, endpoint=False)
ring = np.cos(t)[:, None] * w_side + np.sin(t)[:, None] * w_up
P = WHEEL_C + ring[:, None] * (0.19 + 0.032 * np.cos(q))[None, :, None] + w_axis * (0.032 * np.sin(q))[None, :, None]
surface(P, solid_T((41, 11), "steering"), wrap_u=True, wrap_v=True, outward=(0, 0, ring[0]))
cylinder(WHEEL_C, WHEEL_C + np.array([0.35, -0.3, 0]), 0.03, 0.03, "steering", 10)

# Flag pole behind the starfish
POLE_BASE, POLE_TOP = np.array([-0.85, 1.35, 0.72]), np.array([-1.05, 3.6, 0.72])
cylinder(POLE_BASE, POLE_TOP, 0.032, 0.026, "pole", 12)
cylinder(POLE_TOP, POLE_TOP + [0, 0.05, 0], 0.045, 0.045, "pole", 12)


# ---------------------------------------------------------------- inflated cut-outs

def ball_height(points, inside_pts, D_inside):
    """Height of the union of balls: each inside point carries a ball as big as its distance to the
    outline; this is how far the union of those balls rises above each point."""
    h = np.zeros(len(points))
    sel = D_inside > 1
    q, r2 = inside_pts[sel], D_inside[sel] ** 2
    for i in range(0, len(points), 400):
        p = points[i:i + 400]
        d2 = ((p[:, None, :] - q[None, :, :]) ** 2).sum(-1)
        h[i:i + 400] = np.sqrt(np.clip(r2[None] - d2, 0, None).max(1))
    return h


def inflate(mask, step, thickness, place, front_T, back_T, outward_x=True):
    """Turns a picture mask into a closed puffy shape: a grid over the mask, points outside snapped onto
    the outline. thickness(D, ball) gives the half thickness in pixels; place(px, py, t) the 3D position."""
    ys, xs = np.where(mask)
    y0, y1, x0, x1 = ys.min() - step, ys.max() + step, xs.min() - step, xs.max() + step
    gx = np.arange(x0, x1 + 1, step); gy = np.arange(y0, y1 + 1, step)
    GX, GY = np.meshgrid(gx, gy, indexing="ij")
    inside = mask[np.clip(GY, 0, H - 1), np.clip(GX, 0, W - 1)]
    border = mask & ~erode(mask)
    by, bx = np.where(border)
    bpts = np.stack([bx, by], -1).astype(np.float64)

    pts = np.stack([GX.ravel(), GY.ravel()], -1).astype(np.float64)
    # distance to the outline, and the nearest outline pixel
    D = np.zeros(len(pts)); near = np.zeros((len(pts), 2))
    for i in range(0, len(pts), 1000):
        d2 = ((pts[i:i + 1000, None, :] - bpts[None]) ** 2).sum(-1)
        j = d2.argmin(1)
        D[i:i + 1000] = np.sqrt(d2[np.arange(len(j)), j]); near[i:i + 1000] = bpts[j]
    ins = inside.ravel()
    D[~ins] = 0
    # cells with any corner inside; their outside corners move onto the outline
    cell_in = inside[:-1, :-1] | inside[1:, :-1] | inside[:-1, 1:] | inside[1:, 1:]
    used = np.zeros_like(inside)
    used[:-1, :-1] |= cell_in; used[1:, :-1] |= cell_in; used[:-1, 1:] |= cell_in; used[1:, 1:] |= cell_in
    usedf = used.ravel()
    P2 = np.where(ins[:, None], pts, near)
    ball = ball_height(P2, pts[ins], D[ins])
    t = np.where(ins, thickness(D, ball), 0)

    order = -np.ones(len(pts), np.int64)
    order[usedf] = np.arange(usedf.sum())
    ny = len(gy)
    ci, cj = np.where(cell_in)
    a = ci * ny + cj; b = (ci + 1) * ny + cj; c = (ci + 1) * ny + cj + 1; e = ci * ny + cj + 1
    quads = order[np.stack([a, b, c, e], -1)]
    tris = np.concatenate([quads[:, [0, 1, 2]], quads[:, [0, 2, 3]]])
    Pu, tu = P2[usedf], t[usedf]
    for front in (True, False):
        P3 = place(Pu[:, 0], Pu[:, 1], tu if front else -tu)
        T = (front_T if front else back_T)(Pu[:, 0], Pu[:, 1])
        tr = tris.copy()
        fn = np.cross(P3[tr[:, 1]] - P3[tr[:, 0]], P3[tr[:, 2]] - P3[tr[:, 0]])
        # the front faces forwards (or right, for the pennant), the back the other way
        axis = np.array([1.0, 0, 0]) if outward_x else np.array([0, 0, 1.0])
        if (fn @ axis).sum() * (1 if front else -1) < 0:
            tr = tr[:, [0, 2, 1]]
            fn = -fn
        N = np.zeros_like(P3)
        for k in range(3):
            np.add.at(N, tr[:, k], fn)
        N = normalise(N)
        mesh.add(P3, N, T, tr)
    return Pu, tu


def character_texture(mask, known, colour, x_at, y_at, pad=10):
    ys, xs = np.where(mask)
    x0, x1, y0, y1 = xs.min() - pad, xs.max() + pad, ys.min() - pad, ys.max() + pad
    crop_known = known[y0:y1 + 1, x0:x1 + 1]
    # a few pixels in, away from the anti-aliased edge
    k = np.asarray(Image.fromarray((crop_known * 255).astype(np.uint8)).filter(ImageFilter.MinFilter(3))) > 0
    filled = push_pull(colour[y0:y1 + 1, x0:x1 + 1], k)
    region = put(filled, x_at, y_at)
    return region, (x0, y0), filled


def picture_T(region, origin):
    return lambda px, py: uv_of(region, px - origin[0] + 0.5, py - origin[1] + 0.5)


star_region, star_origin, star_tex = character_texture(star, star_known, rgb, 0, 740)
sponge_region, sponge_origin, sponge_tex = character_texture(sponge, sponge_known, sponge_rgb, 1000, 740)

# Backs: the same picture with the faces painted over in body colour (and holes on the sponge)
def back_of(tex, origin, face_poly, body, spots=None):
    t = tex.copy()
    fm = np.asarray(_poly_local(face_poly, origin, t.shape)) > 0
    t[fm] = body
    if spots is not None:
        ys, xs = np.where(fm)
        im = Image.fromarray(t.clip(0, 255).astype(np.uint8))
        d = ImageDraw.Draw(im)
        for _ in range(spots[1]):
            i = rng.integers(len(xs))
            r = rng.uniform(6, 14)
            d.ellipse([xs[i] - r, ys[i] - r * 0.8, xs[i] + r, ys[i] + r * 0.8], fill=spots[0])
        t = np.asarray(im).astype(np.float64)
        t[~fm] = tex[~fm]
    return np.asarray(Image.fromarray(t.clip(0, 255).astype(np.uint8)).filter(ImageFilter.GaussianBlur(1))).astype(np.float64) * fm[..., None] + t * ~fm[..., None]


def _poly_local(points, origin, shape):
    m = Image.new("L", (shape[1], shape[0]), 0)
    ImageDraw.Draw(m).polygon([(x - origin[0], y - origin[1]) for x, y in points], fill=255)
    return m


star_pink = np.median(rgb[star & pinkish & (val > 0.85)], axis=0)
sponge_yellow = np.median(rgb[sponge & (hue > 48) & (hue < 62) & (sat > 0.4) & (val > 0.9)], axis=0)
sponge_hole = np.median(rgb[sponge & (hue > 55) & (hue < 75) & (sat > 0.35) & (val > 0.5) & (val < 0.8)], axis=0)
star_back = back_of(star_tex, star_origin,
                    [(700, 165), (760, 140), (815, 170), (850, 215), (872, 260), (868, 340), (845, 420), (745, 420), (700, 330), (690, 230)], star_pink)
sponge_back = back_of(sponge_tex, sponge_origin,
                      [(985, 190), (1255, 190), (1255, 450), (985, 450)], sponge_yellow, (tuple(int(c) for c in sponge_hole), 26))
star_back_region = put(star_back, 500, 740)
sponge_back_region = put(sponge_back, 1500, 740)

pen_region, pen_origin, _ = character_texture(pennant, pennant, rgb, 0, 1320)


def char_place(base_py, x_mid):
    return lambda px, py, t: np.stack([x_mid + t * MPP, RIDER_Y + (base_py - py) * MPP, -(px - PICTURE_MID) * MPP], -1)


# Starfish: plump, round all over (a bit flatter than a ball)
star_pts, star_t = inflate(star, 5, lambda D, ball: np.minimum(0.72 * ball, 95), char_place(600, -0.2),
                           picture_T(star_region, star_origin), picture_T(star_back_region, star_origin))
# Sponge: a slab with rounded edges (thin arms stay round)
sponge_pts, sponge_t = inflate(sponge, 5, lambda D, ball: np.minimum(ball, 58 * np.sqrt(1 - (1 - np.clip(D / 18, 0, 1)) ** 2)),
                               char_place(545, 0.15),
                               picture_T(sponge_region, sponge_origin), picture_T(sponge_back_region, sponge_origin))
# Pennant: thin, flying backwards from the top of the pole
PEN_ATTACH = (568, 58)
inflate(pennant, 4, lambda D, ball: np.minimum(ball, 3.5),
        lambda px, py, t: np.stack([POLE_TOP[0] - (PEN_ATTACH[0] - px) * MPP, POLE_TOP[1] - 0.02 - (py - PEN_ATTACH[1]) * MPP,
                                    np.full_like(px, POLE_TOP[2]) + t * MPP], -1),
        picture_T(pen_region, pen_origin), picture_T(pen_region, pen_origin), outward_x=False)

# ---------------------------------------------------------------- write out

Image.fromarray(atlas.clip(0, 255).astype(np.uint8)).save(os.path.join(OUT, "burgercar.png"))

pos = np.concatenate(mesh.pos).astype(np.float32)
nrm = np.concatenate(mesh.nrm).astype(np.float32)
uv = np.concatenate(mesh.uv).astype(np.float32)
idx = np.concatenate(mesh.idx).astype(np.uint32).ravel()
with open(os.path.join(OUT, "burgercar.bin"), "wb") as f:
    f.write(struct.pack("<ii", len(pos), len(idx)))
    f.write(pos.tobytes()); f.write(nrm.tobytes()); f.write(uv.tobytes()); f.write(idx.tobytes())


# Ray-tracing proxy: boxes that sit inside the body (centre, half size, colour)
def box(x0, x1, y0, y1, z0, z1, colour):
    return {"centre": [(x0 + x1) / 2, (y0 + y1) / 2, (z0 + z1) / 2], "half": [(x1 - x0) / 2, (y1 - y0) / 2, (z1 - z0) / 2],
            "colour": [int(c) for c in colour]}


def char_box(pts, t, x_mid, base_py, colour, min_t):
    """The biggest-ish box inside a character: where it is at least min_t pixels thick."""
    sel = t >= min_t
    xs, ys = pts[sel, 0], pts[sel, 1]
    lo_x, hi_x = np.percentile(xs, [15, 85]); lo_y, hi_y = np.percentile(ys, [20, 80])
    z0, z1 = sorted([-(lo_x - PICTURE_MID) * MPP, -(hi_x - PICTURE_MID) * MPP])
    y0, y1 = RIDER_Y + (base_py - hi_y) * MPP, RIDER_Y + (base_py - lo_y) * MPP
    half_t = min_t * MPP * 0.8
    return box(x_mid - half_t, x_mid + half_t, max(y0, FLOOR_Y), y1, z0, z1, colour)


proxy = [
    box(-1.4, 1.4, 0.36, 0.9, -1.0, 1.0, BUN),                 # bottom bun and patty
    box(1.38, 1.92, 1.08, 1.45, -0.5, 0.5, BUN),               # top bun, front and back
    box(-1.92, -1.38, 1.08, 1.45, -0.5, 0.5, BUN),
    char_box(star_pts, star_t, -0.2, 600, star_pink, 45),
    char_box(sponge_pts, sponge_t, 0.15, 545, sponge_yellow, 45),
]

everything = fill_holes(grow_from((950, 700), alpha > 20))
ys, xs = np.where(everything)
icon = np.dstack([rgb, np.where(everything, alpha, 0)]).clip(0, 255).astype(np.uint8)
x0, x1, y0, y1 = xs.min(), xs.max(), ys.min(), ys.max()
Image.fromarray(icon[y0:y1 + 1, x0:x1 + 1]).save(os.path.join(OUT, "burgercar_icon.png"))

meta = {
    "wheelRadius": WHEEL_R, "wheelWidth": WHEEL_W, "wheelbase": WHEELBASE, "track": TRACK,
    "length": 2 * A, "width": 2 * B,
    "proxy": proxy,
}
with open(os.path.join(OUT, "burgercar.json"), "w") as f:
    json.dump(meta, f, indent=1)

print(f"burger car: {len(pos)} vertices, {len(idx) // 3} triangles, height {pos[:, 1].max():.2f} m, "
      f"icon {x1 - x0 + 1}x{y1 - y0 + 1}")
