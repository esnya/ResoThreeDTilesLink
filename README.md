# 3DTilesLink

A .NET toolset that fetches Google Photorealistic 3D Tiles around a specified area and visualizes them non-persistently through Resonite Link.
It includes both single-shot CLI mode (`ThreeDTilesLink.Cli`) and resident interactive mode (`ThreeDTilesLink.Interactive`).

This `README.md` is the human-facing entry point. Current operational details and AI-oriented procedures are kept separately under `docs/`.

<img width="2560" height="1440" alt="2026-04-04 23 31 20" src="https://github.com/user-attachments/assets/3d52ad6c-7d7a-400e-a89a-46d1cf2d128e" />

## Prerequisites

- `.NET SDK 10.0+`
- Authentication for the Google Map Tiles API
  - Use `GOOGLE_MAPS_API_KEY`
  - Or use ADC (`gcloud auth application-default login` / `GOOGLE_APPLICATION_CREDENTIALS`)
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

```bash
dotnet run --project src/ThreeDTilesLink.Cli -- \
  --latitude 35.65858 \
  --longitude 139.745433 \
  --range 400 \
  --resonite-host 127.0.0.1 \
  --resonite-port 12000
```

- `--range` is the minimum coverage range from the center point.
- Add `--dry-run` to verify only the fetch and conversion path without sending anything to Resonite.
- If `GOOGLE_MAPS_API_KEY` is set, the API key is used; otherwise ADC is used.
- If `--height-offset` is omitted, `0` is used.
- Run `dotnet run --project src/ThreeDTilesLink.Cli -- --help` for units and defaults.

Example using ADC:

```bash
gcloud auth application-default login
dotnet run --project src/ThreeDTilesLink.Cli -- \
  --latitude 35.65858 \
  --longitude 139.745433 \
  --range 400 \
  --resonite-host 127.0.0.1 \
  --resonite-port 12000 \
  --dry-run
```

## Usage (Interactive / Resident)

At connection time, the app creates a probe slot and watches `DynamicValueVariable<T>` values under:

- `World/ThreeDTilesLink.Latitude`
- `World/ThreeDTilesLink.Longitude`
- `World/ThreeDTilesLink.Range`

Value updates are handled with debounce/throttle; when a new run starts, the previous run task is canceled and old run slots are removed.
If probe `Range` is `0` or less, no streaming run is started.

```bash
dotnet run --project src/ThreeDTilesLink.Interactive -- \
  --resonite-host 127.0.0.1 \
  --resonite-port 12000 \
  --poll-interval 250 \
  --debounce 800 \
  --throttle 3000 \
  --probe-path World/ThreeDTilesLink \
  --probe-name "3DTilesLink Probe"
```

Run `dotnet run --project src/ThreeDTilesLink.Interactive -- --help` for units and defaults.

## Documentation

- `AGENTS.md`: Minimal guide for coding agents
- `docs/current-state.md`: Current operational information and constraints
- `docs/agent-procedures.md`: Work procedures for AI agents
