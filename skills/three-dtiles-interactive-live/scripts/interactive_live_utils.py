#!/usr/bin/env python3
from __future__ import annotations

import json
import re
import subprocess
import tempfile
import time
from pathlib import Path
from typing import Any

DISCOVER_PATTERN = (
    r"(?m)^\s*(?P<session_name>.*?)\s+(?P<session_id>S-[0-9a-fA-F-]+)\s+"
    r"(?P<port>\d{2,5})\s+(?P<address>\d+\.\d+\.\d+\.\d+)\b"
)
INPUT_NAMES = ("Search", "Latitude", "Longitude", "Range", "ProgressText")
SESSION_ROOT_PREFIX = "3DTilesLink Session "


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


def cmd_quote(value: str) -> str:
    escaped = value.replace('"', '""')
    return f'"{escaped}"'


def invoke_resonite(repo_path: Path, args: list[str], timeout: float | None = None) -> str:
    with tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".cmd", delete=False) as handle:
        script_path = Path(handle.name)
    stdout_path = script_path.with_suffix(".stdout.log")
    stderr_path = script_path.with_suffix(".stderr.log")
    command = " ".join(
        [
            "pwsh.exe",
            "-NoLogo",
            "-NoProfile",
            "-File",
            cmd_quote(wsl_to_windows(invoke_script(repo_path))),
            *[cmd_quote(arg) for arg in args],
            f"1>{cmd_quote(wsl_to_windows(stdout_path))}",
            f"2>{cmd_quote(wsl_to_windows(stderr_path))}",
        ]
    )
    script_path.write_text(f"@echo off\r\n{command}\r\nexit /b %errorlevel%\r\n", encoding="utf-8")
    try:
        result = run_process(["cmd.exe", "/c", wsl_to_windows(script_path)], cwd=repo_path, timeout=timeout)
        stdout = stdout_path.read_text(encoding="utf-8", errors="replace") if stdout_path.exists() else ""
        stderr = stderr_path.read_text(encoding="utf-8", errors="replace") if stderr_path.exists() else ""
    finally:
        script_path.unlink(missing_ok=True)
        stdout_path.unlink(missing_ok=True)
        stderr_path.unlink(missing_ok=True)
    if result.returncode != 0:
        raise RuntimeError(stdout + stderr + result.stdout + result.stderr)
    return stdout


def extract_json_payload(output: str) -> dict[str, Any]:
    for line in reversed(output.splitlines()):
        candidate = line.strip()
        if candidate.startswith("{") and candidate.endswith("}"):
            return json.loads(candidate)
    raise RuntimeError(f"Failed to locate JSON payload in output:\n{output}")


def send_json(repo_path: Path, payload: dict[str, Any], timeout: float | None = None) -> dict[str, Any]:
    with tempfile.NamedTemporaryFile("w", encoding="utf-8", suffix=".json", delete=False) as handle:
        json.dump(payload, handle, ensure_ascii=False)
        handle.flush()
        request_path = Path(handle.name)
    try:
        output = invoke_resonite(
            repo_path,
            ["send-json", "-JsonFile", wsl_to_windows(request_path), "-Compact"],
            timeout=timeout,
        )
        return extract_json_payload(output)
    finally:
        request_path.unlink(missing_ok=True)


def discover_port(repo_path: Path) -> int:
    import re

    output = invoke_resonite(repo_path, ["discover"], timeout=30)
    matches = list(re.finditer(DISCOVER_PATTERN, output))
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


def guarded_cleanup_sessions(repo_path: Path, port: int) -> None:
    active_processes = list_active_live_cli_processes(port)
    if active_processes:
        raise RuntimeError(
            "cleanup-sessions is unsafe while live stream/interactive processes are still running on the target port.\n"
            + "\n".join(active_processes)
        )
    cleanup_sessions(repo_path, port)


def fetch_slot(repo_path: Path, slot_id: str, *, include_component_data: bool, depth: int) -> dict[str, Any]:
    response = send_json(
        repo_path,
        {
            "$type": "getSlot",
            "slotId": slot_id,
            "includeComponentData": include_component_data,
            "depth": depth,
        },
        timeout=120,
    )
    if not response.get("success", False):
        raise RuntimeError(f"getSlot {slot_id} failed: {response.get('errorInfo')}")
    return response["data"]


def fetch_session_root(repo_path: Path, session_root_id: str | None = None) -> dict[str, Any]:
    if session_root_id is not None:
        return fetch_slot(repo_path, session_root_id, include_component_data=False, depth=1)
    root_slot = fetch_slot(repo_path, "Root", include_component_data=False, depth=1)
    session_roots = [
        child
        for child in root_slot.get("children") or []
        if (child.get("name") or {}).get("value", "").startswith(SESSION_ROOT_PREFIX)
    ]
    if not session_roots:
        raise RuntimeError("Could not locate a 3DTilesLink session root under Root.")
    if len(session_roots) > 1:
        raise RuntimeError("Multiple 3DTilesLink session roots are present. Clean up stale roots or select a single live session.")
    session_root_id = session_roots[0]["id"]
    return fetch_slot(repo_path, session_root_id, include_component_data=False, depth=1)


def iter_slots(slot: dict[str, Any]) -> list[dict[str, Any]]:
    items = [slot]
    for child in slot.get("children") or []:
        items.extend(iter_slots(child))
    return items


def find_text_component(slot: dict[str, Any]) -> dict[str, Any]:
    text_field_slot = next(
        (child for child in slot.get("children") or [] if child.get("name", {}).get("value") == "TextField"),
        None,
    )
    if text_field_slot is None:
        raise RuntimeError(f"Slot {slot.get('id')} is missing its TextField child.")
    for nested in iter_slots(text_field_slot):
        for component in nested.get("components") or []:
            if component.get("componentType") == "[FrooxEngine]FrooxEngine.UIX.Text":
                content = component.get("members", {}).get("Content")
                if isinstance(content, dict):
                    return {
                        "component_id": component["id"],
                        "content_member_id": content["id"],
                        "value": content.get("value"),
                    }
    raise RuntimeError(f"Slot {slot.get('id')} is missing a UIX.Text content field.")


def get_interactive_inputs(repo_path: Path, session_root_id: str | None = None) -> dict[str, Any]:
    session_root = fetch_session_root(repo_path, session_root_id=session_root_id)
    session_root = fetch_slot(repo_path, session_root["id"], include_component_data=False, depth=6)
    result: dict[str, Any] = {
        "group_slot_id": session_root["id"],
        "group_slot_name": session_root.get("name", {}).get("value"),
        "inputs": {},
    }
    children_by_name: dict[str, dict[str, Any]] = {}
    for child in iter_slots(session_root):
        name = child.get("name", {}).get("value")
        if name in INPUT_NAMES and name not in children_by_name:
            children_by_name[name] = child
    for name in INPUT_NAMES:
        child = children_by_name.get(name)
        if child is None:
            continue
        child_details = fetch_slot(repo_path, child["id"], include_component_data=True, depth=4)
        text_component = find_text_component(child_details)
        result["inputs"][name] = {
            "slot_id": child_details["id"],
            "text_component_id": text_component["component_id"],
            "content_member_id": text_component["content_member_id"],
            "value": text_component["value"],
        }
    missing = [name for name in ("Search", "Latitude", "Longitude", "Range") if name not in result["inputs"]]
    if missing:
        raise RuntimeError(f"Interactive input cluster is missing fields: {', '.join(missing)}")
    return result


def wait_for_interactive_inputs(
    repo_path: Path,
    timeout_s: float,
    session_root_id: str | None = None,
) -> dict[str, Any]:
    deadline = time.monotonic() + timeout_s
    last_error: RuntimeError | None = None
    while time.monotonic() < deadline:
        try:
            return get_interactive_inputs(repo_path, session_root_id=session_root_id)
        except RuntimeError as exc:
            last_error = exc
            time.sleep(0.5)
    if last_error is not None:
        raise last_error
    raise TimeoutError("Timed out waiting for interactive inputs to appear.")


def set_interactive_input(repo_path: Path, name: str, value: str, session_root_id: str | None = None) -> dict[str, Any]:
    bindings = get_interactive_inputs(repo_path, session_root_id=session_root_id)
    target = bindings["inputs"].get(name)
    if target is None:
        raise RuntimeError(f"Unknown interactive input name: {name}")
    response = send_json(
        repo_path,
        {
            "$type": "updateComponent",
            "data": {
                "id": target["text_component_id"],
                "members": {
                    "Content": {
                        "$type": "string",
                        "value": value,
                        "id": target["content_member_id"],
                    }
                },
            },
        },
        timeout=120,
    )
    if not response.get("success", False):
        raise RuntimeError(f"Failed to update {name}: {response.get('errorInfo')}")
    return get_interactive_inputs(repo_path, session_root_id=session_root_id)


def start_interactive_process(
    repo_path: Path,
    *,
    port: int | None,
    output_dir: Path,
    prefix: str,
    no_build: bool,
    poll_interval_ms: int,
    debounce_ms: int,
    throttle_ms: int,
    log_level: str,
) -> dict[str, Any]:
    resolved_port = port if port is not None else discover_port(repo_path)
    output_dir.mkdir(parents=True, exist_ok=True)
    stdout_log = output_dir / f"{prefix}.stdout.log"
    stderr_log = output_dir / f"{prefix}.stderr.log"
    launcher_script = output_dir / f"{prefix}.launch.cmd"
    stdout_log.unlink(missing_ok=True)
    stderr_log.unlink(missing_ok=True)
    launcher_script.unlink(missing_ok=True)

    repo_win = wsl_to_windows(repo_path)
    stdout_win = wsl_to_windows(stdout_log)
    stderr_win = wsl_to_windows(stderr_log)
    command = "dotnet.exe run --project src\\ThreeDTilesLink"
    if no_build:
        command += " --no-build"
    command += (
        f" -- interactive --resonite-port {resolved_port}"
        f" --poll-interval {poll_interval_ms}"
        f" --debounce {debounce_ms}"
        f" --throttle {throttle_ms}"
        f' --log-level "{log_level}"'
        f' 1>"{stdout_win}" 2>"{stderr_win}"'
    )
    launcher_script.write_text(
        "\r\n".join(
            [
                "@echo off",
                f'cd /d "{repo_win}"',
                command,
            ]
        )
        + "\r\n",
        encoding="utf-8",
    )

    start_script = "\n".join(
        [
            f"$launcher = '{wsl_to_windows(launcher_script)}'",
            f"$workingDirectory = '{repo_win}'",
            "$process = Start-Process -FilePath 'cmd.exe' "
            "-WorkingDirectory $workingDirectory "
            "-ArgumentList @('/c', $launcher) "
            "-PassThru",
            "$process.Id",
        ]
    )
    start_script_path = output_dir / f"{prefix}.start.ps1"
    start_script_path.write_text(start_script, encoding="utf-8")

    try:
        result = run_process(
            [
                "pwsh.exe",
                "-NoLogo",
                "-NoProfile",
                "-ExecutionPolicy",
                "Bypass",
                "-File",
                wsl_to_windows(start_script_path),
            ],
            cwd=repo_path,
            timeout=60,
        )
    finally:
        start_script_path.unlink(missing_ok=True)

    if result.returncode != 0:
        raise RuntimeError(result.stdout + result.stderr)
    pid_text = result.stdout.strip().splitlines()[-1].strip()
    if not pid_text.isdigit():
        raise RuntimeError(f"Failed to parse interactive PID from output:\n{result.stdout}")
    return {
        "pid": int(pid_text),
        "port": resolved_port,
        "stdout_log": str(stdout_log),
        "stderr_log": str(stderr_log),
        "launcher_script": str(launcher_script),
    }


def read_text(path: Path) -> str:
    if not path.exists():
        return ""
    return path.read_text(encoding="utf-8", errors="replace")


def kill_process(pid: int) -> None:
    _ = run_process(["cmd.exe", "/c", f"taskkill /PID {pid} /T /F"], cwd=repo_root(), timeout=30)


def wait_for_input_value(
    repo_path: Path,
    name: str,
    expected: str,
    timeout_s: float,
    session_root_id: str | None = None,
) -> dict[str, Any]:
    deadline = time.monotonic() + timeout_s
    while time.monotonic() < deadline:
        bindings = get_interactive_inputs(repo_path, session_root_id=session_root_id)
        if str(bindings["inputs"][name]["value"]) == expected:
            return bindings
        time.sleep(0.5)
    raise TimeoutError(f"Timed out waiting for {name}={expected}.")
