# Builds every 3D model the game uses and exports them as .glb files.
#
#   /Applications/Blender.app/Contents/MacOS/Blender --background --python RacingGame/blender/build_models.py
#
# Output (RacingGame/assets/models/):
#   car_<type>_paint.glb   body panels in white; the game tints them with each racer's colour
#   car_<type>_detail.glb  glass, tyres-free trim, grille, headlights, wings (fixed colours)
#   car_<type>_tail.glb    tail lights in white; the game tints them dark red / bright red when braking
#   wheel.glb              one wheel, centred on its axle (the game places, steers and spins it)
#   tree_pine.glb, tree_round.glb, gantry.glb
#   cars.json              wheel positions per car, read by the game
# It also saves RacingGame/blender/models.blend so you can look at / tweak everything in Blender.
#
# Units are metres. Cars face +X, left is +Y, Z is up, ground at Z = 0.
# (glTF export turns that into +X forward, +Y up, right = +Z, which is what the game expects.)

import bpy
import bmesh
import json
import math
import os
import sys
from mathutils import Matrix, Vector

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "assets", "models"))
os.makedirs(OUT, exist_ok=True)

# ---------------------------------------------------------------- scene helpers

def reset_scene():
    bpy.ops.wm.read_factory_settings(use_empty=True)


_materials = {}

def material(name, rgb, rough=0.5, metal=0.0):
    if name in _materials:
        return _materials[name]
    m = bpy.data.materials.new(name)
    try:
        m.use_nodes = True  # always on (and deprecated) in newer Blender
    except Exception:
        pass
    m.diffuse_color = (*rgb, 1.0)
    bsdf = m.node_tree.nodes.get("Principled BSDF") if m.node_tree else None
    if bsdf:
        bsdf.inputs["Base Color"].default_value = (*rgb, 1.0)
        bsdf.inputs["Roughness"].default_value = rough
        bsdf.inputs["Metallic"].default_value = metal
    _materials[name] = m
    return m


def srgb(r, g, b):
    """0-255 colour as stored; raylib uses the stored value directly as the display colour."""
    def c(v):
        v /= 255.0
        return v
    return (c(r), c(g), c(b))


def make_object(name, bm, mat, collection):
    me = bpy.data.meshes.new(name)
    bm.to_mesh(me)
    bm.free()
    me.materials.append(mat)
    ob = bpy.data.objects.new(name, me)
    collection.objects.link(ob)
    return ob


def hull(points, bevel=0.0, segments=2):
    """Convex block from 8 corners: 4 bottom (z0) then 4 top, each listed
    rear-right, front-right, front-left, rear-left."""
    bm = bmesh.new()
    v = [bm.verts.new(p) for p in points]
    faces = [(0, 3, 2, 1), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    for f in faces:
        bm.faces.new([v[i] for i in f])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    if bevel > 0:
        bmesh.ops.bevel(bm, geom=bm.edges[:] + bm.verts[:], offset=bevel, segments=segments,
                        profile=0.5, affect='EDGES', clamp_overlap=True)
    return bm


def box(cx, cy, cz, sx, sy, sz, bevel=0.0, segments=1):
    x0, x1, y0, y1, z0, z1 = cx - sx / 2, cx + sx / 2, cy - sy / 2, cy + sy / 2, cz - sz / 2, cz + sz / 2
    return hull([(x0, y0, z0), (x1, y0, z0), (x1, y1, z0), (x0, y1, z0),
                 (x0, y0, z1), (x1, y0, z1), (x1, y1, z1), (x0, y1, z1)], bevel, segments)


def cylinder(r, depth, segments=24, matrix=Matrix(), r2=None):
    bm = bmesh.new()
    bmesh.ops.create_cone(bm, cap_ends=True, cap_tris=False, segments=segments,
                          radius1=r, radius2=r if r2 is None else r2, depth=depth, matrix=matrix)
    return bm


def ico(r, matrix=Matrix(), subdivisions=1):
    bm = bmesh.new()
    bmesh.ops.create_icosphere(bm, subdivisions=subdivisions, radius=r, matrix=matrix)
    return bm


def export(objects, filename):
    bpy.ops.object.select_all(action='DESELECT')
    for ob in objects:
        ob.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    bpy.ops.export_scene.gltf(filepath=os.path.join(OUT, filename), export_format='GLB',
                              use_selection=True, export_yup=True, export_apply=True,
                              export_materials='EXPORT', export_texcoords=False)


def new_collection(name):
    col = bpy.data.collections.new(name)
    bpy.context.scene.collection.children.link(col)
    return col

# ---------------------------------------------------------------- materials

reset_scene()  # start from an empty scene (before creating materials, which a reset would wipe)

WHITE = material("Paint", (1, 1, 1), rough=0.25, metal=0.2)   # tinted per racer in game
TAIL = material("TailLight", (1, 1, 1), rough=0.3)            # tinted in game (brake lights)
TYRE = material("Tyre", srgb(20, 20, 22), rough=0.9)
RIM = material("Rim", srgb(160, 164, 172), rough=0.3, metal=0.8)
BARK = material("Bark", srgb(84, 60, 40), rough=0.9)
PINE = material("Pine", srgb(34, 84, 46), rough=0.9)
LEAF = material("Leaf", srgb(58, 120, 54), rough=0.9)
STEEL = material("Steel", srgb(70, 74, 82), rough=0.5, metal=0.6)
BANNER = material("Banner", srgb(18, 20, 26), rough=0.6)
LAMP = material("Lamp", srgb(255, 60, 50), rough=0.2)
ACCENT = material("Accent", srgb(248, 190, 60), rough=0.4)

# ---------------------------------------------------------------- cars
#
# Each body is one smooth surface lofted along the car: at every station along the length a
# cross-section runs from the middle of the floor, out over the sill and door, up the side glass
# and across the roof to the centre line (mirrored for the other side). How those sections change
# along the car comes from a few smooth curves per car: the side-view silhouette, the beltline,
# the roof/bonnet width, the plan-view outline and the wheel arches.
#
# Windows, lights, grilles, number plates, door shut-lines and trim are "decals": shapes drawn in
# a flat side / top / front / rear view and projected onto that surface, then lifted a few
# millimetres along its normal. That keeps every edge clean and lets them wrap round corners.

import bisect

GLASS = material("CarGlass", srgb(16, 22, 30), rough=0.05, metal=0.3)
FRIT = material("Frit", srgb(10, 10, 12), rough=0.5)              # black window surrounds / pillars
UNDER = material("Under", srgb(16, 16, 18), rough=0.9)            # floor, wheel wells
PLASTIC = material("Plastic", srgb(30, 31, 34), rough=0.8)        # unpainted bumpers, cladding
SEAM = material("Seam", srgb(6, 6, 8), rough=0.9)                 # panel gaps
LENS = material("Lens", srgb(214, 222, 230), rough=0.1)           # headlight lens / reflectors
LAMPHOUSING = material("LampHousing", srgb(54, 58, 66), rough=0.2, metal=0.5)
DRL = material("Drl", srgb(255, 255, 252), rough=0.1)             # daytime running light strip
PLATE = material("Plate", srgb(238, 238, 232), rough=0.5)
PLATE_BLUE = material("PlateBlue", srgb(20, 50, 160), rough=0.5)
INK = material("Ink", srgb(20, 20, 22), rough=0.6)
SILVER = material("Silver", srgb(150, 154, 160), rough=0.3, metal=0.8)
MIRRORGLASS = material("MirrorGlass", srgb(120, 130, 140), rough=0.05, metal=0.9)
CARBON = material("Carbon", srgb(26, 27, 30), rough=0.4)
REAR_DARK = material("TailDark", srgb(60, 10, 14), rough=0.2)      # smoked parts of the rear lights
CALIPER = material("Caliper", srgb(190, 26, 30), rough=0.4)


def pchip(keys):
    """Smooth curve through (x, value) keys that never overshoots between them (monotone cubic)."""
    keys = sorted(keys)
    xs = [k[0] for k in keys]
    vs = [k[1] for k in keys]
    n = len(xs)
    if n == 1:
        return lambda x: vs[0]
    d = [(vs[i + 1] - vs[i]) / (xs[i + 1] - xs[i]) for i in range(n - 1)]
    m = [0.0] * n
    m[0], m[-1] = d[0], d[-1]
    for i in range(1, n - 1):
        if d[i - 1] * d[i] > 0:
            h0, h1 = xs[i] - xs[i - 1], xs[i + 1] - xs[i]
            w1, w2 = 2 * h1 + h0, h1 + 2 * h0
            m[i] = (w1 + w2) / (w1 / d[i - 1] + w2 / d[i])
    if n > 2:
        if m[0] * d[0] < 0: m[0] = 0
        if m[-1] * d[-1] < 0: m[-1] = 0

    def f(x):
        if x <= xs[0]: return vs[0]
        if x >= xs[-1]: return vs[-1]
        i = bisect.bisect_right(xs, x) - 1
        h = xs[i + 1] - xs[i]
        t = (x - xs[i]) / h
        t2, t3 = t * t, t * t * t
        return ((2 * t3 - 3 * t2 + 1) * vs[i] + (t3 - 2 * t2 + t) * h * m[i]
                + (-2 * t3 + 3 * t2) * vs[i + 1] + (t3 - t2) * h * m[i + 1])
    return f


def catmull(p0, p1, p2, p3, t):
    t2, t3 = t * t, t * t * t
    return tuple(0.5 * (2 * b + (-a + c) * t + (2 * a - 5 * b + 4 * c - d) * t2 + (-a + 3 * b - 3 * c + d) * t3)
                 for a, b, c, d in zip(p0, p1, p2, p3))


def smoothstep(e0, e1, x):
    t = min(1.0, max(0.0, (x - e0) / (e1 - e0)))
    return t * t * (3 - 2 * t)


class Body:
    # subdivisions between the cross-section control points: floor, sill, lower door, door,
    # tumblehome, upper glass, roof edge, roof
    SEG = (3, 3, 3, 5, 4, 3, 3, 3)
    UNDER_SEGS = 3   # the first SEG[0] strips are the floor

    def __init__(self, s):
        self.s = s
        self.xr, self.xf = s["xr"], s["xf"]
        self.W = s["W"]
        R, _, wb, _ = s["wheel"]
        self.wheels = [(-wb / 2, R), (wb / 2, R)]
        self.arch_r = R + s.get("arch_gap", 0.065)
        self.zt = pchip(s["zt"])
        self.belt = pchip(s["belt"])
        self.tk = pchip(s["tk"])
        self.fender = pchip(s.get("fender", [(0, 0)]))
        self.hips = pchip(s.get("hips", [(0, 1)]))
        self.crown = pchip(s.get("crown", [(0, 0.05)]))
        clr = s["clr"]
        self.zb = pchip([(self.xr, clr + s.get("lift_r", 0.16)), (self.xr + 0.5, clr + 0.01),
                         (self.xf - 0.5, clr), (self.xf, clr + s.get("lift_f", 0.14))])
        self.cache = {}

    def width(self, x):
        s = self.s
        xc, hx = (self.xf + self.xr) / 2, (self.xf - self.xr) / 2
        xn = (x - xc) / hx
        p = s["pf"] if xn > 0 else s["pr"]
        v = max(0.0, 1 - abs(xn) ** p) ** (1 / p)
        v *= 1 - s.get("taper", 0.05) * max(0.0, xn) ** 2
        return self.W / 2 * v * self.hips(x)

    def params(self, x):
        zt = self.zt(x)
        zedge = zt - self.crown(x)
        zbelt = min(self.belt(x), zedge - 0.02 + self.fender(x))
        zs = min(zbelt - self.s.get("shoulder", 0.13), zbelt - 0.04)
        zb = self.zb(x)
        for xa, za in self.wheels:
            d = x - xa
            if abs(d) < self.arch_r:
                zb = max(zb, za + math.sqrt(self.arch_r ** 2 - d * d))
        zb = min(zb, zs - 0.06)
        w = self.width(x)
        return zt, zedge, zbelt, zs, zb, w, w * 0.965, w * self.tk(x)

    def ring(self, x):
        """Half cross-section at x, from the middle of the floor round to the middle of the roof."""
        key = round(x, 7)
        r = self.cache.get(key)
        if r is not None:
            return r
        zt, zedge, zbelt, zs, zb, w, wbelt, wt = self.params(x)
        c = [(0.0, zb), (w * 0.86, zb), (w * 0.985, zb + 0.4 * (zs - zb)), (w, zs), (wbelt, zbelt),
             (wt + (wbelt - wt) * 0.38, zbelt + (zedge - zbelt) * 0.72), (wt, zedge),
             (wt * 0.5, zt - (zt - zedge) * 0.2), (0.0, zt)]
        ext = [(-c[1][0], c[1][1])] + c + [(-c[-2][0], c[-2][1])]
        pts = []
        for i, n in enumerate(self.SEG):
            for k in range(n):
                pts.append(catmull(ext[i], ext[i + 1], ext[i + 2], ext[i + 3], k / n))
        pts.append(c[-1])
        if len(self.cache) > 20000:
            self.cache.clear()
        self.cache[key] = pts
        return pts

    # ---- projections onto the surface

    def side_y(self, x, z):
        best = None
        pts = self.ring(x)
        for (y0, z0), (y1, z1) in zip(pts, pts[1:]):
            if z0 != z1 and (z0 - z) * (z1 - z) <= 0:
                y = y0 + (y1 - y0) * (z - z0) / (z1 - z0)
                if best is None or y > best:
                    best = y
        return best

    def top_z(self, x, y):
        y = abs(y)
        best = None
        pts = self.ring(x)
        for (y0, z0), (y1, z1) in zip(pts, pts[1:]):
            if y0 != y1 and (y0 - y) * (y1 - y) <= 0:
                z = z0 + (z1 - z0) * (y - y0) / (y1 - y0)
                if best is None or z > best:
                    best = z
        return best if best is not None else self.zt(x)

    def end_x(self, y, z, front):
        """Where a line along the car at (y, z) leaves the front / rear of the body."""
        xa, _ = self.wheels[1 if front else 0]
        lo = xa + (self.arch_r + 0.02) * (1 if front else -1)
        hi = self.xf if front else self.xr
        for _ in range(22):
            mid = (lo + hi) / 2
            sy = self.side_y(mid, z)
            if sy is not None and sy >= abs(y):
                lo = mid
            else:
                hi = mid
        return lo

    def point(self, proj, p, q):
        if proj == "side":
            y = self.side_y(p, q)
            if y is None:  # just outside the section: snap to its nearest end
                pts = self.ring(p)
                y = min(pts, key=lambda pt: abs(pt[1] - q))[0]
            return Vector((p, y, q))
        if proj == "top":
            return Vector((p, q, self.top_z(p, q)))
        if proj == "front":
            return Vector((self.end_x(p, q, True), p, q))
        return Vector((self.end_x(p, q, False), p, q))

    HINT = {"side": Vector((0, 1, 0)), "top": Vector((0, 0, 1)), "front": Vector((1, 0, 0)), "rear": Vector((-1, 0, 0))}

    def normal(self, proj, p, q):
        e = 0.004
        a = self.point(proj, p + e, q) - self.point(proj, p - e, q)
        b = self.point(proj, p, q + e) - self.point(proj, p, q - e)
        n = a.cross(b)
        if n.length < 1e-12:
            return self.HINT[proj].copy()
        n.normalize()
        if n.dot(self.HINT[proj]) < 0:
            n = -n
        return n


class CarParts:
    """Collects geometry per (output file, material) so each car exports as a handful of meshes."""

    def __init__(self, key, body):
        self.key = key
        self.body = body
        self.bms = {}

    def bm(self, file, mat):
        k = (file, mat.name)
        if k not in self.bms:
            self.bms[k] = (bmesh.new(), mat, file)
        return self.bms[k][0]

    def merge(self, file, mat, src, matrix=None):
        dst = self.bm(file, mat)
        if matrix is not None:
            bmesh.ops.transform(src, matrix=matrix, verts=src.verts)
        bmesh.ops.recalc_face_normals(src, faces=src.faces[:])
        vmap = {v: dst.verts.new(v.co) for v in src.verts}
        for f in src.faces:
            dst.faces.new([vmap[v] for v in f.verts])
        src.free()

    def decal(self, mat, proj, fn, na, nb, off, mirror=None, file="detail"):
        """Grid over (a, b) in [0,1]^2 -> fn -> 2D point in the projection plane -> body surface + off * normal."""
        body = self.body
        if mirror is None:
            mirror = proj == "side"
        grid, nrm = [], []
        for i in range(na + 1):
            row, nrow = [], []
            for j in range(nb + 1):
                p, q = fn(i / na, j / nb)
                n = body.normal(proj, p, q)
                row.append(body.point(proj, p, q) + n * off)
                nrow.append(n)
            grid.append(row)
            nrm.append(nrow)
        sides = (1, -1) if mirror else (1,)
        dst = self.bm(file, mat)
        for sgn in sides:
            vs = [[dst.verts.new((v.x, v.y * sgn, v.z)) for v in row] for row in grid]
            for i in range(na):
                for j in range(nb):
                    quad = [vs[i][j], vs[i + 1][j], vs[i + 1][j + 1], vs[i][j + 1]]
                    a, b, c, d = (q.co for q in quad)
                    if ((c - a).cross(d - b)).length < 1e-10:
                        continue
                    try:
                        f = dst.faces.new(quad)
                    except ValueError:
                        continue
                    f.normal_update()
                    want = nrm[i][j] + nrm[i + 1][j + 1]
                    want = Vector((want.x, want.y * sgn, want.z))
                    if f.normal.dot(want) < 0:
                        f.normal_flip()

    def finish(self, col):
        out = {"paint": [], "detail": [], "tail": []}
        for (file, matname), (bm, mat, _) in sorted(self.bms.items()):
            bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=1e-6)
            for f in bm.faces:
                f.smooth = True
            for e in bm.edges:
                if len(e.link_faces) == 2 and e.calc_face_angle(0) > math.radians(42):
                    e.smooth = False
            out[file].append(make_object(f"{self.key}_{file}_{matname}", bm, mat, col))
        return out


def lofted_body(parts, stations=150):
    """The painted shell plus its black floor, as one closed surface split by material."""
    body = parts.body
    xs = [body.xr + (body.xf - body.xr) * (1 - math.cos(math.pi * i / stations)) / 2 for i in range(stations + 1)]
    # extra stations at the wheel-arch edges so the arches stay round
    for xa, _ in body.wheels:
        for k in range(-12, 13):
            xs.append(xa + body.arch_r * math.sin(k / 12 * math.pi / 2) * 1.0)
    xs = sorted(set(round(x, 6) for x in xs if body.xr <= x <= body.xf))
    rings = []
    for x in xs:
        half = body.ring(x)
        full = [(x, y, z) for y, z in half] + [(x, -y, z) for y, z in reversed(half[1:-1])]
        rings.append(full)
    n = len(rings[0])
    nhalf = len(body.ring(xs[0]))
    under = Body.UNDER_SEGS
    s = body.s
    clad = s.get("cladding", False)
    sill = s.get("sill", 0.05)
    xa0, xa1 = body.wheels[0][0], body.wheels[1][0]

    def plastic(c):
        """Unpainted black trim: a band along the bottom, and on an SUV round the wheel arches too."""
        zb = body.params(c.x)[4]
        between = xa0 + body.arch_r < c.x < xa1 - body.arch_r
        return between and c.z < body.zb(c.x) + sill

    bm = bmesh.new()
    verts = [[bm.verts.new(p) for p in r] for r in rings]
    for i in range(len(rings) - 1):
        for j in range(n):
            jn = (j + 1) % n
            f = bm.faces.new([verts[i][j], verts[i][jn], verts[i + 1][jn], verts[i + 1][j]])
            # strip index measured from the floor centre on either side; the floor strips are tagged 1
            k = j if j < nhalf - 1 else n - 1 - j
            f.material_index = 1 if k < under else 2 if plastic(f.calc_center_median()) else 0
    bmesh.ops.remove_doubles(bm, verts=bm.verts[:], dist=1e-5)
    bmesh.ops.dissolve_degenerate(bm, dist=1e-6, edges=bm.edges[:])
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces[:])
    for tag, mat, file in ((0, WHITE, "paint"), (1, UNDER, "detail"), (2, PLASTIC, "detail")):
        dst = parts.bm(file, mat)
        vmap = {}
        for f in bm.faces:
            if f.material_index != tag:
                continue
            vs = []
            for v in f.verts:
                if v not in vmap:
                    vmap[v] = dst.verts.new(v.co)
                vs.append(vmap[v])
            dst.faces.new(vs)
    bm.free()
    R, tw, wb, track = s["wheel"]
    inner = track / 2 - tw / 2 - 0.04
    for xa, za in body.wheels:
        # keep it under the bonnet / boot: low cars have arches nearly as high as the bodywork
        top = min([za + body.arch_r] + [body.zt(xa + body.arch_r * k / 8) - 0.06 for k in range(-8, 9)])
        liner = box(xa, 0, (s["clr"] + top) / 2, body.arch_r * 2 + 0.02, inner * 2, top - s["clr"])
        parts.merge("detail", UNDER, liner)


# ---- window shapes -------------------------------------------------------

def window_region(body, m_bot, m_top, edges, x_lo, x_hi):
    """Side-view region between the beltline and the roof edge, cut by pillar edges.
    edges: list of ((x_bottom, x_top), side) where side=+1 keeps x <= edge (front edge), -1 keeps x >= edge.
    Returns fn(x) -> (zlo, zhi) or None, plus the x extent where it is non-empty."""
    def lines(x):
        zbelt = body.params(x)[2]
        zedge = body.params(x)[1]
        lo, hi = zbelt + m_bot, zedge - m_top
        for (xb, xt), side in edges:
            zb_ref = body.params(xb)[2]
            zt_ref = body.params(xt)[1]
            # edge as x(z) = xb + (xt - xb) * (z - zb_ref) / (zt_ref - zb_ref)
            k = (xt - xb) / (zt_ref - zb_ref)
            if abs(k) < 1e-9:
                if (x - xb) * side > 0:
                    return None
                continue
            zcross = zb_ref + (x - xb) / k
            # keep side of the line: front edge (side +1): x <= x(z)
            if side * k < 0:  # x(z) decreases with z -> the condition bounds z from above
                hi = min(hi, zcross)
            else:
                lo = max(lo, zcross)
        return (lo, hi) if hi > lo else None

    xs = [x_lo + (x_hi - x_lo) * i / 400 for i in range(401)]
    ok = [x for x in xs if lines(x) is not None]
    if not ok:
        return None
    a, b = ok[0], ok[-1]
    # refine both ends
    def refine(good, bad):
        for _ in range(30):
            mid = (good + bad) / 2
            if lines(mid) is not None:
                good = mid
            else:
                bad = mid
        return good
    a = refine(a, a - (x_hi - x_lo) / 400)
    b = refine(b, b + (x_hi - x_lo) / 400)

    def fn(u, v):
        x = a + (b - a) * u
        r = lines(x)
        if r is None:
            r = lines(min(max(x, a + 1e-5), b - 1e-5)) or (body.params(x)[2], body.params(x)[2])
            mid = (r[0] + r[1]) / 2
            return x, mid
        return x, r[0] + (r[1] - r[0]) * v
    return fn


def quad_fn(c00, c10, c01, c11):
    """Bilinear patch: c00 at (0,0), c10 at (1,0), c01 at (0,1), c11 at (1,1)."""
    def fn(u, v):
        p = [c00[i] * (1 - u) * (1 - v) + c10[i] * u * (1 - v) + c01[i] * (1 - u) * v + c11[i] * u * v for i in range(2)]
        return p[0], p[1]
    return fn


def rect_fn(p0, p1, q0, q1, round_=0.0):
    """Rectangle in the projection plane with optionally rounded ends along p."""
    def fn(u, v):
        p = p0 + (p1 - p0) * u
        q = q0 + (q1 - q0) * v
        if round_ > 0:
            e = min(u, 1 - u) * (p1 - p0)
            if e < round_:
                k = max(0.5, math.sqrt(max(0.0, 1 - ((round_ - e) / round_) ** 2)))
                qc = (q0 + q1) / 2
                q = qc + (q - qc) * k
        return p, q
    return fn


def strip_fn(a, b, width):
    """Thin line from a to b in the projection plane."""
    d = Vector((b[0] - a[0], b[1] - a[1]))
    n = Vector((-d.y, d.x)).normalized() * width / 2

    def fn(u, v):
        p = Vector(a) + d * u + n * (2 * v - 1)
        return p.x, p.y
    return fn


def top_fn(body, x0, x1, inset, corner=0.06, y_scale=1.0):
    """Region on the roof/bonnet between x0 and x1, inside the roof edge (tk * width) minus inset.
    Mapped from -y_max..y_max so one patch covers both sides."""
    def fn(u, v):
        x = x0 + (x1 - x0) * u
        ymax = max(0.0, body.params(x)[7] * y_scale - inset)
        e = min(x - x0, x1 - x)
        if e < corner:
            ymax *= math.sqrt(max(0.0, 1 - ((corner - e) / corner) ** 2)) * 0.3 + 0.7
        return x, -ymax + 2 * ymax * v
    return fn


def end_fn(y0, y1, z0, z1, slope=0.0, round_=0.0):
    """Shape on the front / rear face: y0..y1 across, z0..z1 up; slope lifts the outer end; round_ rounds the corners."""
    def fn(u, v):
        y = y0 + (y1 - y0) * u
        lift = slope * u
        za, zb = z0 + lift, z1 + lift
        if round_ > 0:
            e = min(u, 1 - u) * abs(y1 - y0)
            if e < round_:
                k = max(0.5, math.sqrt(max(0.0, 1 - ((round_ - e) / round_) ** 2)))
                zc = (za + zb) / 2
                za, zb = zc + (za - zc) * k, zc + (zb - zc) * k
        return y, za + (zb - za) * v
    return fn


# ---- car descriptions ------------------------------------------------------
#
# Lengths in metres, x forward (front wheels at +wheelbase/2), z up.
#   zt     side-view silhouette (top of the body along the centre line)
#   belt   beltline (bottom of the side windows / top of the doors)
#   tk     roof / bonnet width as a share of the body width
#   fender how far the wings may rise above the bonnet
#   hips   plan-view width multiplier along the car
#   pf/pr  plan-view corner squareness at the front / rear (higher = squarer)
#   cowl, roof_f, roof_r, deck: where the windscreen and rear glass start and end

CARS = {
    # premium compact SUV
    "suv": dict(
        W=1.95, clr=0.21, xr=-2.36, xf=2.30, wheel=(0.38, 0.27, 2.80, 1.66),
        lift_f=0.17, lift_r=0.16, pf=6, pr=9, taper=0.07, shoulder=0.10, arch_gap=0.07,
        zt=[(-2.36, 0.92), (-2.33, 1.02), (-2.27, 1.13), (-2.20, 1.27), (-2.05, 1.47), (-1.92, 1.56),
            (-1.70, 1.60), (-0.8, 1.645), (0.0, 1.65), (0.18, 1.625), (0.32, 1.585), (1.05, 1.15),
            (1.60, 1.10), (2.08, 1.04), (2.24, 0.95), (2.30, 0.80)],
        belt=[(-2.36, 1.13), (-1.6, 1.12), (0.0, 1.07), (1.05, 1.04), (2.30, 1.00)],
        tk=[(-2.36, 0.78), (-2.0, 0.78), (0.3, 0.78), (1.0, 0.84), (2.3, 0.84)],
        crown=[(0, 0.045)],
        cowl=1.05, roof_f=0.32, roof_r=-1.92, deck=-2.24,
        rear_glass=("top",),
        windows=[dict(front=((0.96, 0.36), 1), rear=((-0.06, -0.06), -1)),
                 dict(front=((-0.16, -0.16), 1), rear=((-1.12, -1.04), -1)),
                 dict(front=((-1.22, -1.12), 1), rear=((-1.86, -1.62), -1))],
        dlo=dict(x_lo=-2.0, x_hi=1.1, edges=[((1.00, 0.40), 1), ((-1.94, -1.70), -1)]),
        doors=[(1.02, 1.00), (-0.11, -0.09), (-1.24, -1.18)],
        handles=[0.38, -0.80],
        head=dict(y0=0.40, y1=0.90, z0=0.835, z1=0.935, slope=0.035),
        grille=dict(y=0.40, z0=0.56, z1=0.86, slats=6),
        lower=dict(y=0.62, z0=0.37, z1=0.47),
        plate_f=0.475, plate_r=0.70,
        tail=dict(y0=0.32, y1=0.94, z0=1.02, z1=1.10, slope=0.02, bar=True),
        exhaust=dict(kind="trapezoid", y=0.55, z=0.33),
        cladding=True, rails=True, fin=True, mirror_x=0.90,
    ),
    # family liftback
    "sedan": dict(
        W=1.84, clr=0.145, xr=-2.40, xf=2.30, wheel=(0.33, 0.24, 2.70, 1.56),
        lift_f=0.13, lift_r=0.17, pf=5, pr=8, taper=0.07, shoulder=0.12,
        zt=[(-2.40, 0.78), (-2.37, 0.93), (-2.28, 1.01), (-2.10, 1.055), (-1.95, 1.07), (-1.55, 1.27),
            (-1.05, 1.415), (-0.45, 1.465), (0.10, 1.455), (0.30, 1.42), (1.15, 0.99), (1.60, 0.93),
            (2.05, 0.845), (2.24, 0.75), (2.30, 0.62)],
        belt=[(-2.40, 0.995), (-1.5, 0.985), (1.2, 0.93), (2.30, 0.88)],
        tk=[(-2.40, 0.80), (-1.9, 0.78), (-1.2, 0.72), (0.3, 0.72), (1.1, 0.80), (2.30, 0.84)],
        crown=[(0, 0.05)],
        cowl=1.15, roof_f=0.30, roof_r=-1.05, deck=-1.95,
        rear_glass=("top",),
        windows=[dict(front=((1.02, 0.30), 1), rear=((-0.08, -0.08), -1)),
                 dict(front=((-0.18, -0.18), 1), rear=((-1.30, -1.08), -1))],
        dlo=dict(x_lo=-1.8, x_hi=1.2, edges=[((1.08, 0.34), 1), ((-1.62, -1.18), -1)]),
        doors=[(1.08, 1.06), (-0.12, -0.10), (-1.30, -1.38)],
        handles=[0.30, -0.95],
        head=dict(y0=0.40, y1=0.85, z0=0.635, z1=0.715, slope=0.05),
        grille=dict(y=0.38, z0=0.50, z1=0.66, slats=4),
        lower=dict(y=0.56, z0=0.22, z1=0.33),
        plate_f=0.36, plate_r=0.64,
        tail=dict(y0=0.30, y1=0.88, z0=0.86, z1=0.95, slope=0.03, bar=False),
        exhaust=dict(kind="hidden"),
        mirror_x=0.98,
        fin=True,
    ),
    # rear-engined sports coupe with a fixed rear wing
    "sports": dict(
        W=1.90, clr=0.105, xr=-2.33, xf=2.17, wheel=(0.34, 0.28, 2.45, 1.62),
        lift_f=0.08, lift_r=0.13, pf=4, pr=6, taper=0.10, shoulder=0.10, arch_gap=0.05,
        zt=[(-2.33, 0.70), (-2.30, 0.84), (-2.18, 0.92), (-1.85, 0.96), (-1.25, 1.08), (-0.70, 1.215),
            (-0.20, 1.275), (0.15, 1.265), (0.30, 1.24), (0.98, 0.84), (1.55, 0.73), (2.0, 0.63),
            (2.13, 0.54), (2.17, 0.42)],
        belt=[(-2.33, 0.86), (-1.7, 0.90), (-0.9, 0.86), (0.9, 0.80), (1.6, 0.76), (2.17, 0.62)],
        fender=[(-2.33, 0.0), (-1.9, 0.06), (0.6, 0.0), (1.1, 0.07), (1.95, 0.07), (2.17, 0.0)],
        hips=[(-2.33, 1.0), (-1.25, 1.0), (-0.55, 0.955), (0.4, 0.955), (1.2, 0.985), (2.17, 1.0)],
        tk=[(-2.33, 0.70), (-1.6, 0.64), (-0.8, 0.62), (0.5, 0.64), (1.2, 0.60), (2.17, 0.62)],
        crown=[(0, 0.05)],
        cowl=0.98, roof_f=0.30, roof_r=-0.70, deck=-1.85,
        rear_glass=("top",),
        windows=[dict(front=((0.88, 0.26), 1), rear=((-0.70, -0.42), -1))],
        dlo=dict(x_lo=-1.0, x_hi=1.0, edges=[((0.92, 0.30), 1), ((-0.86, -0.50), -1)]),
        doors=[(0.86, 0.84), (-0.70, -0.62)],
        handles=[-0.42],
        head=dict(y0=0.52, y1=0.88, z0=0.585, z1=0.665, slope=0.03, round=0.10),
        grille=None,
        lower=dict(y=0.36, z0=0.17, z1=0.30),
        side_intakes=dict(y0=0.48, y1=0.82, z0=0.16, z1=0.33),
        plate_f=0.31, plate_r=0.50,
        tail=dict(y0=0.25, y1=0.92, z0=0.80, z1=0.86, slope=0.0, bar=True),
        exhaust=dict(kind="centre", y=0.11, z=0.27, r=0.05),
        louvres=(-2.10, -1.80),
        wing=dict(x=-2.02, z=1.16, chord=0.32, span=1.62),
        mirror_x=0.86, mirror_scale=0.85,
    ),
    # mid-engined hypercar
    "hyper": dict(
        W=2.02, clr=0.09, xr=-2.30, xf=2.40, wheel=(0.35, 0.30, 2.70, 1.70),
        lift_f=0.06, lift_r=0.11, pf=3.2, pr=5, taper=0.18, shoulder=0.09, arch_gap=0.05,
        zt=[(-2.30, 0.64), (-2.27, 0.82), (-2.15, 0.90), (-1.60, 0.96), (-1.0, 1.04), (-0.55, 1.105),
            (-0.15, 1.12), (0.12, 1.10), (0.62, 0.84), (1.15, 0.68), (1.75, 0.57), (2.20, 0.46),
            (2.35, 0.38), (2.40, 0.28)],
        belt=[(-2.30, 0.86), (-1.6, 0.86), (-0.6, 0.80), (0.5, 0.74), (1.4, 0.70), (2.40, 0.50)],
        fender=[(-2.30, 0.02), (-1.5, 0.07), (-0.4, 0.0), (0.7, 0.03), (1.3, 0.10), (2.0, 0.08), (2.40, 0.0)],
        hips=[(-2.30, 0.99), (-1.4, 1.0), (-0.75, 0.93), (0.1, 0.93), (0.9, 0.985), (2.40, 1.0)],
        tk=[(-2.30, 0.66), (-1.4, 0.60), (-0.6, 0.56), (0.3, 0.56), (1.2, 0.50), (2.40, 0.55)],
        crown=[(0, 0.04)],
        cowl=0.62, roof_f=0.12, roof_r=-0.55, deck=-1.0,
        rear_glass=("top",),
        windows=[dict(front=((0.55, 0.10), 1), rear=((-0.72, -0.40), -1))],
        dlo=dict(x_lo=-1.0, x_hi=0.7, edges=[((0.60, 0.14), 1), ((-0.92, -0.52), -1)]),
        doors=[(0.58, 0.56), (-0.95, -0.80)],
        handles=[],
        head=dict(y0=0.50, y1=0.90, z0=0.43, z1=0.49, slope=0.07, round=0.05),
        grille=None,
        lower=dict(y=0.62, z0=0.13, z1=0.30),
        side_intakes=None,
        body_intake=dict(x0=-1.55, x1=-0.75, z0=0.30, z1=0.62),
        plate_f=None, plate_r=0.40,
        tail=dict(y0=0.30, y1=0.90, z0=0.74, z1=0.79, slope=-0.02, bar=False),
        exhaust=dict(kind="centre", y=0.0, z=0.56, r=0.055, count=1),
        louvres=(-1.85, -1.20),
        diffuser=True,
        ducktail=True,
        mirror_x=0.52, mirror_scale=0.8,
    ),
}


def build_car(key, s):
    col = new_collection(f"car_{key}")
    body = Body(s)
    parts = CarParts(key, body)
    lofted_body(parts)
    R, tw, wb, track = s["wheel"]
    hx = body.xf

    # ---- glass
    d = s["dlo"]
    trim = window_region(body, 0.012, 0.025, d["edges"], d["x_lo"], d["x_hi"])
    if trim:
        parts.decal(FRIT, "side", trim, 40, 6, 0.006)
    for w in s["windows"]:
        fn = window_region(body, 0.032, 0.05, [w["front"], w["rear"]], d["x_lo"], d["x_hi"])
        if fn:
            parts.decal(GLASS, "side", fn, 28, 6, 0.010)
    # windscreen: black band round the edge, then the glass
    parts.decal(FRIT, "top", top_fn(body, s["cowl"] - 0.02, s["roof_f"] + 0.01, 0.0, corner=0.04), 16, 16, 0.006, mirror=False)
    parts.decal(GLASS, "top", top_fn(body, s["cowl"] - 0.05, s["roof_f"] + 0.06, 0.05, corner=0.08), 16, 16, 0.010, mirror=False)
    # wipers parked at the bottom of the screen
    for y0, y1 in ((-0.62, -0.02), (0.10, 0.62)):
        zc = s["cowl"] - 0.09
        parts.decal(FRIT, "top", strip_fn((zc, y0), (zc + 0.035, y1), 0.022), 6, 1, 0.022, mirror=False)
    rg = s["rear_glass"]
    if rg[0] == "top":
        parts.decal(FRIT, "top", top_fn(body, s["deck"] + 0.01, s["roof_r"] - 0.01, 0.0, corner=0.04), 14, 16, 0.006, mirror=False)
        parts.decal(GLASS, "top", top_fn(body, s["deck"] + 0.06, s["roof_r"] - 0.07, 0.06, corner=0.08), 14, 16, 0.010, mirror=False)
    else:
        _, z0, z1, wb0, wt0 = rg
        parts.decal(FRIT, "rear", quad_fn((-wb0 - 0.03, z0 - 0.04), (wb0 + 0.03, z0 - 0.04), (-wt0 - 0.03, z1 + 0.03), (wt0 + 0.03, z1 + 0.03)), 12, 10, 0.006, mirror=False)
        parts.decal(GLASS, "rear", quad_fn((-wb0, z0), (wb0, z0), (-wt0, z1), (wt0, z1)), 12, 10, 0.010, mirror=False)

    # ---- panel gaps and door handles
    for xb, xt in s["doors"]:
        zb = body.params(xb)[4] + 0.035
        zt = body.params(xt)[2] - 0.005
        parts.decal(SEAM, "side", strip_fn((xb, zb), (xt, zt), 0.007), 10, 1, 0.003)
    for xh in s.get("handles", []):
        zh = body.params(xh)[2] - 0.085
        parts.decal(SILVER, "side", rect_fn(xh - 0.12, xh + 0.12, zh - 0.018, zh + 0.018, round_=0.018), 8, 2, 0.008)
    # bonnet shut line
    xh0 = s["cowl"] + 0.10
    parts.decal(SEAM, "top", strip_fn((xh0, -0.6), (xh0, 0.6), 0.007), 12, 1, 0.003, mirror=False)

    # ---- front
    h = s["head"]
    parts.decal(LAMPHOUSING, "front", end_fn(h["y0"], h["y1"], h["z0"], h["z1"], h["slope"], h.get("round", 0.03)), 14, 6, 0.008, mirror=True)
    hh = h["z1"] - h["z0"]
    parts.decal(LENS, "front", end_fn(h["y0"] + 0.05, h["y1"] - 0.10, h["z0"] + hh * 0.30, h["z1"] - hh * 0.25, h["slope"] * 0.8, 0.03), 10, 4, 0.012, mirror=True)
    parts.decal(DRL, "front", end_fn(h["y0"] + 0.02, h["y1"] - 0.02, h["z0"] + hh * 0.06, h["z0"] + hh * 0.20, h["slope"], 0.01), 14, 1, 0.014, mirror=True)
    g = s.get("grille")
    if g:
        parts.decal(SILVER, "front", end_fn(-g["y"] - 0.025, g["y"] + 0.025, g["z0"] - 0.025, g["z1"] + 0.02, 0, 0.06), 16, 8, 0.007, mirror=False)
        parts.decal(FRIT, "front", end_fn(-g["y"], g["y"], g["z0"], g["z1"], 0, 0.05), 16, 8, 0.010, mirror=False)
        n = g["slats"]
        for i in range(n):
            if g.get("vertical"):
                y = -g["y"] + (i + 0.5) * 2 * g["y"] / n
                parts.decal(SILVER, "front", end_fn(y - 0.008, y + 0.008, g["z0"] + 0.01, g["z1"] - 0.01), 1, 6, 0.013, mirror=False)
            else:
                z = g["z0"] + (i + 0.5) * (g["z1"] - g["z0"]) / n
                parts.decal(SILVER, "front", end_fn(-g["y"] + 0.02, g["y"] - 0.02, z - 0.006, z + 0.006), 12, 1, 0.013, mirror=False)
    lw = s["lower"]
    parts.decal(PLASTIC, "front", end_fn(-lw["y"], lw["y"], lw["z0"], lw["z1"], 0, 0.06), 16, 4, 0.008, mirror=False)
    si = s.get("side_intakes")
    if si:
        parts.decal(PLASTIC, "front", end_fn(si["y0"], si["y1"], si["z0"], si["z1"], 0.02, 0.06), 10, 4, 0.008, mirror=True)
    # splitter lip
    zl = body.params(hx - 0.3)[4]
    parts.decal(PLASTIC, "front", end_fn(-body.W / 2 * 0.80, body.W / 2 * 0.80, zl + 0.005, zl + 0.05), 20, 1, 0.006, mirror=False)

    def plate(face, z0, sign):
        pw, ph = 0.26, 0.055
        parts.decal(PLATE, face, end_fn(-pw, pw, z0, z0 + 2 * ph), 10, 3, 0.014, mirror=False)
        parts.decal(PLATE_BLUE, face, end_fn(-pw * sign, (-pw + 0.045) * sign, z0 + 0.004, z0 + 2 * ph - 0.004), 1, 3, 0.017, mirror=False)
        for i in range(7):
            yc = -pw + 0.085 + i * 0.058
            if i == 2: continue
            parts.decal(INK, face, end_fn(yc * sign - 0.017, yc * sign + 0.017, z0 + 0.025, z0 + 2 * ph - 0.025), 1, 2, 0.017, mirror=False)
    if s.get("plate_f") is not None:
        plate("front", s["plate_f"], 1)
    plate("rear", s["plate_r"], -1)

    # ---- rear
    t = s["tail"]
    parts.decal(REAR_DARK, "rear", end_fn(t["y0"] - 0.01, t["y1"] + 0.005, t["z0"] - 0.012, t["z1"] + 0.012, t["slope"], 0.03), 14, 4, 0.008, mirror=True)
    parts.decal(TAIL, "rear", end_fn(t["y0"] + (0.0 if t.get("bar") else 0.10), t["y1"] - 0.01, t["z0"], t["z1"], t["slope"], 0.025), 14, 3, 0.012, mirror=True, file="tail")
    if t.get("bar"):
        parts.decal(TAIL, "rear", end_fn(-t["y0"] - 0.005, t["y0"] + 0.005, t["z1"] - 0.022, t["z1"] - 0.004), 8, 1, 0.012, mirror=False, file="tail")
    zr = body.params(body.xr + 0.3)[4]
    parts.decal(PLASTIC, "rear", end_fn(-body.W / 2 * 0.86, body.W / 2 * 0.86, zr + 0.01, zr + (0.17 if s.get("diffuser") else 0.11), 0, 0.08), 20, 2, 0.006, mirror=False)
    if s.get("diffuser"):
        for i in range(5):
            y = (i - 2) * 0.22
            parts.decal(CARBON, "rear", end_fn(y - 0.012, y + 0.012, zr + 0.01, zr + 0.17), 1, 3, 0.03, mirror=False)

    ex = s.get("exhaust", {})
    if ex.get("kind") in ("centre", "trapezoid"):
        r = ex.get("r", 0.045)
        offsets = [0.0] if ex.get("count") == 1 else [-ex["y"], ex["y"]] if ex["kind"] == "centre" else [-ex["y"], ex["y"]]
        for y in offsets:
            xe = body.end_x(y, ex["z"], False)
            if ex["kind"] == "trapezoid":
                parts.decal(SILVER, "rear", end_fn(y - 0.11, y + 0.11, ex["z"] - 0.035, ex["z"] + 0.035, 0, 0.03), 6, 2, 0.012, mirror=False)
                parts.decal(INK, "rear", end_fn(y - 0.09, y + 0.09, ex["z"] - 0.022, ex["z"] + 0.022, 0, 0.02), 6, 2, 0.016, mirror=False)
                continue
            m = Matrix.Translation((xe - 0.02, y, ex["z"])) @ Matrix.Rotation(math.pi / 2, 4, 'Y')
            parts.merge("detail", SILVER, cylinder(r, 0.10, 18, m))
            m2 = Matrix.Translation((xe - 0.069, y, ex["z"])) @ Matrix.Rotation(math.pi / 2, 4, 'Y')
            parts.merge("detail", INK, cylinder(r * 0.78, 0.004, 18, m2))

    # ---- body-side details
    if s.get("cladding"):
        # black plastic arch extensions and a deeper sill, typical of an SUV
        for xa, za in body.wheels:
            def arch(u, v, xa=xa, za=za):
                a = math.pi * (0.0 + 1.0 * u)
                r = body.arch_r + 0.004 + v * 0.055
                x = xa + r * math.cos(a)
                z = max(za + r * math.sin(a), body.zb(x) + 0.004)
                return x, z
            parts.decal(PLASTIC, "side", arch, 36, 2, 0.010)
        xa0, xa1 = body.wheels[0][0] + body.arch_r + 0.06, body.wheels[1][0] - body.arch_r - 0.06
        parts.decal(PLASTIC, "side", lambda u, v: (xa0 + (xa1 - xa0) * u, body.zb(xa0 + (xa1 - xa0) * u) + 0.004 + v * 0.12), 20, 3, 0.010)
    bi = s.get("body_intake")
    if bi:
        def scoop(u, v):
            x = bi["x0"] + (bi["x1"] - bi["x0"]) * u
            z0 = bi["z0"] + (bi["z1"] - bi["z0"]) * 0.55 * u
            return x, z0 + (bi["z1"] - z0) * v
        parts.decal(FRIT, "side", scoop, 14, 6, 0.008)
    if s.get("louvres"):
        x0, x1 = s["louvres"]
        parts.decal(FRIT, "top", top_fn(body, x0, x1, 0.12, corner=0.05, y_scale=0.75), 10, 8, 0.006, mirror=False)
        for i in range(7):
            x = x0 + 0.03 + i * (x1 - x0 - 0.06) / 6
            parts.decal(CARBON, "top", strip_fn((x, -0.32), (x, 0.32), 0.012), 8, 1, 0.012, mirror=False)

    # ---- mirrors on short stalks
    mx = s["mirror_x"]
    zbelt = body.params(mx)[2]
    ys = body.side_y(mx, zbelt + 0.02) or body.W / 2
    ms = s.get("mirror_scale", 1.0)
    for side in (-1, 1):
        cap = hull([
            (mx - 0.06, 0.03, 0.0), (mx + 0.05, 0.03, 0.0), (mx + 0.07, 0.20, 0.0), (mx - 0.06, 0.20, 0.0),
            (mx - 0.06, 0.03, 0.12), (mx + 0.04, 0.03, 0.12), (mx + 0.05, 0.19, 0.115), (mx - 0.06, 0.19, 0.115),
        ], bevel=0.03, segments=3)
        bmesh.ops.transform(cap, matrix=Matrix.Translation((mx, 0, 0)) @ Matrix.Diagonal((ms, ms, ms, 1)) @ Matrix.Translation((-mx, 0, 0)), verts=cap.verts)
        parts.merge("paint", WHITE, cap, Matrix.Diagonal((1, side, 1, 1)) @ Matrix.Translation((0, ys + 0.03, zbelt + 0.06)))
        glass_m = box(mx - 0.06 * ms - 0.006, ys + 0.03 + 0.115 * ms, zbelt + 0.06 + 0.06 * ms, 0.01, 0.15 * ms, 0.09 * ms, 0.02)
        parts.merge("detail", MIRRORGLASS, glass_m, Matrix.Diagonal((1, side, 1, 1)))
        stalk = box(mx, ys + 0.02, zbelt + 0.06, 0.06, 0.07, 0.03, 0.01)
        parts.merge("detail", FRIT, stalk, Matrix.Diagonal((1, side, 1, 1)))

    # ---- roof furniture
    if s.get("rails"):
        for side in (-1, 1):
            pts = []
            for i in range(25):
                x = s["roof_r"] + 0.05 + (s["roof_f"] - 0.12 - s["roof_r"]) * i / 24
                y = body.params(x)[7] - 0.07
                pts.append((x, y, body.top_z(x, y)))
            for (x0, y0, z0), (x1, y1, z1) in zip(pts, pts[1:]):
                seg = hull([
                    (x0, y0 - 0.025, z0), (x1, y1 - 0.025, z1), (x1, y1 + 0.025, z1), (x0, y0 + 0.025, z0),
                    (x0, y0 - 0.02, z0 + 0.055), (x1, y1 - 0.02, z1 + 0.055), (x1, y1 + 0.02, z1 + 0.055), (x0, y0 + 0.02, z0 + 0.055),
                ])
                parts.merge("detail", SILVER, seg, Matrix.Diagonal((1, side, 1, 1)))
    if s.get("fin"):
        xfn = s["roof_r"] + 0.25
        zfn = body.top_z(xfn, 0)
        fin = hull([
            (xfn - 0.09, -0.03, zfn - 0.01), (xfn + 0.07, -0.02, zfn - 0.01), (xfn + 0.07, 0.02, zfn - 0.01), (xfn - 0.09, 0.03, zfn - 0.01),
            (xfn - 0.08, -0.008, zfn + 0.06), (xfn - 0.04, -0.006, zfn + 0.065), (xfn - 0.04, 0.006, zfn + 0.065), (xfn - 0.08, 0.008, zfn + 0.06),
        ], bevel=0.008, segments=2)
        parts.merge("detail", FRIT, fin)

    # ---- aero
    wg = s.get("wing")
    if wg:
        # aerofoil section swept across the car, on two swan-neck stands
        prof = [(0.0, 0.0), (0.06, 0.03), (0.16, 0.04), (0.28, 0.03), (0.32, 0.012), (0.30, -0.004), (0.16, -0.01), (0.05, -0.012)]
        c = wg["chord"] / 0.32
        span = wg["span"]
        bm = bmesh.new()
        for side in (-1, 1):
            pass
        ring_a = [bm.verts.new((wg["x"] + wg["chord"] / 2 - px * c, -span / 2, wg["z"] + pz * c - px * c * 0.18)) for px, pz in prof]
        ring_b = [bm.verts.new((v.co.x, span / 2, v.co.z)) for v in ring_a]
        n = len(prof)
        for i in range(n):
            bm.faces.new([ring_a[i], ring_a[(i + 1) % n], ring_b[(i + 1) % n], ring_b[i]])
        bm.faces.new(ring_a)
        bm.faces.new(list(reversed(ring_b)))
        parts.merge("detail", CARBON, bm)
        for side in (-1, 1):
            plate_bm = box(wg["x"] - 0.01, side * (span / 2 + 0.008), wg["z"] - 0.01, wg["chord"] + 0.08, 0.012, 0.16, 0.004)
            parts.merge("detail", CARBON, plate_bm)
            ys2 = side * 0.36
            zdeck = body.top_z(wg["x"] + 0.12, ys2)
            neck = hull([
                (wg["x"] + 0.05, ys2 - 0.012, zdeck - 0.01), (wg["x"] + 0.22, ys2 - 0.012, zdeck - 0.01),
                (wg["x"] + 0.22, ys2 + 0.012, zdeck - 0.01), (wg["x"] + 0.05, ys2 + 0.012, zdeck - 0.01),
                (wg["x"] + 0.04, ys2 - 0.012, wg["z"] + 0.05), (wg["x"] + 0.14, ys2 - 0.012, wg["z"] + 0.05),
                (wg["x"] + 0.14, ys2 + 0.012, wg["z"] + 0.05), (wg["x"] + 0.04, ys2 + 0.012, wg["z"] + 0.05),
            ])
            parts.merge("detail", CARBON, neck)
    if s.get("ducktail"):
        xd = body.xr + 0.10
        zd = body.top_z(xd, 0)
        lip = hull([
            (xd - 0.06, -0.80, zd - 0.02), (xd + 0.16, -0.80, zd - 0.02), (xd + 0.16, 0.80, zd - 0.02), (xd - 0.06, 0.80, zd - 0.02),
            (xd - 0.08, -0.78, zd + 0.045), (xd + 0.02, -0.78, zd + 0.02), (xd + 0.02, 0.78, zd + 0.02), (xd - 0.08, 0.78, zd + 0.045),
        ], bevel=0.01, segments=2)
        parts.merge("detail", CARBON, lip)

    out = parts.finish(col)
    export(out["paint"], f"car_{key}_paint.glb")
    export(out["detail"], f"car_{key}_detail.glb")
    export(out["tail"], f"car_{key}_tail.glb")

    # numbers the game needs (ray-traced stand-in boxes and the shadow footprint)
    cab_x0, cab_x1 = s["roof_r"], s["roof_f"]
    return {"wheelRadius": R, "wheelWidth": tw, "wheelbase": wb, "track": track,
            "length": round(body.xf - body.xr, 3), "width": body.W, "centre": round((body.xf + body.xr) / 2, 3),
            "clearance": s["clr"], "belt": round(body.belt(0), 3), "roof": round(body.zt(0), 3),
            "roofX0": cab_x0, "roofX1": cab_x1, "roofHalfWidth": round(body.params((cab_x0 + cab_x1) / 2)[7], 3),
            "wingHeight": wg["z"] if wg else 0, "wingChord": wg["chord"] if wg else 0, "wingX": wg["x"] if wg else 0,
            "wingSpan": wg["span"] if wg else 0}


def lathe(profile, segments, matrix=Matrix()):
    """Surface of revolution around the Y axis; profile is (radius, y) points."""
    bm = bmesh.new()
    rings = []
    for k in range(segments):
        a = 2 * math.pi * k / segments
        ca, sa = math.cos(a), math.sin(a)
        rings.append([bm.verts.new(matrix @ Vector((r * ca, y, r * sa))) for r, y in profile])
    for k in range(segments):
        r0, r1 = rings[k], rings[(k + 1) % segments]
        for i in range(len(profile) - 1):
            bm.faces.new([r0[i], r1[i], r1[i + 1], r0[i + 1]])
    return bm


def build_wheel():
    """Radius 1 m and width 1 m; the game scales it per car. Axle along Y (game: Z).
    Both faces look the same because the game draws left and right wheels with the same rotation."""
    col = new_collection("wheel")
    acc = {}

    def add(mat, bm, both=False):
        for f in bm.faces:
            f.normal_update()
            if f.normal.dot(f.calc_center_median()) < 0:
                f.normal_flip()
        if both:
            flipped = bm.copy()
            for f in flipped.faces:
                f.normal_flip()
            vmap = {v: bm.verts.new(v.co) for v in flipped.verts}
            for f in flipped.faces:
                bm.faces.new([vmap[v] for v in f.verts])
            flipped.free()
        if mat.name not in acc:
            acc[mat.name] = (bmesh.new(), mat)
        dst = acc[mat.name][0]
        vmap = {v: dst.verts.new(v.co) for v in bm.verts}
        for f in bm.faces:
            dst.faces.new([vmap[v] for v in f.verts])
        bm.free()

    # tyre: rounded shoulders, sidewalls bulging slightly, closed to the rim on both faces
    tyre = [(0.70, 0.43), (0.76, 0.47), (0.86, 0.50), (0.94, 0.48), (0.985, 0.43), (1.0, 0.34),
            (1.0, -0.34), (0.985, -0.43), (0.94, -0.48), (0.86, -0.50), (0.76, -0.47), (0.70, -0.43)]
    add(TYRE, lathe(tyre, 48))
    # rim barrel (seen through the spokes) and the outer lip on both faces
    add(UNDER, lathe([(0.705, -0.42), (0.705, 0.42)], 40), both=True)
    for sgn in (1, -1):
        lip = [(0.705, 0.43 * sgn), (0.72, 0.45 * sgn), (0.70, 0.46 * sgn), (0.66, 0.44 * sgn)]
        if sgn < 0:
            lip = list(reversed(lip))
        add(RIM, lathe(lip, 40))
        # brake disc behind the spokes
        add(STEEL, lathe([(0.20, 0.20 * sgn), (0.56, 0.20 * sgn)] if sgn > 0 else [(0.56, -0.20), (0.20, -0.20)], 32))
        # hub with a centre cap
        add(RIM, lathe([(0.0, 0.40 * sgn), (0.16, 0.38 * sgn), (0.20, 0.34 * sgn), (0.20, 0.25 * sgn)] if sgn > 0
                       else [(0.20, -0.25), (0.20, -0.34), (0.16, -0.38), (0.0, -0.40)], 24))
        # ten split spokes, tapering out to the rim, set back slightly (concave dish)
        for i in range(5):
            for split in (-1, 1):
                a = i * 2 * math.pi / 5 + split * 0.13
                rot = Matrix.Rotation(-a, 4, 'Y')
                y0, y1 = 0.37 * sgn, 0.30 * sgn
                pts = [(0.17, -0.035, y0 - 0.03 * sgn), (0.68, -0.025, y1 - 0.02 * sgn), (0.68, 0.025, y1 - 0.02 * sgn), (0.17, 0.035, y0 - 0.03 * sgn),
                       (0.17, -0.035, y0 + 0.02 * sgn), (0.68, -0.022, y1 + 0.06 * sgn), (0.68, 0.022, y1 + 0.06 * sgn), (0.17, 0.035, y0 + 0.02 * sgn)]
                # local (radius, tangent, axle) -> wheel space (x, axle, z)
                pts = [(r, ax, t) for r, t, ax in pts]
                bm = hull(pts, bevel=0.008, segments=1)
                bmesh.ops.transform(bm, matrix=rot, verts=bm.verts)
                add(RIM, bm)
        # wheel nuts
        for i in range(5):
            a = (i + 0.5) * 2 * math.pi / 5
            m = Matrix.Translation((0.11 * math.cos(a), 0.39 * sgn, 0.11 * math.sin(a))) @ Matrix.Rotation(math.pi / 2, 4, 'X')
            add(STEEL, cylinder(0.022, 0.03, 6, m))
    parts = []
    for name, (bm, mat) in acc.items():
        for f in bm.faces:
            f.smooth = True
        for e in bm.edges:
            if len(e.link_faces) == 2 and e.calc_face_angle(0) > math.radians(40):
                e.smooth = False
        parts.append(make_object(f"wheel_{name}", bm, mat, col))
    export(parts, "wheel.glb")


# ---------------------------------------------------------------- scenery

def build_trees():
    col = new_collection("tree_pine")
    trunk = make_object("pine_trunk", cylinder(0.28, 2.4, 8, Matrix.Translation((0, 0, 1.2))), BARK, col)
    parts = [trunk]
    for i, (z, r, h) in enumerate([(1.8, 2.6, 3.6), (3.8, 2.0, 3.2), (5.6, 1.4, 2.8), (7.2, 0.8, 2.2)]):
        m = Matrix.Translation((0, 0, z + h / 2)) @ Matrix.Rotation(i * 0.4, 4, 'Z')
        parts.append(make_object(f"pine_cone{i}", cylinder(r, h, 9, m, r2=0.0), PINE, col))
    export(parts, "tree_pine.glb")

    col = new_collection("tree_round")
    trunk = make_object("round_trunk", cylinder(0.32, 3.4, 8, Matrix.Translation((0, 0, 1.7)), r2=0.22), BARK, col)
    parts = [trunk]
    for i, (x, y, z, r) in enumerate([(0, 0, 4.6, 2.5), (0.9, 0.6, 5.6, 1.7), (-0.9, -0.5, 5.3, 1.8), (0.2, -0.9, 6.4, 1.4), (-0.4, 0.8, 6.5, 1.3)]):
        parts.append(make_object(f"round_crown{i}", ico(r, Matrix.Translation((x, y, z))), LEAF, col))
    export(parts, "tree_round.glb")


def build_gantry():
    """Start/finish gantry spanning the track. The track is ~15 m wide in game units."""
    col = new_collection("gantry")
    span, height = 19.0, 6.6
    parts = []
    for side in (-1, 1):
        parts.append(make_object(f"pillar{side}", box(0, side * span / 2, height / 2, 0.6, 0.6, height, 0.05), STEEL, col))
        parts.append(make_object(f"foot{side}", box(0, side * span / 2, 0.15, 1.2, 1.2, 0.3, 0.05), STEEL, col))
    parts.append(make_object("beam", box(0, 0, height, 0.7, span + 0.6, 0.7, 0.05), STEEL, col))
    parts.append(make_object("banner", box(0, 0, height - 0.95, 0.25, span * 0.62, 1.2, 0.04), BANNER, col))
    for face in (-1, 1):  # start lights on both sides of the banner
        for i in range(5):
            y = (i - 2) * 1.5
            m = Matrix.Translation((face * 0.16, y, height - 0.9)) @ Matrix.Rotation(math.pi / 2, 4, 'Y')
            parts.append(make_object(f"lamp{face}_{i}", cylinder(0.42, 0.08, 16, m), LAMP, col))
        parts.append(make_object(f"stripe{face}", box(face * 0.13, 0, height - 1.45, 0.02, span * 0.62, 0.12), ACCENT, col))
    export(parts, "gantry.glb")

# ---------------------------------------------------------------- preview render

def render_preview(path):
    scene = bpy.context.scene
    # Lay copies out in a row for a quick look
    offsets = {"car_suv": (0, -7), "car_sedan": (0, -2.4), "car_sports": (0, 2.2), "car_hyper": (0, 6.8),
               "wheel": (-6, -9), "tree_pine": (-12, 0), "tree_round": (-12, 7), "gantry": (-30, 0)}
    for name, (x, y) in offsets.items():
        col = bpy.data.collections.get(name)
        for ob in col.objects:
            ob.location = (x, y, 0)
    for ob in bpy.data.collections["wheel"].objects:
        ob.location = (-6, -9, 1)
        ob.scale = (0.35, 0.28, 0.35)

    cam_data = bpy.data.cameras.new("cam")
    cam = bpy.data.objects.new("cam", cam_data)
    scene.collection.objects.link(cam)
    cam.location = (11, -14, 6)
    direction = Vector((0, 0, 0.5)) - cam.location
    cam.rotation_euler = direction.to_track_quat('-Z', 'Y').to_euler()
    cam_data.lens = 32
    scene.camera = cam

    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", 'SUN'))
    sun.rotation_euler = (math.radians(50), 0, math.radians(30))
    scene.collection.objects.link(sun)

    scene.render.engine = 'BLENDER_WORKBENCH'
    scene.display.shading.light = 'STUDIO'
    scene.display.shading.color_type = 'MATERIAL'
    scene.display.shading.show_shadows = True
    scene.render.resolution_x, scene.render.resolution_y = 1400, 800
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)

# ---------------------------------------------------------------- main

meta = {}
for key, spec in CARS.items():
    meta[key] = build_car(key, spec)
build_wheel()
build_trees()
build_gantry()

with open(os.path.join(OUT, "cars.json"), "w") as f:
    json.dump(meta, f, indent=2)

bpy.ops.wm.save_as_mainfile(filepath=os.path.join(HERE, "models.blend"))

# Optional: --preview <png path>
argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
if "--preview" in argv:
    render_preview(argv[argv.index("--preview") + 1])

print("Exported models to", OUT)
