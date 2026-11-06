"""Generates src/TimeTracker/app.ico and the 128 px icon.png used by the About popover (a blue rounded tile with a white clock face)."""
import math, sys
from PIL import Image, ImageDraw

def render(size: int) -> Image.Image:
    S = 1024
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle([40, 40, S - 40, S - 40], radius=230, fill=(42, 120, 214, 255))
    c = S // 2
    r = 330
    d.ellipse([c - r, c - r, c + r, c + r], outline=(255, 255, 255, 255), width=68)
    # hands
    def hand(angle_deg, length, width):
        a = math.radians(angle_deg - 90)
        x, y = c + length * math.cos(a), c + length * math.sin(a)
        d.line([(c, c), (x, y)], fill=(255, 255, 255, 255), width=width)
        d.ellipse([x - width / 2, y - width / 2, x + width / 2, y + width / 2], fill=(255, 255, 255, 255))
    hand(0, 215, 64)     # minute hand (12)
    hand(110, 150, 64)   # hour hand
    d.ellipse([c - 42, c - 42, c + 42, c + 42], fill=(255, 255, 255, 255))
    return img.resize((size, size), Image.LANCZOS)

out = sys.argv[1] if len(sys.argv) > 1 else "src/TimeTracker/app.ico"
sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
base = render(256)
base.save(out, format="ICO", sizes=[(s, s) for s in sizes])
web = "src/TimeTracker.Core/Web/icon.png"
render(128).save(web, format="PNG")
print("wrote", out, "and", web)
