"""Generates the PlanHawk icon: src/PlanHawk.Desktop/Assets/planhawk.ico + .png and assets/planhawk-256.png.
Run: python tools/make_icon.py   (needs Pillow)

A teal planning board with three staggered Gantt bars joined by dependency arrows, ending in an amber milestone diamond.
"""
from PIL import Image, ImageDraw, ImageFilter
import os

S = 1024
here = os.path.dirname(os.path.abspath(__file__))
root = os.path.dirname(here)


def lerp(a, b, t):
    t = max(0.0, min(1.0, t))
    return tuple(int(a[i] + (b[i] - a[i]) * t) for i in range(len(a)))


def grad(box, c0, c1, diagonal=True):
    x0, y0, x1, y1 = box
    g = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    px = g.load()
    for y in range(int(y0), int(y1)):
        for x in range(int(x0), int(x1)):
            t = ((x - x0) / (x1 - x0) * 0.35 + (y - y0) / (y1 - y0) * 0.65) if diagonal else (y - y0) / (y1 - y0)
            px[x, y] = lerp(c0, c1, t)
    return g


def rounded_mask(box, r):
    m = Image.new("L", (S, S), 0)
    ImageDraw.Draw(m).rounded_rectangle(box, radius=r, fill=255)
    return m


img = Image.new("RGBA", (S, S), (0, 0, 0, 0))

# Shadow under the board
shadow = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(shadow).rounded_rectangle([110, 130, 914, 934], radius=190, fill=(0, 30, 30, 130))
img.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(34)))

# Board
board = [96, 96, 928, 928]
img.paste(grad(board, (44, 214, 178, 255), (10, 92, 104, 255)), (0, 0), rounded_mask(board, 196))

# Soft top highlight
hl = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(hl).ellipse([60, -260, 964, 360], fill=(255, 255, 255, 34))
img.paste(hl.filter(ImageFilter.GaussianBlur(40)), (0, 0), Image.composite(hl.filter(ImageFilter.GaussianBlur(40)).split()[3], Image.new("L", (S, S), 0), rounded_mask(board, 196)))

# Timescale: faint column lines and a header strip
layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(layer)
for x in range(250, 860, 122):
    d.line([(x, 300), (x, 820)], fill=(255, 255, 255, 46), width=6)
for i, x in enumerate(range(214, 800, 122)):
    d.rounded_rectangle([x + 18, 206, x + 92, 228], radius=11, fill=(255, 255, 255, 120 if i % 2 == 0 else 80))
mask = rounded_mask(board, 196)
img.paste(layer, (0, 0), Image.composite(layer.split()[3], Image.new("L", (S, S), 0), mask))

# Gantt bars: (left, right, y, done fraction)
bars = [(196, 520, 360, 1.0), (430, 742, 520, 0.55), (300, 610, 680, 0.0)]
H = 92
bar_layer = Image.new("RGBA", (S, S), (0, 0, 0, 0))
bd = ImageDraw.Draw(bar_layer)
for x0, x1, y, done in bars:
    bd.rounded_rectangle([x0, y - H // 2, x1, y + H // 2], radius=H // 2, fill=(255, 255, 255, 255))
    if done > 0:
        xd = x0 + (x1 - x0) * done
        prog = Image.new("RGBA", (S, S), (0, 0, 0, 0))
        ImageDraw.Draw(prog).rectangle([x0, y - H // 2, xd, y + H // 2], fill=(150, 236, 210, 255))
        m = Image.new("L", (S, S), 0)
        ImageDraw.Draw(m).rounded_rectangle([x0, y - H // 2, x1, y + H // 2], radius=H // 2, fill=255)
        bar_layer.paste(prog, (0, 0), Image.composite(prog.split()[3], Image.new("L", (S, S), 0), m))
        bd = ImageDraw.Draw(bar_layer)
# bar shadows
bs = Image.new("RGBA", (S, S), (0, 0, 0, 0))
bs.paste((0, 40, 45, 120), (0, 0), bar_layer.split()[3])
img.alpha_composite(bs.filter(ImageFilter.GaussianBlur(14)), (0, 10))
img.alpha_composite(bar_layer)

# Dependency arrows (dark teal elbows) from the end of each bar down to the next
arrow = (8, 70, 78, 255)
ad = ImageDraw.Draw(img)
W = 22


def elbow(points):
    ad.line(points, fill=arrow, width=W, joint="curve")
    for x, y in points[1:-1]:
        ad.ellipse([x - W / 2, y - W / 2, x + W / 2, y + W / 2], fill=arrow)


def head_down(x, y):
    ad.polygon([(x - 30, y - 34), (x + 30, y - 34), (x, y + 2)], fill=arrow)


# bar 1 end -> down into bar 2 (top)
elbow([(520 + 6, 360), (580, 360), (580, 520 - H // 2 - 34)])
head_down(580, 520 - H // 2 - 2)
# bar 2 start -> down into bar 3 (start-to-start style, enters bar 3 from the top)
elbow([(430 - 4, 520), (372, 520), (372, 680 - H // 2 - 34)])
head_down(372, 680 - H // 2 - 2)

# Milestone diamond (amber) after bar 3, joined by a short arrow
cx, cy, r = 790, 680, 92
ms = Image.new("RGBA", (S, S), (0, 0, 0, 0))
ImageDraw.Draw(ms).polygon([(cx, cy - r), (cx + r, cy), (cx, cy + r), (cx - r, cy)], fill=(0, 0, 0, 255))
shade = Image.new("RGBA", (S, S), (0, 0, 0, 0))
shade.paste((60, 30, 0, 120), (0, 0), ms.split()[3])
img.alpha_composite(shade.filter(ImageFilter.GaussianBlur(14)), (0, 10))
col = grad([cx - r, cy - r, cx + r, cy + r], (255, 214, 102, 255), (240, 140, 40, 255))
img.paste(col, (0, 0), ms.split()[3])
ad = ImageDraw.Draw(img)
ad.line([(610 + 6, 680), (cx - r - 30, 680)], fill=arrow, width=W)
ad.polygon([(cx - r - 40, 680 - 30), (cx - r - 40, 680 + 30), (cx - r - 4, 680)], fill=arrow)

out_dir = os.path.join(root, "src", "PlanHawk.Desktop", "Assets")
os.makedirs(out_dir, exist_ok=True)
os.makedirs(os.path.join(root, "assets"), exist_ok=True)
small = img.resize((256, 256), Image.LANCZOS)
small.save(os.path.join(out_dir, "planhawk.png"))
small.save(os.path.join(root, "assets", "planhawk-256.png"))
img.save(os.path.join(out_dir, "planhawk.ico"), sizes=[(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)])
print("icon written")
