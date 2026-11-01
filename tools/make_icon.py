#!/usr/bin/env python3
"""生成 src/EdifierCenter.App/Assets/app.ico（纯 Python，无第三方依赖）。

图标语汇与 docs/ui.html §01 一致：深色圆角机身 + 高音/中低音双铜膜单元。
方式：2×2 超采样光栅化 256×256 RGBA → PNG → 封装为 ICO（256px PNG 条目，Win Vista+ 支持）。
"""
import math
import struct
import zlib
from pathlib import Path

W = 256
SS = 2  # 超采样

def lerp(a, b, t):
    return a + (b - a) * t

def clamp255(v):
    return 0 if v < 0 else 255 if v > 255 else int(v)

def in_rounded_rect(x, y, x0, y0, x1, y1, r):
    if x < x0 or x > x1 or y < y0 or y > y1:
        return False
    nx = min(max(x, x0 + r), x1 - r)
    ny = min(max(y, y0 + r), y1 - r)
    return (x - nx) ** 2 + (y - ny) ** 2 <= r * r

def copper(d, r):
    """铜膜径向渐变：高光 → 主色 → 暗边。d∈[0,1]。"""
    if d < 0.32:
        t = d / 0.32
        return (lerp(0xED, 0xC0, t), lerp(0xA7, 0x7A, t), lerp(0x6E, 0x45, t))
    t = (d - 0.32) / 0.68
    return (lerp(0xC0, 0x7E, t), lerp(0x7A, 0x48, t), lerp(0x45, 0x20, t))

def render_pixel(sx, sy):
    """单像素着色（sx/sy ∈ [0,256)）。返回 (r,g,b,a)。"""
    if not in_rounded_rect(sx, sy, 16, 16, 240, 240, 56):
        return (0, 0, 0, 0)
    # 机身：对角深色渐变 + 顶部微高光
    t = (sx + sy) / 2 / 255
    r = lerp(0x37, 0x14, t); g = lerp(0x3C, 0x16, t); b = lerp(0x44, 0x19, t)
    sheen = max(0.0, 1.0 - sy / 110) * 0.10
    r = lerp(r, 255, sheen); g = lerp(g, 255, sheen); b = lerp(b, 255, sheen)

    def cone(cx, cy, radius, ring, cap, dot=None):
        nonlocal r, g, b
        d = math.hypot(sx - cx, sy - cy)
        if d > radius:
            return
        if d >= radius - ring:  # 悬边（深色圈）
            r, g, b = 0x19, 0x1B, 0x1F
            return
        if d <= cap:
            r, g, b = 0x1B, 0x1D, 0x21  # 防尘帽
            if d <= cap * 0.38:
                r, g, b = 0xC0, 0x7A, 0x45  # 帽心铜点
            return
        cr, cg, cb = copper(d / radius, radius)
        r, g, b = cr, cg, cb

    cone(128, 94, 27, 7, 9)      # 高音
    cone(128, 168, 53, 9, 17)    # 中低音 + 内圈
    # 内圈暗环
    d = math.hypot(sx - 128, sy - 168)
    if 33 <= d <= 36:
        r *= 0.72; g *= 0.72; b *= 0.72
    # 顶部高光内描边
    if in_rounded_rect(sx, sy, 18, 18, 238, 238, 54) and not in_rounded_rect(sx, sy, 21, 21, 235, 235, 53):
        if sy < 150:
            r = min(255, r + 26); g = min(255, g + 26); b = min(255, b + 26)
    return (clamp255(r), clamp255(g), clamp255(b), 255)

def rasterize():
    rows = []
    for y in range(W):
        row = bytearray([0])  # PNG filter 0
        for x in range(W):
            rr = gg = bb = aa = 0
            for oy in range(SS):
                for ox in range(SS):
                    pr, pg, pb, pa = render_pixel(x * SS + ox + 0.5, y * SS + oy + 0.5)
                    rr += pr; gg += pg; bb += pb; aa += pa
            n = SS * SS
            row += bytes((rr // n, gg // n, bb // n, aa // n))
        rows.append(bytes(row))
    return b"".join(rows)

def png(raw):
    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)
    ihdr = struct.pack(">IIBBBBB", W, W, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))

def ico(png_bytes):
    # ICONDIR: reserved=0, type=1(icon), count=1
    # ICONDIRENTRY: width=0(256), height=0(256), colors=0, reserved=0, planes=1, bpp=32
    out = struct.pack("<HHH", 0, 1, 1)
    out += struct.pack("<BBBBHHII", 0, 0, 0, 0, 1, 32, len(png_bytes), 22)
    return out + png_bytes

def main():
    target = Path(__file__).resolve().parent.parent / "src" / "EdifierCenter.App" / "Assets" / "app.ico"
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(ico(png(rasterize())))
    print(f"written {target} ({target.stat().st_size} bytes)")

if __name__ == "__main__":
    main()
