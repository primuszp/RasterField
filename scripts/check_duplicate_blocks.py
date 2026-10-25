#!/usr/bin/env python3
"""Report exact C# blocks repeated in production sources (comments/usings excluded)."""
from collections import defaultdict
from pathlib import Path
import argparse


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--lines", type=int, default=12, help="minimum consecutive non-comment lines")
    args = parser.parse_args()
    if args.lines < 2:
        parser.error("--lines must be at least 2")
    root = Path(__file__).resolve().parents[1]
    blocks = defaultdict(list)
    for path in sorted((root / "Source").rglob("*.cs")):
        if any(part in {"bin", "obj", "RasterField.Tests"} for part in path.parts):
            continue
        rows = [(number, line.strip()) for number, line in enumerate(path.read_text().splitlines(), 1)
                if line.strip() and not line.strip().startswith(("//", "using "))]
        for start in range(len(rows) - args.lines + 1):
            block = tuple(line for _, line in rows[start:start + args.lines])
            blocks[block].append((path.relative_to(root), rows[start][0]))
    groups = 0
    reported = set()
    for locations in blocks.values():
        if len(locations) < 2:
            continue
        key = tuple(path for path, _ in locations)
        if key in reported:
            continue
        reported.add(key)
        groups += 1
        print("Repeated block: " + ", ".join(f"{path}:{line}" for path, line in locations))
    print(f"{groups} repeated production block group(s), threshold {args.lines} lines.")
    return bool(groups)


if __name__ == "__main__":
    raise SystemExit(main())
