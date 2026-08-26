"""Generate the Excavator++ mod thumbnail (512x512 PNG).

Same style as the Elevation++ / Shipping++ thumbnails (which follow the native
Captain of Industry mod thumbnails): an isometric in-game-style render of the
mod's subject on a tiled lot, a dark vignette frame, and a bold white title
with a heavy black outline and a green "++". Here the scene is the mod's
subject: an excavator loading a dump truck with a full MIXED load (coal +
limestone) next to its dig site. Rendered at 3x and LANCZOS-downscaled.

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

# --- terrain lot (slab with depth + tile grid, like the construction lot) ----
G = 6                                   # grid size in tiles
dirt = (181, 155, 106)
box(d, 0, 0, -0.5, G, G, 0.5, dirt)     # ground slab
for i in range(G + 1):                  # subtle tile grid
    d.line([iso(i, 0, 0), iso(i, G, 0)], fill=shade(dirt, 0.9), width=S)
    d.line([iso(0, i, 0), iso(G, i, 0)], fill=shade(dirt, 0.9), width=S)

# --- dig site (right): dark excavated pit the excavator is working -----------
pit = shade(dirt, 0.55)
d.polygon([iso(4.30, 0.85, 0), iso(5.80, 0.85, 0), iso(5.80, 2.35, 0),
           iso(4.30, 2.35, 0)], fill=pit)
d.polygon([iso(4.60, 1.15, 0), iso(5.55, 1.15, 0), iso(5.55, 2.10, 0),
           iso(4.60, 2.10, 0)], fill=shade(dirt, 0.42))
coal = (45, 44, 48)
limestone = (208, 203, 186)
glass = (52, 66, 82)
box(d, 4.40, 2.45, 0, 0.28, 0.28, 0.20, coal)        # stray coal chunk
box(d, 4.85, 2.57, 0, 0.24, 0.24, 0.16, limestone)   # stray limestone chunk

# --- excavator (top), boom reaching right into the pit -----------------------
yellow = (224, 172, 44)
track = (62, 62, 68)
steel = (104, 106, 112)
box(d, 2.32, 1.19, 0.60, 0.20, 0.78, 0.48, shade(yellow, 0.8))  # counterweight
box(d, 2.45, 0.97, 0, 1.60, 0.40, 0.34, track)       # far track
box(d, 2.45, 1.75, 0, 1.60, 0.40, 0.34, track)       # near track
box(d, 2.52, 1.05, 0.34, 1.50, 1.05, 0.20, steel)    # slew deck
box(d, 2.45, 1.09, 0.54, 1.45, 0.98, 0.66, yellow)   # house
box(d, 3.88, 1.27, 0.72, 0.10, 0.62, 0.42, glass)    # cab glass, facing the pit
box(d, 3.86, 1.35, 0.96, 1.12, 0.30, 0.26, yellow)   # boom, out over the pit
box(d, 4.90, 1.37, 0.30, 0.24, 0.26, 0.68, shade(yellow, 0.92))  # stick, down
box(d, 4.78, 1.25, 0.02, 0.55, 0.50, 0.30, (84, 86, 92))         # bucket digging
box(d, 4.88, 1.35, 0.32, 0.36, 0.30, 0.14, coal)     # scoop coming up full

# --- dump truck (bottom-left), hauling off a FULL mixed load -----------------
truck_bed = (116, 120, 128)
box(d, 1.55, 2.70, 0, 0.82, 2.00, 0.32, track)         # wheels / chassis
box(d, 1.47, 2.65, 0.32, 0.98, 1.40, 0.52, truck_bed)  # dump bed
box(d, 1.53, 2.71, 0.84, 0.86, 0.62, 0.24, coal)       # coal half of the load
box(d, 1.53, 3.35, 0.84, 0.86, 0.62, 0.30, limestone)  # limestone half, heaped
box(d, 1.50, 4.07, 0.32, 0.92, 0.60, 0.62, yellow)     # cab
box(d, 1.57, 4.63, 0.62, 0.78, 0.10, 0.32, glass)      # windshield

# --- small mixed-ore heap (bottom-right accent) ------------------------------
box(d, 4.35, 3.30, 0, 0.50, 0.45, 0.26, coal)
box(d, 4.65, 3.62, 0, 0.30, 0.30, 0.18, limestone)
box(d, 4.20, 3.66, 0, 0.26, 0.26, 0.15, limestone)

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

# --- title: "Excavator" white + "++" green, heavy black stroke ---------------
font = ImageFont.truetype("C:/Windows/Fonts/segoeuib.ttf", 52 * S)
stroke = 7 * S
t1, t2 = "Excavator", "++"
w1 = d.textlength(t1, font=font)
w2 = d.textlength(t2, font=font)
x = (N - (w1 + w2)) / 2
ty = 28 * S
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
