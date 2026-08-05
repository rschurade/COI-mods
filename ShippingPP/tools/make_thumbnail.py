"""Generate the Shipping++ mod thumbnail (512x512 PNG).

Same style as the Elevation++ thumbnail (which follows the native Captain of
Industry mod thumbnails): an isometric in-game-style render of the mod's
subject on a tiled lot, a dark vignette frame, and a bold white title with a
heavy black outline and a green "++". Here the scene is local shipping: a
dock with a small terminal, a cargo ship on the water and a red navigation
buoy (the mod's own model). Rendered at 3x and LANCZOS-downscaled.

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

# --- ocean lot (slab with depth + tile grid, like the construction lot) ------
G = 6                                   # grid size in tiles
water = (54, 108, 138)
box(d, 0, 0, -0.5, G, G, 0.5, water)    # water slab
for i in range(G + 1):                  # subtle tile grid on the water
    d.line([iso(i, 0, 0), iso(i, G, 0)], fill=shade(water, 0.9), width=S)
    d.line([iso(0, i, 0), iso(G, i, 0)], fill=shade(water, 0.9), width=S)

# --- dock: concrete pier at the back-left with a small terminal --------------
concrete = (122, 126, 134)
box(d, 0.0, 0.0, 0.0, 2.6, 1.5, 0.42, concrete)        # pier slab over the water
box(d, 0.15, 0.12, 0.42, 1.1, 0.9, 0.75, (198, 166, 102))   # terminal building
box(d, 0.10, 0.07, 1.17, 1.2, 1.0, 0.13, (176, 102, 56))    # roof overhang

# small dockside crane on the pier edge
crane = (224, 178, 48)
box(d, 1.95, 0.75, 0.42, 0.28, 0.28, 1.5, crane)       # mast
box(d, 2.02, 0.82, 1.80, 0.14, 1.6, 0.14, crane)       # jib reaching over the water

# --- cargo ship heading front-right ------------------------------------------
hull = (128, 58, 46)
deck = (150, 74, 58)
DKT = 0.62                                             # deck top height
box(d, 2.05, 2.85, 0.06, 2.9, 1.05, 0.44, hull)        # hull sitting in the water
box(d, 2.15, 2.90, 0.50, 2.7, 0.95, 0.12, deck)        # deck rim
box(d, 4.95, 3.08, 0.06, 0.5, 0.6, 0.38, hull)         # bow step

# white superstructure with funnel at the stern (furthest back — drawn first)
box(d, 2.25, 2.98, DKT, 0.7, 0.8, 0.85, (224, 222, 214))
box(d, 2.42, 3.12, DKT + 0.85, 0.3, 0.3, 0.35, (60, 60, 64))

# containers amidships (the cargo!), back to front then the stacked one on top
box(d, 3.05, 3.02, DKT, 0.75, 0.72, 0.5, (96, 158, 92))
box(d, 3.85, 3.02, DKT, 0.75, 0.72, 0.5, (198, 122, 60))
box(d, 3.45, 3.10, DKT + 0.5, 0.75, 0.6, 0.45, (86, 118, 168))

# --- navigation buoy front-right (the mod's buoy: red, white band, mast) -----
BX, BY = 5.05, 4.75
buoy_red = (196, 44, 38)
box(d, BX, BY, 0.02, 0.42, 0.42, 0.28, buoy_red)                 # float barrel
box(d, BX + 0.03, BY + 0.03, 0.30, 0.36, 0.36, 0.20, (238, 236, 226))  # white band
box(d, BX + 0.10, BY + 0.10, 0.50, 0.22, 0.22, 0.14, buoy_red)   # tapered top
box(d, BX + 0.17, BY + 0.17, 0.64, 0.08, 0.08, 0.42, (66, 66, 72))     # mast
box(d, BX + 0.14, BY + 0.14, 1.06, 0.14, 0.14, 0.14, buoy_red)   # light housing

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

# --- title: "Shipping" white + "++" green, heavy black stroke ----------------
font = ImageFont.truetype("C:/Windows/Fonts/segoeuib.ttf", 56 * S)
stroke = 7 * S
t1, t2 = "Shipping", "++"
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
