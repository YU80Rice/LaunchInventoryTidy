# LaunchInventoryTidy

`LaunchInventoryTidy` is a BepInEx 5 plugin for Unturned that provides server-authoritative manual inventory tidying with transaction verification, rollback protection, hotkey restoration, request admission, and per-player cooldown controls.

Current source release: **v3.0.1**

## Install

Download `publish/LaunchInventoryTidy.zip` and extract it into the Unturned installation directory. The archive has this exact layout:

```text
BepInEx/
  plugins/
    LaunchInventoryTidy.dll
```

Install the mandatory dependency `LaunchMultiplayerNet.dll` separately into the same `BepInEx/plugins/` directory. It is deliberately not bundled in this archive.

## Dependency and project relationship matrix

| Project / component | Relationship to LaunchInventoryTidy | Required at runtime |
|---|---|---:|
| [LaunchMultiplayerNet](https://github.com/YU80Rice/LaunchMultiplayerNet) | Hard BepInEx dependency. Provides the mod transport and LIT owns channel 100. Minimum supported version: **4.0.0**. | Yes |
| BepInEx 5 | Plugin loader. | Yes |
| Harmony 2 | Runtime UI patching support. | Yes |
| Unturned 3.x and its bundled Steamworks.NET assemblies | Host game and game API surface. | Yes |
| [SteamP2PFriends](https://github.com/YU80Rice/SteamP2PFriends) | Optional P2P Listen Host orchestrator. It may initialize LIT's P2P fault scope through a soft, reflection-based bridge. LIT does not reference SteamP2PFriends. | No for single-player/U3DS |
| [LaunchInPlaceReload](https://github.com/YU80Rice/LaunchInPlaceReload) | Sibling plugin. It uses LMN channel 101; there is no LIT runtime dependency. | No |
| [LaunchHordeTracker](https://github.com/YU80Rice/LaunchHordeTracker) | Sibling plugin. It uses LMN channel 102; there is no LIT runtime dependency. | No |
| [UnturnedModManager](https://github.com/YU80Rice/UnturnedModManager) | Optional launcher/deployment tool. It is not required by LIT. | No |
| LaunchTidyTestHarness / LaunchP2PDiagnostics | Development and audit tools only. They must never be shipped in the LIT release archive. | No |

See [DEPENDENCIES.md](DEPENDENCIES.md) for channel ownership, direction of optional dependencies, and deployment boundaries.

## Environment status

| Environment | Status | Evidence scope |
|---|---|---|
| Single-player | Verified | Automated suite: conservation, hotkey restoration, fault isolation, and shutdown coverage. |
| U3DS dedicated server | Verified | Controlled client/server dual-end snapshot comparison, hotkey recovery, and cooldown coverage. |
| Steam P2P / SteamP2PFriends Listen Host | Alpha, not released | v3.0.1 includes static safety wiring for scoped fault persistence. Dynamic T1-T3 two-machine validation is still required. |

Do not describe P2P as production-ready until its dynamic matrix has passed and received a separate audit decision.

## Safety model

- Manual tidy operations run through `Prepare -> fingerprint recheck -> Commit -> Verify/Rollback` on the Unity game thread.
- Unknown post-commit states fail closed and open the persistent fault circuit rather than applying destructive rollback guesses.
- Requests are session-bound, replay-checked, leased per player, admitted atomically, and rate-limited.
- Persistent fault records are scoped by mode, map identity, and save slot. Single-player and P2P records do not share a file.
- Passive inventory sorting is intentionally disabled. Use the manual UI or the configured Plugin 0 hotkey.

## Network channel ownership

LIT exclusively owns LaunchMultiplayerNet channel **100**. Do not reuse it in another plugin.

| Channel | Owner |
|---:|---|
| 100 | LaunchInventoryTidy |
| 101 | LaunchInPlaceReload |
| 102 | LaunchHordeTracker |
| 103+ | Allocate through the LaunchMultiplayerNet project before use |

## Build from source

The project targets .NET Framework 4.7.2 and expects local Unturned/BepInEx reference assemblies under `../Libs/`.

```powershell
dotnet build .\LaunchInventoryTidy.csproj -c Release -nologo
```

The only distributable DLL is:

```text
bin/Release/LaunchInventoryTidy.dll
```

TestHarness builds, audit fixtures, generated logs, and local dependency DLLs are not release artifacts.

## Versioning and license

- Assembly and file version: `3.0.1.0`
- BepInEx plugin version: `3.0.1`
- DLL filename: always `LaunchInventoryTidy.dll` without a version suffix
- License: [MIT](LICENSE)

Change history: [CHANGELOG.md](CHANGELOG.md). Detailed project/version record: [mod_version_history.md](mod_version_history.md).
