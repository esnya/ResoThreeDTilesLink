#!/usr/bin/env python3
from __future__ import annotations

import argparse
import json
import re
import statistics
import subprocess
import sys
import tempfile
import time
from pathlib import Path
from typing import Any

TOKYO_TOWER_LATITUDE = 35.65858
TOKYO_TOWER_LONGITUDE = 139.745433
TOKYO_TOWER_RANGE_M = 60.0
DEFAULT_OUTPUT_DIR = Path("/mnt/c/Temp/3DTilesLinkSkillLogs/stream")
SUMMARY_PATTERN = re.compile(
    r"CandidateTiles=(?P<candidate>\d+)\s+ProcessedTiles=(?P<processed>\d+)\s+"
    r"StreamedMeshes=(?P<streamed>\d+)\s+FailedTiles=(?P<failed>\d+)"
)
VISIBLE_MS_PATTERN = re.compile(r"visibleMs=(?P<value>\d+(?:\.\d+)?)")
STREAMED_TILE_PATTERN = re.compile(r"Streamed tile (?P<tile_id>\d+):")
DISCOVER_PATTERN = re.compile(
    r"(?m)^\s*(?P<session_name>.*?)\s+(?P<session_id>S-[0-9a-fA-F-]+)\s+"
    r"(?P<port>\d{2,5})\s+(?P<address>\d+\.\d+\.\d+\.\d+)\b"
)
TIMEOUT_EXIT_CODE = 124


def repo_root() -> Path:
    return Path(__file__).resolve().parents[3]


def invoke_script(repo_path: Path) -> Path:
    return repo_path / "tools" / "Invoke-ResoniteLinkCommand.ps1"


def decode_output(data: bytes) -> str:
    for encoding in ("utf-8", "cp932"):
        try:
            return data.decode(encoding)
        except UnicodeDecodeError:
            continue
    return data.decode("utf-8", errors="replace")


def run_process(args: list[str], cwd: Path, timeout: float | None = None) -> subprocess.CompletedProcess[str]:
    result = subprocess.run(
        args,
        cwd=cwd,
        check=False,
        capture_output=True,
        text=False,
        timeout=timeout,
    )
    return subprocess.CompletedProcess(
        args=result.args,
        returncode=result.returncode,
        stdout=decode_output(result.stdout),
        stderr=decode_output(result.stderr),
    )


def wsl_to_windows(path: Path) -> str:
    result = run_process(["wslpath", "-w", str(path)], cwd=repo_root())
    if result.returncode != 0:
        raise RuntimeError(f"wslpath failed for {path}: {result.stderr.strip()}")
    return result.stdout.strip()


def invoke_resonite(repo_path: Path, args: list[str], timeout: float | None = None) -> str:
    command = [
        "pwsh.exe",
        "-NoLogo",
        "-NoProfile",
        "-File",
        wsl_to_windows(invoke_script(repo_path)),
        *args,
    ]
    result = run_process(command, cwd=repo_path, timeout=timeout)
    if result.returncode != 0:
        raise RuntimeError(result.stdout + result.stderr)
    return result.stdout


def list_windows_processes() -> list[dict[str, Any]]:
    command = [
        "pwsh.exe",
        "-NoLogo",
        "-NoProfile",
        "-Command",
        "Get-CimInstance Win32_Process | Select-Object ProcessId,Name,CommandLine | ConvertTo-Json -Compress",
    ]
    result = run_process(command, cwd=repo_root(), timeout=30)
    if result.returncode != 0:
        raise RuntimeError(result.stdout + result.stderr)
    payload = result.stdout.strip()
    if not payload:
        return []
    parsed = json.loads(payload)
    if isinstance(parsed, dict):
        return [parsed]
    return parsed


def list_active_live_cli_processes(port: int) -> list[str]:
    matches: list[str] = []
    port_pattern = re.compile(rf"--resonite-port(?:=|\s+){port}\b")
    for process in list_windows_processes():
        command_line = str(process.get("CommandLine") or "")
        normalized = command_line.lower()
        if "threedtileslink" not in normalized:
            continue
        if port_pattern.search(normalized) is None:
            continue
        matches.append(f"{process.get('ProcessId')}: {command_line}")
    return matches


def discover_port(repo_path: Path) -> int:
    output = invoke_resonite(repo_path, ["discover"], timeout=30)
    matches = list(DISCOVER_PATTERN.finditer(output))
    if len(matches) == 0:
        raise RuntimeError(f"Failed to parse port from discover output:\n{output}")
    if len(matches) > 1:
        raise RuntimeError(
            "Multiple Resonite Link sessions were discovered. Re-run with --port to select the intended live session.\n"
            f"{output}"
        )
    return int(matches[0].group("port"))


def cleanup_sessions(repo_path: Path, port: int | None) -> None:
    args = ["cleanup-sessions"]
    if port is not None:
        args.extend(["-Port", str(port)])
    _ = invoke_resonite(repo_path, args, timeout=120)


def percentile(values: list[float], ratio: float) -> float | None:
    if not values:
        return None
    if len(values) == 1:
        return values[0]
    index = (len(values) - 1) * ratio
    lower = int(index)
    upper = min(lower + 1, len(values) - 1)
    if lower == upper:
        return values[lower]
    fraction = index - lower
    return values[lower] + (values[upper] - values[lower]) * fraction


def parse_summary(stdout: str) -> dict[str, int] | None:
    match = SUMMARY_PATTERN.search(stdout)
    if match is None:
        return None
    return {
        "candidate_tiles": int(match.group("candidate")),
        "processed_tiles": int(match.group("processed")),
        "streamed_meshes": int(match.group("streamed")),
        "failed_tiles": int(match.group("failed")),
    }


def parse_visible_ms(stdout: str) -> dict[str, float | int | None]:
    values = sorted(float(match.group("value")) for match in VISIBLE_MS_PATTERN.finditer(stdout))
    if not values:
        return {
            "count": 0,
            "median": None,
            "p95": None,
            "max": None,
        }
    return {
        "count": len(values),
        "median": statistics.median(values),
        "p95": percentile(values, 0.95),
        "max": values[-1],
    }


def parse_max_tile_id_length(stdout: str) -> int:
    lengths = [len(match.group("tile_id")) for match in STREAMED_TILE_PATTERN.finditer(stdout)]
    return max(lengths, default=0)


def build_stream_command(
    repo_path: Path,
    port: int,
    *,
    latitude: float,
    longitude: float,
    range_m: float,
    no_build: bool,
    measure_performance: bool,
    log_level: str,
) -> str:
    repo_win = wsl_to_windows(repo_path)
    arguments = [
        "dotnet.exe",
        "run",
        "--project",
        r"src\ThreeDTilesLink",
    ]
    if no_build:
        arguments.append("--no-build")
    arguments.extend(
        [
            "--",
            "stream",
            "--latitude",
            f"{latitude}",
            "--longitude",
            f"{longitude}",
            "--range",
            f"{range_m}",
            "--resonite-port",
            f"{port}",
            "--log-level",
            log_level,
        ]
    )
    if measure_performance:
        arguments.append("--measure-performance")
    script = "\r\n".join(
        [
            "@echo off",
            f'cd /d "{repo_win}"',
            " ".join(arguments),
        ]
    )
    return script


def run_stream_case(
    *,
    repo_path: Path,
    latitude: float,
    longitude: float,
    range_m: float,
    port: int | None,
    cleanup: bool,
    no_build: bool,
    measure_performance: bool,
    log_level: str,
    output_dir: Path,
    label: str,
    timeout_s: float,
) -> dict[str, Any]:
    output_dir.mkdir(parents=True, exist_ok=True)

    resolved_port = port if port is not None else discover_port(repo_path)
    if cleanup:
        active_processes = list_active_live_cli_processes(resolved_port)
        if active_processes:
            formatted = "\n".join(active_processes)
            raise RuntimeError(
                "cleanup-sessions is unsafe while live stream/interactive processes are still running on the target port.\n"
                f"Stop these processes or re-run with --skip-cleanup once the session is known clean:\n{formatted}"
            )
        cleanup_sessions(repo_path, resolved_port)

    script_body = build_stream_command(
        repo_path,
        resolved_port,
        latitude=latitude,
        longitude=longitude,
        range_m=range_m,
        no_build=no_build,
        measure_performance=measure_performance,
        log_level=log_level,
    )

    started = time.monotonic()
    with tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".cmd", delete=False) as handle:
        handle.write(script_body)
        handle.flush()
        script_path = Path(handle.name)
    timed_out = False
    process = subprocess.Popen(
        ["cmd.exe", "/c", wsl_to_windows(script_path)],
        cwd=repo_path,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
        text=False,
    )
    try:
        stdout_bytes, stderr_bytes = process.communicate(timeout=timeout_s)
        result = subprocess.CompletedProcess(
            args=process.args,
            returncode=process.returncode,
            stdout=decode_output(stdout_bytes),
            stderr=decode_output(stderr_bytes),
        )
    except subprocess.TimeoutExpired:
        timed_out = True
        _ = run_process(["cmd.exe", "/c", f"taskkill /PID {process.pid} /T /F"], cwd=repo_path, timeout=30)
        stdout_bytes, stderr_bytes = process.communicate()
        result = subprocess.CompletedProcess(
            args=process.args,
            returncode=TIMEOUT_EXIT_CODE,
            stdout=decode_output(stdout_bytes),
            stderr=decode_output(stderr_bytes),
        )
    finally:
        script_path.unlink(missing_ok=True)
    elapsed_s = time.monotonic() - started

    stdout_path = output_dir / f"{label}.stdout.log"
    stderr_path = output_dir / f"{label}.stderr.log"
    stdout_path.write_text(result.stdout, encoding="utf-8")
    stderr_path.write_text(result.stderr, encoding="utf-8")
    stdout = result.stdout
    stderr = result.stderr

    summary = parse_summary(stdout)
    visible_ms = parse_visible_ms(stdout)
    max_tile_id_length = parse_max_tile_id_length(stdout)

    return {
        "repo_path": str(repo_path),
        "label": label,
        "port": resolved_port,
        "stdout_log": str(stdout_path),
        "stderr_log": str(stderr_path),
        "elapsed_s": round(elapsed_s, 3),
        "exit_code": result.returncode,
        "timed_out": timed_out,
        "summary": summary,
        "visible_ms": visible_ms,
        "max_streamed_tile_id_length": max_tile_id_length,
        "stderr_nonempty": bool(stderr.strip()),
    }


def describe_result(result: dict[str, Any]) -> str:
    return (
        f"label={result['label']} port={result['port']} stdout_log={result['stdout_log']} "
        f"stderr_log={result['stderr_log']} elapsed_s={result['elapsed_s']}"
    )


def validate_result(result: dict[str, Any]) -> None:
    details = describe_result(result)
    if result["timed_out"]:
        raise RuntimeError(f"stream timed out before completion ({details})")
    if result["exit_code"] != 0:
        raise RuntimeError(f"stream exited with code {result['exit_code']} ({details})")
    if result["stderr_nonempty"]:
        raise RuntimeError(f"stream wrote to stderr ({details})")
    if result["summary"] is None:
        raise RuntimeError(f"stream did not emit the final summary line ({details})")
    if result["summary"]["failed_tiles"] != 0:
        raise RuntimeError(f"stream failed tiles: {result['summary']['failed_tiles']} ({details})")
    if result["max_streamed_tile_id_length"] == 0:
        raise RuntimeError(f"stream emitted no 'Streamed tile' markers ({details})")
    if int(result["visible_ms"]["count"]) == 0:
        raise RuntimeError(f"stream emitted no removal 'visibleMs' markers ({details})")


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Run the standard live Tokyo Tower stream case and emit JSON summary.",
    )
    parser.add_argument("--repo-path", type=Path, default=repo_root())
    parser.add_argument("--port", type=int)
    parser.add_argument("--latitude", type=float, default=TOKYO_TOWER_LATITUDE)
    parser.add_argument("--longitude", type=float, default=TOKYO_TOWER_LONGITUDE)
    parser.add_argument("--range", type=float, dest="range_m", default=TOKYO_TOWER_RANGE_M)
    parser.add_argument("--output-dir", type=Path, default=DEFAULT_OUTPUT_DIR)
    parser.add_argument("--label")
    parser.add_argument("--skip-cleanup", action="store_true")
    parser.add_argument("--no-build", action="store_true")
    parser.add_argument("--no-measure-performance", action="store_true")
    parser.add_argument("--log-level", default="Information")
    parser.add_argument("--timeout", type=float, default=180.0)
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    label = args.label or f"tokyo-tower-stream-{time.strftime('%Y%m%d-%H%M%S')}"
    try:
        result = run_stream_case(
            repo_path=args.repo_path.resolve(),
            latitude=args.latitude,
            longitude=args.longitude,
            range_m=args.range_m,
            port=args.port,
            cleanup=not args.skip_cleanup,
            no_build=args.no_build,
            measure_performance=not args.no_measure_performance,
            log_level=args.log_level,
            output_dir=args.output_dir.resolve(),
            label=label,
            timeout_s=args.timeout,
        )
        validate_result(result)
    except Exception as exc:
        print(str(exc), file=sys.stderr)
        return 1

    json.dump(result, sys.stdout, ensure_ascii=False, indent=2)
    sys.stdout.write("\n")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
