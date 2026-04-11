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
SESSION_DATA_REQUEST_JSON = '{"$type":"requestSessionData"}'


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


def discover_session(repo_path: Path) -> dict[str, Any]:
    output = invoke_resonite(repo_path, ["discover"], timeout=30)
    matches = list(DISCOVER_PATTERN.finditer(output))
    if len(matches) == 0:
        raise RuntimeError(f"Failed to parse port from discover output:\n{output}")
    if len(matches) > 1:
        raise RuntimeError(
            "Multiple Resonite Link sessions were discovered. Re-run with --port to select the intended live session.\n"
            f"{output}"
        )
    match = matches[0]
    return {
        "session_name": match.group("session_name").strip(),
        "session_id": match.group("session_id"),
        "port": int(match.group("port")),
        "address": match.group("address"),
    }


def cleanup_sessions(repo_path: Path, host: str, port: int | None) -> None:
    args = ["cleanup-sessions"]
    if host != "localhost":
        args.append(host)
    if port is not None:
        args.extend(["-Port", str(port)])
    _ = invoke_resonite(repo_path, args, timeout=120)


def probe_listener_from_host(port: int, *, hosts: list[str], timeout_s: float = 3.0) -> dict[str, Any]:
    attempts: list[dict[str, Any]] = []
    timeout_ms = max(1, int(timeout_s * 1000))
    for host in hosts:
        command = [
            "pwsh.exe",
            "-NoLogo",
            "-NoProfile",
            "-Command",
            (
                "$client = [System.Net.Sockets.TcpClient]::new();"
                "try {"
                f" $async = $client.ConnectAsync('{host}', {port});"
                f" if (-not $async.Wait({timeout_ms})) {{ throw 'timed out'; }}"
                " if (-not $client.Connected) { throw 'connect failed'; }"
                " 'OK'"
                "} finally {"
                " $client.Dispose()"
                "}"
            ),
        ]
        result = run_process(command, cwd=repo_root(), timeout=timeout_s + 2)
        if result.returncode == 0 and result.stdout.strip() == "OK":
            return {
                "ok": True,
                "host": host,
                "port": port,
                "attempts": attempts,
            }

        error_text = (result.stderr.strip() or result.stdout.strip() or "unknown host-side probe failure")
        attempts.append(
            {
                "host": host,
                "port": port,
                "error": error_text,
            }
        )

    return {
        "ok": False,
        "port": port,
        "attempts": attempts,
    }


def ensure_listener_ready(port: int, *, hosts: list[str]) -> dict[str, Any]:
    probe = probe_listener_from_host(port, hosts=hosts)
    if probe["ok"]:
        return probe

    formatted_attempts = "\n".join(
        f"- host={attempt['host']} port={attempt['port']} error={attempt['error']}"
        for attempt in probe["attempts"]
    )
    raise RuntimeError(
        "Resonite Link listener preflight failed from the Windows host process for the selected port. "
        "Do not trust UDP discovery alone; re-resolve the listener or pass the correct --host/--port.\n"
        f"{formatted_attempts}"
    )


def probe_protocol_from_host(repo_path: Path, host: str, port: int, *, timeout_s: float = 10.0) -> dict[str, Any]:
    timeout_sec = max(1, int(timeout_s))
    with tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".json", delete=False) as handle:
        handle.write(SESSION_DATA_REQUEST_JSON)
        handle.flush()
        payload_path = Path(handle.name)
    try:
        response = invoke_resonite(
            repo_path,
            [
                "send-json",
                host,
                "-Port",
                str(port),
                "-TimeoutSec",
                str(timeout_sec),
                "-JsonFile",
                wsl_to_windows(payload_path),
                "-Compact",
            ],
            timeout=timeout_s + 5.0,
        )
    except (RuntimeError, subprocess.TimeoutExpired) as exc:
        return {
            "ok": False,
            "host": host,
            "port": port,
            "error": str(exc).strip(),
        }
    finally:
        payload_path.unlink(missing_ok=True)

    response_text = response.strip()
    response_json = extract_json_object(response_text)
    if response_json is None:
        return {
            "ok": False,
            "host": host,
            "port": port,
            "error": f"send-json completed but no JSON object could be parsed from output: {response_text}",
        }

    success = response_json.get("success")
    response_type = response_json.get("$type")
    if success is not True:
        return {
            "ok": False,
            "host": host,
            "port": port,
            "error": f"send-json returned success={success!r}: {response_json}",
        }
    if response_type != "sessionData":
        return {
            "ok": False,
            "host": host,
            "port": port,
            "error": f"unexpected response type {response_type!r}: {response_json}",
        }

    return {
        "ok": True,
        "host": host,
        "port": port,
        "response": response_text,
        "response_json": response_json,
    }


def ensure_protocol_ready(repo_path: Path, host: str, port: int) -> dict[str, Any]:
    probe = probe_protocol_from_host(repo_path, host, port)
    if probe["ok"]:
        return probe

    raise RuntimeError(
        "Resonite Link protocol preflight failed from the Windows host process after TCP connect succeeded. "
        "The listener accepted the socket but did not answer a minimal send-json request.\n"
        f"- host={host} port={port} error={probe['error']}"
    )


def unique_hosts(hosts: list[str]) -> list[str]:
    seen: set[str] = set()
    result: list[str] = []
    for host in hosts:
        normalized = host.strip()
        if not normalized or normalized in seen:
            continue
        seen.add(normalized)
        result.append(normalized)
    return result


def resolve_candidate_hosts(host: str | None, discovered_session: dict[str, Any] | None) -> list[str]:
    if host is not None:
        return unique_hosts([host])

    discovered_address = None if discovered_session is None else str(discovered_session.get("address") or "").strip()
    return unique_hosts(["localhost", discovered_address] if discovered_address else ["localhost"])


def extract_json_object(output: str) -> dict[str, Any] | None:
    for line in reversed(output.splitlines()):
        candidate = line.strip()
        if not candidate.startswith("{") or not candidate.endswith("}"):
            continue
        try:
            parsed = json.loads(candidate)
        except json.JSONDecodeError:
            continue
        if isinstance(parsed, dict):
            return parsed
    return None


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
    host: str,
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
            "--resonite-host",
            host,
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
    host: str | None = None,
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

    discovered_session = None if port is not None else discover_session(repo_path)
    resolved_port = port if port is not None else int(discovered_session["port"])
    candidate_hosts = resolve_candidate_hosts(host, discovered_session)
    listener_probe = ensure_listener_ready(resolved_port, hosts=candidate_hosts)
    resolved_host = str(listener_probe["host"])
    protocol_probe = ensure_protocol_ready(repo_path, resolved_host, resolved_port)
    if cleanup:
        active_processes = list_active_live_cli_processes(resolved_port)
        if active_processes:
            formatted = "\n".join(active_processes)
            raise RuntimeError(
                "cleanup-sessions is unsafe while live stream/interactive processes are still running on the target port.\n"
                f"Stop these processes or re-run with --skip-cleanup once the session is known clean:\n{formatted}"
            )
        cleanup_sessions(repo_path, resolved_host, resolved_port)

    script_body = build_stream_command(
        repo_path,
        resolved_host,
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
        try:
            stdout_bytes, stderr_bytes = process.communicate(timeout=30)
        except subprocess.TimeoutExpired:
            process.kill()
            stdout_bytes, stderr_bytes = process.communicate(timeout=30)
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
        "host": resolved_host,
        "port": resolved_port,
        "listener_probe": listener_probe,
        "protocol_probe": protocol_probe,
        "discovered_session": discovered_session,
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
    parser.add_argument("--host")
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
            host=args.host,
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
