# Current State

This document contains only current operational information that is difficult to infer from the code and tests alone.

## Operational Assumptions

- This project assumes the use case of streaming Google Photorealistic 3D Tiles into Resonite Link non-persistently.
- Persistent storage, assetization, and maintenance of design documents are not project goals.
- The version baseline for official releases is standardized on `git tag` values in the form `v1.2.3`.
- Builds from commits without a tag are treated as prereleases and kept distinct from official releases.
- For authentication, use an API key through `GOOGLE_MAPS_API_KEY`.
- The required APIs differ by feature as follows.
- `stream` tile fetch: Google Map Tiles API
- `interactive` tile fetch based on `Latitude` / `Longitude` / `Range`: Google Map Tiles API
- Interactive free-text search (`World/... .Search`): Google Geocoding API
- Interactive free-text search requires `GOOGLE_MAPS_API_KEY` and does not support ADC.
- The app auto-loads `.env` with parent-directory discovery and does not overwrite existing environment variables.

## Environment Variables

- `GOOGLE_MAPS_API_KEY`: set this when using the Google Map Tiles API and Google Geocoding API with an API key
- `THREEDTILESLINK_DUMP_MESH_JSON`: use this when a JSON dump of mesh transmission contents is needed

## Handling Resonite Integration

- Do not fill in Resonite component type names or member names by guesswork.
- When real values are required, query the running Resonite Link and confirm them before fixing them in code.
- Use the official ResoniteLink REPL for live inspection and member confirmation.
- From this repository, launch it through `tools/Invoke-ResoniteLinkCommand.ps1 repl ...`.
- When operating from WSL, run the verification command on the Windows host with `pwsh.exe`; do not rely on a Linux-side `pwsh` setup.
- In some live environments, `SimpleAvatarProtection` may not be exposed. Even in that case, assume connection and mesh transmission can continue.
- Put persistent writable `DynamicValueVariable<T>` members attached to the session root or parent slot on the session side first.
- When `DynamicVariableSpace.OnlyDirectBinding` is enabled, the session-side source DV name must explicitly include `SpaceName/`.
- Expose `World/` aliases as separate `DynamicValueVariable<T>` members driven from the session-side source by `ValueCopy<T>`, instead of `DynamicField`.
- Control target-side overwrite through `ValueCopy.WriteBack`; enable it only for Interactive input parameters that must flow from `World/` back into the session-side values.
- Publish progress as a float in the range `0.0..1.0` from a session-side `DynamicValueVariable<float>` on the parent slot to `World/ThreeDTilesLink.Progress` through `ValueCopy<float>`.
- Publish the human-readable progress string from a session-side `DynamicValueVariable<string>` on the parent slot to `World/ThreeDTilesLink.ProgressText` through `ValueCopy<string>`.

## One-Off Verification from WSL

- Even if Linux `pwsh` is not installed inside WSL, invoke Windows `pwsh.exe` from WSL and run the command on the host side.
- Use `tools/Invoke-ResoniteLinkCommand.ps1` for one-off verification. The primary mode is `send-json`.
- Use `repl` when you need interactive inspection through the official ResoniteLink REPL implementation.
- In some environments, `dotnet` is not visible from `pwsh.exe` through `PATH`, so the script also searches default locations for `dotnet.exe`.
- Because Linux `dotnet` and Windows `dotnet.exe` are expected to coexist in the same checkout, keep `obj` separated by host OS.
- NuGet restore metadata contains host-dependent paths, so do not return to an operation mode that shares `obj`.
- Correct version calculation in CI and verification environments requires `git tag` history, so do not keep a shallow checkout.
- Use `tools/ResoniteRawJson` for raw JSON transmission.
- From WSL, invoke host-side commands in the form `pwsh.exe -File "$(wslpath -w tools/Invoke-ResoniteLinkCommand.ps1)" <command> localhost <port> ...`.
- The port numbers used in examples must match the live Resonite Link at that moment; do not treat them as fixed values.

Example:

```bash
pwsh.exe -NoLogo -NoProfile -File "$(wslpath -w tools/Invoke-ResoniteLinkCommand.ps1)" repl localhost 6216

pwsh.exe -NoLogo -NoProfile -File "$(wslpath -w tools/Invoke-ResoniteLinkCommand.ps1)" send-json localhost 6216 \
  -Json '{"$type":"requestSessionData"}'

pwsh.exe -NoLogo -NoProfile -File "$(wslpath -w tools/Invoke-ResoniteLinkCommand.ps1)" send-json localhost 6216 \
  -JsonFile "$(wslpath -w /tmp/get-slot-root.json)"
```

- `send-json` sends one arbitrary ResoniteLink JSON message as-is, waits for the response with the matching `sourceMessageId`, and prints it in formatted form.
- If there is no `messageId`, one is added automatically before sending.
- The live `$type` values may differ from old examples in the README, so verify them against actual responses or the SDK implementation when needed.
- `repl` starts the official ResoniteLink REPL controller and is the default path for connection checks, slot traversal, component inspection, and member confirmation.
- For mesh send verification, use the application code paths or targeted JSON/message tests.

## What May Be Written in This Document

- Mandatory requirements that should be stated explicitly
- Assumptions needed in real operation
- Current constraints that are difficult to judge from the code alone

## What Not to Write in This Document

- Design explanations of class structure or processing flow
- Restatements of behavior that are already clear from reading the code and tests
