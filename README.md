# 3DTilesLink

A .NET tool that fetches Google Photorealistic 3D Tiles around a specified area and visualizes them non-persistently through Resonite Link.
It provides subcommand-based entry points for single-shot streaming and resident interactive mode.

This `README.md` is the human-facing entry point. Current operational details and AI-oriented procedures are kept separately under `docs/`.

<img width="2560" height="1440" alt="2026-04-04 23 31 20" src="https://github.com/user-attachments/assets/3d52ad6c-7d7a-400e-a89a-46d1cf2d128e" />

## Constraints

- To comply with Google Map Tiles API policy, streamed tiles must remain non-persistent.
- Saving streamed content into the Resonite inventory is not supported.
- `SimpleAvatarProtection` is part of the Resonite-side mitigation for keeping the streamed content non-persistent.

## Prerequisites

- `.NET SDK 10.0+`
- Google APIs required by feature
  - 3D Tiles fetch (`stream`, `interactive` tile streaming): Google Map Tiles API
    - `GOOGLE_MAPS_API_KEY` is required
  - Free-text location search in Interactive (`World/ThreeDTilesLink.Search`): Google Geocoding API
    - `GOOGLE_MAPS_API_KEY` is required
- Enable Resonite Link in Resonite and confirm the destination port

At startup, the app automatically loads `.env` with parent-directory discovery and does not overwrite existing environment variables.
Use `.env.example` as the starting template when creating a local `.env`.

## Build

```bash
dotnet build ThreeDTilesLink.slnx
```

## Test

```bash
dotnet test ThreeDTilesLink.slnx
```

## Usage (`stream`)

Required Google API: Google Map Tiles API

```bash
dotnet run --project src/ThreeDTilesLink -- stream \
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
- Run `dotnet run --project src/ThreeDTilesLink -- stream --help` for units and defaults.

## Usage (`interactive`)

Required Google APIs by operation:

- Tile streaming from `Latitude` / `Longitude` / `Range`: Google Map Tiles API
- Free-text search from `Search`: Google Geocoding API

At connection time, the app attaches watch `DynamicValueVariable<T>` values to the session root and watches:

- `World/ThreeDTilesLink.Latitude`
- `World/ThreeDTilesLink.Longitude`
- `World/ThreeDTilesLink.Range`
- `World/ThreeDTilesLink.Search`

The session-root writable values are kept as separate `DynamicValueVariable<T>` members.
The `World/` paths are exposed as alias `DynamicValueVariable<T>` members driven through `ValueCopy<T>`.
For the Interactive input parameters (`Latitude` / `Longitude` / `Range` / `Search`), `ValueCopy.WriteBack` is enabled so changes from `World/` flow back into the session-side values.
For observation-only aliases such as progress and credit text, `ValueCopy.WriteBack` stays disabled so changes on the alias side do not overwrite the source values.

Value updates are handled with debounce/throttle; when a new run starts, the previous run task is canceled and old run slots are removed.
If `Search` is updated to a non-empty string, the app resolves it with the Google Geocoding API and writes the resulting coordinates back into `Latitude` / `Longitude`.
If watch `Range` is `0` or less, no streaming run is started.

```bash
dotnet run --project src/ThreeDTilesLink -- interactive \
  --resonite-port 12000 \
  --poll-interval 250 \
  --debounce 800 \
  --throttle 3000 \
  --watch-path World/ThreeDTilesLink
```

Run `dotnet run --project src/ThreeDTilesLink -- interactive --help` for units and defaults.

- `--content-workers` controls bounded fetch/decode parallelism per run. Default is `8`.
- If `--resonite-host` is omitted, `localhost` is used.

## Documentation

- `AGENTS.md`: Minimal guide for coding agents
- `docs/current-state.md`: Current operational information and constraints
- `docs/agent-procedures.md`: Work procedures for AI agents
