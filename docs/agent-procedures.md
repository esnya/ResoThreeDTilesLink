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
3. Use the official ResoniteLink REPL via `tools/Invoke-ResoniteLinkCommand.ps1 repl ...` for live inspection and member confirmation.
4. If real-environment verification was not possible, state that assumption explicitly in the change description.

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
