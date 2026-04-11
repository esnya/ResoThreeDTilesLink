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
    parser.add_argument("--host")
    parser.add_argument("--port", type=int)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    repo_path = args.repo_path.resolve()
    try:
        discovered_session = None if args.port is not None else run_tokyo_tower_stream.discover_session(repo_path)
        port = args.port if args.port is not None else int(discovered_session["port"])
        candidate_hosts = run_tokyo_tower_stream.resolve_candidate_hosts(args.host, discovered_session)
        probe = run_tokyo_tower_stream.ensure_listener_ready(
            port,
            hosts=candidate_hosts,
        )
        host = str(probe["host"])
        _ = run_tokyo_tower_stream.ensure_protocol_ready(repo_path, host, port)
        active_processes = run_tokyo_tower_stream.list_active_live_cli_processes(port)
        if active_processes:
            raise RuntimeError(
                "cleanup-sessions is unsafe while live stream/interactive processes are still running on the target port.\n"
                + "\n".join(active_processes)
            )
        run_tokyo_tower_stream.cleanup_sessions(repo_path, host, port)
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        return 1

    json.dump({"repo_path": str(repo_path), "host": host, "port": port, "cleaned": True}, sys.stdout, ensure_ascii=False, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
