#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import re
import sys
import time
from pathlib import Path
from typing import Any

import interactive_live_utils


def wait_for_startup(stdout_log: Path, stderr_log: Path, timeout_s: float) -> dict[str, Any]:
    deadline = time.monotonic() + timeout_s
    attached_pattern = re.compile(r"Interactive input bindings attached")
    host_failure_pattern = re.compile(r"Hosting failed to start|System\.InvalidOperationException")

    while time.monotonic() < deadline:
        stdout_text = interactive_live_utils.read_text(stdout_log)
        stderr_text = interactive_live_utils.read_text(stderr_log)
        if stderr_text.strip():
            raise RuntimeError(stderr_text.strip())
        if host_failure_pattern.search(stdout_text):
            raise RuntimeError(stdout_text.strip())
        if attached_pattern.search(stdout_text):
            return {
                "attached": True,
                "stdout_size": len(stdout_text),
            }
        time.sleep(0.5)
    raise TimeoutError("Timed out waiting for interactive input bindings to attach.")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Start one live interactive process and wait for startup.")
    parser.add_argument("--repo-path", type=Path, default=interactive_live_utils.repo_root())
    parser.add_argument("--port", type=int)
    parser.add_argument("--output-dir", type=Path, default=Path("/mnt/c/Temp/3DTilesLinkSkillLogs/interactive"))
    parser.add_argument("--prefix", default=f"interactive-live-{time.strftime('%Y%m%d-%H%M%S')}")
    parser.add_argument("--skip-cleanup", action="store_true")
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--poll-interval", type=int, default=250)
    parser.add_argument("--debounce", type=int, default=800)
    parser.add_argument("--throttle", type=int, default=3000)
    parser.add_argument("--log-level", default="Information")
    parser.add_argument("--startup-timeout", type=float, default=20.0)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    repo_path = args.repo_path.resolve()
    launch: dict[str, Any] | None = None
    phase = "discover"
    try:
        phase = "discover"
        port = args.port if args.port is not None else interactive_live_utils.discover_port(repo_path)
        if not args.skip_cleanup:
            phase = "pre-cleanup"
            interactive_live_utils.guarded_cleanup_sessions(repo_path, port)
        phase = "launch"
        launch = interactive_live_utils.start_interactive_process(
            repo_path,
            port=port,
            output_dir=args.output_dir.resolve(),
            prefix=args.prefix,
            no_build=args.no_build,
            poll_interval_ms=args.poll_interval,
            debounce_ms=args.debounce,
            throttle_ms=args.throttle,
            log_level=args.log_level,
        )
        phase = "startup"
        startup = wait_for_startup(
            Path(launch["stdout_log"]),
            Path(launch["stderr_log"]),
            args.startup_timeout,
        )
    except Exception as exc:
        if launch is not None:
            interactive_live_utils.kill_process(int(launch["pid"]))
            if not args.skip_cleanup:
                try:
                    interactive_live_utils.guarded_cleanup_sessions(repo_path, int(launch["port"]))
                except Exception:
                    pass
        message = f"{phase} failed: {exc}"
        if launch is not None:
            message += (
                f"\nstdout_log={launch['stdout_log']}"
                f"\nstderr_log={launch['stderr_log']}"
            )
        print(message, file=sys.stderr)
        return 1

    result: dict[str, Any] = {
        **launch,
        "startup": startup,
    }
    json.dump(result, sys.stdout, ensure_ascii=False, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
