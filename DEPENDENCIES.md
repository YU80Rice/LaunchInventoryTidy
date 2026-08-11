# Dependency and Integration Boundaries

## Mandatory runtime dependency

`LaunchMultiplayerNet.dll` version **4.0.0 or newer** is a hard BepInEx dependency of LaunchInventoryTidy. Deploy the bare filename `LaunchMultiplayerNet.dll` next to `LaunchInventoryTidy.dll` in `BepInEx/plugins/`.

LIT owns LMN channel 100. This ownership includes its request, commit, rejection, session, and hotkey-result messages. Other plugins must not register or send on channel 100.

## Optional integration projects

| Project | Direction | Contract |
|---|---|---|
| SteamP2PFriends | SteamP2PFriends -> LIT, optional | After P2P host identity and Stage6A context are stable, it may reflectively invoke LIT `BeginScope("p2p", mapName, saveSlot)`. LIT never depends on SteamP2PFriends. |
| LaunchInPlaceReload | None | Independent sibling plugin; LMN channel 101. |
| LaunchHordeTracker | None | Independent sibling plugin; LMN channel 102. |
| UnturnedModManager | None | Optional deployment/launcher utility. |
| LaunchTidyTestHarness | Test-only | Never ship or install with the release package. |
| LaunchP2PDiagnostics | Test-only | Never ship or install with the release package. |

## Platform-provided dependencies

LIT is built against Unturned 3.x, BepInEx 5, Harmony 2, Unity assemblies, Steamworks.NET, and Newtonsoft.Json supplied by the local development/runtime environment. Those assemblies are not redistributed in this repository or the LIT release ZIP.

## Release boundaries

- The release ZIP contains only `BepInEx/plugins/LaunchInventoryTidy.dll`.
- LaunchMultiplayerNet and every optional sibling/plugin must be obtained and installed separately.
- Single-player and U3DS are verified environments. Steam P2P remains an Alpha integration until its dynamic matrix passes.
