#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
build/cursors-check.py -- validates every built .cur / .ani cursor file:
  - ICONDIR / RIFF ACON signatures
  - declared frame sizes (32/48/64) and image byte lengths line up with the DIB header
  - hotspot lies inside the frame bounds
  - ANI files: 8 animation frames, each a well-formed embedded .cur
  - Windows actually accepts the file: user32!LoadCursorFromFileW returns a non-null handle

Prints a table and exits non-zero if anything fails.
"""
from __future__ import annotations

import ctypes
import os
import struct
import sys

REPO = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
OUT_ROOT = os.path.join(REPO, "build", "out", "cursors")
VARIANTS = ["mark", "line", "mono"]
STATIC_ROLES = [
    "arrow", "hand", "ibeam", "size_ns", "size_we", "size_nwse", "size_nesw",
    "move", "no", "help", "crosshair",
]
ANIM_ROLES = ["wait", "busy"]
EXPECTED_SIZES = {32, 48, 64}
EXPECTED_ANIM_FRAMES = 8


class Fail(Exception):
    pass


def parse_cur_bytes(data: bytes, where: str):
    """Validates one .cur blob's structure, returns list of (w, h, hx, hy)."""
    if len(data) < 6:
        raise Fail(f"{where}: file too short for ICONDIR")
    reserved, typ, count = struct.unpack_from("<HHH", data, 0)
    if reserved != 0:
        raise Fail(f"{where}: ICONDIR.reserved != 0")
    if typ != 2:
        raise Fail(f"{where}: ICONDIR.type != 2 (got {typ}), not a CUR file")
    if count < 1:
        raise Fail(f"{where}: ICONDIR.count == 0")

    sizes = []
    off = 6
    for i in range(count):
        if off + 16 > len(data):
            raise Fail(f"{where}: entry {i} out of range")
        b_w, b_h, colors, resv, hx, hy, bytes_in_res, img_off = struct.unpack_from(
            "<BBBBHHII", data, off)
        w = b_w or 256
        h = b_h or 256
        if resv != 0:
            raise Fail(f"{where}: entry {i} reserved byte != 0")
        if img_off + bytes_in_res > len(data):
            raise Fail(f"{where}: entry {i} image data out of range "
                       f"(offset {img_off} + size {bytes_in_res} > file {len(data)})")
        if not (0 <= hx < w and 0 <= hy < h):
            raise Fail(f"{where}: entry {i} hotspot ({hx},{hy}) outside frame {w}x{h}")

        img = data[img_off:img_off + bytes_in_res]
        if len(img) < 40:
            raise Fail(f"{where}: entry {i} image data shorter than a BITMAPINFOHEADER")
        bi_size, bi_w, bi_h, bi_planes, bi_bitcount = struct.unpack_from("<IiiHH", img, 0)
        if bi_size != 40:
            raise Fail(f"{where}: entry {i} unexpected DIB header size {bi_size}")
        if bi_w != w:
            raise Fail(f"{where}: entry {i} DIB width {bi_w} != directory width {w}")
        if bi_h != h * 2:
            raise Fail(f"{where}: entry {i} DIB height {bi_h} != 2x directory height ({h*2})")
        if bi_bitcount != 32:
            raise Fail(f"{where}: entry {i} bitcount {bi_bitcount} != 32")
        expected_color = w * h * 4
        mask_row = ((w + 31) // 32) * 4
        expected_total = 40 + expected_color + mask_row * h
        if len(img) < expected_total:
            raise Fail(f"{where}: entry {i} image data too short: {len(img)} < {expected_total}")

        sizes.append((w, h, hx, hy))
        off += 16
    return sizes


def check_cur_file(path: str):
    with open(path, "rb") as f:
        data = f.read()
    sizes = parse_cur_bytes(data, os.path.basename(path))
    got_sizes = {w for w, h, hx, hy in sizes}
    missing = EXPECTED_SIZES - got_sizes
    if missing:
        raise Fail(f"{os.path.basename(path)}: missing sizes {sorted(missing)} "
                   f"(got {sorted(got_sizes)})")
    return sizes


def parse_riff_chunks(data: bytes, start: int, end: int):
    chunks = []
    off = start
    while off + 8 <= end:
        fourcc = data[off:off + 4]
        size = struct.unpack_from("<I", data, off + 4)[0]
        body_start = off + 8
        body_end = body_start + size
        chunks.append((fourcc, body_start, body_end))
        off = body_end + (size % 2)
    return chunks


def check_ani_file(path: str):
    with open(path, "rb") as f:
        data = f.read()
    if data[0:4] != b"RIFF" or data[8:12] != b"ACON":
        raise Fail(f"{os.path.basename(path)}: not a RIFF/ACON file")
    riff_size = struct.unpack_from("<I", data, 4)[0]
    if riff_size + 8 > len(data):
        raise Fail(f"{os.path.basename(path)}: RIFF size {riff_size} exceeds file length")

    chunks = parse_riff_chunks(data, 12, len(data))
    by_id = {}
    fram_chunks = []
    for fourcc, s, e in chunks:
        if fourcc == b"LIST":
            list_type = data[s:s + 4]
            if list_type == b"fram":
                fram_chunks = parse_riff_chunks(data, s + 4, e)
        else:
            by_id.setdefault(fourcc, []).append((s, e))

    if b"anih" not in by_id:
        raise Fail(f"{os.path.basename(path)}: missing 'anih' chunk")
    s, e = by_id[b"anih"][0]
    if e - s != 36:
        raise Fail(f"{os.path.basename(path)}: 'anih' chunk size {e-s} != 36")
    (cbsizeof, cframes, csteps, cx, cy, cbitcount, cplanes, cjifrate, flags) = \
        struct.unpack_from("<IIIIIIIII", data, s)
    if cframes != EXPECTED_ANIM_FRAMES:
        raise Fail(f"{os.path.basename(path)}: cFrames={cframes}, expected {EXPECTED_ANIM_FRAMES}")
    if not (flags & 0x1):
        raise Fail(f"{os.path.basename(path)}: AF_ICON flag not set")

    icon_chunks = [c for c in fram_chunks if c[0] == b"icon"]
    if len(icon_chunks) != EXPECTED_ANIM_FRAMES:
        raise Fail(f"{os.path.basename(path)}: {len(icon_chunks)} 'icon' chunks in LIST fram, "
                   f"expected {EXPECTED_ANIM_FRAMES}")

    all_sizes = set()
    for i, (fourcc, s, e) in enumerate(icon_chunks):
        sizes = parse_cur_bytes(data[s:e], f"{os.path.basename(path)} frame {i}")
        all_sizes |= {w for w, h, hx, hy in sizes}
    missing = EXPECTED_SIZES - all_sizes
    if missing:
        raise Fail(f"{os.path.basename(path)}: animation frames missing sizes {sorted(missing)}")
    return icon_chunks


# ------------------------------------------------------------------ LoadCursorFromFile

def load_cursor_handle(path: str):
    user32 = ctypes.windll.user32
    LoadCursorFromFileW = user32.LoadCursorFromFileW
    LoadCursorFromFileW.argtypes = [ctypes.c_wchar_p]
    LoadCursorFromFileW.restype = ctypes.c_void_p
    handle = LoadCursorFromFileW(path)
    if not handle:
        err = ctypes.GetLastError()
        return None, err
    user32.DestroyCursor(ctypes.c_void_p(handle))
    return handle, 0


# ------------------------------------------------------------------ main

def main():
    rows = []
    ok_all = True

    for variant in VARIANTS:
        vdir = os.path.join(OUT_ROOT, variant)
        for role in STATIC_ROLES:
            path = os.path.join(vdir, f"{role}.cur")
            status = "OK"
            detail = ""
            try:
                if not os.path.isfile(path):
                    raise Fail("file missing")
                sizes = check_cur_file(path)
                detail = f"{len(sizes)} frames " + ",".join(f"{w}x{h}@{hx},{hy}" for w, h, hx, hy in sizes)
                handle, err = load_cursor_handle(path)
                if not handle:
                    raise Fail(f"LoadCursorFromFileW failed (GetLastError={err})")
                detail += f" | LoadCursorFromFile=0x{handle:X}"
            except Fail as exc:
                status = "FAIL"
                detail = str(exc)
                ok_all = False
            rows.append((variant, role, "cur", status, detail))

        for role in ANIM_ROLES:
            path = os.path.join(vdir, f"{role}.ani")
            status = "OK"
            detail = ""
            try:
                if not os.path.isfile(path):
                    raise Fail("file missing")
                icon_chunks = check_ani_file(path)
                detail = f"{len(icon_chunks)} anim frames"
                handle, err = load_cursor_handle(path)
                if not handle:
                    raise Fail(f"LoadCursorFromFileW failed (GetLastError={err})")
                detail += f" | LoadCursorFromFile=0x{handle:X}"
            except Fail as exc:
                status = "FAIL"
                detail = str(exc)
                ok_all = False
            rows.append((variant, role, "ani", status, detail))

    w_variant = max(len(r[0]) for r in rows) + 2
    w_role = max(len(r[1]) for r in rows) + 2
    w_kind = 5
    w_status = 6
    print(f"{'variant':<{w_variant}}{'role':<{w_role}}{'kind':<{w_kind}}{'status':<{w_status}}detail")
    print("-" * (w_variant + w_role + w_kind + w_status + 60))
    for variant, role, kind, status, detail in rows:
        print(f"{variant:<{w_variant}}{role:<{w_role}}{kind:<{w_kind}}{status:<{w_status}}{detail}")

    n_ok = sum(1 for r in rows if r[3] == "OK")
    print("-" * (w_variant + w_role + w_kind + w_status + 60))
    print(f"{n_ok}/{len(rows)} OK")

    if not ok_all:
        print("CHECK FAILED", file=sys.stderr)
        sys.exit(1)
    print("ALL OK")
    sys.exit(0)


if __name__ == "__main__":
    main()
