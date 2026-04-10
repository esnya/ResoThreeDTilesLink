# Interactive Live Workflow

## Preconditions

- Use a live Windows-hosted Resonite Link session.
- Keep the session disposable because this workflow creates and removes `3DTilesLink Session ...` roots.
- Keep `GOOGLE_MAPS_API_KEY` available when the test depends on `Search`.
- Use `scripts/cleanup_sessions.py` for the packaged cleanup step referenced below.

## Valid Sequence

1. Clean up stale session roots before starting the `interactive` process.
2. Start `interactive` on Windows and wait for:
   - `Interactive mode started`
   - `Interactive input bindings attached`
3. Change the live fields sequentially in Resonite and verify each rerun from the captured stdout log.
4. Stop the process before cleaning up the world again.

## Standard Regression Exercise

- Set `Range` to a known value, usually `100`, and confirm the binding changed.
- Set `Search` to `東京タワー` and wait for:
  - `Search query changed`
  - `Search resolved`
  - `Run completed`
- Set `Range` to `60` and wait for:
  - `Selection input changed ... range=60.0m`
  - `Run started ... range=60.0m`
  - `Run completed`
- Set `Search` to `東京駅` and wait for:
  - `Search resolved: query=東京駅`
  - `Run completed`

## Invalid Procedure

- Do not run `scripts/cleanup_sessions.py` while the `interactive` process is still alive for the same session root.
  That causes `Component ... not found` noise and invalidates the test.

- Do not set `Search` and `Range` simultaneously if you want to attribute the rerun to one change.
  Update them sequentially and wait for the previous step to finish.

## Common Failure Modes

- Host startup fails before bindings attach.
  Check stderr or the stdout host log for DI or host construction faults.

- `Search resolved` never appears.
  Check `GOOGLE_MAPS_API_KEY`, the actual `Search` field value, and whether the live binding IDs still exist.

- `Range` changes in the world but no rerun starts.
  Confirm that valid `Latitude` and `Longitude` are already present, then retry the `Range` step on its own.

- Repeated `Component ... not found` warnings after a previously healthy run.
  The session root was likely cleaned up externally while the process kept polling.
