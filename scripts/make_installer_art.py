#!/usr/bin/env python3
"""
Generate the Syspulse installer wizard artwork with the Python standard library.

Outputs (Inno Setup 6 classic layout):
  installer/wizard-image.png       164x314  left wizard bitmap
  installer/wizard-small-image.png  55x55    top-right wizard logo

The art mirrors the app icon: a deep glass field, a faint HUD grid, a pulsing
ECG waveform and a neon-teal halo at the R spike, so the installer feels like
part of the same product rather than a stock setup wizard.

Run:  python scripts/make_installer_art.py
"""

from __future__ import annotations

import math
import struct
import zlib
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
INSTALLER = ROOT / "installer"

TILE_TOP = (0x13, 0x24, 0x3E)
TILE_BOTTOM = (0x07, 0x10, 0x1F)
PULSE = (0x3D, 0xF5, 0xC0)
PULSE_BRIGHT = (0xC8, 0xFF, 0xEE)


def clamp(value: float) -> float:
    return 0.0 if value < 0.0 else 1.0 if value > 1.0 else value


def blend(dst, rgb, alpha):
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


def encode_png(width: int, height: int, rows: list[bytes]) -> bytes:
    ihdr = struct.pack(">IIBBBBB", width, height, 8, 6, 0, 0, 0)
    raw = b"".join(b"\x00" + row for row in rows)
    return (b"\x89PNG\r\n\x1a\n" +
            png_chunk(b"IHDR", ihdr) +
            png_chunk(b"IDAT", zlib.compress(raw, 9)) +
            png_chunk(b"IEND", b""))


def to_row(pixels: list[tuple[float, float, float, float]]) -> bytes:
    out = bytearray()
    for p in pixels:
        out += bytes((int(max(0, min(255, round(p[0])))),
                      int(max(0, min(255, round(p[1])))),
                      int(max(0, min(255, round(p[2])))),
                      int(max(0, min(255, round(p[3] * 255))))))
    return bytes(out)


def render_wizard(width: int = 164, height: int = 314) -> bytes:
    """Vertical wizard bitmap: halo ring + ECG pulse over a glass grid field."""
    edge = 1.35 / width
    cx, cy = width * 0.5, height * 0.40
    ring_r = width * 0.335

    # ECG gesture across the middle, coordinates are absolute pixels.
    base = cy
    amp = width * 0.30
    pts = [
        (width * 0.06, base),
        (width * 0.24, base),
        (width * 0.30, base - amp * 0.16),
        (width * 0.36, base),
        (width * 0.41, base + amp * 0.12),
        (width * 0.47, base - amp),
        (width * 0.53, base + amp * 0.62),
        (width * 0.59, base),
        (width * 0.67, base - amp * 0.14),
        (width * 0.75, base),
        (width * 0.94, base),
    ]
    peak = pts[5]

    rows = []
    for j in range(height):
        py = j + 0.5
        v = py / height
        row = []
        for i in range(width):
            px = i + 0.5
            u = px / width

            t = 0.30 * u + 0.70 * v
            colour = tuple(TILE_TOP[k] + (TILE_BOTTOM[k] - TILE_TOP[k]) * t for k in range(3))
            pixel = (colour[0], colour[1], colour[2], 1.0)

            # Vignette: darker toward the edges, richer in the middle band
            vig = clamp(1.0 - 0.45 * abs(v - 0.45) / 0.55)
            pixel = blend(pixel, TILE_BOTTOM, 0.30 * (1.0 - vig))

            # HUD grid
            grid = 0.0
            for gx in (0.16, 0.32, 0.50, 0.68, 0.84):
                grid = max(grid, clamp(0.5 - (abs(u - gx) - 0.0015) / edge))
            for gy in (0.12, 0.28, 0.44, 0.60, 0.76, 0.90):
                grid = max(grid, clamp(0.5 - (abs(v - gy) - 0.0015) / edge))
            pixel = blend(pixel, PULSE, 0.075 * grid)

            # Halo rings around the pulse centre
            d = math.hypot(px - cx, py - cy)
            ring1 = clamp(0.5 - (abs(d - ring_r) - 0.010) / edge)
            ring2 = clamp(0.5 - (abs(d - ring_r * 0.74) - 0.006) / edge)
            pixel = blend(pixel, PULSE, 0.30 * ring1 + 0.16 * ring2)

            # ECG waveform
            distance = min(distance_to_segment(px, py, *pts[k], *pts[k + 1])
                           for k in range(len(pts) - 1))
            line_alpha = clamp(0.5 + (width * 0.036 - distance) / edge)
            pixel = blend(pixel, PULSE, 0.96 * line_alpha)

            # Bright peak + halo at the R spike
            dp = math.hypot(px - peak[0], py - peak[1])
            core = clamp(0.5 + (width * 0.012 - dp) / edge)
            pixel = blend(pixel, PULSE_BRIGHT, 0.98 * core)
            halo = clamp(0.5 - (abs(dp - width * 0.045) - width * 0.016) / edge)
            pixel = blend(pixel, PULSE, 0.26 * halo)

            # Subtle dotted footer band where the wizard renders its caption
            if v > 0.93 and int(px) % 12 < 2 and int(py) % 10 < 2:
                pixel = blend(pixel, PULSE, 0.12)

            row.append(pixel)
        rows.append(to_row(row))
    return encode_png(width, height, rows)


def render_small(width: int = 55, height: int = 55) -> bytes:
    """Compact top-right logo: glass tile with a single bold ECG beat."""
    edge = 1.35 / width
    rows = []
    for j in range(height):
        v = (j + 0.5) / height
        row = []
        for i in range(width):
            u = (i + 0.5) / width
            px, py = u - 0.5, v - 0.5
            inner = 0.455 - 0.205
            qx, qy = abs(px) - inner, abs(py) - inner
            sd = math.hypot(max(qx, 0.0), max(qy, 0.0)) + min(max(qx, qy), 0.0) - 0.205
            alpha = clamp(0.5 - sd / edge)
            if alpha <= 0:
                row.append((0, 0, 0, 0))
                continue
            t = 0.5 * u + 0.5 * v
            colour = tuple(TILE_TOP[k] + (TILE_BOTTOM[k] - TILE_TOP[k]) * t for k in range(3))
            pixel = (colour[0], colour[1], colour[2], alpha)
            rim = clamp(0.5 - (abs(sd + 0.010) - 0.006) / edge)
            pixel = blend(pixel, PULSE, 0.28 * rim * alpha)
            pts = [
                (0.10, 0.56), (0.28, 0.56), (0.34, 0.48), (0.40, 0.56),
                (0.45, 0.64), (0.52, 0.23), (0.59, 0.69), (0.65, 0.56),
                (0.73, 0.47), (0.82, 0.56), (0.93, 0.56),
            ]
            distance = min(distance_to_segment(u, v, *pts[k], *pts[k + 1])
                           for k in range(len(pts) - 1))
            line = clamp(0.5 + (0.038 - distance) / edge)
            pixel = blend(pixel, PULSE, 0.97 * line * alpha)
            row.append((int(max(0, min(255, round(pixel[0])))),
                        int(max(0, min(255, round(pixel[1])))),
                        int(max(0, min(255, round(pixel[2])))),
                        int(max(0, min(255, round(pixel[3] * 255))))))
        rows.append(to_row(row))
    return encode_png(width, height, rows)


def main() -> None:
    INSTALLER.mkdir(parents=True, exist_ok=True)
    big = render_wizard()
    small = render_small()
    (INSTALLER / "wizard-image.png").write_bytes(big)
    (INSTALLER / "wizard-small-image.png").write_bytes(small)
    print(f"wizard-image.png      : {len(big):,} bytes (164x314)")
    print(f"wizard-small-image.png: {len(small):,} bytes (55x55)")


if __name__ == "__main__":
    main()
