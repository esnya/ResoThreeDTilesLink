# 3DTilesLink

A .NET CLI tool that fetches Google Photorealistic 3D Tiles only around a specified location and visualizes them non-persistently through Resonite Link.

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

## Usage

```bash
dotnet run --project src/ThreeDTilesLink.Cli -- \
  --lat 35.65858 \
  --lon 139.745433 \
  --height-offset-m 20 \
  --half-width-m 400 \
  --link-host 127.0.0.1 \
  --link-port 12000 \
  --max-tiles 1024 \
  --max-depth 16 \
  --timeout-sec 120 \
  --log-level Information
```

- Add `--dry-run` to verify only the fetch and conversion path without sending anything to Resonite.
- If `GOOGLE_MAPS_API_KEY` is set, the API key is used; otherwise ADC is used.
- If `--height-offset-m` is omitted, `0` is used.

Example using ADC:

```bash
gcloud auth application-default login
dotnet run --project src/ThreeDTilesLink.Cli -- \
  --lat 35.65858 \
  --lon 139.745433 \
  --half-width-m 400 \
  --link-host 127.0.0.1 \
  --link-port 12000 \
  --dry-run
```

## Documentation

- `AGENTS.md`: Minimal guide for coding agents
- `docs/current-state.md`: Current operational information and constraints
- `docs/agent-procedures.md`: Work procedures for AI agents
