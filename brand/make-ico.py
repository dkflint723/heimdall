#!/usr/bin/env python3
"""Build brand/icons/heimdall.ico from the hicolor SVGs.

Windows wants one file holding every size, and this project already draws the
mark four times rather than scaling one of them: 16 and 24 carry two bands
because a third would close the gaps and read as a solid triangle, 48 is redrawn
on a 48 grid so the strokes land on whole pixels, and the scalable one is the
full three-band Bifrost. Scaling a single source would throw all of that away,
so each ICO entry is rendered from the drawing intended for its size.

Rendered rather than traced: this reads the SVGs and draws them with Pillow,
so editing a drawing and re-running this keeps the ICO honest. It understands
only the subset those five files use -- one or two rects with a corner radius,
and polylines with round caps and joins. It is not an SVG renderer and will
quietly ignore anything else, so check the output if you add a new shape.

    python brand/make-ico.py

Needs Pillow. Run from the repository root.
"""

import re
import sys
from pathlib import Path

try:
    from PIL import Image, ImageDraw
except ImportError:
    sys.exit("Pillow is required: python -m pip install pillow")

ROOT = Path(__file__).resolve().parent.parent
ICONS = ROOT / "brand" / "icons" / "hicolor"
OUT = ROOT / "brand" / "icons" / "heimdall.ico"

# Which drawing serves which size. The scalable one says of itself that it is
# "used from 32px upward"; 16, 24 and 48 have their own.
SOURCES = {
    16: ICONS / "16x16" / "apps" / "heimdall.svg",
    24: ICONS / "24x24" / "apps" / "heimdall.svg",
    32: ICONS / "scalable" / "apps" / "heimdall.svg",
    48: ICONS / "48x48" / "apps" / "heimdall.svg",
    64: ICONS / "scalable" / "apps" / "heimdall.svg",
    128: ICONS / "scalable" / "apps" / "heimdall.svg",
    256: ICONS / "scalable" / "apps" / "heimdall.svg",
}

# Supersample before downscaling. The strokes are diagonal and the corners are
# round, so drawing at the target size directly gives visibly stepped edges.
SUPERSAMPLE = 8


def attrs(tag):
    return dict(re.findall(r'([a-zA-Z-]+)\s*=\s*"([^"]*)"', tag))


def colour(value):
    value = value.lstrip("#")
    return tuple(int(value[i:i + 2], 16) for i in (0, 2, 4)) + (255,)


def render(svg_path, size):
    svg = svg_path.read_text(encoding="utf-8")

    view = re.search(r'viewBox="([\d.\s]+)"', svg)
    _, _, view_w, _ = (float(n) for n in view.group(1).split())

    canvas = size * SUPERSAMPLE
    scale = canvas / view_w

    image = Image.new("RGBA", (canvas, canvas), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)

    for tag in re.findall(r"<rect\b[^>]*>", svg):
        a = attrs(tag)
        x, y = float(a["x"]) * scale, float(a["y"]) * scale
        w, h = float(a["width"]) * scale, float(a["height"]) * scale
        radius = float(a.get("rx", 0)) * scale
        box = [x, y, x + w, y + h]

        fill = a.get("fill", "none")
        if fill != "none":
            draw.rounded_rectangle(box, radius=radius, fill=colour(fill))

        stroke = a.get("stroke", "none")
        if stroke != "none":
            width = max(1, round(float(a.get("stroke-width", 1)) * scale))
            draw.rounded_rectangle(
                box, radius=radius, outline=colour(stroke), width=width)

    # Every path in these files sits in one <g> that carries the stroke width.
    group = re.search(r"<g\b[^>]*>", svg)
    group_width = float(attrs(group.group(0)).get("stroke-width", 1)) if group else 1

    for tag in re.findall(r"<path\b[^>]*>", svg):
        a = attrs(tag)
        numbers = [float(n) for n in re.findall(r"-?\d+\.?\d*", a["d"])]
        points = [(numbers[i] * scale, numbers[i + 1] * scale)
                  for i in range(0, len(numbers), 2)]

        stroke = colour(a["stroke"])
        width = max(1, round(float(a.get("stroke-width", group_width)) * scale))

        # joint="curve" rounds the corner; the caps are ours to draw.
        draw.line(points, fill=stroke, width=width, joint="curve")
        for x, y in (points[0], points[-1]):
            r = width / 2
            draw.ellipse([x - r, y - r, x + r, y + r], fill=stroke)

    return image.resize((size, size), Image.LANCZOS)


def main():
    missing = [p for p in set(SOURCES.values()) if not p.exists()]
    if missing:
        sys.exit("missing source drawings:\n  " + "\n  ".join(str(p) for p in missing))

    sizes = sorted(SOURCES)
    frames = [render(SOURCES[size], size) for size in sizes]

    OUT.parent.mkdir(parents=True, exist_ok=True)

    # Pillow writes every requested size from the image it is given, so the
    # largest frame is saved with the rest appended -- otherwise all seven
    # entries would come from one drawing and the point of this script is lost.
    frames[-1].save(
        OUT, format="ICO",
        sizes=[(s, s) for s in sizes],
        append_images=frames[:-1])

    print(f"wrote {OUT.relative_to(ROOT)}  ({', '.join(str(s) for s in sizes)})")


if __name__ == "__main__":
    main()
