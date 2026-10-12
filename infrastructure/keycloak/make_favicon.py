"""Writes the login theme's favicon.ico from the Stream mark (docs/branding.md#logo-mark--stream).

Keycloak's login template links img/favicon.ico, and browsers need a raster for it. Standard library only:
  python3 infrastructure/keycloak/make_favicon.py
"""
import struct
import zlib
from pathlib import Path

# (x, y, width, opacity) on the 32-unit grid; bars are 5 high with fully rounded ends. Colours: Ink, Verdigris.
INK, VERDIGRIS = (0x16, 0x20, 0x1D), (0x2C, 0x7A, 0x6A)
BARS = [(3, 6.5, 17, INK, 1.0), (9, 13.5, 20, VERDIGRIS, 1.0), (5, 20.5, 12, INK, 0.5)]
SIZES = (16, 32, 48)
SUPERSAMPLE = 8
OUT = Path(__file__).parent / "themes/flowstate/login/resources/img/favicon.ico"


def coverage(px: float, py: float) -> tuple[tuple[int, int, int], float] | None:
    for x, y, w, colour, opacity in BARS:
        r = 2.5
        cx = min(max(px, x + r), x + w - r)
        if (px - cx) ** 2 + (py - (y + r)) ** 2 <= r * r:
            return colour, opacity
    return None


def render(size: int) -> bytes:
    rows = []
    n = SUPERSAMPLE
    for j in range(size):
        row = bytearray([0])  # PNG filter: none
        for i in range(size):
            rgb, alpha = [0.0, 0.0, 0.0], 0.0
            for sj in range(n):
                for si in range(n):
                    hit = coverage((i + (si + 0.5) / n) * 32 / size, (j + (sj + 0.5) / n) * 32 / size)
                    if hit:
                        colour, opacity = hit
                        alpha += opacity
                        rgb = [c + v * opacity for c, v in zip(rgb, colour)]
            a = alpha / (n * n)
            row += bytes([round(c / alpha) if alpha else 0 for c in rgb] + [round(a * 255)])
        rows.append(bytes(row))

    def chunk(kind: bytes, data: bytes) -> bytes:
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data))

    header = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(b"".join(rows), 9)) + chunk(b"IEND", b"")


def main() -> None:
    images = [render(s) for s in SIZES]
    offset = 6 + 16 * len(images)
    directory = b""
    for size, png in zip(SIZES, images):
        directory += struct.pack("<BBBBHHII", size, size, 0, 0, 1, 32, len(png), offset)
        offset += len(png)
    OUT.write_bytes(struct.pack("<HHH", 0, 1, len(images)) + directory + b"".join(images))
    print(f"wrote {OUT} ({OUT.stat().st_size} bytes)")


if __name__ == "__main__":
    main()
