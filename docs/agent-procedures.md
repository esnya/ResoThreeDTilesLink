# Agent Procedures

This document contains procedures for AI and coding agents. It keeps only decision criteria for work, not design explanations.

## Before Making Changes

1. Read the related code and tests first.
2. Treat the code and tests as the primary sources for the design.
3. Decide whether documentation changes are needed based on whether there is information that cannot be inferred from the code.

## When Changing Behavior

1. Check the affected test scope first.
2. If the specification changes, prioritize updating the code and tests over the documentation.
3. Use documentation only to supplement mandatory requirements, operational assumptions, and reasons for decisions.

## When Touching Resonite Integration

1. Do not guess type names or member names.
2. Confirm required values from the live Resonite Link.
3. From WSL, prefer host-side execution for live verification, including `cmd.exe /c dotnet.exe run ...` or `pwsh.exe` wrappers.
4. Use `stream` to confirm live send and remove ordering.
5. Prefer Resonite Link autodiscovery over manually typing a port. The Coding Agent entry point is `tools/Invoke-ResoniteLinkCommand.ps1`.
6. The autodiscovery mechanism is based on the Resonite Unity SDK and `YellowDogMan.ResoniteLink` implementation, not on Unity Editor behavior itself:
   `LinkSessionListener` binds UDP port `12512`, listens for JSON `ResoniteLinkSession` announcements, and uses the announced `linkPort`.
7. For one-off inspection, first run `pwsh.exe -NoLogo -NoProfile -File "$(wslpath -w tools/Invoke-ResoniteLinkCommand.ps1)" discover`.
8. If exactly one session is present, omit `-Port` for `repl`, `send-json`, `benchmark-send`, and `cleanup-slot`; the script resolves it automatically.
9. If multiple sessions are present, select one with `-SessionId` or `-SessionName` instead of copying a fixed port into notes or scripts.
10. Use the official ResoniteLink REPL via `tools/Invoke-ResoniteLinkCommand.ps1 repl ...` for live inspection and member confirmation when raw JSON inspection is insufficient.
11. If application entry points still require `--resonite-port`, discover the current session immediately before running and treat that value as ephemeral input only.
12. If real-environment verification was not possible, state that assumption explicitly in the change description.

## Documentation Update Rules

1. Keep `README.md` short.
2. Keep `AGENTS.md` limited to the minimal stable and generic set.
3. Keep `docs/` limited to current information and procedures.
4. Do not add new design-level explanations.

## Verification

- Normally run `dotnet test ThreeDTilesLink.slnx`.
- In restricted agent sandboxes that block local IPC for child nodes, run `dotnet build ThreeDTilesLink.slnx --no-restore -m:1`.
- In those same environments, prefer `dotnet test tests/ThreeDTilesLink.Tests/ThreeDTilesLink.Tests.csproj --no-build` because solution-level `dotnet test` requires VSTest socket communication.
- If the change scope is limited, prioritize the relevant tests first.
- If checks that depend on the live environment were not performed, finish by stating that gap explicitly.
