---
name: three-dtiles-interactive-live
description: Run real Resonite Link `interactive` verification for this repo, centered on host-side startup, session-root binding attachment, stable stdout/stderr capture, and guided live rerun checks. Use when the user wants machine-level validation of interactive behavior instead of dry-run or unit tests, especially after host, DI, shutdown, input-binding, or rerun changes.
---

# 3D Tiles Interactive Live

Use this skill for live `interactive` checks against a Windows-hosted Resonite Link session. Prefer unit tests first. Switch to this skill when the question depends on real session-root bindings, live rerun behavior, or host startup under the actual Windows `dotnet.exe` path.

Warning: this workflow creates and removes live `3DTilesLink Session ...` roots and starts a long-running `interactive` process. Do not run cleanup while that process is still being used for the test sequence.

## Workflow

1. Start `interactive` through `scripts/run_interactive_live.py`.
Use it when you need the process PID plus stable stdout/stderr log files.

2. Use `scripts/cleanup_sessions.py` before or after runs when you need an explicit packaged cleanup step.

3. Use the startup logs as the primary truth source.
After startup, change the live fields sequentially in Resonite and confirm `Search query changed`, `Search resolved`, `Run started`, and `Run completed` from the captured stdout log.

4. Read the reference doc when the run behaves unexpectedly.
Use [references/workflow.md](./references/workflow.md) for log markers, failure modes, and the sequence rules that keep the test valid.

## Scripts

- `scripts/run_interactive_live.py`
Start one live `interactive` process on Windows, capture stdout/stderr, and wait until startup succeeds or fails.

- `scripts/cleanup_sessions.py`
Run the packaged cleanup step for stale `3DTilesLink Session ...` roots on the selected live session.

## Output

Return the script JSON plus the key facts:

- listener port
- interactive PID
- stdout/stderr log paths
- whether startup reached `Interactive input bindings attached`
- whether `Search resolved` appeared for each requested query
- whether `Run completed` appeared for each requested rerun
- whether the result came from the stable startup path plus manual sequential live edits
