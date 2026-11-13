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
GLASS = material("Glass", srgb(28, 36, 50), rough=0.05, metal=0.3)
TRIM = material("Trim", srgb(22, 22, 26), rough=0.6)
GRILLE = material("Grille", srgb(12, 12, 14), rough=0.8)
HEAD = material("HeadLight", srgb(255, 248, 226), rough=0.1)
CHROME = material("Chrome", srgb(196, 200, 208), rough=0.2, metal=0.9)
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

# L, W: body size. clr: ground clearance. top: body (shoulder) height.
# nose: height of the front edge of the bonnet. cab: cabin rear/front x, roof height, rake (windscreen lean), taper (side lean).
# wheel: radius, width, wheelbase, track. extras: spoiler, intakes, roof rails.
CARS = {
    "suv":    dict(L=4.60, W=1.95, clr=0.24, top=1.00, nose=0.86, cab=(-1.55, 0.95, 1.66, 0.40, 0.10),
                   wheel=(0.38, 0.27, 2.80, 1.68), spoiler=None, intakes=False, rails=True),
    "sedan":  dict(L=4.70, W=1.84, clr=0.15, top=0.80, nose=0.66, cab=(-1.30, 0.85, 1.42, 0.60, 0.14),
                   wheel=(0.33, 0.24, 2.75, 1.58), spoiler=None, intakes=False, rails=False),
    "sports": dict(L=4.50, W=1.88, clr=0.11, top=0.74, nose=0.52, cab=(-1.05, 0.55, 1.28, 0.70, 0.17),
                   wheel=(0.34, 0.28, 2.45, 1.62), spoiler=(0.95, 0.30), intakes=False, rails=False),
    "hyper":  dict(L=4.75, W=2.00, clr=0.09, top=0.70, nose=0.42, cab=(-0.80, 0.70, 1.13, 0.80, 0.22),
                   wheel=(0.35, 0.30, 2.70, 1.70), spoiler=(1.10, 0.38), intakes=True, rails=False),
}


def build_car(key, s):
    col = new_collection(f"car_{key}")
    L, W, clr, top, nose = s["L"], s["W"], s["clr"], s["top"], s["nose"]
    hx, hy = L / 2, W / 2
    paint, detail, tail = [], [], []

    # Lower body: full footprint at the bottom, bonnet slopes down to the nose, tail slightly lower.
    rear_top = top - 0.06
    body = hull([
        (-hx, -hy, clr), (hx, -hy, clr), (hx, hy, clr), (-hx, hy, clr),
        (-hx + 0.08, -hy + 0.06, rear_top), (hx - 0.10, -hy + 0.08, nose),
        (hx - 0.10, hy - 0.08, nose), (-hx + 0.08, hy - 0.06, rear_top),
    ], bevel=0.09, segments=3)
    paint.append(make_object(f"{key}_body", body, WHITE, col))

    # Cabin (glass greenhouse) with a painted roof on top
    cx0, cx1, roof, rake, taper = s["cab"]
    cab_w = hy - 0.12
    z_base = top - 0.08
    greenhouse = hull([
        (cx0, -cab_w, z_base), (cx1 + 0.25, -cab_w, z_base), (cx1 + 0.25, cab_w, z_base), (cx0, cab_w, z_base),
        (cx0 + rake * 0.45, -cab_w + taper, roof), (cx1 - rake * 0.55, -cab_w + taper, roof),
        (cx1 - rake * 0.55, cab_w - taper, roof), (cx0 + rake * 0.45, cab_w - taper, roof),
    ], bevel=0.05, segments=2)
    detail.append(make_object(f"{key}_glass", greenhouse, GLASS, col))

    rx0, rx1, rw = cx0 + rake * 0.45 + 0.08, cx1 - rake * 0.55 - 0.05, cab_w - taper - 0.03
    roof_bm = hull([
        (rx0 - 0.05, -rw, roof - 0.03), (rx1, -rw, roof - 0.03), (rx1, rw, roof - 0.03), (rx0 - 0.05, rw, roof - 0.03),
        (rx0, -rw + 0.04, roof + 0.035), (rx1 - 0.04, -rw + 0.04, roof + 0.035),
        (rx1 - 0.04, rw - 0.04, roof + 0.035), (rx0, rw - 0.04, roof + 0.035),
    ], bevel=0.02, segments=1)
    paint.append(make_object(f"{key}_roof", roof_bm, WHITE, col))

    # Pillars between the windows (B-pillar), in body colour
    bx = (cx0 + cx1) / 2 - 0.05
    for side in (-1, 1):
        pil = box(bx, side * (cab_w - taper / 2 - 0.005), (z_base + roof) / 2, 0.10, taper + 0.03, roof - z_base - 0.02)
        paint.append(make_object(f"{key}_pillar{side}", pil, WHITE, col))

    # Side skirts, front splitter, rear diffuser
    for side in (-1, 1):
        detail.append(make_object(f"{key}_skirt{side}", box(0, side * (hy - 0.02), clr + 0.06, L * 0.62, 0.06, 0.12, 0.02), TRIM, col))
    detail.append(make_object(f"{key}_splitter", box(hx - 0.06, 0, clr + 0.03, 0.22, W - 0.18, 0.06, 0.02), TRIM, col))
    detail.append(make_object(f"{key}_diffuser", box(-hx + 0.05, 0, clr + 0.08, 0.14, W - 0.4, 0.16, 0.02), TRIM, col))

    # Grille + headlights on the nose, tail lights at the back
    nose_x = hx - 0.06
    detail.append(make_object(f"{key}_grille", box(nose_x, 0, (clr + nose) / 2, 0.08, W * 0.42, (nose - clr) * 0.42, 0.015), GRILLE, col))
    for side in (-1, 1):
        detail.append(make_object(f"{key}_head{side}", box(hx - 0.14, side * (hy - 0.32), nose - 0.07, 0.22, 0.42, 0.08, 0.02), HEAD, col))
        tail.append(make_object(f"{key}_tail{side}", box(-hx + 0.05, side * (hy - 0.30), rear_top - 0.1, 0.1, 0.46, 0.09, 0.015), TAIL, col))
    # exhausts
    for side in (-1, 1):
        m = Matrix.Translation((-hx - 0.02, side * 0.4, clr + 0.12)) @ Matrix.Rotation(math.pi / 2, 4, 'Y')
        detail.append(make_object(f"{key}_exhaust{side}", cylinder(0.05, 0.12, 12, m), CHROME, col))

    # Mirrors
    for side in (-1, 1):
        mx = cx1 + 0.05
        detail.append(make_object(f"{key}_mirror{side}", box(mx, side * (hy + 0.06), z_base + 0.12, 0.12, 0.16, 0.09, 0.02), TRIM, col))

    if s["intakes"]:
        for side in (-1, 1):
            detail.append(make_object(f"{key}_intake{side}", box(-0.35, side * (hy - 0.01), (clr + top) / 2, 0.7, 0.04, (top - clr) * 0.45, 0.02), GRILLE, col))

    if s["rails"]:
        for side in (-1, 1):
            detail.append(make_object(f"{key}_rail{side}", box((rx0 + rx1) / 2, side * (rw - 0.08), roof + 0.06, rx1 - rx0 - 0.1, 0.04, 0.04), TRIM, col))

    if s["spoiler"]:
        height, chord = s["spoiler"]
        wx = -hx + 0.25
        span = W - 0.06 if key == "hyper" else W - 0.2
        detail.append(make_object(f"{key}_wing", box(wx, 0, height, chord, span, 0.05, 0.02), TRIM, col))
        paint.append(make_object(f"{key}_wingtip", box(wx - chord * 0.3, 0, height + 0.03, chord * 0.35, span - 0.02, 0.025, 0.01), WHITE, col))
        for side in (-1, 1):
            detail.append(make_object(f"{key}_strut{side}", box(wx + 0.02, side * 0.42, (rear_top + height) / 2, 0.08, 0.04, height - rear_top + 0.02), TRIM, col))
            detail.append(make_object(f"{key}_endplate{side}", box(wx, side * span / 2, height + 0.02, chord + 0.04, 0.03, 0.16), TRIM, col))

    export(paint, f"car_{key}_paint.glb")
    export(detail, f"car_{key}_detail.glb")
    export(tail, f"car_{key}_tail.glb")
    r, w, wb, tr = s["wheel"]
    return {"wheelRadius": r, "wheelWidth": w, "wheelbase": wb, "track": tr, "length": L, "width": W}


def build_wheel():
    """Radius 1 m and width 1 m; the game scales it per car. Axle along Y (game: Z)."""
    col = new_collection("wheel")
    rot = Matrix.Rotation(math.pi / 2, 4, 'X')
    tyre = make_object("tyre", cylinder(1.0, 1.0, 28, rot), TYRE, col)
    rim = make_object("rim", cylinder(0.66, 1.04, 20, rot), RIM, col)
    hub = make_object("hub", cylinder(0.2, 1.1, 10, rot), CHROME, col)
    # spokes so you can see the wheel turn
    parts = [tyre, rim, hub]
    for i in range(5):
        a = i * 2 * math.pi / 5
        m = Matrix.Rotation(a, 4, 'Y') @ Matrix.Translation((0, 0, 0.36))
        bm = box(0, 0, 0, 0.16, 1.08, 0.62)
        bmesh.ops.transform(bm, matrix=m, verts=bm.verts)
        parts.append(make_object(f"spoke{i}", bm, GRILLE, col))
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
