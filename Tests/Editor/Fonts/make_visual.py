"""
Phase 1c visual proof: Silkscreen pixel font rendered pixel-perfect (Mono, 0/255) vs Smooth
(grayscale AA) at 1x/2x/3x of the font's native 8 px/em, nearest-upscaled so per-texel crispness
is visible. Uses Pillow's bundled FreeType:
  fontmode "1" -> 1-bit mono, no anti-aliasing   (mirrors MonoGlyphRenderer's 0/255 output)
  fontmode "L" -> grayscale anti-aliasing         (mirrors the Smooth atlas)
The pixel-perfect column rasterises ONCE at native ppem then nearest-upscales (what the engine
does with a point-filtered atlas + integer snap). The Smooth column rasterises at each target
ppem with AA; at fractional intermediate sizes AA fringing/blur appears, which is exactly what
pixel-perfect mode avoids.
"""
import os
from PIL import Image, ImageDraw, ImageFont

FONT = os.path.join(os.path.dirname(__file__), "Silkscreen-Regular.ttf") \
    if "__file__" in globals() else r"C:\Users\PlatformTeam-SRE\workspace\openglyph\phase1c-pixel\Tests\Editor\Fonts\Silkscreen-Regular.ttf"
OUT = os.environ.get("KIROCREW_SCRATCH", ".") + os.sep + "phase1c_pixelperfect_vs_smooth.png"

NATIVE = 8          # detected native px/em
TEXT = "Ag5!"       # mixed ascender/descender/digit/punct
SCALES = [1, 2, 3]  # display multiples
DISPLAY = 12        # each raster texel drawn as DISPLAY device px for on-screen visibility
PAD = 6

def raster(ppem, mono):
    f = ImageFont.truetype(FONT, ppem)
    # measure
    tmp = Image.new("L", (4, 4), 0)
    d = ImageDraw.Draw(tmp)
    d.fontmode = "1" if mono else "L"
    l, t, r, b = d.textbbox((0, 0), TEXT, font=f)
    w = max(1, r - l); h = max(1, b - t)
    img = Image.new("L", (w + 2, h + 2), 0)
    dd = ImageDraw.Draw(img)
    dd.fontmode = "1" if mono else "L"
    dd.text((1 - l, 1 - t), TEXT, fill=255, font=f)
    return img

def upscale(img, factor, resample):
    return img.resize((img.width * factor, img.height * factor), resample)

# Build a grid: rows = {Pixel-Perfect (Mono), Smooth (AA)}, cols = 1x/2x/3x.
cells = {}
maxw = maxh = 0
for scale in SCALES:
    # Pixel-perfect: rasterise at native, nearest-upscale by scale, then blow up for display.
    pp = raster(NATIVE, mono=True)
    pp = upscale(pp, scale, Image.NEAREST)
    pp = upscale(pp, DISPLAY, Image.NEAREST)
    # Smooth: rasterise WITH AA at native*scale, then display-magnify with bilinear (shows blur).
    sm = raster(NATIVE * scale, mono=False)
    sm = upscale(sm, DISPLAY, Image.BILINEAR)
    cells[("pp", scale)] = pp
    cells[("sm", scale)] = sm
    maxw = max(maxw, pp.width, sm.width)
    maxh = max(maxh, pp.height, sm.height)

cols = len(SCALES)
label_h = 16
canvas_w = PAD + cols * (maxw + PAD)
canvas_h = PAD + label_h + 2 * (maxh + PAD + label_h)
canvas = Image.new("RGB", (canvas_w, canvas_h), (30, 30, 34))
cd = ImageDraw.Draw(canvas)
try:
    ui = ImageFont.truetype(FONT, 8)
except Exception:
    ui = ImageFont.load_default()

def paste(img, cx, cy):
    rgb = Image.merge("RGB", (img, img, img))
    canvas.paste(rgb, (cx, cy))

y = PAD
cd.text((PAD, y), "OpenGlyph Phase 1c  Silkscreen  native=8px/em", fill=(220, 220, 60), font=ui)
y += label_h
for row_key, row_name in [("pp", "PIXEL-PERFECT (Mono 0/255, nearest)"), ("sm", "SMOOTH (grayscale AA)")]:
    cd.text((PAD, y), row_name, fill=(180, 200, 255), font=ui)
    y += label_h
    x = PAD
    for scale in SCALES:
        img = cells[(row_key, scale)]
        paste(img, x, y)
        cd.text((x, y + maxh - 2), f"{scale}x", fill=(160, 160, 160), font=ui)
        x += maxw + PAD
    y += maxh + PAD

canvas.save(OUT)
print("WROTE", OUT, canvas.size)
