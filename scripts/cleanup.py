#!/usr/bin/env python3
"""Скрипт очистки репозитория carddiagnostik"""
import os
import shutil
import sys
from pathlib import Path

DRY_RUN = os.getenv("DRY_RUN", "true").lower() == "true"

TO_REMOVE = [
    "server/main.py.bak.*",
    "mobile/**/*.bak.*",
    "**/*.tmp",
    "**/*.temp",
    "**/*.log",
    "**/.DS_Store",
    "**/Thumbs.db",
    "server/check_deps.py",
    "server/check_deps2.py",
    "server/check_links.py",
    "mobile/setup.cmd",
]

def remove_file(path: Path):
    if DRY_RUN:
        print(f"[DRY RUN] Would remove: {path}")
        return
    try:
        if path.is_file():
            path.unlink()
            print(f"Removed file: {path}")
        elif path.is_dir():
            shutil.rmtree(path)
            print(f"Removed dir: {path}")
    except Exception as e:
        print(f"Error removing {path}: {e}")

def main():
    repo_root = Path(".")
    removed_count = 0
    print(f"=== Repository Cleanup {'(DRY RUN)' if DRY_RUN else '(LIVE)'} ===\n")
    for pattern in TO_REMOVE:
        for path in repo_root.rglob(pattern):
            remove_file(path)
            removed_count += 1
    print(f"\n=== Summary ===")
    print(f"Files processed: {removed_count}")
    if DRY_RUN:
        print("Run with DRY_RUN=false to actually remove files")
    return 0

if __name__ == "__main__":
    sys.exit(main())
