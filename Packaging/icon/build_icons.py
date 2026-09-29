#!/usr/bin/env python3
"""
Builds the RasterField app icon for macOS (.icns) and Windows (.ico), plus PNGs for the
Avalonia window icon and Linux, from SVG masters generated here.

Design (see Packaging/icon/README.md):
  * Motif: a terrain summit in nested elevation bands, off-centre like real terrain. Its lower-left
    half is drawn as raster cells (the ER Mapper / BIL grid the app reads), its upper-right half as
    smooth bands whose outlines are Catmull-Rom → Bézier curves — the app's "blocky grid → smooth
    surface" (Bézier-patch subdivision) in one picture. The staircase along the diagonal is the
    moment of transition.
  * Colours: the app palette — slate plate, teal accent — with the elevation bands running
    teal → green → sand → cream, like the app's own Elevation palette.
  * macOS (Apple HIG, Big Sur+): 1024 px canvas, 824 px rounded-rectangle ("squircle") plate
    centred with 100 px margins, soft top light, baked drop shadow; the plate outline is the
    same at every size.
  * Windows (Fluent): the plate fills the frame (no macOS margin), smaller corner radius, no
    baked shadow; 16-32 px use a simplified drawing (three bands, no grid, no strokes), as
    Fluent asks for per-size simplification rather than scaling one image down.

Rendering uses headless Chromium (transparent background). Run:
    python3 Packaging/icon/build_icons.py [path-to-chrome]
"""
import io, math, os, shutil, struct, subprocess, sys, tempfile

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
ASSETS = os.path.join(ROOT, "Source", "RasterField", "Assets")

# ---------------------------------------------------------------- geometry ---

def blob(cx, cy, radii, rot=0.0):
    """Closed smooth curve through points at the given radii (evenly spaced angles), as cubic
    Béziers from Catmull-Rom tangents — the same construction the app uses for its patches."""
    n = len(radii)
    pts = []
    for i, r in enumerate(radii):
        a = rot + 2 * math.pi * i / n - math.pi / 2
        pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    d = f"M{pts[0][0]:.1f},{pts[0][1]:.1f}"
    for i in range(n):
        p0, p1, p2, p3 = pts[i - 1], pts[i], pts[(i + 1) % n], pts[(i + 2) % n]
        c1 = (p1[0] + (p2[0] - p0[0]) / 6, p1[1] + (p2[1] - p0[1]) / 6)
        c2 = (p2[0] - (p3[0] - p1[0]) / 6, p2[1] - (p3[1] - p1[1]) / 6)
        d += f" C{c1[0]:.1f},{c1[1]:.1f} {c2[0]:.1f},{c2[1]:.1f} {p2[0]:.1f},{p2[1]:.1f}"
    return d + " Z"

def squircle(x, y, w, h, n=5.0, steps=160):
    """Superellipse approximating Apple's continuous-corner icon plate."""
    cx, cy, a, b = x + w / 2, y + h / 2, w / 2, h / 2
    pts = []
    for i in range(steps):
        t = 2 * math.pi * i / steps
        c, s = math.cos(t), math.sin(t)
        pts.append((cx + a * math.copysign(abs(c) ** (2 / n), c), cy + b * math.copysign(abs(s) ** (2 / n), s)))
    return "M" + " L".join(f"{px:.2f},{py:.2f}" for px, py in pts) + " Z"

# Elevation bands, outer → inner: (centre offset from the hill centre, 8 radii, fill, stroke).
# Inner bands drift up and to the left, so the summit sits off-centre like real terrain rather
# than forming a bull's-eye.
BANDS = [
    ((24, 24),   [352, 376, 356, 318, 298, 310, 334, 356], "#2F9C90", "#23776E"),
    ((0, 0),     [262, 278, 258, 226, 212, 222, 246, 264], "#63B898", "#3D8C71"),
    ((-24, -26), [174, 186, 164, 142, 134, 144, 162, 176], "#D6B878", "#A6874C"),
    ((-44, -50), [84, 92, 78, 68, 66, 70, 80, 88],          "#F5EBD2", "#C7B287"),
]
ROT = 0.18
HILL = (500, 500)   # hill centre in the 1024 art space (bands drift up-left, so this reads as centred)
CELL = 56           # raster cell size in the pixelated half

def band_radius(radii, theta):
    """Radius of a band blob at polar angle theta (periodic Catmull-Rom through the radii)."""
    n = len(radii)
    u = ((theta - ROT + math.pi / 2) / (2 * math.pi) * n) % n
    i = int(math.floor(u)); t = u - i
    p0, p1, p2, p3 = radii[(i - 1) % n], radii[i % n], radii[(i + 1) % n], radii[(i + 2) % n]
    return 0.5 * ((2 * p1) + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t * t + (-p0 + 3 * p1 - 3 * p2 + p3) * t ** 3)

def band_at(x, y):
    """Index of the innermost band containing (x, y), or -1 outside the hill."""
    found = -1
    for k, ((ox, oy), radii, _, _) in enumerate(BANDS):
        cx, cy = HILL[0] + ox, HILL[1] + oy
        dx, dy = x - cx, y - cy
        if math.hypot(dx, dy) <= band_radius(radii, math.atan2(dy, dx)):
            found = k
    return found

def pixelated(x, y):
    """The lower-left half of the art (below the rising diagonal through the hill) is raster."""
    return (x - HILL[0]) - (y - HILL[1]) < -40

def art(simple, stroke_scale=1.0):
    """The motif in 1024-space. Full: the hill is raster cells in its lower-left half and smooth
    Bézier bands in the upper-right half — the app's 'blocky grid → smooth surface' in one image.
    Simple (≤32 px): just the smooth bands, which is all that reads at that size."""
    g = []
    cx, cy = HILL
    # Small sizes keep the same idea with far fewer, larger cells (3 px at 16 px, 6 px at 32 px),
    # no outlines, no index contour and no background grid — Fluent/HIG per-size simplification.
    cell = 192 if simple else CELL  # 192 units = exactly 3 px at 16 px and 6 px at 32 px (pixel-snapped)
    grid_origin = (0, 0)

    # Smooth half: vector bands, clipped to exactly the cells that are NOT raster, so the two
    # halves meet along a clean staircase with no gaps.
    n_cells = 1024 // cell + 3
    cells = [(grid_origin[0] + col * cell, grid_origin[1] + row * cell)
             for row in range(-3, n_cells) for col in range(-3, n_cells)]
    rects = "".join(f'<rect x="{x0 - 1}" y="{y0 - 1}" width="{cell + 2}" height="{cell + 2}"/>'
                    for x0, y0 in cells if not pixelated(x0 + cell / 2, y0 + cell / 2))
    g.append(f'<clipPath id="smooth">{rects}</clipPath>')
    g.append('<g clip-path="url(#smooth)">')
    for (ox, oy), radii, fill, stroke in BANDS:
        d = blob(cx + ox, cy + oy, radii, rot=ROT)
        outline = "" if simple else f' stroke="{stroke}" stroke-width="{6 * stroke_scale:.1f}" stroke-opacity="0.5"'
        g.append(f'<path d="{d}" fill="{fill}"{outline}/>')
    if not simple:
        (ox, oy), radii, _, _ = BANDS[0]
        d = blob(cx + ox - 6, cy + oy - 6, [r * 0.87 for r in radii], rot=ROT)
        g.append(f'<path d="{d}" fill="none" stroke="#EAF7F4" stroke-opacity="0.6" stroke-width="{5 * stroke_scale:.1f}"/>')
    g.append('</g>')

    # Raster half: cells coloured by the band their centre falls in; empty cells hint the grid.
    g.append('<g clip-path="url(#plate)">')
    gap, radius = (10, 22) if simple else (2, 7)
    for x0, y0 in cells:
        mx, my = x0 + cell / 2, y0 + cell / 2
        if not pixelated(mx, my):
            continue
        b = band_at(mx, my)
        if b >= 0:
            g.append(f'<rect x="{x0 + gap}" y="{y0 + gap}" width="{cell - 2 * gap}" height="{cell - 2 * gap}" rx="{radius}" fill="{BANDS[b][2]}"/>')
        elif not simple:
            dist = math.hypot(mx - cx, my - cy)
            a = max(0.0, 0.07 * (1 - (dist - 360) / 150))
            if a > 0.01:
                g.append(f'<rect x="{x0 + 3}" y="{y0 + 3}" width="{cell - 6}" height="{cell - 6}" rx="7" fill="#7FD3C7" fill-opacity="{a:.3f}"/>')
    g.append('</g>')
    return "\n".join(g)

def svg(platform, simple):
    rim = 40 if simple else 8  # ≈ 0.6 px at 16 px, 2 px at 256 px
    if platform == "mac":
        # Apple grid: 824 px continuous-corner plate centred on the 1024 canvas, baked shadow.
        plate = squircle(100, 100, 824, 824, n=4.2)
        defs_extra = ('<filter id="shadow" x="-20%" y="-20%" width="140%" height="140%">'
                      '<feDropShadow dx="0" dy="12" stdDeviation="14" flood-color="#000" flood-opacity="0.32"/></filter>'
                      f'<clipPath id="plate"><path d="{squircle(0, 0, 1024, 1024, n=4.2)}"/></clipPath>')
        plate_elem = f'<path d="{plate}" fill="url(#bg)" filter="url(#shadow)"/>'
        # A faint light rim keeps the dark plate distinct on a dark Dock / menu bar.
        highlight = (f'<path d="{plate}" fill="url(#hl)"/>'
                     f'<path d="{plate}" fill="none" stroke="#B9CBD8" stroke-opacity="0.22" stroke-width="{rim}"/>')
        motif = f'<g transform="translate(100,100) scale({824 / 1024})">{art(simple)}</g>'
    else:
        # Fluent: the plate fills the frame, corner radius ~1/6, no baked shadow.
        rect = 'x="16" y="16" width="992" height="992" rx="176"'
        defs_extra = f'<clipPath id="plate"><rect {rect}/></clipPath>'
        plate_elem = f'<rect {rect} fill="url(#bg)"/>'
        # Fluent: legible on light and dark taskbars; the rim separates the plate from dark ones.
        highlight = (f'<rect {rect} fill="url(#hl)"/>'
                     f'<rect {rect} fill="none" stroke="#B9CBD8" stroke-opacity="0.26" stroke-width="{rim}"/>')
        motif = art(simple)
    return f'''<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1024 1024" width="1024" height="1024">
<defs>
  <linearGradient id="bg" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="#26343F"/><stop offset="1" stop-color="#121920"/>
  </linearGradient>
  <linearGradient id="hl" x1="0" y1="0" x2="0" y2="1">
    <stop offset="0" stop-color="#FFFFFF" stop-opacity="0.08"/><stop offset="0.6" stop-color="#FFFFFF" stop-opacity="0"/>
  </linearGradient>
  {defs_extra}
</defs>
{plate_elem}
{highlight}
{motif}
</svg>'''

# ---------------------------------------------------------------- rendering ---

def find_chrome(arg):
    cands = [arg] if arg else []
    base = "/opt/pw-browsers"
    if os.path.isdir(base):
        for d in sorted(os.listdir(base), reverse=True):
            cands.append(os.path.join(base, d, "chrome-linux", "chrome"))
    cands += [shutil.which(n) for n in ("chromium", "chromium-browser", "google-chrome")]
    for c in cands:
        if c and os.path.isfile(c):
            return c
    sys.exit("Chromium not found — pass its path as the first argument.")

def render(chrome, svg_text, size, tmp):
    """Renders the SVG at exactly size×size (Chromium draws vectors natively at that size, so small
    icons stay crisp). Chromium has a minimum window size, so render top-left in a larger
    window and crop."""
    from PIL import Image  # Pillow
    html = os.path.join(tmp, "icon.html")
    sized = svg_text.replace('width="1024" height="1024"', f'width="{size}" height="{size}"', 1)
    with open(html, "w") as f:
        f.write('<html><body style="margin:0;background:transparent;overflow:hidden">'
                '<div style="line-height:0">' + sized + '</div></body></html>')
    win = max(size, 600)
    out = os.path.join(tmp, f"r{size}.png")
    subprocess.run([chrome, "--headless", "--no-sandbox", "--disable-gpu", "--hide-scrollbars",
                    "--force-device-scale-factor=1", "--default-background-color=00000000",
                    f"--window-size={win},{win}", f"--screenshot={out}", "file://" + html],
                   check=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    img = Image.open(out).convert("RGBA").crop((0, 0, size, size))
    buf = io.BytesIO()
    img.save(buf, format="PNG", optimize=True)
    return buf.getvalue()

# ---------------------------------------------------------------- containers ---

def write_ico(path, pngs):
    """ICO with PNG-compressed entries (Vista+), one per size."""
    entries = sorted(pngs.items())
    header = struct.pack("<HHH", 0, 1, len(entries))
    offset = 6 + 16 * len(entries)
    dir_, data = b"", b""
    for size, png in entries:
        dim = 0 if size >= 256 else size
        dir_ += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(png), offset + len(data))
        data += png
    with open(path, "wb") as f:
        f.write(header + dir_ + data)

ICNS_TYPES = [  # (OSType, pixel size)
    (b"icp4", 16), (b"icp5", 32), (b"ic11", 32), (b"icp6", 64), (b"ic12", 64),
    (b"ic07", 128), (b"ic08", 256), (b"ic13", 256), (b"ic09", 512), (b"ic14", 512), (b"ic10", 1024),
]

def write_icns(path, pngs):
    chunks = b""
    for ostype, size in ICNS_TYPES:
        png = pngs[size]
        chunks += ostype + struct.pack(">I", 8 + len(png)) + png
    with open(path, "wb") as f:
        f.write(b"icns" + struct.pack(">I", 8 + len(chunks)) + chunks)

# ---------------------------------------------------------------- main ---

def main():
    chrome = find_chrome(sys.argv[1] if len(sys.argv) > 1 else None)
    os.makedirs(ASSETS, exist_ok=True)
    masters = {
        ("mac", False): svg("mac", False), ("mac", True): svg("mac", True),
        ("win", False): svg("win", False), ("win", True): svg("win", True),
    }
    for (plat, simple), text in masters.items():
        name = f"icon-{plat}{'-small' if simple else ''}.svg"
        with open(os.path.join(HERE, name), "w") as f:
            f.write(text)

    with tempfile.TemporaryDirectory() as tmp:
        mac = {s: render(chrome, masters[("mac", s <= 32)], s, tmp) for s in (16, 32, 64, 128, 256, 512, 1024)}
        win = {s: render(chrome, masters[("win", s <= 32)], s, tmp) for s in (16, 20, 24, 32, 40, 48, 64, 128, 256)}

    write_icns(os.path.join(ASSETS, "RasterField.icns"), mac)
    write_ico(os.path.join(ASSETS, "RasterField.ico"), win)
    for s in (256, 1024):
        with open(os.path.join(ASSETS, f"RasterField-{s}.png"), "wb") as f:
            f.write(mac[s])
    print("icons written to", ASSETS)

if __name__ == "__main__":
    main()
