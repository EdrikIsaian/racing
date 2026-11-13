# Turns the head-car photo (blender/headcar_source.png, a side view of the car) into what the game
# needs to build the 3D head car:
#
#   python3 RacingGame/blender/build_headcar.py        (needs Pillow + numpy)
#
# Output (RacingGame/assets/models/):
#   headcar.png   the photo, cleaned up: the photo's own wheels and the gaps around them are painted
#                 as dark wheel arches (the game draws real 3D wheels there), white background showing
#                 through inside the head is filled in, and a stray UI button is removed.
#   headcar_icon.png  the original photo on a transparent background, for the menu
#   headcar.json  side profile of the head (top / bottom edge per pixel column), wheel positions and
#                 the size of a pixel in metres. Scene3D builds a rounded 3D body from the profile and
#                 projects the photo onto both sides.

import json
import os
import numpy as np
from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.normpath(os.path.join(HERE, "..", "assets", "models"))

img = np.asarray(Image.open(os.path.join(HERE, "headcar_source.png")).convert("RGB")).astype(np.int32)
H, W, _ = img.shape

# Measured on the photo (pixels): ground line, sill line under the head, and the two wheels.
GROUND_Y = 405
SILL_Y = 380
WHEELS = [(134, 352, 54), (599, 352, 52)]  # x, y, radius (rear, front)
LENGTH_M = 4.7                              # how long the car is in the game

lo, hi = img.min(axis=2), img.max(axis=2)
fg = (lo < 215) | ((hi - lo) > 35)          # anything that isn't white background
yy, xx = np.mgrid[0:H, 0:W]

# Instagram's "<" carousel button sits on the back of the head: count it as head
BUTTON = (22, 228, 17)
in_button = (xx - BUTTON[0]) ** 2 + (yy - BUTTON[1]) ** 2 < BUTTON[2] ** 2
fg |= in_button & (xx >= 9)

# The photo's wheels (with a margin for the tyres' perspective) don't count as head
in_wheel = np.zeros_like(fg)
for wx, wy, r in WHEELS:
    in_wheel |= (xx - wx) ** 2 + (yy - wy) ** 2 < (r + 10) ** 2
head = fg & ~in_wheel & (yy <= SILL_Y)

cols = np.where(head.any(axis=0))[0]
x0, x1 = int(cols[0]), int(cols[-1])

top = np.full(W, np.nan)
bottom = np.full(W, np.nan)
for x in range(x0, x1 + 1):
    ys = np.where(head[:, x])[0]
    if len(ys) == 0:
        continue
    top[x], bottom[x] = ys[0], ys[-1]
    # Above the wheels the body stops at the wheel arch (so the 3D wheels have room)
    for wx, wy, r in WHEELS:
        ra = r + 6
        if abs(x - wx) < ra:
            bottom[x] = min(bottom[x], wy - np.sqrt(ra * ra - (x - wx) ** 2))

# Fill any missing columns and smooth out JPEG jaggies
idx = np.arange(W)
ok = ~np.isnan(top)
top = np.interp(idx, idx[ok], top[ok])
bottom = np.interp(idx, idx[ok], bottom[ok])
k = np.ones(7) / 7
top_s = np.convolve(np.pad(top, 3, mode="edge"), k, mode="valid")
bottom_s = np.convolve(np.pad(bottom, 3, mode="edge"), k, mode="valid")

# ---------------------------------------------------------------- texture clean-up
tex = img.copy()

# The button: clone skin from just to its right
by, bx = np.where(in_button)
tex[by, bx] = img[by, bx + 2 * BUTTON[2] + 4]

# Photo wheels → dark arches
ARCH = np.array([26, 20, 20])
for wx, wy, r in WHEELS:
    tex[(xx - wx) ** 2 + (yy - wy) ** 2 < (r + 12) ** 2] = ARCH

# White background inside the head outline (under the nose, between head and wheels) → nearest
# colour to the left. The teeth band stays white.
inside = (yy >= top_s[None, :] - 1) & (yy <= bottom_s[None, :] + 1)
teeth = (yy > 262) & (yy < 292) & (xx > 540)
lo2, hi2 = tex.min(axis=2), tex.max(axis=2)
white = (lo2 >= 215) & ((hi2 - lo2) <= 35)
holes = inside & white & ~teeth
for y in range(H):
    row = holes[y]
    if not row.any():
        continue
    last = None
    for x in range(W):
        if row[x]:
            if last is not None:
                tex[y, x] = last
        else:
            last = tex[y, x].copy()
    # holes at the start of a row: take the first good colour to their right
    first = next((x for x in range(W) if not row[x]), None)
    if first is not None:
        for x in range(first):
            if row[x]:
                tex[y, x] = tex[y, first]

# Menu icon: the photo as it is (wheels and all) on a transparent background
icon = np.dstack([img.copy(), np.where(fg, 255, 0)]).astype(np.uint8)
by, bx = np.where(in_button)
icon[by, bx, :3] = img[by, bx + 2 * BUTTON[2] + 4]
Image.fromarray(icon).save(os.path.join(OUT, "headcar_icon.png"))

# The back of the head is seen end-on (it's what you look at while racing), where the side
# projection stretches its pixels into stripes: blur that part of the photo vertically
from PIL import ImageFilter
back = Image.fromarray(tex[:, x0:x0 + 60].astype(np.uint8))
back = np.asarray(back.filter(ImageFilter.GaussianBlur(7))).astype(np.int32)
ramp = np.clip((np.arange(60) - 30) / 30, 0, 1)[None, :, None]   # fade back to sharp by the ear
tex[:, x0:x0 + 60] = (back * (1 - ramp) + tex[:, x0:x0 + 60] * ramp).astype(np.int32)

# Outside the head, repeat the edge colours outwards so that blurry (far away, mipmapped)
# texture lookups near the outline don't pick up white background
for x in range(W):
    xc = min(max(x, x0 + 3), x1 - 4)
    t, b = int(top_s[xc]) + 4, int(bottom_s[xc]) - 2
    tex[:t, x] = tex[t, xc]
    tex[b:, x] = tex[b, xc]
    if x != xc:
        tex[t:b, x] = tex[t:b, xc]

Image.fromarray(tex.astype(np.uint8)).save(os.path.join(OUT, "headcar.png"))

# ---------------------------------------------------------------- profile
mpp = LENGTH_M / (x1 - x0)
meta = {
    "imageWidth": W, "imageHeight": H,
    "groundY": GROUND_Y,
    "metresPerPixel": mpp,
    "x0": x0, "x1": x1,
    "top": [round(float(v), 2) for v in top_s[x0:x1 + 1]],
    "bottom": [round(float(v), 2) for v in bottom_s[x0:x1 + 1]],
    "wheels": [{"x": wx, "y": wy, "r": r} for wx, wy, r in WHEELS],
    "skin": [int(v) for v in np.median(tex[head], axis=0)],
}
with open(os.path.join(OUT, "headcar.json"), "w") as f:
    json.dump(meta, f)

print(f"head car: {x1 - x0} px long, {mpp * 1000:.2f} mm/px, height {(GROUND_Y - top_s[x0:x1].min()) * mpp:.2f} m")
