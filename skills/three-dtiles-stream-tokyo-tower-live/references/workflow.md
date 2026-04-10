# Stream Tokyo Tower Workflow

## Preconditions

- Use a live Windows-hosted Resonite Link session.
- Run from WSL or Linux only if host-side execution is still delegated to `cmd.exe` or `pwsh.exe`.
- Keep the target session disposable because cleanup removes `3DTilesLink Session ...` roots.
- Use `scripts/cleanup_sessions.py` for the packaged cleanup step referenced below.

## Standard Case

- Latitude: `35.65858`
- Longitude: `139.745433`
- Range: `60`
- Use the app defaults unless the user explicitly wants a different tuning point.

## Valid Run Criteria

- `scripts/cleanup_sessions.py` succeeded before the run.
- The app emitted the final hosted-service summary line:
  `CandidateTiles=... ProcessedTiles=... StreamedMeshes=... FailedTiles=...`
- `FailedTiles == 0`
- `stderr` is empty

## Useful Signals

- `Streamed tile ...` plus `Removed tile ... visibleMs=...` are the log markers this skill uses as a proxy for real replacement/removal behavior.
- Long streamed tile IDs, often reaching length `26` in the Tokyo Tower case, are evidence that traversal reached leaf-level tiles.
- `visibleMs` is the best quick latency signal for replacement/removal regressions in the live case.

## Latest Tag Comparison

- Use the latest `v*` tag as the release baseline.
- Build both trees first, then run with `--no-build` so runtime differences are not dominated by build cost.
- Compare:
  - final output counts
  - elapsed seconds
  - `visibleMs` median
  - `visibleMs` p95
  - `visibleMs` max

If final output counts diverge, treat that as a functional regression until proven otherwise.

## Common Failure Modes

- `No Resonite Link session was discovered`
  The live session is missing or discovery was pointed at the wrong machine.

- `scripts/cleanup_sessions.py` removed an unexpected root
  The session was not disposable; invalidate the run and confirm scope with the user.

- Missing final summary line
  The run was interrupted, host startup failed, or the hosted service faulted before exit.

- `FailedTiles > 0`
  Treat the run as failed even if some meshes streamed.
