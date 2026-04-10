#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Any

import run_tokyo_tower_stream

DEFAULT_OUTPUT_DIR = Path("/mnt/c/Temp/3DTilesLinkSkillLogs/stream-compare")


def run_git(repo_path: Path, *args: str) -> str:
    result = run_tokyo_tower_stream.run_process(["git", "-C", str(repo_path), *args], cwd=repo_path)
    if result.returncode != 0:
        raise RuntimeError(result.stderr or result.stdout)
    return result.stdout.strip()


def latest_vtag(repo_path: Path) -> str:
    output = run_git(repo_path, "tag", "--list", "v*", "--sort=-version:refname")
    tags = [line.strip() for line in output.splitlines() if line.strip()]
    if not tags:
        raise RuntimeError("No v* tag found.")
    return tags[0]


def ensure_worktree(repo_path: Path, tag: str) -> Path:
    worktrees_root = repo_path / ".worktrees"
    worktrees_root.mkdir(parents=True, exist_ok=True)
    worktree_path = worktrees_root / f"skill-stream-{tag}"
    if worktree_path.exists():
        status = run_tokyo_tower_stream.run_process(
            ["git", "-C", str(worktree_path), "status", "--porcelain"],
            cwd=repo_path,
        )
        if status.returncode != 0:
            raise RuntimeError(status.stderr or status.stdout)
        if status.stdout.strip():
            raise RuntimeError(
                f"Existing worktree {worktree_path} is dirty. Clean or remove it before running the latest-tag comparison again."
            )
        result = run_tokyo_tower_stream.run_process(
            ["git", "-C", str(repo_path), "worktree", "remove", "--force", str(worktree_path)],
            cwd=repo_path,
        )
        if result.returncode != 0:
            raise RuntimeError(result.stderr or result.stdout)
    result = run_tokyo_tower_stream.run_process(
        ["git", "-C", str(repo_path), "worktree", "add", "--detach", str(worktree_path), tag],
        cwd=repo_path,
    )
    if result.returncode != 0:
        raise RuntimeError(result.stderr or result.stdout)
    return worktree_path


def build_solution(repo_path: Path) -> None:
    repo_win = run_tokyo_tower_stream.wsl_to_windows(repo_path)
    script = "\r\n".join(
        [
            "@echo off",
            f'cd /d "{repo_win}"',
            "dotnet.exe build ThreeDTilesLink.slnx --verbosity minimal",
        ]
    )
    with tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".cmd", delete=False) as handle:
        handle.write(script)
        handle.flush()
        script_path = Path(handle.name)
    try:
        result = run_tokyo_tower_stream.run_process(
            ["cmd.exe", "/c", run_tokyo_tower_stream.wsl_to_windows(script_path)],
            cwd=repo_path,
        )
    finally:
        script_path.unlink(missing_ok=True)
    if result.returncode != 0:
        raise RuntimeError(result.stdout + result.stderr)


def percentage_change(before: float | None, after: float | None) -> float | None:
    if before in (None, 0) or after is None:
        return None
    return ((after - before) / before) * 100.0


def compare_results(baseline: dict[str, Any], head: dict[str, Any]) -> dict[str, Any]:
    baseline_summary = baseline["summary"]
    head_summary = head["summary"]
    baseline_visible = baseline["visible_ms"]
    head_visible = head["visible_ms"]

    return {
        "summary_counts_match": baseline_summary == head_summary,
        "elapsed_delta_s": head["elapsed_s"] - baseline["elapsed_s"],
        "elapsed_change_pct": percentage_change(baseline["elapsed_s"], head["elapsed_s"]),
        "visible_ms": {
            "median_delta": None
            if baseline_visible["median"] is None or head_visible["median"] is None
            else head_visible["median"] - baseline_visible["median"],
            "median_change_pct": percentage_change(baseline_visible["median"], head_visible["median"]),
            "p95_delta": None
            if baseline_visible["p95"] is None or head_visible["p95"] is None
            else head_visible["p95"] - baseline_visible["p95"],
            "p95_change_pct": percentage_change(baseline_visible["p95"], head_visible["p95"]),
            "max_delta": None
            if baseline_visible["max"] is None or head_visible["max"] is None
            else head_visible["max"] - baseline_visible["max"],
            "max_change_pct": percentage_change(baseline_visible["max"], head_visible["max"]),
        },
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Compare the current repo against the latest v* tag with the live Tokyo Tower stream case.",
    )
    parser.add_argument("--repo-path", type=Path, default=run_tokyo_tower_stream.repo_root())
    parser.add_argument("--baseline-tag")
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT_DIR)
    parser.add_argument("--skip-build", action="store_true")
    parser.add_argument("--timeout", type=float, default=180.0)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    repo_path = args.repo_path.resolve()

    try:
        tag = args.baseline_tag or latest_vtag(repo_path)
        worktree_path = ensure_worktree(repo_path, tag)
        if not args.skip_build:
            build_solution(worktree_path)
            build_solution(repo_path)

        output_dir = args.output_dir.resolve()
        output_dir.mkdir(parents=True, exist_ok=True)
        tag_label = tag.replace("/", "-")
        baseline = run_tokyo_tower_stream.run_stream_case(
            repo_path=worktree_path,
            latitude=run_tokyo_tower_stream.TOKYO_TOWER_LATITUDE,
            longitude=run_tokyo_tower_stream.TOKYO_TOWER_LONGITUDE,
            range_m=run_tokyo_tower_stream.TOKYO_TOWER_RANGE_M,
            port=None,
            cleanup=True,
            no_build=True,
            measure_performance=True,
            log_level="Information",
            output_dir=output_dir,
            label=f"{tag_label}-{time.strftime('%Y%m%d-%H%M%S')}",
            timeout_s=args.timeout,
        )
        run_tokyo_tower_stream.validate_result(baseline)

        head = run_tokyo_tower_stream.run_stream_case(
            repo_path=repo_path,
            latitude=run_tokyo_tower_stream.TOKYO_TOWER_LATITUDE,
            longitude=run_tokyo_tower_stream.TOKYO_TOWER_LONGITUDE,
            range_m=run_tokyo_tower_stream.TOKYO_TOWER_RANGE_M,
            port=None,
            cleanup=True,
            no_build=True,
            measure_performance=True,
            log_level="Information",
            output_dir=output_dir,
            label=f"head-{time.strftime('%Y%m%d-%H%M%S')}",
            timeout_s=args.timeout,
        )
        run_tokyo_tower_stream.validate_result(head)
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        return 1

    result = {
        "baseline_tag": tag,
        "baseline_repo": str(worktree_path),
        "current_repo": str(repo_path),
        "baseline": baseline,
        "current": head,
        "comparison": compare_results(baseline, head),
    }
    json.dump(result, sys.stdout, ensure_ascii=False, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
