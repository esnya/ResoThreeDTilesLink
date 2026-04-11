---
name: three-dtiles-stream-tokyo-tower-live
description: Run real Resonite Link `stream` verification around Tokyo Tower and compare the current repo against the latest `v*` tag. Use when the user wants machine-level live-send or performance regression checks instead of dry-run or unit tests, including session discovery, stale-session cleanup, host-side Windows execution, log capture, summary extraction, `visibleMs` aggregation, and latest-tag comparisons.
---

# 3D Tiles Stream Tokyo Tower Live

Use this skill for real `stream` validation against a Windows-hosted Resonite Link session. Prefer unit tests and dry-run checks first. Switch to this skill when the question is about live send ordering, log-backed refinement/removal behavior, wall-clock regressions, or comparison against the latest release tag.

Warning: this workflow removes existing `3DTilesLink Session ...` roots in the target live world. Use it only in a disposable session, or after the user has clearly accepted that cleanup.

## Workflow

1. Resolve the listener port, then verify it from the same Windows host context that will run `dotnet.exe`.
Prefer an explicit `--host` and `--port` when the target listener is already known. If auto-discovery is used, treat the discovered endpoint as provisional until the script's host-side TCP preflight passes. The script tries `localhost` first and then the discovered announce address as a fallback host candidate.

2. Verify protocol responsiveness before cleanup or `stream`.
The script sends a minimal host-side `send-json` request after TCP connect succeeds. Treat TCP success without protocol response as invalid.

3. Run the standard Tokyo Tower live case with `scripts/run_tokyo_tower_stream.py`.
Use it when you need one live run with logs, summary extraction, `visibleMs` stats, the host-side TCP preflight result, and the host-side protocol preflight result.

4. Use `scripts/cleanup_sessions.py` when you need an explicit packaged cleanup step.

5. Run `scripts/compare_latest_vtag.py` when the question is regression-oriented.
It prepares a worktree for the latest `v*` tag, builds both trees on Windows, runs the same live scenario with `--no-build`, and prints a JSON comparison.

6. Treat preflight failure, `stderr`, missing summary lines, or non-zero `FailedTiles` as invalid.
If UDP discovery succeeds but the script reports host-side TCP preflight failure or host-side protocol preflight failure, do not continue with cleanup or performance conclusions. Re-resolve the port or fix the listener first.

7. If the user says the listener is already up, verify that claim with the host-side preflights instead of assuming that UDP announcements are current.

8. Use the reference doc for interpretation details.
Do not call the run clean if the app did not emit the final `CandidateTiles=... ProcessedTiles=... StreamedMeshes=... FailedTiles=...` line.
Read [references/workflow.md](./references/workflow.md) when you need the expected markers, common failure modes, or how to judge visual/performance drift.

## Scripts

- `scripts/run_tokyo_tower_stream.py`
Run one live Tokyo Tower `stream` case, clean the live world first, capture stdout/stderr logs, and emit a JSON summary.
The script first verifies that the selected host/port accepts a TCP connection from a Windows host process, then sends a minimal `send-json` request from the same host context before cleanup or `stream`.

- `scripts/cleanup_sessions.py`
Run the packaged cleanup step for stale `3DTilesLink Session ...` roots on the selected live session.

- `scripts/compare_latest_vtag.py`
Compare the current repo against the latest `v*` tag with the same live Tokyo Tower scenario and emit JSON with per-case summaries plus deltas.

## Output

Return the script JSON plus the key facts:

- listener port
- host-side TCP preflight result
- host-side protocol preflight result
- stdout/stderr log paths
- wall-clock duration
- `CandidateTiles`, `ProcessedTiles`, `StreamedMeshes`, `FailedTiles`
- `visibleMs` count, median, p95, max
- whether the latest-tag comparison changed output counts or latency
