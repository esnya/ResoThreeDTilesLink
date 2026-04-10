---
name: three-dtiles-stream-tokyo-tower-live
description: Run real Resonite Link `stream` verification around Tokyo Tower and compare the current repo against the latest `v*` tag. Use when the user wants machine-level live-send or performance regression checks instead of dry-run or unit tests, including session discovery, stale-session cleanup, host-side Windows execution, log capture, summary extraction, `visibleMs` aggregation, and latest-tag comparisons.
---

# 3D Tiles Stream Tokyo Tower Live

Use this skill for real `stream` validation against a Windows-hosted Resonite Link session. Prefer unit tests and dry-run checks first. Switch to this skill when the question is about live send ordering, log-backed refinement/removal behavior, wall-clock regressions, or comparison against the latest release tag.

Warning: this workflow removes existing `3DTilesLink Session ...` roots in the target live world. Use it only in a disposable session, or after the user has clearly accepted that cleanup.

## Workflow

1. Run the standard Tokyo Tower live case with `scripts/run_tokyo_tower_stream.py`.
Use it when you need one live run with logs, summary extraction, and `visibleMs` stats.

2. Use `scripts/cleanup_sessions.py` when you need an explicit packaged cleanup step.

3. Run `scripts/compare_latest_vtag.py` when the question is regression-oriented.
It prepares a worktree for the latest `v*` tag, builds both trees on Windows, runs the same live scenario with `--no-build`, and prints a JSON comparison.

4. Treat `stderr`, missing summary lines, or non-zero `FailedTiles` as invalid.
Do not call the run clean if the app did not emit the final `CandidateTiles=... ProcessedTiles=... StreamedMeshes=... FailedTiles=...` line.

5. Use the reference doc for interpretation details.
Read [references/workflow.md](./references/workflow.md) when you need the expected markers, common failure modes, or how to judge visual/performance drift.

## Scripts

- `scripts/run_tokyo_tower_stream.py`
Run one live Tokyo Tower `stream` case, clean the live world first, capture stdout/stderr logs, and emit a JSON summary.

- `scripts/cleanup_sessions.py`
Run the packaged cleanup step for stale `3DTilesLink Session ...` roots on the selected live session.

- `scripts/compare_latest_vtag.py`
Compare the current repo against the latest `v*` tag with the same live Tokyo Tower scenario and emit JSON with per-case summaries plus deltas.

## Output

Return the script JSON plus the key facts:

- listener port
- stdout/stderr log paths
- wall-clock duration
- `CandidateTiles`, `ProcessedTiles`, `StreamedMeshes`, `FailedTiles`
- `visibleMs` count, median, p95, max
- whether the latest-tag comparison changed output counts or latency
