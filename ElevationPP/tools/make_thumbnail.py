"""Generate the Elevation++ mod thumbnail (512x512 PNG).

Styled after the native Captain of Industry mod thumbnails (Nimb's NoPillars /
RetainingWalls, gameplay++): an isometric in-game-style render of the actual
structure on a tiled construction lot, a dark vignette frame, and a bold white
title with a heavy black outline. Here the structure is an elevated train
station on tall support pillars. Rendered at 3x and LANCZOS-downscaled.

Run from anywhere; writes ../src/thumbnail.png.
"""
import os
import numpy as np
from PIL import Image, ImageDraw, ImageFont

S = 3                       # supersample factor
W = 512
N = W * S                   # working canvas size

# --- isometric projection ----------------------------------------------------
HW = 34 * S                 # tile half-width
HH = 17 * S                 # tile half-height (2:1 iso)
ZH = 30 * S                 # pixels per unit of height
OX = 256 * S                # screen origin
OY = 150 * S


def iso(x, y, z):
    return (OX + (x - y) * HW, OY + (x + y) * HH - z * ZH)


def shade(rgb, f):
    return tuple(max(0, min(255, int(c * f))) for c in rgb)


def box(d, x0, y0, z0, dx, dy, dz, base):
    """Draw an isometric box with shaded top / right / left faces."""
    top = [iso(x0, y0, z0 + dz), iso(x0 + dx, y0, z0 + dz),
           iso(x0 + dx, y0 + dy, z0 + dz), iso(x0, y0 + dy, z0 + dz)]
    right = [iso(x0 + dx, y0, z0 + dz), iso(x0 + dx, y0 + dy, z0 + dz),
             iso(x0 + dx, y0 + dy, z0), iso(x0 + dx, y0, z0)]
    left = [iso(x0, y0 + dy, z0 + dz), iso(x0 + dx, y0 + dy, z0 + dz),
            iso(x0 + dx, y0 + dy, z0), iso(x0, y0 + dy, z0)]
    d.polygon(left, fill=shade(base, 0.62))
    d.polygon(right, fill=shade(base, 0.82))
    d.polygon(top, fill=shade(base, 1.06))


img = Image.new("RGB", (N, N), (26, 24, 22))
d = ImageDraw.Draw(img)

# --- background: warm dark industrial gradient -------------------------------
top_c, bot_c = (54, 50, 46), (20, 18, 17)
for y in range(N):
    t = y / N
    d.line([(0, y), (N, y)],
           fill=tuple(int(top_c[i] + (bot_c[i] - top_c[i]) * t) for i in range(3)))

# --- ground construction lot (slab with depth + tile grid) -------------------
G = 6                                   # grid size in tiles
dirt = (150, 128, 92)
box(d, 0, 0, -0.5, G, G, 0.5, dirt)     # slab
for i in range(G + 1):                  # tile grid on top face
    d.line([iso(i, 0, 0), iso(i, G, 0)], fill=shade(dirt, 0.78), width=S)
    d.line([iso(0, i, 0), iso(G, i, 0)], fill=shade(dirt, 0.78), width=S)

# --- pillars, elevated deck, rails, station building -------------------------
concrete = (122, 126, 134)
PILLAR_X = [0.7, 3.0, 5.3]
PH = 3.0                                 # pillar height
for cx in PILLAR_X:
    box(d, cx - 0.35, 2.55, 0.0, 0.7, 1.0, PH, concrete)

deck = (104, 108, 118)
DT = PH + 0.32                                          # deck top height
box(d, 0.2, 2.45, PH, 5.6, 1.30, 0.32, deck)           # wide elevated platform

# station building set toward the BACK of the platform (drawn first)
wall = (198, 166, 102)
box(d, 1.7, 2.58, DT, 2.0, 0.55, 0.86, wall)
box(d, 1.65, 2.53, DT + 0.86, 2.1, 0.65, 0.14, (176, 102, 56))   # roof overhang

# track runs along the FRONT edge, passing in front of the building (drawn last)
rail = (188, 193, 200)
for tie in range(0, 11):                               # sleepers under the rails
    tx = 0.4 + tie * 0.5
    box(d, tx, 3.24, DT - 0.02, 0.12, 0.36, 0.04, (96, 74, 56))
for ry in (3.28, 3.54):                                # two rails = one track
    box(d, 0.3, ry, DT, 5.4, 0.07, 0.07, rail)

# --- vignette ---------------------------------------------------------------
yy, xx = np.mgrid[0:N, 0:N]
cx = cy = N / 2
r = np.sqrt((xx - cx) ** 2 + (yy - cy) ** 2) / (N / 2 * 1.18)
vig = np.clip(1.0 - r ** 2.4 * 0.85, 0.18, 1.0)
arr = (np.asarray(img).astype(np.float32) * vig[..., None]).astype(np.uint8)
img = Image.fromarray(arr)
d = ImageDraw.Draw(img)

# --- frame border (bevelled, like the Nimb thumbnails) ----------------------
m = 10 * S
d.rounded_rectangle([m, m, N - m, N - m], radius=14 * S,
                    outline=(14, 12, 11), width=6 * S)
d.rounded_rectangle([m + 6 * S, m + 6 * S, N - m - 6 * S, N - m - 6 * S],
                    radius=10 * S, outline=(96, 86, 72), width=S)

# --- title: "Elevation" white + "++" green, heavy black stroke ---------------
font = ImageFont.truetype("C:/Windows/Fonts/segoeuib.ttf", 56 * S)
stroke = 7 * S
t1, t2 = "Elevation", "++"
w1 = d.textlength(t1, font=font)
w2 = d.textlength(t2, font=font)
x = (N - (w1 + w2)) / 2
ty = 26 * S
d.text((x, ty), t1, font=font, fill=(245, 245, 245),
       stroke_width=stroke, stroke_fill=(12, 11, 10))
d.text((x + w1, ty), t2, font=font, fill=(104, 214, 132),
       stroke_width=stroke, stroke_fill=(12, 11, 10))

# --- corner compass mark (bottom-right), echoing the Nimb thumbnails --------
ccx, ccy, cr = N - 40 * S, N - 40 * S, 12 * S
d.polygon([(ccx, ccy - cr), (ccx + cr * 0.4, ccy), (ccx, ccy + cr),
           (ccx - cr * 0.4, ccy)], fill=(190, 180, 165))
d.polygon([(ccx - cr, ccy), (ccx, ccy - cr * 0.4), (ccx + cr, ccy),
           (ccx, ccy + cr * 0.4)], fill=(150, 140, 128))

# --- downscale & save -------------------------------------------------------
out = img.resize((W, W), Image.LANCZOS)
dst = os.path.join(os.path.dirname(__file__), "..", "src", "thumbnail.png")
out.save(os.path.abspath(dst))
print("wrote", os.path.abspath(dst), out.size)
