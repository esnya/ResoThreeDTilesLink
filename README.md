# 3DTilesLink

A .NET toolset that fetches Google Photorealistic 3D Tiles around a specified area and visualizes them non-persistently through Resonite Link.
It includes both single-shot CLI mode (`ThreeDTilesLink.Cli`) and resident interactive mode (`ThreeDTilesLink.Interactive`).

This `README.md` is the human-facing entry point. Current operational details and AI-oriented procedures are kept separately under `docs/`.

<img width="2560" height="1440" alt="2026-04-04 23 31 20" src="https://github.com/user-attachments/assets/3d52ad6c-7d7a-400e-a89a-46d1cf2d128e" />

## Prerequisites

- `.NET SDK 10.0+`
- Google APIs required by feature
  - 3D Tiles fetch (`ThreeDTilesLink.Cli`, `ThreeDTilesLink.Interactive` tile streaming): Google Map Tiles API
    - Use `GOOGLE_MAPS_API_KEY`
  - Free-text location search in Interactive (`World/ThreeDTilesLink.Search`): Google Geocoding API
    - Use `GOOGLE_MAPS_API_KEY`
- Enable Resonite Link in Resonite and confirm the destination port

At startup, the CLI automatically loads `.env` with parent-directory discovery and does not overwrite existing environment variables.

## Build

```bash
dotnet build ThreeDTilesLink.slnx
```

## Test

```bash
dotnet test ThreeDTilesLink.slnx
```

## Usage (CLI)

Required Google API: Google Map Tiles API

```bash
dotnet run --project src/ThreeDTilesLink.Cli -- \
  --latitude 35.65858 \
  --longitude 139.745433 \
  --range 400 \
  --resonite-port 12000
```

- `--range` is the minimum coverage range from the center point.
- Add `--dry-run` to verify only the fetch and conversion path without sending anything to Resonite.
- `--content-workers` controls bounded fetch/decode parallelism. Default is `8`.
- If `--resonite-host` is omitted, `localhost` is used.
- If `--height-offset` is omitted, `0` is used.
- Run `dotnet run --project src/ThreeDTilesLink.Cli -- --help` for units and defaults.

## Usage (Interactive / Resident)

Required Google APIs by operation:

- Tile streaming from `Latitude` / `Longitude` / `Range`: Google Map Tiles API
- Free-text search from `Search`: Google Geocoding API

At connection time, the app attaches probe `DynamicValueVariable<T>` values to the session root and watches:

- `World/ThreeDTilesLink.Latitude`
- `World/ThreeDTilesLink.Longitude`
- `World/ThreeDTilesLink.Range`
- `World/ThreeDTilesLink.Search`

For external observation, the `World/` paths are treated as aliases exposed through `DynamicField`. The underlying writable values remain session-root DV members.

Value updates are handled with debounce/throttle; when a new run starts, the previous run task is canceled and old run slots are removed.
If `Search` is updated to a non-empty string, the app resolves it with the Google Geocoding API and writes the resulting coordinates back into `Latitude` / `Longitude`.
If probe `Range` is `0` or less, no streaming run is started.

```bash
dotnet run --project src/ThreeDTilesLink.Interactive -- \
  --resonite-port 12000 \
  --poll-interval 250 \
  --debounce 800 \
  --throttle 3000 \
  --probe-path World/ThreeDTilesLink \
  --probe-name "3DTilesLink Probe"
```

Run `dotnet run --project src/ThreeDTilesLink.Interactive -- --help` for units and defaults.

- `--content-workers` controls bounded fetch/decode parallelism per run. Default is `8`.
- If `--resonite-host` is omitted, `localhost` is used.

## Documentation

- `AGENTS.md`: Minimal guide for coding agents
- `docs/current-state.md`: Current operational information and constraints
- `docs/agent-procedures.md`: Work procedures for AI agents
