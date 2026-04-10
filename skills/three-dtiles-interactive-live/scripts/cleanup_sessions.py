#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import interactive_live_utils


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Clean up stale 3DTilesLink interactive session roots.")
    parser.add_argument("--repo-path", type=Path, default=interactive_live_utils.repo_root())
    parser.add_argument("--port", type=int)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    repo_path = args.repo_path.resolve()
    try:
        port = args.port if args.port is not None else interactive_live_utils.discover_port(repo_path)
        interactive_live_utils.guarded_cleanup_sessions(repo_path, port)
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        return 1

    json.dump({"repo_path": str(repo_path), "port": port, "cleaned": True}, sys.stdout, ensure_ascii=False, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
