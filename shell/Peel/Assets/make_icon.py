"""Draw Peel's own tray icon: a four segment wheel with one segment lifted.

Clean room: this is our own mark, drawn from code here, not taken from any
other product. Run it again if the palette in Wheel/WheelTheme.cs changes.
"""
from PIL import Image, ImageDraw

ORANGE = (255, 138, 30, 255)
LIFTED = (255, 196, 107, 255)
GAP = (0, 0, 0, 0)
SS = 8  # supersample factor


def wheel(size):
    n = size * SS
    img = Image.new("RGBA", (n, n), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    pad = n * 0.04
    box = (pad, pad, n - pad, n - pad)
    lift = n * 0.06
    # three seated segments, then the lifted one offset up and to the right
    for i in range(4):
        start = i * 90 - 45 + 3
        end = start + 90 - 6
        colour = LIFTED if i == 0 else ORANGE
        offset = (lift, -lift) if i == 0 else (0, 0)
        shifted = (box[0] + offset[0], box[1] + offset[1], box[2] + offset[0], box[3] + offset[1])
        d.pieslice(shifted, start, end, fill=colour)
    # punch the hub so the mark reads as a wheel, not a dot
    hub = n * 0.19
    c = n / 2
    d.ellipse((c - hub, c - hub, c + hub, c + hub), fill=GAP)
    return img.resize((size, size), Image.LANCZOS)


sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256]
frames = [wheel(s) for s in sizes]
frames[-1].save("peel.ico", sizes=[(s, s) for s in sizes])
frames[sizes.index(256)].save("peel-256.png")
print("ok", [f.size for f in frames])
