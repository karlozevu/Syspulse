#!/usr/bin/env python3
"""
Generate the System Assistant application icon with the Python standard library only.

Design
------
A rounded blue tile (the same accent blue as the UI) carrying a rising white
monitoring curve, a soft area fill, a baseline grid line and a highlighted end
point with a halo. It stays readable down to 16x16 because it uses a single
bold gesture instead of fine detail.

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

# Brand gradient: bright accent blue -> deep blue
TOP_LEFT = (0x21, 0x88, 0xE0)
BOTTOM_RIGHT = (0x0A, 0x4A, 0x8E)

# The rising "monitoring" polyline, in normalized [0,1] coordinates (y grows down)
POINTS = [(0.16, 0.70), (0.34, 0.56), (0.50, 0.62), (0.68, 0.38), (0.85, 0.30)]
LINE_WIDTH = 0.085
BASELINE = 0.815
TILE_HALF = 0.455
TILE_RADIUS = 0.205
SIZES = (16, 24, 32, 40, 48, 64, 96, 128, 256)
ICO_SIZES = (16, 24, 32, 40, 48, 64, 96, 128, 256)


def clamp(value: float) -> float:
    return 0.0 if value < 0.0 else 1.0 if value > 1.0 else value


def blend(dst, rgb, alpha):
    """Alpha-composite an opaque colour over a premultiplied-free RGBA tuple."""
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


def curve_y(u: float):
    """Interpolated y of the polyline at x=u, or None outside its span."""
    for (x0, y0), (x1, y1) in zip(POINTS, POINTS[1:]):
        if x0 <= u <= x1:
            t = (u - x0) / (x1 - x0) if x1 != x0 else 0.0
            return y0 + (y1 - y0) * t
    return None


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

            # Rounded-rectangle signed distance field
            px, py = u - 0.5, v - 0.5
            inner = TILE_HALF - TILE_RADIUS
            qx, qy = abs(px) - inner, abs(py) - inner
            sd = math.hypot(max(qx, 0.0), max(qy, 0.0)) + min(max(qx, qy), 0.0) - TILE_RADIUS
            alpha = clamp(0.5 - sd / edge)
            if alpha <= 0:
                row += b"\x00\x00\x00\x00"
                continue

            # Diagonal gradient
            t = 0.5 * u + 0.5 * v
            colour = tuple(TOP_LEFT[k] + (BOTTOM_RIGHT[k] - TOP_LEFT[k]) * t for k in range(3))
            pixel = (colour[0], colour[1], colour[2], alpha)

            # Soft top highlight + thin bright rim give the tile some depth
            pixel = blend(pixel, white, 0.11 * max(0.0, 1.0 - v / 0.62) * alpha)
            rim = clamp(0.5 - (abs(sd + 0.010) - 0.006) / edge)
            pixel = blend(pixel, white, 0.22 * rim * alpha)

            # Translucent area under the curve
            y = curve_y(u)
            if y is not None and y < v < BASELINE:
                pixel = blend(pixel, white, 0.20 * clamp((v - y) / 0.35) * alpha)

            # Baseline grid line
            if 0.14 < u < 0.88 and BASELINE - 0.008 < v < BASELINE + 0.008:
                pixel = blend(pixel, white, 0.32 * alpha)

            # The curve itself (round caps via segment distance field)
            distance = min(distance_to_segment(u, v, *POINTS[k], *POINTS[k + 1])
                           for k in range(len(POINTS) - 1))
            line_alpha = clamp(0.5 + (LINE_WIDTH / 2 - distance) / edge)
            pixel = blend(pixel, white, 0.97 * line_alpha * alpha)

            # Highlighted end point + halo
            end_x, end_y = POINTS[-1]
            d = math.hypot(u - end_x, v - end_y)
            dot = clamp(0.5 + (0.058 - d) / edge)
            pixel = blend(pixel, white, 0.98 * dot * alpha)
            ring = clamp(0.5 - (abs(d - 0.105) - 0.009) / edge)
            pixel = blend(pixel, white, 0.30 * ring * alpha)

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
      <stop offset="0" stop-color="#2188E0"/>
      <stop offset="1" stop-color="#0A4A8E"/>
    </linearGradient>
    <linearGradient id="area" x1="0" y1="0" x2="0" y2="1">
      <stop offset="0" stop-color="#FFFFFF" stop-opacity=".26"/>
      <stop offset="1" stop-color="#FFFFFF" stop-opacity=".04"/>
    </linearGradient>
  </defs>
  <rect x="11" y="11" width="234" height="234" rx="52" fill="url(#bg)"/>
  <rect x="11" y="11" width="234" height="234" rx="52" fill="none" stroke="#FFFFFF" stroke-opacity=".22" stroke-width="3"/>
  <path d="M41 179 L87 143 L128 159 L174 97 L218 77 L218 209 L41 209 Z" fill="url(#area)"/>
  <line x1="36" y1="209" x2="224" y2="209" stroke="#FFFFFF" stroke-opacity=".35" stroke-width="4" stroke-linecap="round"/>
  <polyline points="41,179 87,143 128,159 174,97 218,77" fill="none" stroke="#FFFFFF"
            stroke-width="22" stroke-linecap="round" stroke-linejoin="round"/>
  <circle cx="218" cy="77" r="27" fill="none" stroke="#FFFFFF" stroke-opacity=".35" stroke-width="4"/>
  <circle cx="218" cy="77" r="15" fill="#FFFFFF"/>
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
