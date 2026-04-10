#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path

import run_tokyo_tower_stream


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Clean up stale 3DTilesLink live session roots.")
    parser.add_argument("--repo-path", type=Path, default=run_tokyo_tower_stream.repo_root())
    parser.add_argument("--port", type=int)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    repo_path = args.repo_path.resolve()
    try:
        port = args.port if args.port is not None else run_tokyo_tower_stream.discover_port(repo_path)
        active_processes = run_tokyo_tower_stream.list_active_live_cli_processes(port)
        if active_processes:
            raise RuntimeError(
                "cleanup-sessions is unsafe while live stream/interactive processes are still running on the target port.\n"
                + "\n".join(active_processes)
            )
        run_tokyo_tower_stream.cleanup_sessions(repo_path, port)
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        return 1

    json.dump({"repo_path": str(repo_path), "port": port, "cleaned": True}, sys.stdout, ensure_ascii=False, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
