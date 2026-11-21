#!/usr/bin/env python3
"""
Generate the Syspulse application icon with the Python standard library only.

Design
------
A dark glass tile carrying a neon-teal ECG / heartbeat pulse, faint HUD grid
lines and a glowing peak. The pulse is the whole identity: a flat baseline,
a small P wave, a tall R spike, a deep S dip and a T wave — the universally
recognised "heartbeat" gesture. Neon teal on deep navy reads as both a
terminal/HUD and a health monitor, and stays legible at 16x16 because the
small sizes keep only the bold pulse while the grid fades into the glass.

Outputs
-------
  src/SystemAssistant.App/Assets/app.ico        multi-size ICO (exe + installer)
  src/SystemAssistant.App/Assets/app-icon.png   256x256 PNG (Avalonia window icon)
  installer/app-icon.svg                        editable source/preview

Run:  python scripts/make_icon.py
"""

from __future__ import annotations

import math
import struct
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
ASSETS = ROOT / "src" / "SystemAssistant.App" / "Assets"
INSTALLER = ROOT / "installer"

# Deep glass tile: near-black navy -> slightly lighter navy.
TILE_TOP_LEFT = (0x13, 0x24, 0x3E)
TILE_BOTTOM_RIGHT = (0x07, 0x10, 0x1F)

# Neon teal pulse and its bright core.
PULSE = (0x3D, 0xF5, 0xC0)
PULSE_BRIGHT = (0xC8, 0xFF, 0xEE)

# ECG waveform in normalized [0,1] coordinates (y grows down).
# baseline -> P wave -> Q dip -> R spike -> S dip -> baseline -> T wave -> baseline.
PULSE_POINTS = [
    (0.10, 0.56),
    (0.28, 0.56),
    (0.34, 0.48),
    (0.40, 0.56),
    (0.45, 0.64),
    (0.52, 0.23),
    (0.59, 0.69),
    (0.65, 0.56),
    (0.73, 0.47),
    (0.82, 0.56),
    (0.93, 0.56),
]
PEAK = PULSE_POINTS[5]
LINE_WIDTH = 0.076

# Faint HUD grid lines (fractions of the tile).
GRID_X = (0.26, 0.5, 0.74)
GRID_Y = (0.31, 0.56, 0.81)
GRID_ALPHA = 0.085

TILE_HALF = 0.455
TILE_RADIUS = 0.205
SIZES = (16, 24, 32, 40, 48, 64, 96, 128, 256)
ICO_SIZES = (16, 24, 32, 40, 48, 64, 96, 128, 256)


def clamp(value: float) -> float:
    return 0.0 if value < 0.0 else 1.0 if value > 1.0 else value


def blend(dst, rgb, alpha):
    """Alpha-composite an opaque colour over an RGBA tuple."""
    if alpha <= 0:
        return dst
    r, g, b, a = dst
    return (r + (rgb[0] - r) * alpha,
            g + (rgb[1] - g) * alpha,
            b + (rgb[2] - b) * alpha,
            a)


def distance_to_segment(px, py, ax, ay, bx, by) -> float:
    vx, vy = bx - ax, by - ay
    wx, wy = px - ax, py - ay
    length_sq = vx * vx + vy * vy
    t = clamp((wx * vx + wy * vy) / length_sq) if length_sq else 0.0
    return math.hypot(px - (ax + t * vx), py - (ay + t * vy))


def png_chunk(tag: bytes, payload: bytes) -> bytes:
    return (struct.pack(">I", len(payload)) + tag + payload +
            struct.pack(">I", zlib.crc32(tag + payload) & 0xFFFFFFFF))


def encode_png(size: int, scanlines: bytes) -> bytes:
    signature = b"\x89PNG\r\n\x1a\n"
    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)  # RGBA
    return (signature +
            png_chunk(b"IHDR", ihdr) +
            png_chunk(b"IDAT", zlib.compress(scanlines, 9)) +
            png_chunk(b"IEND", b""))


def render(size: int) -> bytes:
    """Analytic anti-aliasing via signed distance fields (no Pillow needed)."""
    edge = 1.35 / size
    white = (255, 255, 255)
    rows = []
    for j in range(size):
        v = (j + 0.5) / size
        row = bytearray(b"\x00")  # PNG filter: None
        for i in range(size):
            u = (i + 0.5) / size

            # Rounded-rectangle signed distance field (the glass tile)
            px, py = u - 0.5, v - 0.5
            inner = TILE_HALF - TILE_RADIUS
            qx, qy = abs(px) - inner, abs(py) - inner
            sd = math.hypot(max(qx, 0.0), max(qy, 0.0)) + min(max(qx, qy), 0.0) - TILE_RADIUS
            alpha = clamp(0.5 - sd / edge)
            if alpha <= 0:
                row += b"\x00\x00\x00\x00"
                continue

            # Diagonal navy gradient
            t = 0.5 * u + 0.5 * v
            colour = tuple(TILE_TOP_LEFT[k] + (TILE_BOTTOM_RIGHT[k] - TILE_TOP_LEFT[k]) * t
                           for k in range(3))
            pixel = (colour[0], colour[1], colour[2], alpha)

            # Subtle top glass highlight + thin teal rim
            pixel = blend(pixel, white, 0.07 * max(0.0, 1.0 - v / 0.60) * alpha)
            rim = clamp(0.5 - (abs(sd + 0.010) - 0.006) / edge)
            pixel = blend(pixel, PULSE, 0.28 * rim * alpha)

            # HUD grid: faint teal-white lines, anti-aliased
            grid = 0.0
            for gx in GRID_X:
                grid = max(grid, clamp(0.5 - (abs(u - gx) - 0.0016) / edge))
            for gy in GRID_Y:
                grid = max(grid, clamp(0.5 - (abs(v - gy) - 0.0016) / edge))
            pixel = blend(pixel, PULSE, GRID_ALPHA * grid * alpha)

            # The ECG pulse: round-capped segments via distance field
            distance = min(distance_to_segment(u, v, *PULSE_POINTS[k], *PULSE_POINTS[k + 1])
                           for k in range(len(PULSE_POINTS) - 1))
            line_alpha = clamp(0.5 + (LINE_WIDTH / 2 - distance) / edge)
            pixel = blend(pixel, PULSE, 0.98 * line_alpha * alpha)

            # Glowing peak: bright core + teal halo at the R spike
            d = math.hypot(u - PEAK[0], v - PEAK[1])
            core = clamp(0.5 + (0.020 - d) / edge)
            pixel = blend(pixel, PULSE_BRIGHT, 0.95 * core * alpha)
            halo = clamp(0.5 - (abs(d - 0.046) - 0.018) / edge)
            pixel = blend(pixel, PULSE, 0.30 * halo * alpha)

            row += bytes((int(max(0, min(255, round(pixel[0])))),
                          int(max(0, min(255, round(pixel[1])))),
                          int(max(0, min(255, round(pixel[2])))),
                          int(max(0, min(255, round(pixel[3] * 255))))))
        rows.append(bytes(row))
    return encode_png(size, b"".join(rows))


def build_ico(images: list[tuple[int, bytes]]) -> bytes:
    """ICO container holding PNG-compressed entries (supported since Vista)."""
    count = len(images)
    header = struct.pack("<HHH", 0, 1, count)
    offset = 6 + 16 * count
    entries, payload = bytearray(), bytearray()
    for size, data in images:
        dimension = 0 if size >= 256 else size  # 0 means 256 in ICONDIRENTRY
        entries += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32,
                               len(data), offset)
        offset += len(data)
        payload += data
    return header + bytes(entries) + bytes(payload)


SVG = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 256 256" width="256" height="256">
  <defs>
    <linearGradient id="bg" x1="0" y1="0" x2="1" y2="1">
      <stop offset="0" stop-color="#13243E"/>
      <stop offset="1" stop-color="#07101F"/>
    </linearGradient>
    <clipPath id="tile">
      <rect x="11" y="11" width="234" height="234" rx="52"/>
    </clipPath>
  </defs>
  <rect x="11" y="11" width="234" height="234" rx="52" fill="url(#bg)"/>
  <g clip-path="url(#tile)" stroke="#3DF5C0" stroke-opacity=".09" stroke-width="2">
    <line x1="77" y1="11" x2="77" y2="245"/>
    <line x1="128" y1="11" x2="128" y2="245"/>
    <line x1="179" y1="11" x2="179" y2="245"/>
    <line x1="11" y1="79" x2="245" y2="79"/>
    <line x1="11" y1="143" x2="245" y2="143"/>
    <line x1="11" y1="207" x2="245" y2="207"/>
  </g>
  <rect x="11" y="11" width="234" height="234" rx="52" fill="none" stroke="#3DF5C0" stroke-opacity=".28" stroke-width="3"/>
  <polyline points="26,143 72,143 87,123 102,143 115,164 133,59 151,177 166,143 187,120 210,143 238,143"
            fill="none" stroke="#3DF5C0" stroke-width="19" stroke-linecap="round" stroke-linejoin="round"/>
  <circle cx="133" cy="59" r="15" fill="none" stroke="#3DF5C0" stroke-opacity=".35" stroke-width="3"/>
  <circle cx="133" cy="59" r="6" fill="#C8FFEE"/>
</svg>
"""


def main() -> None:
    ASSETS.mkdir(parents=True, exist_ok=True)
    INSTALLER.mkdir(parents=True, exist_ok=True)

    images: list[tuple[int, bytes]] = []
    for size in SIZES:
        data = render(size)
        images.append((size, data))
        if size == 256:
            (ASSETS / "app-icon.png").write_bytes(data)
        print(f"  rendered {size}x{size}: {len(data):,} bytes")

    ico = build_ico([(s, d) for s, d in images if s in ICO_SIZES])
    (ASSETS / "app.ico").write_bytes(ico)
    # Inno Setup resolves SetupIconFile relative to the .iss file, so keep a
    # copy in installer/ as well (small, avoids path ambiguity).
    (INSTALLER / "app.ico").write_bytes(ico)
    (INSTALLER / "app-icon.svg").write_text(SVG, encoding="utf-8")

    print(f"\nICO : {ASSETS / 'app.ico'} ({len(ico):,} bytes, {len(ICO_SIZES)} sizes)")
    print(f"ICO : {INSTALLER / 'app.ico'} (installer copy)")
    print(f"PNG : {ASSETS / 'app-icon.png'}")
    print(f"SVG : {INSTALLER / 'app-icon.svg'}")


if __name__ == "__main__":
    main()
