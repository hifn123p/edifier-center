#!/usr/bin/env python3
"""生成 src/EdifierCenter.App/Assets/app.ico（纯 Python，无第三方依赖）。

v2：满幅圆角矩形（无四周留白，小尺寸下不显小）+ 多尺寸 PNG 条目（16~256），
Windows 资源管理器/任务栏按需取最合适尺寸，缩放质量更好。
图标语汇与 docs/ui.html §01 一致：深色圆角机身 + 高音/中低音双铜膜单元。
"""
import struct
import zlib
from pathlib import Path

SIZES = [16, 20, 24, 32, 48, 64, 128, 256]


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


def copper(d):
    """铜膜径向渐变：高光 → 主色 → 暗边。d∈[0,1]。"""
    if d < 0.32:
        t = d / 0.32
        return (lerp(0xED, 0xC0, t), lerp(0xA7, 0x7A, t), lerp(0x6E, 0x45, t))
    t = (d - 0.32) / 0.68
    return (lerp(0xC0, 0x7E, t), lerp(0x7A, 0x48, t), lerp(0x45, 0x20, t))


def render_pixel(size, sx, sy):
    """单像素着色（坐标已含超采样偏移）。返回 (r,g,b,a)。满幅：0..size。"""
    radius = 0.24 * size
    if not in_rounded_rect(sx, sy, 0, 0, size, size, radius):
        return (0, 0, 0, 0)

    # 机身：对角深色渐变 + 顶部微高光
    t = (sx + sy) / 2 / size
    r = lerp(0x37, 0x14, t)
    g = lerp(0x3C, 0x16, t)
    b = lerp(0x44, 0x19, t)
    sheen = max(0.0, 1.0 - sy / (0.45 * size)) * 0.10
    r = lerp(r, 255, sheen)
    g = lerp(g, 255, sheen)
    b = lerp(b, 255, sheen)

    def cone(cx, cy, radius_, ring_w, cap_r):
        nonlocal r, g, b
        d = math.hypot(sx - cx, sy - cy)
        if d > radius_:
            return
        if d >= radius_ - ring_w:
            r, g, b = 0x19, 0x1B, 0x1F  # 悬边
            return
        if d <= cap_r:
            r, g, b = 0x1B, 0x1D, 0x21  # 防尘帽
            if d <= cap_r * 0.38:
                r, g, b = 0xC0, 0x7A, 0x45  # 帽心铜点
            return
        cr, cg, cb = copper(d / radius_)
        r, g, b = cr, cg, cb

    cone(0.50 * size, 0.30 * size, 0.185 * size, 0.055 * size, 0.070 * size)   # 高音
    cone(0.50 * size, 0.655 * size, 0.40 * size, 0.075 * size, 0.155 * size)   # 中低音

    # 中低音内圈暗环
    d = math.hypot(sx - 0.50 * size, sy - 0.655 * size)
    if 0.245 * size <= d <= 0.275 * size:
        r *= 0.72
        g *= 0.72
        b *= 0.72

    # 顶部高光内描边
    if in_rounded_rect(sx, sy, 0.02 * size, 0.02 * size, 0.98 * size, 0.98 * size, 0.225 * size) \
            and not in_rounded_rect(sx, sy, 0.04 * size, 0.04 * size, 0.96 * size, 0.96 * size, 0.215 * size):
        if sy < 0.6 * size:
            r = min(255, r + 26)
            g = min(255, g + 26)
            b = min(255, b + 26)

    return (clamp255(r), clamp255(g), clamp255(b), 255)


def rasterize(size):
    ss = 4 if size <= 32 else 2
    rows = []
    for y in range(size):
        row = bytearray([0])  # PNG filter 0
        for x in range(size):
            rr = gg = bb = aa = 0
            for oy in range(ss):
                for ox in range(ss):
                    pr, pg, pb, pa = render_pixel(
                        size, (x + (ox + 0.5) / ss) , (y + (oy + 0.5) / ss))
                    # 渲染坐标系按 [0,size) 采样：换算到该尺寸像素中心
                    rr += pr
                    gg += pg
                    bb += pb
                    aa += pa
            n = ss * ss
            row += bytes((rr // n, gg // n, bb // n, aa // n))
        rows.append(bytes(row))
    return b"".join(rows)


def png(size, raw):
    def chunk(tag, data):
        body = tag + data
        return struct.pack(">I", len(data)) + body + struct.pack(">I", zlib.crc32(body) & 0xFFFFFFFF)
    ihdr = struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0)
    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", ihdr)
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def ico(images):
    """images: [(size, png_bytes), ...]"""
    out = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for size, data in images:
        out += struct.pack("<BBBBHHII",
                           size % 256, size % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    for _, data in images:
        out += data
    return out


def main():
    target = Path(__file__).resolve().parent.parent / "src" / "EdifierCenter.App" / "Assets" / "app.ico"
    target.parent.mkdir(parents=True, exist_ok=True)
    images = [(s, png(s, rasterize(s))) for s in SIZES]
    target.write_bytes(ico(images))
    print(f"written {target} ({target.stat().st_size:,} bytes, {len(images)} sizes)")


if __name__ == "__main__":
    import math
    main()
