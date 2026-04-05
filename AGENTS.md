# AGENTS.md

This file is the minimal, stable, and generic guide for coding agents. Refer to `docs/` for longer explanations and current operational information.

## Document Split

- `README.md`: what the project is, brief usage, and the human-facing entry point
- `AGENTS.md`: minimal, stable, and generic rules for coding agents
- `docs/`: current operational information, decisions, and AI-oriented procedures that cannot be inferred from the code alone

## Language Rules

- The canonical documents are the English files without a language suffix.
- Provide Japanese counterparts as `.ja.*` files only for `AGENTS.md` and every file under `docs/`.
- When updating those canonical English files, transpose the same content accurately into the corresponding `.ja.*` files.

## Rules

- Put the design in the code and tests.
- Documenting mandatory requirements is fine.
- Do not add design-level documentation.
- Update documentation only when operational constraints or decisions need to be recorded and they cannot be understood from the code alone.
- Do not turn `README.md` into a detailed specification dump.
- Keep `AGENTS.md` minimal.
- Put long procedures, changeable information, and current operational details under `docs/`.
- Manage history and background in Git instead of keeping history files.
- Every file should describe only the current state; do not accumulate past states or migration history.
- Use the conventional commit format `type(scope): gitmoji summary` for commit messages.
