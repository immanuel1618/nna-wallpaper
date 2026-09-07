#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
build/cursors.py -- builds the three NNA1618 brand cursor sets (mark, line, mono)
from brand/cursors/<variant>/<role>.svg (+ brand/cursors/shapes.json) into
Windows .cur / .ani files, plus registry manifests and an optional preview PNG.

Dependencies: only Pillow (no cairosvg / resvg / inkscape / imagemagick required).

Rasterizer selection, in order of preference:
  1. cairosvg (if importable AND its native cairo library actually loads)
  2. resvg / rsvg-convert / inkscape / magick (external CLI, if found on PATH)
  3. fallback: brand/cursors/shapes.json vector primitives drawn with
     PIL.ImageDraw (this is what actually runs in this environment: no cairo
     DLL and none of the CLI rasterizers are installed).

Usage:
    python build/cursors.py
    python build/cursors.py --preview H:\\night-runs\\nna-wallpaper-2\\shots\\stage11-cursors.png
"""
from __future__ import annotations

import argparse
import io
import json
import math
import os
import shutil
import struct
import subprocess
import sys

from PIL import Image, ImageDraw, ImageFont

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CURSORS_SRC = os.path.join(REPO, "brand", "cursors")
SHAPES_JSON = os.path.join(CURSORS_SRC, "shapes.json")
OUT_ROOT = os.path.join(REPO, "build", "out", "cursors")
PRESETS_ROOT = os.path.join(REPO, "presets", "cursors")
FONT_PATH = os.path.join(REPO.rsplit(os.sep, 1)[0], "brand", "assets", "fonts", "v3", "JetBrainsMono-Regular.ttf")
if not os.path.isfile(FONT_PATH):
    # brand assets live outside the repo worktree at a fixed path
    FONT_PATH = r"H:\brand\assets\fonts\v3\JetBrainsMono-Regular.ttf"

SIZES = [32, 48, 64]
SUPERSAMPLE = 4
ANIM_ROLES = {"wait", "busy"}
STATIC_ROLES = [
    "arrow", "hand", "ibeam", "size_ns", "size_we", "size_nwse", "size_nesw",
    "move", "no", "help", "crosshair",
]
ALL_ROLES = STATIC_ROLES + ["wait", "busy"]
VARIANTS = ["mark", "line", "mono"]

VARIANT_META = {
    "mark": {
        "name": {"ru": "Знак", "en": "Mark"},
        "description": "Бренд-стрелка по золотому треугольнику знака NNA1618, короткий перелом хвоста.",
    },
    "line": {
        "name": {"ru": "Линия", "en": "Line"},
        "description": "Тонкая линия 2px + точка Signal в вершине, минималистичный набор.",
    },
    "mono": {
        "name": {"ru": "Моно", "en": "Mono"},
        "description": "Классическая белая стрелка с чёрным контуром, углы 72/36, без украшений.",
    },
}


# ------------------------------------------------------------------ rasterizer detection

def detect_rasterizer():
    """Returns ('cairosvg', None) | ('cli', <exe path>) | ('fallback', None)."""
    try:
        import cairosvg  # noqa: F401
        try:
            cairosvg.svg2png(bytestring=b'<svg xmlns="http://www.w3.org/2000/svg" '
                                         b'width="4" height="4"/>', output_width=4, output_height=4)
            return "cairosvg", None
        except Exception as exc:
            print(f"[detect] cairosvg importable but native cairo failed to load: {exc}")
    except ImportError:
        print("[detect] cairosvg not installed")
    except OSError as exc:
        print(f"[detect] cairosvg installed but its native cairo library is missing: {exc.args[0] if exc.args else exc}".splitlines()[0])

    for exe in ("resvg", "rsvg-convert", "inkscape", "magick"):
        path = shutil.which(exe)
        if path:
            return "cli", path

    print("[detect] no SVG rasterizer available (cairosvg native lib / resvg / rsvg-convert / "
          "inkscape / magick) -- using brand/cursors/shapes.json + Pillow ImageDraw fallback")
    return "fallback", None


# ------------------------------------------------------------------ shapes.json -> Pillow renderer

def load_shapes():
    with open(SHAPES_JSON, "r", encoding="utf-8") as f:
        return json.load(f)


def resolve_color(palette, key):
    if key is None:
        return None
    if key == "outline":
        return palette["base"]
    if key == "fill":
        return palette["white"]
    if key == "dim":
        return "#2A2A2A"
    return palette.get(key, key)


def hex_to_rgba(hexcolor, alpha=255):
    hexcolor = hexcolor.lstrip("#")
    r = int(hexcolor[0:2], 16)
    g = int(hexcolor[2:4], 16)
    b = int(hexcolor[4:6], 16)
    return (r, g, b, alpha)


_FONT_CACHE = {}


def get_font(size_px):
    size_px = max(6, int(round(size_px)))
    if size_px not in _FONT_CACHE:
        try:
            _FONT_CACHE[size_px] = ImageFont.truetype(FONT_PATH, size_px)
        except Exception:
            _FONT_CACHE[size_px] = ImageFont.load_default()
    return _FONT_CACHE[size_px]


def draw_shapes(draw: ImageDraw.ImageDraw, shapes, palette, scale):
    for s in shapes:
        t = s["type"]
        if t == "polygon":
            pts = [(x * scale, y * scale) for x, y in s["points"]]
            if s.get("outline_only"):
                color = hex_to_rgba(resolve_color(palette, s.get("stroke", "white")))
                w = max(1, int(round(s.get("width", 2) * scale)))
                draw.line(pts + [pts[0]], fill=color, width=w, joint="curve")
                continue
            fill_key = s.get("fill")
            outline_key = s.get("outline")
            fill = hex_to_rgba(resolve_color(palette, fill_key)) if fill_key else None
            draw.polygon(pts, fill=fill)
            if outline_key:
                ocol = hex_to_rgba(resolve_color(palette, outline_key))
                ow = max(1, int(round(s.get("width", 1) * scale)))
                draw.line(pts + [pts[0]], fill=ocol, width=ow, joint="curve")
        elif t == "line":
            x1, y1, x2, y2 = s["x1"] * scale, s["y1"] * scale, s["x2"] * scale, s["y2"] * scale
            color = hex_to_rgba(resolve_color(palette, s["stroke"]))
            w = max(1, int(round(s.get("width", 2) * scale)))
            draw.line([(x1, y1), (x2, y2)], fill=color, width=w)
            if s.get("cap", "round") == "round":
                r = w / 2.0
                draw.ellipse([x1 - r, y1 - r, x1 + r, y1 + r], fill=color)
                draw.ellipse([x2 - r, y2 - r, x2 + r, y2 + r], fill=color)
        elif t == "circle":
            cx, cy, r = s["cx"] * scale, s["cy"] * scale, s["r"] * scale
            bbox = [cx - r, cy - r, cx + r, cy + r]
            fill_key = s.get("fill")
            outline_key = s.get("outline")
            fill = hex_to_rgba(resolve_color(palette, fill_key)) if fill_key else None
            outline = hex_to_rgba(resolve_color(palette, outline_key)) if outline_key else None
            ow = max(1, int(round(s.get("width", 1) * scale))) if outline else 1
            draw.ellipse(bbox, fill=fill, outline=outline, width=ow)
        elif t == "text":
            color = hex_to_rgba(resolve_color(palette, s.get("fill")))
            font = get_font(s.get("size", 10) * scale)
            draw.text((s["x"] * scale, s["y"] * scale), s["text"], font=font, fill=color, anchor="mm")
        else:
            raise ValueError(f"unknown shape type {t}")


def render_icon(shapes, palette, size, canvas=32, supersample=SUPERSAMPLE):
    big = size * supersample
    img = Image.new("RGBA", (big, big), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    scale = big / canvas
    draw_shapes(draw, shapes, palette, scale)
    return img.resize((size, size), Image.LANCZOS)


def scaled_hotspot(hotspot, size, canvas=32):
    hx = max(0, min(size - 1, int(round(hotspot[0] / canvas * size))))
    hy = max(0, min(size - 1, int(round(hotspot[1] / canvas * size))))
    return hx, hy


# ------------------------------------------------------------------ .cur / .ani binary writers

def bmp_dib_bytes(img: Image.Image) -> bytes:
    """32bpp BGRA DIB (BITMAPINFOHEADER, no file header), double-height with 1bpp AND mask,
    bottom-up rows, as required inside an ICO/CUR resource."""
    w, h = img.size
    px = img.load()
    header = struct.pack(
        "<IiiHHIIiiII",
        40,          # biSize
        w,           # biWidth
        h * 2,       # biHeight (color + mask)
        1,           # biPlanes
        32,          # biBitCount
        0,           # biCompression (BI_RGB)
        w * h * 4,   # biSizeImage
        0, 0,        # biX/YPelsPerMeter
        0, 0,        # biClrUsed / biClrImportant
    )
    color = bytearray(w * h * 4)
    idx = 0
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = px[x, y]
            color[idx] = b
            color[idx + 1] = g
            color[idx + 2] = r
            color[idx + 3] = a
            idx += 4

    mask_row_bytes = ((w + 31) // 32) * 4
    mask = bytearray(mask_row_bytes * h)
    for y in range(h - 1, -1, -1):
        row_out = (h - 1 - y) * mask_row_bytes
        for x in range(w):
            a = px[x, y][3]
            if a < 8:
                byte_i = row_out + (x // 8)
                bit = 7 - (x % 8)
                mask[byte_i] |= (1 << bit)

    return header + bytes(color) + bytes(mask)


def build_cur_bytes(frames_by_size) -> bytes:
    """frames_by_size: list of (size, PIL.Image RGBA, (hx, hy)) -> full .cur file bytes."""
    n = len(frames_by_size)
    entries = []
    images = []
    offset = 6 + 16 * n
    for size, img, (hx, hy) in frames_by_size:
        data = bmp_dib_bytes(img)
        b_w = size if size < 256 else 0
        b_h = size if size < 256 else 0
        entries.append(struct.pack(
            "<BBBBHHII", b_w, b_h, 0, 0, hx, hy, len(data), offset,
        ))
        images.append(data)
        offset += len(data)
    header = struct.pack("<HHH", 0, 2, n)
    return header + b"".join(entries) + b"".join(images)


def riff_chunk(fourcc: bytes, data: bytes) -> bytes:
    out = fourcc + struct.pack("<I", len(data)) + data
    if len(data) % 2:
        out += b"\x00"
    return out


def build_ani_bytes(frame_curs, rate_jiffies=4) -> bytes:
    n = len(frame_curs)
    anih = struct.pack(
        "<IIIIIIIII",
        36,              # cbSizeof
        n,               # cFrames
        n,               # cSteps
        0, 0,            # cx, cy (unused: icon chunks carry real size)
        0,               # cBitCount
        0,               # cPlanes
        rate_jiffies,    # cJifRate (default rate)
        0x00000001 | 0x00000002,  # AF_ICON | AF_SEQ
    )
    rate = struct.pack(f"<{n}I", *([rate_jiffies] * n))
    seq = struct.pack(f"<{n}I", *range(n))

    fram_body = b"".join(riff_chunk(b"icon", cur) for cur in frame_curs)
    body = (
        riff_chunk(b"anih", anih)
        + riff_chunk(b"rate", rate)
        + riff_chunk(b"seq ", seq)
        + b"LIST" + struct.pack("<I", len(fram_body) + 4) + b"fram" + fram_body
    )
    return b"RIFF" + struct.pack("<I", len(body) + 4) + b"ACON" + body


# ------------------------------------------------------------------ build pipeline

def build_role_frames(role_data, palette, sizes=SIZES):
    """Static role -> list[(size, img, hotspot_px)]."""
    hotspot = role_data["hotspot"]
    frames = []
    for size in sizes:
        img = render_icon(role_data["shapes"], palette, size)
        frames.append((size, img, scaled_hotspot(hotspot, size)))
    return frames


def build_anim_role_curs(role_data, palette, sizes=SIZES):
    """Animated role -> list of full .cur bytes, one per animation frame."""
    hotspot = role_data["hotspot"]
    curs = []
    for frame_shapes in role_data["frames"]:
        frames = []
        for size in sizes:
            img = render_icon(frame_shapes, palette, size)
            frames.append((size, img, scaled_hotspot(hotspot, size)))
        curs.append(build_cur_bytes(frames))
    return curs


def build_all(manifest, rasterizer_kind):
    palette = manifest["palette"]
    registry_role = manifest["registryRole"]
    written = []

    for variant in VARIANTS:
        vdir = os.path.join(OUT_ROOT, variant)
        os.makedirs(vdir, exist_ok=True)
        roles_data = manifest["variants"][variant]["roles"]
        files = []

        for role in STATIC_ROLES:
            frames = build_role_frames(roles_data[role], palette)
            cur_bytes = build_cur_bytes(frames)
            out_path = os.path.join(vdir, f"{role}.cur")
            with open(out_path, "wb") as f:
                f.write(cur_bytes)
            files.append({"role": role, "file": f"{role}.cur", "registryRole": registry_role[role]})
            written.append(out_path)

        for role in ("wait", "busy"):
            curs = build_anim_role_curs(roles_data[role], palette)
            ani_bytes = build_ani_bytes(curs, rate_jiffies=4)
            out_path = os.path.join(vdir, f"{role}.ani")
            with open(out_path, "wb") as f:
                f.write(ani_bytes)
            files.append({"role": role, "file": f"{role}.ani", "registryRole": registry_role[role]})
            written.append(out_path)

        meta = VARIANT_META[variant]
        preset = {
            "id": variant,
            "name": meta["name"],
            "description": meta["description"],
            "rasterizer": rasterizer_kind,
            "canvas": manifest["canvas"],
            "sizes": SIZES,
            "files": files,
        }
        os.makedirs(PRESETS_ROOT, exist_ok=True)
        preset_path = os.path.join(PRESETS_ROOT, f"{variant}.json")
        with open(preset_path, "w", encoding="utf-8") as f:
            json.dump(preset, f, ensure_ascii=False, indent=1)
        written.append(preset_path)

    return written


# ------------------------------------------------------------------ preview

def build_preview(manifest, out_path):
    palette = manifest["palette"]
    W, H = 1440, 800
    base = hex_to_rgba(palette["base"])
    white = hex_to_rgba(palette["white"])
    steel = hex_to_rgba(palette["steel"])
    ash = hex_to_rgba(palette["ash"])

    canvas = Image.new("RGB", (W, H), base[:3])
    draw = ImageDraw.Draw(canvas)

    header_font = get_font(26)
    label_font = get_font(13)

    col_w = W // 3
    margin_top = 56
    grid_cols = 4
    cell_w = col_w // grid_cols
    icon_size = 64
    cell_h = icon_size + 34

    strip_h = 132
    grid_top = margin_top + 40

    for ci, variant in enumerate(VARIANTS):
        cx0 = ci * col_w
        title = f"{variant.upper()}"
        draw.text((cx0 + 24, margin_top - 30), title, font=header_font, fill=white)
        draw.line([(cx0 + 24, margin_top + 4), (cx0 + col_w - 24, margin_top + 4)], fill=steel, width=1)

        roles_data = manifest["variants"][variant]["roles"]
        for i, role in enumerate(ALL_ROLES):
            r, c = divmod(i, grid_cols)
            data = roles_data[role]
            shapes = data["frames"][0] if "frames" in data else data["shapes"]
            icon = render_icon(shapes, palette, icon_size)
            px = cx0 + c * cell_w + (cell_w - icon_size) // 2
            py = grid_top + r * cell_h
            canvas.paste(icon, (px, py), icon)
            label = role
            bbox = draw.textbbox((0, 0), label, font=label_font)
            lw = bbox[2] - bbox[0]
            draw.text((cx0 + c * cell_w + (cell_w - lw) // 2, py + icon_size + 6),
                      label, font=label_font, fill=steel)

    # bottom light-background check strip
    strip_top = H - strip_h
    draw.rectangle([0, strip_top, W, H], fill=ash[:3])
    draw.text((24, strip_top + 10), "LIGHT BG CHECK", font=label_font, fill=(0x2A, 0x2A, 0x2A))
    x = 24
    y = strip_top + 40
    for variant in VARIANTS:
        roles_data = manifest["variants"][variant]["roles"]
        for role in ("arrow", "hand"):
            icon = render_icon(roles_data[role]["shapes"], palette, 64)
            canvas.paste(icon, (x, y), icon)
            bbox = draw.textbbox((0, 0), role, font=label_font)
            draw.text((x + (64 - (bbox[2] - bbox[0])) // 2, y + 68), role,
                      font=label_font, fill=(0x2A, 0x2A, 0x2A))
            x += 110
        x += 40

    os.makedirs(os.path.dirname(out_path), exist_ok=True)
    canvas.save(out_path, "PNG")
    return out_path


# ------------------------------------------------------------------ main

def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--preview", metavar="PNG_PATH", help="also render a 1440x800 preview PNG")
    args = ap.parse_args()

    kind, _ = detect_rasterizer()
    if kind != "fallback":
        print(f"[build] NOTE: an SVG rasterizer ({kind}) is available but this build always uses "
              "the shapes.json + Pillow fallback path for deterministic, dependency-free output.")
    manifest = load_shapes()

    written = build_all(manifest, rasterizer_kind="pillow-shapes.json (no cairo/resvg/inkscape/magick found)")
    print(f"[build] wrote {len(written)} files under {OUT_ROOT} and {PRESETS_ROOT}")

    if args.preview:
        path = build_preview(manifest, args.preview)
        print(f"[build] preview written to {path}")


if __name__ == "__main__":
    main()
