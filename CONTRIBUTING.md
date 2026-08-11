# Contributing

## Scope

Contributions must preserve LIT's server-authoritative transaction model. Do not introduce a direct client-side inventory mutation path, a new LMN channel, or a background-thread Unity inventory call without a separate design and audit review.

## Required checks

Before opening a change:

1. Build `Release` and `TestHarness` with no newly introduced warnings or errors.
2. Keep `LaunchInventoryTidy.dll` as the only release DLL. Do not package test harnesses, logs, local reference DLLs, or audit fixtures.
3. Update `README.md`, `CHANGELOG.md`, `mod_version_history.md`, and `DEPENDENCIES.md` when behavior, version, deployment, or dependency relationships change.
4. Advance assembly/file/plugin version metadata for every behavior or public-API change.
5. Preserve the negative constraints in `P2P_PERMANENT_ADMISSION_GATES.md` for P2P-related work.

## Dependency boundaries

- LIT hard-depends only on LaunchMultiplayerNet at runtime.
- LIT must not reference SteamP2PFriends types or use reflection to infer P2P mode.
- SteamP2PFriends may optionally call LIT's public P2P scope entry after its session context is stable.
- Do not modify LaunchMultiplayerNet, Unturned assemblies, or sibling plugins as part of an LIT fix unless the task explicitly includes them.

## Tests

Single-player and U3DS evidence is required for relevant production changes. P2P changes additionally require the T1-T3 dynamic matrix before any P2P release claim.
