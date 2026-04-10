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

## Standard Coverage Guide

- Initial run
  Start from a clean session root and confirm the first resolved search or coordinate set leads to one normal `Run started` -> `Run completed` cycle.

- Relocation by new target
  Change `Search` to a clearly different place, or set a distant coordinate target directly, and confirm the rerun starts around the new target. This is the broad "jump somewhere else" case, distinct from the smaller overlap-focused moves below.

- Range-only change
  Keep location fixed and change only `Range`. Confirm the rerun is attributable to `Selection input changed ... range=...m`.

- Move within range
  Move the selection center by a smaller distance that should keep substantial overlap with the existing area. Confirm a rerun occurs and inspect whether retention/replacement behavior still looks healthy.

- Move out of range
  Move the selection center far enough that the previous area should no longer overlap materially. Confirm a rerun occurs and inspect whether the old area is replaced cleanly.

Treat those five cases as the default coverage set for a substantial interactive regression pass.

## Step Selection

- Do not force one canonical edit sequence.
  Choose the exact `Search` / `Latitude` / `Longitude` / `Range` changes that best isolate the behavior you need to test in the current session.

- Prefer one-variable changes when you want attribution.
  If the purpose is to validate a range-only or move-only reaction, change only that input and wait for completion before the next step.

- Use `Search` when you need a human-meaningful relocation point.
  Use direct coordinate edits when you need a controlled move size or exact overlap expectations.

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
