#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
build/check-links.py -- checks every Markdown link/image in README.md and docs/*.md.

Relative links (and images) must resolve to a file that actually exists on disk, relative to
the folder the Markdown file itself lives in. External links (http/https/mailto) are only
checked for form (a plausible URL), never fetched -- this script has no network access
requirement and none is wanted for a docs check. A link's own "#fragment" is stripped before
resolving the file; the fragment itself is not verified against the target's headings.

Usage:
    python build/check-links.py                  # checks README.md + docs/*.md
    python build/check-links.py <file> [<file>...]  # checks exactly the given files

Exits 0 and prints "OK" if every link resolves; exits 1 and lists every broken link otherwise.
"""
from __future__ import annotations

import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent

# [text](target) and ![alt](target) -- target may carry a "title" in quotes after a space.
LINK_RE = re.compile(r'!?\[[^\]]*\]\(([^)]+)\)')
EXTERNAL_RE = re.compile(r'^(https?://|mailto:)', re.IGNORECASE)
FENCE_RE = re.compile(r'^\s*```')


def strip_code_blocks(text: str) -> str:
    """Blank out fenced code blocks and inline code spans, so a link-looking string inside a
    code sample is never checked as a real Markdown link."""
    out_lines = []
    in_fence = False
    for line in text.split("\n"):
        if FENCE_RE.match(line):
            in_fence = not in_fence
            out_lines.append("")
            continue
        out_lines.append("" if in_fence else re.sub(r'`[^`]*`', '', line))
    return "\n".join(out_lines)


def default_targets() -> list[Path]:
    targets = [REPO_ROOT / "README.md"]
    targets += sorted((REPO_ROOT / "docs").glob("*.md"))
    return [t for t in targets if t.exists()]


def check_file(path: Path) -> list[str]:
    problems = []
    text = strip_code_blocks(path.read_text(encoding="utf-8"))
    for m in LINK_RE.finditer(text):
        target = m.group(1).strip()
        # Drop a trailing quoted title: (target "title")
        target = re.split(r'\s+"', target, maxsplit=1)[0].strip()
        if not target or target.startswith("#"):
            continue  # empty or same-file anchor, nothing to resolve on disk
        if EXTERNAL_RE.match(target):
            # Form check only: scheme plus a non-empty host.
            if not re.match(r'^(https?://|mailto:)\S+\.\S+', target) and "mailto:" not in target:
                problems.append(f"{path.relative_to(REPO_ROOT)}: malformed external link: {target}")
            continue
        if target.startswith("//"):
            continue  # protocol-relative external link, form-only as above
        file_part = target.split("#", 1)[0]
        if not file_part:
            continue
        resolved = (path.parent / file_part).resolve()
        if not resolved.exists():
            problems.append(f"{path.relative_to(REPO_ROOT)}: broken relative link: {target}")
    return problems


def main(argv: list[str]) -> int:
    targets = [Path(a) for a in argv[1:]] if len(argv) > 1 else default_targets()
    all_problems: list[str] = []
    checked = 0
    for path in targets:
        if not path.exists():
            all_problems.append(f"{path}: file does not exist")
            continue
        checked += 1
        all_problems.extend(check_file(path))

    if all_problems:
        print(f"FAIL ({len(all_problems)} problem(s) across {checked} file(s)):")
        for p in all_problems:
            print("  " + p)
        return 1

    print(f"OK: {checked} file(s) checked, all links resolve")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv))
