"""Regenerates AppCenter.ico and docs/icon.png.

    python tools/make_icon.py          (needs Pillow + numpy)

Everything is laid out on a 1024x1024 design grid, drawn at 4x supersample and
downsampled with LANCZOS into each icon size, so the small sizes stay clean.
Colours come from Themes/Palette.xaml: the Explore banner ramp for the tile and
AccentBrush for the install arrow.
"""

import pathlib

import numpy as np
from PIL import Image, ImageDraw, ImageFilter

G = 1024          # design grid
SS = 4            # supersample factor
S = G * SS        # render size


def u(v):
    """design units -> render pixels"""
    return int(round(v * SS))


# ---------------------------------------------------------------- palette ---
# The Explore banner ramp, chroma pushed a little so it still reads at 16px.
STOPS = [
    (0.00, (0x3A, 0x1D, 0x82)),   # indigo
    (0.36, (0x22, 0x50, 0x7E)),   # blue
    (0.70, (0x1F, 0xA9, 0x72)),   # teal-green
    (1.00, (0x4F, 0xDF, 0x83)),   # green
]
ORANGE = (0xE9, 0x54, 0x20)       # AccentBrush
WHITE = (255, 255, 255)
RIM = (0xDD, 0xE2, 0xE7)

# tile
INSET = 40
TILE = (u(INSET), u(INSET), u(G - INSET), u(G - INSET))
RADIUS = u(212)

# bag — crisp top rim, rounded base, wide shallow handle. The square top corners
# and the rim are what stop it reading as a padlock.
BODY = (u(244), u(345), u(780), u(767))
BODY_R = u(88)
RIM_BOX = (u(244), u(345), u(780), u(410))
HANDLE_C, HANDLE_OUTER, HANDLE_W = (u(512), u(402)), u(145), u(35)

# install arrow
STEM = (u(468), u(464), u(556), u(632))
HEAD = [(u(368), u(582)), (u(656), u(582)), (u(512), u(722))]

SIZES = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256]


# --------------------------------------------------------------- surfaces ---
def gradient():
    """Diagonal multi-stop ramp, top-left to bottom-right."""
    ramp = np.zeros((256, 3), np.float64)
    xs = np.linspace(0.0, 1.0, 256)
    for ch in range(3):
        ramp[:, ch] = np.interp(xs, [s[0] for s in STOPS], [s[1][ch] for s in STOPS])

    ax = np.linspace(0.0, 1.0, S)
    idx = np.clip((ax[None, :] + ax[:, None]) * 0.5 * 255.0, 0, 255)
    lo, hi = np.floor(idx).astype(int), np.ceil(idx).astype(int)
    f = (idx - lo)[..., None]
    return Image.fromarray((ramp[lo] * (1 - f) + ramp[hi] * f).round().astype(np.uint8), "RGB")


def radial_alpha(cx, cy, r, peak):
    """Soft round falloff, used for the top-left sheen."""
    ax = np.arange(S, dtype=np.float64)
    d = np.sqrt((ax[None, :] - cx) ** 2 + (ax[:, None] - cy) ** 2)
    return Image.fromarray((np.clip(1.0 - d / r, 0, 1) ** 2.2 * peak * 255).astype(np.uint8), "L")


def vertical_shade(top, bottom):
    """Vertical ramp, so the white bag isn't dead flat."""
    col = np.linspace(0.0, 1.0, S)[:, None]
    rgb = np.stack([np.full((S, S), top[c]) * (1 - col) +
                    np.full((S, S), bottom[c]) * col for c in range(3)], axis=-1)
    return Image.fromarray(rgb.round().astype(np.uint8), "RGB")


def mask(draw_fn):
    m = Image.new("L", (S, S), 0)
    draw_fn(ImageDraw.Draw(m))
    return m


def fill(canvas, m, colour):
    canvas.paste(Image.new("RGB", (S, S), colour), (0, 0), m)


# ------------------------------------------------------------------ build ---
def build():
    canvas = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    tile = mask(lambda d: d.rounded_rectangle(TILE, RADIUS, fill=255))

    # contact shadow, tucked just under the tile
    shadow = tile.filter(ImageFilter.GaussianBlur(u(9)))
    shadow = shadow.transform(shadow.size, Image.AFFINE, (1, 0, 0, 0, 1, -u(10)))
    canvas.paste(Image.new("RGBA", (S, S), (0, 0, 0, 255)), (0, 0),
                 shadow.point(lambda v: int(v * 0.26)))

    canvas.paste(gradient(), (0, 0), tile)

    sheen = Image.composite(radial_alpha(u(300), u(230), u(760), 0.12),
                            Image.new("L", (S, S), 0), tile)
    canvas.paste(Image.new("RGBA", (S, S), (255, 255, 255, 255)), (0, 0), sheen)

    # hairline inner edge, the glassy lift Yaru surfaces have
    edge = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    ImageDraw.Draw(edge).rounded_rectangle(TILE, RADIUS, outline=(255, 255, 255, 48), width=u(3))
    canvas = Image.alpha_composite(canvas, edge)

    # handle goes down first, so the rim covers where it enters the bag
    hb = (HANDLE_C[0] - HANDLE_OUTER, HANDLE_C[1] - HANDLE_OUTER,
          HANDLE_C[0] + HANDLE_OUTER, HANDLE_C[1] + HANDLE_OUTER)
    fill(canvas, mask(lambda d: d.arc(hb, 180, 360, fill=255, width=HANDLE_W)), WHITE)

    body = mask(lambda d: d.rounded_rectangle(BODY, BODY_R, fill=255,
                                              corners=(False, False, True, True)))
    canvas.paste(vertical_shade((255, 255, 255), (236, 239, 242)), (0, 0), body)
    fill(canvas, mask(lambda d: d.rectangle(RIM_BOX, fill=255)), RIM)

    fill(canvas, mask(lambda d: (d.rounded_rectangle(STEM, u(16), fill=255),
                                 d.polygon(HEAD, fill=255))), ORANGE)
    return canvas


if __name__ == "__main__":
    root = pathlib.Path(__file__).resolve().parent.parent
    master = build()

    frames = [master.resize((n, n), Image.LANCZOS) for n in SIZES]
    frames[-1].save(root / "AppCenter.ico", format="ICO",
                    sizes=[(n, n) for n in SIZES], append_images=frames[:-1])
    master.resize((512, 512), Image.LANCZOS).save(root / "docs" / "icon.png")
    print(f"wrote AppCenter.ico ({', '.join(str(n) for n in SIZES)})")
