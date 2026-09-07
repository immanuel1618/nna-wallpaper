#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
build/widget-previews.py -- generates the 10 static widgets/<id>/preview.png fallback images used
by GET /widgets/<id>/preview.png (see src/NNA.Wallpaper.Host/Services/WidgetsService.cs) when the
block isn't placed on a running monitor, or there is no engine to capture from (headless run,
settings window opened stand-alone). One schematic 610x377 PNG per widget, built from plain
NNA1618 v3 brand shapes -- bars, rings, node graphs, and so on -- never placeholder "lorem" text.

Dependencies: Pillow only.

Usage:
    python build/widget-previews.py
    python build/widget-previews.py --out H:\\night-runs\\nna-wallpaper-2\\shots\\stage5-previews.png
        (writes one contact sheet of all 10 previews next to the real files, for a quick look)
"""
from __future__ import annotations

import argparse
import math
import os

from PIL import Image, ImageDraw

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WIDGETS_DIR = os.path.join(REPO, "widgets")

W, H = 610, 377

# NNA1618 v3 tokens (ui/tokens.css) -- the only colors used here.
BASE = (11, 11, 11)
SURFACE = (22, 22, 22)
SLATE = (67, 67, 67)
STEEL = (128, 128, 128)
ASH = (200, 200, 200)
WHITE = (255, 255, 255)


def new_canvas():
    img = Image.new("RGB", (W, H), SURFACE)
    d = ImageDraw.Draw(img)
    d.rectangle([0, 0, W - 1, H - 1], outline=SLATE, width=1)
    return img, d


def draw_eq(d):
    # Equalizer bars: uneven heights, alternating Steel/White, like a stopped audio meter.
    n = 18
    pad = 70
    gap = 10
    bw = (W - pad * 2 - gap * (n - 1)) / n
    base_y = H - 90
    heights = [0.35, 0.62, 0.24, 0.8, 0.5, 0.9, 0.4, 0.68, 0.3, 0.75, 0.55, 0.2, 0.85, 0.45, 0.6, 0.33, 0.7, 0.5]
    for i, hfrac in enumerate(heights):
        x0 = pad + i * (bw + gap)
        bar_h = 170 * hfrac
        color = WHITE if i % 3 == 1 else STEEL
        d.rectangle([x0, base_y - bar_h, x0 + bw, base_y], fill=color)


def draw_photo(d):
    # A small stack of photo frames, front one holding a sun + mountain line.
    def frame(cx, cy, w, h, angle, color):
        img2 = Image.new("RGBA", (w + 20, h + 20), (0, 0, 0, 0))
        d2 = ImageDraw.Draw(img2)
        d2.rounded_rectangle([10, 10, 10 + w, 10 + h], radius=8, outline=color, width=3)
        img2 = img2.rotate(angle, expand=True, resample=Image.BICUBIC)
        return img2

    base = Image.new("RGBA", (W, H), (0, 0, 0, 0))
    for angle, color in [(-8, SLATE), (5, STEEL)]:
        f = frame(0, 0, 210, 150, angle, color)
        base.alpha_composite(f, (int(W / 2 - f.width / 2), int(H / 2 - f.height / 2)))
    front = frame(0, 0, 210, 150, 0, WHITE)
    fx, fy = int(W / 2 - front.width / 2), int(H / 2 - front.height / 2)
    base.alpha_composite(front, (fx, fy))
    return base, fx, fy


def draw_photo_full(img, d):
    base, fx, fy = draw_photo(d)
    img.paste(base, (0, 0), base)
    d2 = ImageDraw.Draw(img)
    cx, cy = fx + 55, fy + 55
    d2.ellipse([cx - 14, cy - 14, cx + 14, cy + 14], outline=STEEL, width=3)
    mx0, my0 = fx + 25, fy + 120
    d2.line([mx0, my0, fx + 90, fy + 70, fx + 130, fy + 105, fx + 165, fy + 65, fx + 190, fy + 120],
            fill=ASH, width=3, joint="curve")


def draw_focus(d):
    cx, cy = W // 2, H // 2
    r_outer = 95
    r_inner = 55
    d.ellipse([cx - r_outer, cy - r_outer, cx + r_outer, cy + r_outer], outline=SLATE, width=3)
    # progress arc, like a partially completed work phase
    bbox = [cx - r_outer, cy - r_outer, cx + r_outer, cy + r_outer]
    d.arc(bbox, start=-90, end=140, fill=WHITE, width=6)
    d.ellipse([cx - r_inner, cy - r_inner, cx + r_inner, cy + r_inner], outline=STEEL, width=3)
    d.line([cx, cy, cx, cy - r_inner + 12], fill=ASH, width=3)


def draw_weather(d):
    cx, cy = 250, 165
    # cloud: three overlapping circles + a base rectangle-ish underline
    d.ellipse([cx - 70, cy - 10, cx - 10, cy + 50], outline=STEEL, width=3)
    d.ellipse([cx - 20, cy - 40, cx + 60, cy + 40], outline=STEEL, width=3)
    d.ellipse([cx + 20, cy - 5, cx + 90, cy + 55], outline=STEEL, width=3)
    d.line([cx - 75, cy + 50, cx + 95, cy + 50], fill=STEEL, width=3)
    # three small clocks along the bottom, matching "weather + clocks"
    for i, x in enumerate([120, 340, 480]):
        r = 26
        y = 300
        d.ellipse([x - r, y - r, x + r, y + r], outline=SLATE if i else WHITE, width=3)
        d.line([x, y, x, y - r + 6], fill=ASH, width=2)
        d.line([x, y, x + r - 10, y], fill=ASH, width=2)


def draw_calendar(d):
    x0, y0, x1, y1 = 100, 60, 510, 320
    d.rounded_rectangle([x0, y0, x1, y1], radius=10, outline=STEEL, width=3)
    d.line([x0, y0 + 60, x1, y0 + 60], fill=STEEL, width=3)
    d.line([x0 + 60, y0 - 15, x0 + 60, y0 + 15], fill=ASH, width=4)
    d.line([x1 - 60, y0 - 15, x1 - 60, y0 + 15], fill=ASH, width=4)
    cols, rows = 6, 3
    cell_w = (x1 - x0) / cols
    cell_h = (y1 - y0 - 60) / rows
    marks = {(1, 0), (3, 1), (0, 2), (4, 2)}
    for r in range(rows):
        for c in range(cols):
            cx = x0 + c * cell_w + cell_w / 2
            cy = y0 + 60 + r * cell_h + cell_h / 2
            rad = 9 if (c, r) in marks else 3
            color = WHITE if (c, r) in marks else SLATE
            d.ellipse([cx - rad, cy - rad, cx + rad, cy + rad], fill=color)


def draw_launch(d):
    cols, rows = 4, 2
    pad_x, pad_y = 90, 80
    gap = 24
    size = 70
    total_w = cols * size + (cols - 1) * gap
    total_h = rows * size + (rows - 1) * gap
    x0 = (W - total_w) / 2
    y0 = (H - total_h) / 2
    highlight = {(1, 0), (2, 1)}
    for r in range(rows):
        for c in range(cols):
            x = x0 + c * (size + gap)
            y = y0 + r * (size + gap)
            color = WHITE if (c, r) in highlight else STEEL
            d.rounded_rectangle([x, y, x + size, y + size], radius=16, outline=color, width=3)


def draw_graph(d):
    nodes = [(120, 90), (490, 70), (300, 190), (110, 300), (470, 290), (300, 330)]
    edges = [(0, 2), (1, 2), (2, 3), (2, 4), (3, 5), (4, 5)]
    for a, b in edges:
        d.line([nodes[a], nodes[b]], fill=SLATE, width=2)
    for i, (x, y) in enumerate(nodes):
        r = 12 if i == 2 else 8
        color = WHITE if i == 2 else STEEL
        d.ellipse([x - r, y - r, x + r, y + r], outline=color, width=3)


def draw_stats(d):
    x0, y0 = 90, 300
    d.line([x0, 60, x0, y0], fill=SLATE, width=2)
    d.line([x0, y0, 540, y0], fill=SLATE, width=2)
    bars = [0.3, 0.55, 0.4, 0.7, 0.5, 0.85, 0.6, 0.95]
    n = len(bars)
    bw = (540 - x0 - 40) / n
    for i, frac in enumerate(bars):
        bx = x0 + 30 + i * bw
        bh = (y0 - 60) * frac
        color = WHITE if i == n - 1 else STEEL
        d.rectangle([bx, y0 - bh, bx + bw * 0.6, y0], fill=color)


def draw_player(d):
    cx, cy = W // 2, H // 2
    r = 90
    d.ellipse([cx - r, cy - r, cx + r, cy + r], outline=STEEL, width=3)
    d.ellipse([cx - 14, cy - 14, cx + 14, cy + 14], outline=ASH, width=3)
    # play glyph sits below the disc, matching the widget's transport row
    ty = cy + r + 40
    d.polygon([(cx - 18, ty - 20), (cx - 18, ty + 20), (cx + 18, ty)], fill=WHITE)
    d.line([cx - 110, ty, cx - 40, ty], fill=SLATE, width=3)
    d.line([cx + 40, ty, cx + 110, ty], fill=SLATE, width=3)


def draw_tasks(d):
    x0 = 110
    y0 = 90
    row_h = 62
    rows = [(True, 220), (True, 170), (False, 260), (False, 140)]
    for i, (done, w) in enumerate(rows):
        y = y0 + i * row_h
        r = 13
        color = WHITE if done else SLATE
        d.ellipse([x0, y - r, x0 + 2 * r, y + r], outline=color, width=3)
        if done:
            d.line([x0 + 5, y, x0 + 11, y + 6, x0 + 21, y - 8], fill=WHITE, width=3, joint="curve")
        d.line([x0 + 44, y, x0 + 44 + w, y], fill=ASH if done else STEEL, width=4)


DRAWERS = {
    "eq": draw_eq,
    "focus": draw_focus,
    "weather": draw_weather,
    "events": draw_calendar,
    "launch": draw_launch,
    "graph": draw_graph,
    "stats": draw_stats,
    "player": draw_player,
    "planner": draw_tasks,
}


def build_one(widget_id: str) -> Image.Image:
    img, d = new_canvas()
    if widget_id == "photos":
        draw_photo_full(img, d)
    else:
        DRAWERS[widget_id](d)
    return img


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--out", help="also write a contact sheet PNG of all previews to this path")
    args = ap.parse_args()

    ids = ["eq", "photos", "focus", "weather", "events", "launch", "graph", "stats", "player", "planner"]
    images = {}
    for wid in ids:
        img = build_one(wid)
        out_path = os.path.join(WIDGETS_DIR, wid, "preview.png")
        os.makedirs(os.path.dirname(out_path), exist_ok=True)
        img.save(out_path, "PNG")
        images[wid] = img
        print("wrote", out_path)

    if args.out:
        cols = 2
        rows = math.ceil(len(ids) / cols)
        pad = 12
        sheet = Image.new("RGB", (cols * W + (cols + 1) * pad, rows * H + (rows + 1) * pad), BASE)
        for i, wid in enumerate(ids):
            r, c = divmod(i, cols)
            x = pad + c * (W + pad)
            y = pad + r * (H + pad)
            sheet.paste(images[wid], (x, y))
        os.makedirs(os.path.dirname(args.out), exist_ok=True)
        sheet.save(args.out, "PNG")
        print("wrote", args.out)


if __name__ == "__main__":
    main()
