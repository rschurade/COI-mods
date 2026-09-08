"""Generate the ModConfig++ mod thumbnail (512x512 PNG).

Same style as the Elevation++ / Shipping++ / Excavator++ thumbnails (which follow
the native Captain of Industry mod thumbnails): an isometric in-game-style render
of the mod's subject on a tiled lot, a dark vignette frame, and a bold white title
with a heavy black outline and a green "++". Here the scene is the mod's subject:
a control console with a lit screen and buttons, and a bank of slider levers set
to different values — settings being adjusted. Rendered at 3x and
LANCZOS-downscaled.

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

# --- control-room floor (slab with depth + tile grid) ------------------------
G = 6                                   # grid size in tiles
floor = (128, 126, 122)                 # concrete control-room floor
box(d, 0, 0, -0.5, G, G, 0.5, floor)    # floor slab
for i in range(G + 1):                  # subtle tile grid
    d.line([iso(i, 0, 0), iso(i, G, 0)], fill=shade(floor, 0.9), width=S)
    d.line([iso(0, i, 0), iso(G, i, 0)], fill=shade(floor, 0.9), width=S)

steel = (86, 90, 98)
dark = (58, 60, 66)
glass = (64, 132, 168)
green = (96, 196, 110)
red = (204, 74, 58)
amber = (228, 176, 60)
knob = (224, 224, 218)

# --- main console (right): cabinet, lit screen facing the viewer, buttons ----
box(d, 3.10, 0.95, 0, 2.30, 1.55, 0.85, steel)             # cabinet
box(d, 3.18, 1.03, 0.85, 2.14, 1.39, 0.10, dark)           # desk top
box(d, 4.65, 1.10, 0.95, 0.65, 1.30, 1.20, dark)           # screen back panel
box(d, 4.56, 1.18, 1.15, 0.14, 1.14, 0.85, glass)          # lit screen, facing viewer
# rows of "config value" bars on the screen face
for i, ln in enumerate((0.90, 0.55, 0.75, 0.40)):
    box(d, 4.70, 1.28, 1.82 - i * 0.20, 0.015, ln, 0.06, knob)
# button rows on the desk
for i, c in enumerate((green, amber, red)):
    box(d, 3.42 + i * 0.44, 1.62, 0.95, 0.26, 0.26, 0.11, c)
    box(d, 3.42 + i * 0.44, 1.16, 0.95, 0.26, 0.26, 0.09, shade(steel, 1.25))
# a chunky main lever on the near desk corner, away from the screen
box(d, 3.42, 2.12, 0.95, 0.18, 0.18, 0.40, dark)
box(d, 3.34, 2.04, 1.35, 0.34, 0.34, 0.16, red)

# --- slider bank (bottom-left): levers set to different values ---------------
box(d, 1.10, 2.55, 0, 1.20, 2.25, 0.60, steel)             # slider pedestal
box(d, 1.18, 2.63, 0.60, 1.04, 2.09, 0.09, dark)           # panel top
sliders = ((1.05, green), (0.55, amber), (1.35, green), (0.80, red))
for i, (h, c) in enumerate(sliders):
    sy = 2.78 + i * 0.48
    box(d, 1.62, sy, 0.69, 0.16, 0.16, 1.60, dark)         # track post
    box(d, 1.52, sy - 0.10, 0.69 + h, 0.36, 0.36, 0.18, c) # knob at its value
# indicator lights along the pedestal front
for i in range(4):
    box(d, 1.27, 2.80 + i * 0.48, 0.60, 0.14, 0.14, 0.08, (green, amber, green, red)[i])

# --- gear totem (bottom-right accent): stacked steel discs -------------------
box(d, 4.15, 3.30, 0, 1.00, 1.00, 0.24, dark)
box(d, 4.27, 3.42, 0.24, 0.76, 0.76, 0.20, steel)
box(d, 4.39, 3.54, 0.44, 0.52, 0.52, 0.18, dark)
box(d, 4.49, 3.64, 0.62, 0.32, 0.32, 0.15, amber)

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

# --- title: "ModConfig" white + "++" green, heavy black stroke ---------------
font = ImageFont.truetype("C:/Windows/Fonts/segoeuib.ttf", 52 * S)
stroke = 7 * S
t1, t2 = "ModConfig", "++"
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
